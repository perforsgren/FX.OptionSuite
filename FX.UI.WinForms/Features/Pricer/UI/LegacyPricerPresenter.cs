using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using FX.Core.Interfaces;
using FX.Infrastructure.Calendars.Legacy;
using FX.Messages.Events;
using FX.Core.Domain.MarketData; 
using FX.Services.MarketData;
using System.Diagnostics;
using System.Threading;
using System.Linq;
using FX.Services;
using static FX.UI.WinForms.LegacyPricerView;

namespace FX.UI.WinForms
{
    /// <summary>
    /// Presenter för Legacy-pricern.
    /// - Kopplar UI-händelser till bus/servicelager.
    /// - Hanterar expiry-resolve, spot-snapshots och prissättning per ben / alla ben.
    /// - Lyssnar på bus-events (PriceCalculated, ErrorOccurred) och uppdaterar vyn.
    /// </summary>
    public sealed class LegacyPricerPresenter : IDisposable
    {
        #region Variables

        private readonly LegacyPricerView _view;
        private readonly Dictionary<Guid, Guid> _corrToLegId = new Dictionary<Guid, Guid>();
        private readonly IDisposable _subPrice;
        private readonly IDisposable _subError;
        private readonly IMessageBus _bus;
        private readonly ISpotSetDateService _spotSvc;
        private readonly ISpotFeed _spotFeed;
        private readonly IMarketStore _mktStore;
        private readonly int _spotTimeoutMs = 3000;

        // Debounce/single-flight för Reprice
        private const int RepriceDebounceMs = 50; // justera 10–100 ms efter smak
        private readonly object _repriceGate = new object();
        private Timer _repriceTimer;           // System.Threading.Timer
        private volatile bool _repricePending; // det har kommit events under väntetiden
        private volatile bool _repriceRunning; // en reprice kör just nu

        private volatile bool _pricingInFlight; // Vakt mot dubbelprisning: true under pågående priscall från presentern.
        private DateTime _lastPriceFinishedUtc = DateTime.MinValue; //Tidpunkt då senaste priscall avslutades (för cooldown).
        private static readonly TimeSpan _rateWriteCooldown = TimeSpan.FromMilliseconds(200); // Kort buffert där vi ignorerar FeedRd/Rf efter nyss avslutad priscall. Hindrar “egen-skrivning → omedelbar dubbelprisning”.

        #endregion

        #region Constructor / Dispose

        /// <summary>
        /// Skapar presenter och ansluter UI- och bus-händelser.
        /// Om <paramref name="spotFeed"/> inte tillhandahålls används <see cref="BloombergSpotFeed"/> med timeout 3000 ms.
        /// </summary>
        public LegacyPricerPresenter(IMessageBus bus, LegacyPricerView view, IMarketStore marketStore, ISpotSetDateService spotSvc)
        {
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));
            _view = view ?? throw new ArgumentNullException(nameof(view));

            _spotSvc = spotSvc ?? throw new ArgumentNullException(nameof(spotSvc));

            // === Ben: se till att vi startar med 1 stabilt ben ("Vanilla 1") ===
            if (_legStates.Count == 0)
            {
                var ls = new LegState(Guid.NewGuid(), "Vanilla 1");
                _legStates.Add(ls);
                _view.BindLegIdToLabel(ls.LegId, ls.Label); // håller UI-label i sync
                Debug.WriteLine($"[Presenter.AddNewLeg] Added leg Vanilla {_legStates.Count} ({ls.LegId})");
            }

            // Spot-feed kör vi lokalt (ingen DI för den i detta steg)
            _spotFeed = new BloombergSpotFeed(_spotTimeoutMs);

            // Viktigt: använd den injicerade, delade MarketStore-instansen
            _mktStore = marketStore ?? throw new ArgumentNullException(nameof(marketStore));
            _mktStore.Changed += OnMarketChanged;

            // UI →
            _view.PriceRequested += OnPriceRequested;
            _view.NotionalChanged += (_, __) => RepriceAllLegs();
            _view.SpotEdited += OnSpotEditedFromView;
            _view.ExpiryEditRequested += OnExpiryEditRequested;
            _view.SpotModeChanged += OnSpotModeChanged;
            _view.AddLegRequested += OnAddLegRequested; // Lägg till ben (F6)
            _view.RatesRefreshRequested += OnRatesRefreshRequested; //Refresh rates (F7)
            _view.RateEdited += OnRateEdited;
            _view.SpotRefreshRequested += (_, __) => System.Threading.Tasks.Task.Run(() => RefreshSpotSnapshot());

            _view.RateClearOverrideRequested += OnRateClearOverrideRequested;

            // Initialt ViewMode till Store
            {
                var p6 = NormalizePair6(_view.ReadPair6());
                if (p6 != null)
                {
                    var initialVm = _view.IsSpotModeMid() ? ViewMode.Mid : ViewMode.TwoWay;
                    _mktStore.SetSpotViewMode(p6, initialVm, DateTime.UtcNow);
                }
            }

            // BUS →
            _subPrice = _bus.Subscribe<PriceCalculated>(OnPriceCalculated);
            _subError = _bus.Subscribe<ErrorOccurred>(OnError);

            var ctrl = _view as System.Windows.Forms.Control;
            if (ctrl != null)
            {
                ctrl.HandleCreated += (s, e) =>
                {
                    ctrl.BeginInvoke((Action)(() =>
                    {
                        TrySeedDefaultExpiryAndReprice();           // snabbt – mest UI-läsning
                        System.Threading.Tasks.Task.Run(() => RefreshSpotSnapshot()); // tungt – kör bakgrunden
                    }));
                };
            }
        }

        /// <summary>
        /// Frigör bus-prenumerationer.
        /// </summary>
        public void Dispose()
        {
            _subPrice?.Dispose();
            _subError?.Dispose();

            if (_mktStore != null)
                _mktStore.Changed -= OnMarketChanged;

            (_spotFeed as IDisposable)?.Dispose();
        }

        #endregion

        #region Expiry resolve

        /// <summary>
        /// Hanterar användarens editering av expiry (Deal eller specifikt ben).
        /// - Deal: resolve + apply för samtliga ben, invaliddera RD/RF för varje ben och prissätt alla.
        /// - Ben: resolve + apply för just det benet, invaliddera RD/RF och prissätt bara det.
        /// </summary>
        private void OnExpiryEditRequested(object sender, LegacyPricerView.ExpiryEditRequestedEventArgs e)
        {
            var pair6 = (e.Pair6 ?? _view.ReadPair6() ?? "EURSEK").Replace("/", "").ToUpperInvariant();
            var holidays = LoadHolidaysForPair(pair6);

            // === Deal → alla ben ===
            if (string.Equals(e.LegColumn, "Deal", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var ls in _legStates)
                {
                    try
                    {
                        var r = ExpiryInputResolver.Resolve(e.Raw, pair6, holidays);
                        var wdEn = r.ExpiryDate.ToString("ddd", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
                        var rawHint = string.Equals(r.Mode, "Tenor", StringComparison.OrdinalIgnoreCase)
                                      ? r.Normalized?.ToUpperInvariant()
                                      : null;

                        _view.ShowResolvedExpiryById(ls.LegId, r.ExpiryIso, wdEn, rawHint);
                        _view.ShowResolvedSettlementById(ls.LegId, r.SettlementIso);

                        // Invalidera RD/RF för benet → cache-only re-derive vid prisning
                        _mktStore.InvalidateRatesForLeg(pair6, ls.LegId.ToString(), DateTime.UtcNow);

                        PriceSingleLeg(ls.LegId);
                    }
                    catch (Exception ex)
                    {
                        _bus.Publish(new ErrorOccurred
                        {
                            Source = "ExpiryResolver",
                            Message = ex.Message,
                            Detail = ex.ToString(),
                            CorrelationId = Guid.Empty
                        });
                    }
                }
                return;
            }

            // === Ben → hitta rätt leg via kolumnnamn ===
            LegState target = null;
            for (int i = 0; i < _legStates.Count; i++)
            {
                var ls = _legStates[i];
                if (string.Equals(ls.Label, e.LegColumn, StringComparison.OrdinalIgnoreCase))
                {
                    target = ls;
                    break;
                }
            }
            if (target == null)
            {
                System.Diagnostics.Debug.WriteLine($"[Presenter.Expiry] Okänd legkolumn '{e.LegColumn}', avbryter.");
                return;
            }

            try
            {
                var res = ExpiryInputResolver.Resolve(e.Raw, pair6, holidays);
                var wd = res.ExpiryDate.ToString("ddd", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
                var hint = string.Equals(res.Mode, "Tenor", StringComparison.OrdinalIgnoreCase)
                           ? res.Normalized?.ToUpperInvariant()
                           : null;

                _view.ShowResolvedExpiryById(target.LegId, res.ExpiryIso, wd, hint);
                _view.ShowResolvedSettlementById(target.LegId, res.SettlementIso);

                // Invalidera RD/RF för det här benet → cache-only re-derive vid prisning
                _mktStore.InvalidateRatesForLeg(pair6, target.LegId.ToString(), DateTime.UtcNow);

                PriceSingleLeg(target.LegId);
            }
            catch (Exception ex)
            {
                _bus.Publish(new ErrorOccurred
                {
                    Source = "ExpiryResolver",
                    Message = ex.Message,
                    Detail = ex.ToString(),
                    CorrelationId = Guid.Empty
                });
            }
        }

        /// <summary>
        /// Laddar helgdagstabell för givna kalenderkoder kopplade till valutaparet (pair6).
        /// Returnerar tom <see cref="DataTable"/> om anslutning eller kalenderlista saknas.
        /// </summary>
        private static DataTable LoadHolidaysForPair(string pair6)
        {
            var cals = CurrencyCalendarMapper.GetCalendarsForPair(pair6) ?? Array.Empty<string>();
            var conn = Environment.GetEnvironmentVariable("AHS_SQL_CONN");
            if (string.IsNullOrWhiteSpace(conn) || cals.Length == 0)
                return new DataTable();

            var hc = new HolidayCalendar(conn);
            var today = DateTime.Today;
            return hc.GetHolidays(cals, today.AddYears(-1), today.AddYears(3));
        }

        #endregion

        #region Pricing entry points

        /// <summary>
        /// UI-triggad prisning. Om TargetLeg saknas → prisa alla, annars det specifika legacy-benet.
        /// (När vyn senare bär Guid LegId byter vi till den varianten.)
        /// </summary>
        private void OnPriceRequested(object sender, LegacyPricerView.PriceRequestUiArgs ui)
        {
            if (ui == null || ui.LegId == Guid.Empty)
            {
                RepriceAllLegs();
                return;
            }

            PriceSingleLeg(ui.LegId);   // prisa via stabil LegId
        }


        /// <summary>
        /// Prissätter ett ben via stabil identitet (LegId).
        /// Bygger RequestPrice från UI-snapshot och skickar över bus.
        /// Om <paramref name="forceRefreshRates"/> är true:
        ///   - Skippar att skriva UI-räntor till Store (vi vill inte reapplicera ev. gamla overrides)
        ///   - Runtime får ForceRefreshRates=true och hämtar färska RD/RF innan läsning.
        /// </summary>
        private void PriceSingleLeg(Guid legId, bool forceRefreshRates)
        {
            string reason;
            if (!CanPriceNow(out reason))
            {
                System.Diagnostics.Debug.WriteLine("[Presenter] Skip PriceSingleLeg: " + reason);
                return;
            }

            // Viktigt: vid force refresh ska vi INTE skriva UI->Store (annars riskerar vi re-override).
            if (!forceRefreshRates)
            {
                ApplyUiRatesToStore(legId);
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[Presenter.Rates->Store] Skip UI->Store write (ForceRefreshRates=true).");
            }

            var snap = _view.GetLegSnapshotById(legId);
            if (snap == null)
            {
                _bus.Publish(new ErrorOccurred
                {
                    Source = "Presenter",
                    Message = $"UI-snapshot saknas för ben {legId}.",
                    Detail = "",
                    CorrelationId = Guid.Empty
                });
                return;
            }

            // --- Resolve expiry (best effort) ---
            var iso = _view.TryGetResolvedExpiryIsoById(legId);
            if (string.IsNullOrWhiteSpace(iso) && !string.IsNullOrWhiteSpace(snap.ExpiryRaw))
            {
                try
                {
                    var pair6x = (snap.Pair6 ?? "EURSEK").Replace("/", "").ToUpperInvariant();
                    var holidays = LoadHolidaysForPair(pair6x);
                    var r = ExpiryInputResolver.Resolve(snap.ExpiryRaw, pair6x, holidays);
                    var wd = r.ExpiryDate.ToString("ddd", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
                    var hint = string.Equals(r.Mode, "Tenor", StringComparison.OrdinalIgnoreCase)
                                ? r.Normalized?.ToUpperInvariant()
                                : null;

                    _view.ShowResolvedExpiryById(legId, r.ExpiryIso, wd, hint);
                    _view.ShowResolvedSettlementById(legId, r.SettlementIso);
                    iso = r.ExpiryIso;
                }
                catch { /* best effort */ }
            }

            // --- Spot enligt store (oförändrat) ---
            var storeSnap = _mktStore.Current;
            var pair6 = NormalizePair6(snap.Pair6) ?? NormalizePair6(storeSnap?.Pair6) ?? NormalizePair6(_view.ReadPair6());
            if (pair6 == null)
            {
                System.Diagnostics.Debug.WriteLine("[Presenter] Skip pricing: pair6 saknas/ogiltigt.");
                return;
            }

            double sb = 0.0, sa = 0.0;
            if (storeSnap != null && string.Equals(storeSnap.Pair6, pair6, StringComparison.OrdinalIgnoreCase))
            {
                var field = storeSnap.Spot;
                var tw = field.Effective;
                if (field.ViewMode == ViewMode.Mid || field.Override == OverrideMode.Mid)
                {
                    var mid = 0.5 * (tw.Bid + tw.Ask);
                    sb = mid; sa = mid;
                }
                else
                {
                    sb = tw.Bid; sa = tw.Ask;
                }
            }
            else
            {
                var mid = snap.SpotMid > 0.0 ? snap.SpotMid : 0.0;
                sb = mid; sa = mid;
            }

            int dp = 4;
            try { dp = _view.GetSpotUiDecimals(); } catch { dp = 4; }
            sb = Math.Round(sb, dp, MidpointRounding.AwayFromZero);
            sa = Math.Round(sa, dp, MidpointRounding.AwayFromZero);

            // Vol i procent
            double? volBidPct = (snap.VolBid > 0.0) ? (double?)(snap.VolBid * 100.0) : null;
            double? volAskPct = (snap.VolHasTwoWay && snap.VolAsk > 0.0) ? (double?)(snap.VolAsk * 100.0) : null;

            var corr = Guid.NewGuid();
            _corrToLegId[corr] = legId;

            var cmd = new FX.Messages.Commands.RequestPrice
            {
                CorrelationId = corr,
                Pair6 = pair6,
                SpotBidOverride = sb,
                SpotAskOverride = sa,
                RdOverride = snap.Rd,   // ignoreras i runtime; Store används
                RfOverride = snap.Rf,   // ignoreras i runtime; Store används
                SurfaceId = "default",
                StickyDelta = false,
                VolBidPct = volBidPct,
                VolAskPct = volBidPct.HasValue && volAskPct.HasValue ? volAskPct : null,
                ForceRefreshRates = forceRefreshRates,
                Legs = new System.Collections.Generic.List<FX.Messages.Commands.RequestPrice.Leg>
        {
            new FX.Messages.Commands.RequestPrice.Leg
            {
                LegId    = legId,
                Side     = snap.Side,
                Type     = snap.Type,
                Strike   = snap.Strike,
                ExpiryIso= string.IsNullOrWhiteSpace(iso) ? "2030-12-31" : iso,
                Notional = snap.Notional
            }
        }
            };

            // Vakt mot dubbelprisning under pågående call
            _pricingInFlight = true;
            _bus.Publish(cmd);
        }




        /// <summary>
        /// Bekvämlighetsöverlagring: standard = ingen force refresh.
        /// </summary>
        private void PriceSingleLeg(Guid legId)
        {
            PriceSingleLeg(legId, false);
        }

        /// <summary>Prissätter samtliga ben via stabil identitet (LegId).</summary>
        private void RepriceAllLegs()
        {
            foreach (var leg in _legStates)
                PriceSingleLeg(leg.LegId);
        }

        #endregion

        #region Bus callbacks

        /// <summary>
        /// Tar emot prissättningsresultat och uppdaterar rätt ben via LegId.
        /// </summary>
        private void OnPriceCalculated(FX.Messages.Events.PriceCalculated e)
        {
            if (!_corrToLegId.TryGetValue(e.CorrelationId, out var legId))
                return;

            _corrToLegId.Remove(e.CorrelationId);

            // UI-uppdateringar MÅSTE gå via UI-tråden nu
            OnUi(() =>
            {
                _view.ShowTwoWayPremiumFromPerUnitById(legId, e.PremiumBid, e.PremiumAsk);
                _view.ShowLegResultById(legId, e.PremiumMid, e.Delta, e.Vega, e.Gamma, e.Theta);
                _pricingInFlight = false;
                _lastPriceFinishedUtc = DateTime.UtcNow;
            });
        }

        /// <summary>
        /// Tar emot fel från bus och loggar/visar basinformation.
        /// </summary>
        /// <summary>Tar emot fel från bus och loggar/visar basinformation.</summary>
        private void OnError(FX.Messages.Events.ErrorOccurred e)
        {
            _corrToLegId.Remove(e.CorrelationId);   // ändrat namn
            Debug.WriteLine($"[ERR] {e.Source}: {e.Message}");
        }

        #endregion

        #region Seed helpers

        /// <summary>
        /// Försöker sätta standardexpiry (1m) på alla ben (via _legacyColumns) och triggar prissättning.
        /// Tyst fail – användaren kan ange expiry manuellt om resolve misslyckas.
        /// </summary>
        private void TrySeedDefaultExpiryAndReprice()
        {
            try
            {
                string pair6 = (_view.ReadPair6() ?? "EURSEK").Replace("/", "").ToUpperInvariant();
                var holidays = LoadHolidaysForPair(pair6);

                if (_legStates.Count == 0) return;

                foreach (var ls in _legStates)
                {
                    var r = ExpiryInputResolver.Resolve("1m", pair6, holidays);
                    var wd = r.ExpiryDate.ToString("ddd", CultureInfo.GetCultureInfo("en-US"));
                    var hint = string.Equals(r.Mode, "Tenor", StringComparison.OrdinalIgnoreCase)
                                ? r.Normalized?.ToUpperInvariant()
                                : null;

                    _view.ShowResolvedExpiryById(ls.LegId, r.ExpiryIso, wd, hint);
                    _view.ShowResolvedSettlementById(ls.LegId, r.SettlementIso);

                    PriceSingleLeg(ls.LegId);
                }
            }
            catch
            {
                // Tyst – användaren kan skriva in expiry manuellt om resolve misslyckas.
            }
        }



        #endregion

        #region Spot feed

        /// <summary>
        /// Hämtar spot (TryGetTwoWay) från feed och skriver den till MarketStore.
        /// Nytt: vid refresh (F5) låser vi först upp ev. User-override på Spot,
        /// så att feeden faktiskt får slå igenom till Store och därmed UI.
        /// UI uppdateras därefter via OnMarketChanged (store-driven).
        /// </summary>
        private void RefreshSpotSnapshot()
        {
            try
            {
                var p6 = NormalizePair6(_view.ReadPair6());
                if (p6 == null) return;

                // Din feed-signatur: bool TryGetTwoWay(string pair6, out double bid, out double ask)
                double bid, ask;
                var ok = _spotFeed.TryGetTwoWay(p6, out bid, out ask);
                if (!ok) return;

                System.Diagnostics.Debug.WriteLine(
                    $"[RefreshSpotSnapshot][T{System.Threading.Thread.CurrentThread.ManagedThreadId}] {p6} FEED raw {bid:F6}/{ask:F6}");

                // Nytt: lås upp ev. user-override så att feeden inte ignoreras.
                var now = DateTime.UtcNow;
                _mktStore.SetSpotOverride(p6, FX.Core.Domain.MarketData.OverrideMode.None, now);

                // Skriv feed-värdet till Store (respekterar nu Override=None).
                _mktStore.SetSpotFromFeed(p6, new FX.Core.Domain.MarketData.TwoWay<double>(bid, ask), now, isStale: false);

                // Viktigt: vi ritar inte UI här; OnMarketChanged (store → view) gör det redan.
                // (SetSpotFromFeed höjer Changed("FeedSpot") vilket presenterns OnMarketChanged snappar upp.)
            }
            catch (Exception ex)
            {
                _bus.Publish(new ErrorOccurred
                {
                    Source = "Presenter.RefreshSpotSnapshot",
                    Message = ex.Message,
                    Detail = ex.ToString(),
                    CorrelationId = Guid.Empty
                });
            }
        }




        #endregion

        #region Events

        /// <summary>
        /// View ber oss nolla override för ett specifikt ben och fält (RD/RF) efter Delete.
        /// Vi mappar kolumn-label → LegId via _legStates, nollar override i Store och triggar
        /// en debouncad prisning via ScheduleRepriceDebounced().
        /// </summary>
        private void OnRateClearOverrideRequested(object sender, LegacyPricerView.RateClearOverrideRequestedEventArgs e)
        {
            try
            {
                if (e == null || string.IsNullOrWhiteSpace(e.LegColumn) || string.IsNullOrWhiteSpace(e.Field)) return;

                // Hitta leg via kolumnnamn (samma princip som i OnRateEdited)
                var ls = _legStates.Find(s => string.Equals(s.Label, e.LegColumn, StringComparison.OrdinalIgnoreCase));
                if (ls == null) return;

                var legIdStr = ls.LegId.ToString();
                var pair6 = NormalizePair6(_view.ReadPair6()) ?? _mktStore.Current?.Pair6 ?? "EURSEK";
                var nowUtc = DateTime.UtcNow;

                if (string.Equals(e.Field, "Rd", StringComparison.OrdinalIgnoreCase))
                    _mktStore.SetRdOverride(pair6, legIdStr, OverrideMode.None, nowUtc);
                else if (string.Equals(e.Field, "Rf", StringComparison.OrdinalIgnoreCase))
                    _mktStore.SetRfOverride(pair6, legIdStr, OverrideMode.None, nowUtc);
                else
                    return;

                // En debouncad reprice räcker
                ScheduleRepriceDebounced();
            }
            catch (Exception ex)
            {
                _bus.Publish(new ErrorOccurred
                {
                    Source = "Presenter.OnRateClearOverrideRequested",
                    Message = ex.Message,
                    Detail = ex.ToString(),
                    CorrelationId = Guid.Empty
                });
            }
        }




        /// <summary>
        /// UI → MarketStore: en specifik ränta (RD eller RF) har editerats för ett visst ben.
        /// Skriv ENDAST det fältet till Store som User-override. Andra fält lämnas orörda.
        /// </summary>
        private void OnRateEdited(object sender, RateEditedEventArgs e)
        {
            try
            {
                if (e == null) return;

                // 1) Mappa kolumn → LegId (Presentern har _legStates med Label)
                var ls = _legStates.Find(s => string.Equals(s.Label, e.LegColumn, StringComparison.OrdinalIgnoreCase));
                if (ls == null) return;

                var legId = ls.LegId.ToString();
                var pair6 = NormalizePair6(_view.ReadPair6());
                var now = DateTime.UtcNow;

                // 2) Bygg TwoWay av UI (single → mid)
                var tw = new TwoWay<double>(e.Mid, e.Mid);

                // 3) Skriv ENDAST det fält som editerats
                if (string.Equals(e.Field, "Rd", StringComparison.OrdinalIgnoreCase))
                {
                    _mktStore.SetRdFromUser(pair6, legId, tw, wasMid: e.WasMid, viewMode: ViewMode.TwoWay, nowUtc: now);
                    System.Diagnostics.Debug.WriteLine(
                        $"[Presenter.Rates->Store] pair={pair6} leg={ls.LegId} RD={e.Mid:F6} src=User vm=TwoWay ov={(e.WasMid ? "Mid" : "Both")}");
                }
                else if (string.Equals(e.Field, "Rf", StringComparison.OrdinalIgnoreCase))
                {
                    _mktStore.SetRfFromUser(pair6, legId, tw, wasMid: e.WasMid, viewMode: ViewMode.TwoWay, nowUtc: now);
                    System.Diagnostics.Debug.WriteLine(
                        $"[Presenter.Rates->Store] pair={pair6} leg={ls.LegId} RF={e.Mid:F6} src=User vm=TwoWay ov={(e.WasMid ? "Mid" : "Both")}");
                }

                // 4) Forward påverkas av rates → uppdatera just detta ben
                PushForwardUiForLeg(ls.LegId);

                // 5) Reprice debounced
                ScheduleRepriceDebounced();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ERR] Presenter.OnRateEdited: " + ex.Message);
            }
        }


        /// <summary>
        /// Kör en UI-uppdatering säkert: BeginInvoke om handle finns, annars väntar på HandleCreated och kör där.
        /// Skyddar mot "Invoke/BeginInvoke before handle created".
        /// </summary>
        private void OnUi(Action action)
        {
            var ctrl = _view as System.Windows.Forms.Control;
            if (ctrl == null || ctrl.IsDisposed)
            {
                // Fallback (om view inte är ett Control): kör direkt
                action();
                return;
            }

            if (ctrl.IsHandleCreated)
            {
                ctrl.BeginInvoke(action);
                return;
            }

            // Vänta tills handle skapats – kör sedan en gång
            System.EventHandler handler = null;
            handler = (s, e2) =>
            {
                ctrl.HandleCreated -= handler;
                if (!ctrl.IsDisposed)
                    ctrl.BeginInvoke(action);
            };
            ctrl.HandleCreated += handler;
        }

        /// <summary>
        /// Debounce för prisning: (re)startar en kort timer och markerar att reprice är pending.
        /// Flera snabba ändringar coalescas till en reprice-körning.
        /// </summary>
        private void ScheduleRepriceDebounced()
        {
            lock (_repriceGate)
            {
                _repricePending = true;

                if (_repriceTimer == null)
                {
                    _repriceTimer = new Timer(RepriceTimerCallback, null, RepriceDebounceMs, Timeout.Infinite);
                }
                else
                {
                    // Reset timer – börja om för att samla ihop fler ändringar
                    _repriceTimer.Change(RepriceDebounceMs, Timeout.Infinite);
                }
            }
        }

        /// <summary>
        /// Körs på timer-tråd. Startar EN reprice om ingen pågår. Annars lämnas _repricePending true,
        /// och vi schemalägger igen när pågående reprice avslutas (i finally-blocket).
        /// </summary>
        private void RepriceTimerCallback(object state)
        {
            bool shouldRun = false;

            lock (_repriceGate)
            {
                if (!_repriceRunning)
                {
                    _repriceRunning = true;
                    _repricePending = false; // vi tar ansvar för att köra denna batch
                    shouldRun = true;
                }
                // annars: låt _repricePending vara kvar = true; vi tar den efter pågående körning
            }

            if (!shouldRun) return;

            // Kör själva repricen på UI-tråden
            OnUi(() =>
            {
                try
                {
                    string reason;
                    if (CanPriceNow(out reason))
                        RepriceAllLegs();
                    else
                        Debug.WriteLine("[Presenter] Skip reprice: " + reason);
                }
                finally
                {
                    lock (_repriceGate)
                    {
                        _repriceRunning = false;

                        // Om det kom nya events under körningen – starta om debouncern
                        if (_repricePending && _repriceTimer != null)
                            _repriceTimer.Change(RepriceDebounceMs, Timeout.Infinite);
                    }
                }
            });
        }

        /// <summary>
        /// Reagerar på ändringar i MarketStore och uppdaterar UI + prissättning.
        /// Regler:
        /// - Spot-only (även Batch med enbart Spot>0): uppdatera BARA spot i UI + Forward (ej RD/RF) + debounce-reprice.
        /// - Rate-only (Feed/User/Rd/Rf/Override/ViewMode): uppdatera RD/RF i UI + Forward + debounce-reprice.
        /// - Övrigt: noop.
        ///
        /// - Hanterar "UserRd:<leg>" / "UserRf:<leg>" tidigt som rate-only (user-override):
        ///   * Läser effektiva RD/RF ur Store (user ligger i Effective),
        ///   * Pushar UI som override (utan att röra feed-baseline → lila stannar),
        ///   * Uppdaterar Forward för berört ben,
        ///   * Debounce-reprice och return.
        /// </summary>
        private void OnMarketChanged(object sender, MarketChangedEventArgs e)
        {
            try
            {
                var reason = e?.Reason ?? string.Empty;

                var snap = _mktStore.Current;
                var spotField = snap?.Spot;
                TwoWay<double>? eff = (spotField != null ? (TwoWay<double>?)spotField.Effective : null);

                // =========== [ADDED – STEP 1] User-override RD/RF (rate-only user) ===========
                {
                    bool isUserRd = reason.StartsWith("UserRd:", StringComparison.OrdinalIgnoreCase);
                    bool isUserRf = reason.StartsWith("UserRf:", StringComparison.OrdinalIgnoreCase);
                    if (isUserRd || isUserRf)
                    {
                        Guid gid;
                        if (TryParseLegGuidFromReason(reason, out gid) && gid != Guid.Empty)
                        {
                            // Läs effektiva RD/RF för benet (user ligger i Effective)
                            var rdFld = snap?.TryGetRd(gid.ToString());
                            var rfFld = snap?.TryGetRf(gid.ToString());

                            double? rdMid = null, rfMid = null;
                            bool staleRd = false, staleRf = false;

                            if (isUserRd && rdFld != null)
                            {
                                var re = rdFld.Effective;
                                rdMid = 0.5 * (re.Bid + re.Ask);
                                staleRd = rdFld.IsStale;
                            }
                            if (isUserRf && rfFld != null)
                            {
                                var fe = rfFld.Effective;
                                rfMid = 0.5 * (fe.Bid + fe.Ask);
                                staleRf = rfFld.IsStale;
                            }

                            System.Diagnostics.Debug.WriteLine(
                                $"[Presenter.UserRates][T{System.Threading.Thread.CurrentThread.ManagedThreadId}] reason={reason} leg={gid} rd={(rdMid.HasValue ? rdMid.Value.ToString("F6") : "-")} rf={(rfMid.HasValue ? rfMid.Value.ToString("F6") : "-")}");

                            // Visa override i UI (Guid) utan att röra baseline → lila stannar kvar
                            _view.ShowRatesOverrideById(gid, rdMid, rfMid, staleRd, staleRf);

                            // Forward påverkas av RD/RF → uppdatera för just detta ben
                            PushForwardUiForLeg(gid);

                            // Reprice debounced (rate-only user)
                            ScheduleRepriceDebounced();
                            return;
                        }
                    }
                }
                // ======= [END ADDED – STEP 1] =======

                // =========== 1) Direkta spot-reasons =============
                if (IsSpotReason(reason))
                {
                    if (eff.HasValue)
                    {
                        bool isUserSpot =
                            reason.StartsWith("UserSpot", StringComparison.OrdinalIgnoreCase) ||
                            (spotField != null && spotField.Source == FX.Core.Domain.MarketData.MarketSource.User);

                        if (isUserSpot) _view.ShowSpotUserFixed4(eff.Value.Bid, eff.Value.Ask);
                        else _view.ShowSpotFeedFixed4(eff.Value.Bid, eff.Value.Ask);
                    }

                    // Viktigt: RÖR INTE RD/RF-UI här – men uppdatera Forward (påverkas av spot).
                    PushForwardUiAllLegs();
                    ScheduleRepriceDebounced();
                    return;
                }

                // =========== 2) Batch: "Batch:Rd=...;Rf=...;Spot=...;Other=..." ===========
                if (reason.StartsWith("Batch:", StringComparison.OrdinalIgnoreCase))
                {
                    int rdCnt = 0, rfCnt = 0, spotCnt = 0;

                    // Robust, inline parsing av Batch-räknarna (utan externa helpers)
                    var after = reason.Substring(reason.IndexOf(':') + 1);
                    var parts = after.Split(new[] { ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var part in parts)
                    {
                        var kv = part.Split('=');
                        if (kv.Length != 2) continue;
                        var key = kv[0].Trim();
                        var valStr = kv[1].Trim();
                        if (!int.TryParse(valStr, out int val)) continue;

                        if (key.Equals("Rd", StringComparison.OrdinalIgnoreCase)) rdCnt = val;
                        else if (key.Equals("Rf", StringComparison.OrdinalIgnoreCase)) rfCnt = val;
                        else if (key.Equals("Spot", StringComparison.OrdinalIgnoreCase)) spotCnt = val;
                    }

                    // Spot-only i batch → behandla som spot-only
                    if (spotCnt > 0 && rdCnt == 0 && rfCnt == 0)
                    {
                        if (eff.HasValue)
                        {
                            if (spotField.Source == FX.Core.Domain.MarketData.MarketSource.User)
                                _view.ShowSpotUserFixed4(eff.Value.Bid, eff.Value.Ask);
                            else
                                _view.ShowSpotFeedFixed4(eff.Value.Bid, eff.Value.Ask);
                        }

                        // Endast Forward uppdateras här (RD/RF lämnas orörda)
                        PushForwardUiAllLegs();
                        ScheduleRepriceDebounced();
                        return;
                    }

                    // Någon rate ändrades → uppdatera RD/RF + forward
                    if (rdCnt > 0 || rfCnt > 0)
                    {
                        PushRatesUiAllLegs();
                        PushForwardUiAllLegs();

                        if (!_pricingInFlight &&
                            (System.DateTime.UtcNow - _lastPriceFinishedUtc) >= _rateWriteCooldown)
                        {
                            ScheduleRepriceDebounced();
                        }
                        return;
                    }

                    // Other-only → inget att göra
                    return;
                }

                // =========== 3) Rate-only (explicit reasons) ===========
                bool isRateOnly =
                    reason.StartsWith("FeedRd", StringComparison.OrdinalIgnoreCase) ||
                    reason.StartsWith("FeedRf", StringComparison.OrdinalIgnoreCase) ||
                    reason.StartsWith("UserRd", StringComparison.OrdinalIgnoreCase) ||   // täcks tidigt men kvar för tydlighet
                    reason.StartsWith("UserRf", StringComparison.OrdinalIgnoreCase) ||   // täcks tidigt men kvar för tydlighet
                    reason.StartsWith("RdViewMode", StringComparison.OrdinalIgnoreCase) ||
                    reason.StartsWith("RfViewMode", StringComparison.OrdinalIgnoreCase) ||
                    reason.StartsWith("RdOverride", StringComparison.OrdinalIgnoreCase) ||
                    reason.StartsWith("RfOverride", StringComparison.OrdinalIgnoreCase);

                if (isRateOnly)
                {
                    Guid gid;
                    if (TryParseLegGuidFromReason(reason, out gid) && gid != Guid.Empty)
                    {
                        PushRatesUiForLeg(gid);
                        PushForwardUiForLeg(gid);
                    }
                    else
                    {
                        PushRatesUiAllLegs();
                        PushForwardUiAllLegs();
                    }

                    if (_pricingInFlight) return;
                    if ((System.DateTime.UtcNow - _lastPriceFinishedUtc) < _rateWriteCooldown) return;

                    ScheduleRepriceDebounced();
                    return;
                }

                // =========== 4) Övrigt ===========
                System.Diagnostics.Debug.WriteLine("[OnMarketChanged] ignored: " + reason);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ERR] Presenter.OnMarketChanged: " + ex.Message);
            }
        }


        /// <summary>
        /// Parsar Batch-reason "Batch:Rd=1;Rf=1;Spot=0;Other=0 …" till fyra heltal.
        /// </summary>
        private static bool TryParseBatchCounts(string reason, out int rd, out int rf, out int spot, out int other)
        {
            rd = rf = spot = other = 0;
            try
            {
                int Read(string key)
                {
                    var idx = reason.IndexOf(key, StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) return 0;
                    idx += key.Length;
                    int end = reason.IndexOfAny(new[] { ';', ' ', '\t' }, idx);
                    string s = (end >= 0 ? reason.Substring(idx, end - idx) : reason.Substring(idx)).Trim();
                    int val; return int.TryParse(s, out val) ? val : 0;
                }
                rd = Read("Rd=");
                rf = Read("Rf=");
                spot = Read("Spot=");
                other = Read("Other=");
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Får normaliserad two-way Spot från vyn (Bid/Ask + WasMid) och skriver den som User till MarketStore.
        /// MarketStore kommer att trigga OnMarketChanged → RepriceAllLegs().
        /// </summary>
        private void OnSpotEditedFromView(object sender, LegacyPricerView.SpotEditedEventArgs e)
        {
            try
            {
                var vm = _view.IsSpotModeMid()
                    ? FX.Core.Domain.MarketData.ViewMode.Mid
                    : FX.Core.Domain.MarketData.ViewMode.TwoWay;

                _mktStore.SetSpotFromUser(
                    pair6: e.Pair6,
                    value: new FX.Core.Domain.MarketData.TwoWay<double>(e.Bid, e.Ask),
                    wasMid: e.WasMid,
                    viewMode: vm,
                    nowUtc: DateTime.UtcNow
                );

                // OBS: Vi behöver inte kalla RepriceAllLegs() här,
                // eftersom OnMarketChanged gör det när Store signalerar Changed.
                // Om du vill vara extra defensiv kan du avkommentera raden nedan:
                // RepriceAllLegs();
            }
            catch (Exception ex)
            {
                _bus.Publish(new ErrorOccurred
                {
                    Source = "Presenter.OnSpotEditedFromView",
                    Message = ex.Message,
                    Detail = ex.ToString(),
                    CorrelationId = Guid.Empty
                });
            }
        }

        /// <summary>
        /// Reagerar på att användaren växlar SPOT-läge i UI (Mid ⇆ Full).
        /// - Sätter ViewMode i MarketStore.
        /// - Vid växling till Full rensas ev. Mid-override så prisning sker på two-way.
        /// MarketStore triggar Changed → OnMarketChanged → RepriceAllLegs().
        /// </summary>
        private void OnSpotModeChanged(object sender, EventArgs e)
        {
            var p6 = NormalizePair6(_view.ReadPair6());
            if (p6 == null) return;

            var now = DateTime.UtcNow;

            if (_view.IsSpotModeMid())
            {
                // UI → Mid: visa/prisa mid
                _mktStore.SetSpotViewMode(p6, ViewMode.Mid, now);
                // override lämnas orörd här; den sätts till Mid när user matar mid-värde
            }
            else
            {
                // UI → Full: visa/prisa two-way
                _mktStore.SetSpotViewMode(p6, ViewMode.TwoWay, now);

                // Om Spot är låst i Mid (p.g.a. tidigare mid-input) – rensa låset
                var curr = _mktStore.Current?.Spot;
                if (curr != null && curr.Override == OverrideMode.Mid)
                    _mktStore.SetSpotOverride(p6, OverrideMode.None, now);
            }
        }

        /// <summary>
        /// UI-event: användaren vill lägga till ett nytt ben (t.ex. via F6).
        /// Skapar benet, binder kolumnen i vyn, sätter default expiry till "1M"
        /// och triggar EN debouncad priscykel för alla ben.
        /// </summary>
        private void OnAddLegRequested(object sender, EventArgs e)
        {
            var newLegId = AddNewLeg();
            if (newLegId == Guid.Empty) return;

            // Logg för spårbarhet
            Debug.WriteLine($"[Presenter.AddNewLeg] Added leg {_legStates[_legStates.Count - 1].Label} ({newLegId})");

            // Posta UI-init + default expiry + reprice i samma UI-vända
            OnUi(() =>
            {
                var leg = _legStates[_legStates.Count - 1];

                // Säkerställ att kolumnen finns och är bunden (din Notify-view gör det)
                NotifyViewLegAdded(leg.LegId, leg.Label);

                // Default expiry = "1M" (visas som [1M] yyyy-MM-dd (ddd) i UI)
                SeedDefaultExpiry1M(leg.LegId);

                // Kör en lugn, samlad prisning
                ScheduleRepriceDebounced();
            });
        }

        /// <summary>
        /// Sätter default expiry = "1M" för ett ben (by LegId).
        /// Resolv:ar mot aktuellt par & helgdagar och skriver till UI som:
        ///   [1M] yyyy-MM-dd (ddd)
        /// samt visar resolved settlement. Invaliderar RD/RF för benet så att
        /// nästa prisning re-deriverar från cache (ingen fresh).
        /// </summary>
        private void SeedDefaultExpiry1M(Guid legId)
        {
            try
            {
                var pair6 = (_view.ReadPair6() ?? "EURSEK").Replace("/", "").ToUpperInvariant();
                var holidays = LoadHolidaysForPair(pair6);

                var res = ExpiryInputResolver.Resolve("1M", pair6, holidays);

                var wd = res.ExpiryDate.ToString("ddd", System.Globalization.CultureInfo.GetCultureInfo("en-US"));

                _view.ShowResolvedExpiryById(legId, res.ExpiryIso, wd, "1M");
                _view.ShowResolvedSettlementById(legId, res.SettlementIso);

                // Viktigt: invaliddera RD/RF för detta ben → cache-only re-derive vid prisning
                _mktStore.InvalidateRatesForLeg(pair6, legId.ToString(), DateTime.UtcNow);

                System.Diagnostics.Debug.WriteLine(
                    $"[Presenter.SeedDefaultExpiry1M] leg={legId} → [1M] {res.ExpiryIso} ({wd}), settle={res.SettlementIso} (rates invalidated)");
            }
            catch (Exception ex)
            {
                _bus.Publish(new ErrorOccurred
                {
                    Source = "SeedDefaultExpiry1M",
                    Message = ex.Message,
                    Detail = ex.ToString(),
                    CorrelationId = Guid.Empty
                });
            }
        }

        /// <summary>
        /// UI: F7 – Forced rate refresh för alla ben.
        /// 1) Nolla RD/RF-override i Store för varje ben (så Effective inte blir kvar som User).
        /// 2) Ingen direkt UI-push här; siffran byts när feed kommer.
        /// 3) Prisa varje ben med ForceRefreshRates=true – runtime ensure:ar nya RD/RF innan läsning.
        /// </summary>
        private void OnRatesRefreshRequested(object sender, EventArgs e)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[Presenter.F7] Force refresh rates for all legs…");

                var pair6 = NormalizePair6(_view.ReadPair6()) ?? _mktStore.Current?.Pair6 ?? "EURSEK";
                var nowUtc = DateTime.UtcNow;

                // 1) Nolla overrides i Store
                for (int i = 0; i < _legStates.Count; i++)
                {
                    var legIdStr = _legStates[i].LegId.ToString();
                    _mktStore.SetRdOverride(pair6, legIdStr, OverrideMode.None, nowUtc);
                    _mktStore.SetRfOverride(pair6, legIdStr, OverrideMode.None, nowUtc);
                    System.Diagnostics.Debug.WriteLine($"[Presenter.F7] Cleared overrides (RD/RF) leg={legIdStr}");
                }

                // 2) Prisning med ForceRefreshRates=true → nya RD/RF hämtas
                for (int i = 0; i < _legStates.Count; i++)
                {
                    var legId = _legStates[i].LegId;
                    PriceSingleLeg(legId, /*forceRefreshRates:*/ true);
                }
            }
            catch (Exception ex)
            {
                _bus.Publish(new ErrorOccurred
                {
                    Source = "Presenter.OnRatesRefreshRequested",
                    Message = ex.Message,
                    Detail = ex.ToString(),
                    CorrelationId = Guid.Empty
                });
            }
        }






        #endregion

        #region Helpers

        /// <summary>
        /// Money-market year fraction (samma konvention som i feedern/runtime).
        /// Default = ACT/360. Vissa valutor (ex. GBP/AUD/NZD) = ACT/365.
        /// </summary>
        private static double YearFracMm(DateTime start, DateTime end, string ccy)
        {
            if (end <= start) return 0.0;
            var days = (end - start).TotalDays;

            switch ((ccy ?? "").ToUpperInvariant())
            {
                case "GBP":
                case "AUD":
                case "NZD":
                    return days / 365.0;
                default:
                    return days / 360.0;
            }
        }



        /// <summary>
        /// Returnerar true om vi har minsta uppsättning marknadsdata för att prisa.
        /// Idag: Spot (bid och ask eller mid). Senare: lägg till rd/rf/vol.
        /// </summary>
        private bool CanPriceNow(out string reason)
        {
            reason = null;

            var p6 = NormalizePair6(_mktStore.Current?.Pair6);
            if (p6 == null) { reason = "Valutapar saknas."; return false; }

            var s = _mktStore.Current?.Spot;
            if (s == null) { reason = "Spot saknas."; return false; }

            var tw = s.Effective;
            bool useMid = (s.Override == OverrideMode.Mid) || (s.ViewMode == ViewMode.Mid);
            if (useMid)
            {
                var mid = 0.5 * (tw.Bid + tw.Ask);
                if (mid <= 0.0) { reason = "Spot mid saknas."; return false; }
            }
            else
            {
                if (tw.Bid <= 0.0 || tw.Ask <= 0.0) { reason = "Spot bid/ask saknas."; return false; }
            }

            // TODO: när rd/rf/vol ligger i Store, lägg motsvarande kontroller här.
            return true;
        }


        /// <summary>
        /// Normaliserar valutapar till 6 tecken utan slash, upper (ex: "EUR/SEK" -> "EURSEK").
        /// Returnerar null om ogiltigt.
        /// </summary>
        private static string NormalizePair6(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var p = s.Replace("/", "").Trim().ToUpperInvariant();
            return p.Length == 6 ? p : null;
        }

        /// <summary>
        /// Returnerar true om reason avser spot-förändring (FeedSpot/UserSpot/SpotViewMode)
        /// eller om det är ett batch-reason med Spot>0 (Batch:…Spot=K…). Används för att uppdatera spot-UI selektivt.
        /// </summary>
        private static bool IsSpotReason(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return false;

            if (reason.StartsWith("FeedSpot", StringComparison.OrdinalIgnoreCase) ||
                reason.StartsWith("UserSpot", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reason, "SpotViewMode", StringComparison.OrdinalIgnoreCase))
                return true;

            // Känn igen batch med Spot>0
            if (reason.StartsWith("Batch:", StringComparison.OrdinalIgnoreCase))
            {
                // Enkel parsing: leta "Spot=" och kolla att det inte är 0
                var idx = reason.IndexOf("Spot=", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    // Försök läsa ut talet efter Spot=
                    var start = idx + 5;
                    int end = reason.IndexOfAny(new[] { ';', ' ' }, start);
                    var numTxt = (end >= 0 ? reason.Substring(start, end - start) : reason.Substring(start)).Trim();

                    int spotCount;
                    if (int.TryParse(numTxt, out spotCount))
                        return spotCount > 0;
                }
            }

            return false;
        }

        #endregion

        #region Legs with helpers

        // === Stabil benmodell i presentern ===
        private sealed class LegState
        {
            public Guid LegId { get; }
            public string Label { get; set; }
            public LegState(Guid legId, string label) { LegId = legId; Label = label ?? string.Empty; }
        }

        // === Minnessamling av ben (tom från start) ===
        private readonly List<LegState> _legStates = new List<LegState>();

        /// <summary>
        /// Returnerar existerande LegState för en etikett (t.ex. "Vanilla 1") eller
        /// skapar ett nytt med nytt LegId och registrerar det i _legStates.
        /// </summary>
        private LegState EnsureLegStateForLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
                return null;

            var existing = _legStates.Find(ls =>
                string.Equals(ls.Label, label, StringComparison.OrdinalIgnoreCase));

            if (existing != null) return existing;

            var created = new LegState(Guid.NewGuid(), label);
            _legStates.Add(created);
            return created;
        }

        /// <summary>
        /// Hittar ett ben via stabilt LegId (GUID). Returnerar null om det saknas.
        /// </summary>
        private LegState FindLegStateById(Guid legId)
        {
            if (legId == Guid.Empty) return null;
            return _legStates.Find(ls => ls.LegId == legId);
        }

        /// <summary>
        /// Hittar ett ben via nuvarande UI-etikett. Returnerar null om det saknas.
        /// </summary>
        private LegState FindLegStateByLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return null;
            return _legStates.Find(ls => string.Equals(ls.Label, label, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Försöker slå upp nuvarande visningsetikett för ett givet <paramref name="legId"/>.
        /// Returnerar <c>true</c> om benet fanns; annars <c>false</c>.
        /// </summary>
        private bool TryGetLabelByLegId(Guid legId, out string label)
        {
            label = null;
            var ls = FindLegStateById(legId);
            if (ls == null) return false;
            label = ls.Label;
            return true;
        }

        /// <summary>
        /// Uppdaterar/registrerar en etikett för ett givet <paramref name="legId"/> utan att
        /// ändra identiteten (används vid renummerering; stabilt id behålls).
        /// Skapar inget nytt ben om id saknas (returnerar <c>false</c> i så fall).
        /// </summary>
        private bool UpdateLabelForLeg(Guid legId, string newLabel)
        {
            var ls = FindLegStateById(legId);
            if (ls == null) return false;
            ls.Label = newLabel ?? string.Empty;
            return true;
        }

        /// <summary>
        /// Hittar första lediga UI-kolumn (leg) genom att jämföra vy:ns ben-kolumner
        /// med etiketter som redan används av våra LegState-objekt.
        /// Returnerar kolumnnamn (t.ex. "Vanilla 2") eller null om inga lediga finns.
        /// </summary>
        private string FindFirstFreeUiLegColumn()
        {
            var all = _view.GetLegColumns(); // från vyn
            var used = new HashSet<string>(_legStates.Select(ls => ls.Label),
                                           StringComparer.OrdinalIgnoreCase);

            foreach (var col in all)
                if (!used.Contains(col))
                    return col;

            return null;
        }

        /// <summary>
        /// Skapar ett nytt ben sist i listan med ett nytt stabilt <see cref="Guid"/> och
        /// etikett "Vanilla N". Säkerställer att UI-kolumnen finns via vyn och binder Id↔label.
        /// </summary>
        private Guid AddNewLeg()
        {
            var newId = Guid.NewGuid();
            var newLabel = $"Vanilla {_legStates.Count + 1}";

            // 1) Lägg till i presenter-state
            _legStates.Add(new LegState(newId, newLabel));

            // 2) Låt vyn säkra kolumnen + registrera Id→label-mappningen
            //    (BindLegIdToLabel skapar kolumn vid behov via EnsureLegColumnExists.)
            NotifyViewLegAdded(newId, newLabel);

            // 3) Debug-spår
            //System.Diagnostics.Debug.WriteLine($"[Presenter.AddNewLeg] Added leg {newLabel} ({newId})");

            return newId;
        }



        /// <summary>
        /// Klonar ett befintligt ben: skapar ett nytt ben direkt efter källbenet
        /// (med nytt <see cref="Guid"/>), ger det temporärt "Vanilla N",
        /// och renummererar sedan alla etiketter så att de blir 1..N i UI-ordning.
        /// Returnerar det nya LegId (eller Guid.Empty om källan saknas).
        /// </summary>
        private Guid CloneLeg(Guid sourceId)
        {
            var srcIndex = _legStates.FindIndex(x => x.LegId == sourceId);
            if (srcIndex < 0) return Guid.Empty;

            var newId = Guid.NewGuid();
            var tempLabel = $"Vanilla {_legStates.Count + 1}";

            _legStates.Insert(srcIndex + 1, new LegState(newId, tempLabel));

            // UI-hook (no-op tills du kopplar mot vyn)
            NotifyViewLegCloned(sourceId, newId, tempLabel);

            RenumberLabels();
            return newId;
        }

        /// <summary>
        /// Tar bort benet med angivet <paramref name="legId"/> om det finns,
        /// och renummererar kvarvarande etiketter så att de blir 1..N.
        /// Returnerar true om ett ben togs bort.
        /// </summary>
        private bool RemoveLeg(Guid legId)
        {
            var idx = _legStates.FindIndex(x => x.LegId == legId);
            if (idx < 0) return false;

            var removed = _legStates[idx];
            _legStates.RemoveAt(idx);

            // UI-hook (no-op tills du kopplar mot vyn)
            NotifyViewLegRemoved(removed.LegId);

            RenumberLabels();
            return true;
        }

        /// <summary>
        /// Sätter etiketter "Vanilla 1..N" i den ordning som <see cref="_legStates"/> ligger.
        /// Endast ändrade etiketter pushas till UI via <see cref="NotifyViewLabelChanged"/>.
        /// </summary>
        private void RenumberLabels()
        {
            for (int i = 0; i < _legStates.Count; i++)
            {
                var desired = $"Vanilla {i + 1}";
                if (!string.Equals(_legStates[i].Label, desired, StringComparison.Ordinal))
                {
                    _legStates[i].Label = desired;

                    // UI-hook (no-op tills du kopplar mot vyn)
                    NotifyViewLabelChanged(_legStates[i].LegId, desired);
                }
            }
        }

        /// <summary>
        /// Meddelar vyn att ett nytt ben lagts till så att UI-kolumn skapas/binds.
        /// Kolumnen seedas från föregående ben (om det finns) så att format/hints (t.ex. "[1M] …") följer med.
        /// </summary>
        private void NotifyViewLegAdded(Guid legId, string label)
        {
            OnUi(() =>
            {
                // Hitta seed-källa: föregående leg i listan (dvs. benet vi "kopierar")
                string seedFrom = null;
                if (_legStates.Count >= 2)
                {
                    // Nytt ben är sist; föregående är näst sist
                    seedFrom = _legStates[_legStates.Count - 2].Label;
                }

                // Använd nya överlagringen i vyn som seedar från seedFrom
                _view.BindLegIdToLabel(legId, label, seedFrom);
            });
        }


        /// <summary>Hook för att låta vyn klona UI-raden.</summary>
        private void NotifyViewLegCloned(Guid sourceId, Guid newId, string newLabel)
        {
            _view.BindLegIdToLabel(newId, newLabel);
            // Lägg ev. UI-klonlogik här senare.
        }

        /// <summary>Hook för att låta vyn ta bort UI-raden.</summary>
        private void NotifyViewLegRemoved(Guid legId)
        {
            // Om du vill kan du lägga till en “Unbind” i vyn senare.
        }

        /// <summary>Hook för att låta vyn uppdatera visningsetikett.</summary>
        private void NotifyViewLabelChanged(Guid legId, string newLabel)
        {
            _view.BindLegIdToLabel(legId, newLabel);
        }



        #endregion

        #region UI pushers

        /// <summary>
        /// UI-push för RD/RF för ett specifikt ben:
        /// - Om Store har aktiv USER-override på RD och/eller RF → visa partial override (lila) endast för de sidor som är override:ade.
        /// - Annars → visa feed-baseline (neutral), vilket även släcker lila i vyn (via ShowRatesById).
        /// - Mid definieras här som (Bid+Ask)/2 från Effective-fältet.
        /// - Stale-status från Store mappas till tooltip i vyn.
        /// </summary>
        private void PushRatesUiForLeg(Guid legId)
        {
            try
            {
                var snap = _mktStore.Current;
                if (snap == null) return;

                var legIdStr = legId.ToString();

                // Hämta RD/RF-fält ur Store
                var rdFld = snap.TryGetRd(legIdStr);
                var rfFld = snap.TryGetRf(legIdStr);

                // Effektiva mid-värden (User om override, annars Feed)
                double? rdMid = null, rfMid = null;
                bool staleRd = false, staleRf = false;

                if (rdFld != null)
                {
                    var e = rdFld.Effective;
                    rdMid = 0.5 * (e.Bid + e.Ask);
                    staleRd = rdFld.IsStale;
                }
                if (rfFld != null)
                {
                    var e = rfFld.Effective;
                    rfMid = 0.5 * (e.Bid + e.Ask);
                    staleRf = rfFld.IsStale;
                }

                // Override-aktiv? (User-låsning)
                bool rdOverrideActive = rdFld != null &&
                                        rdFld.Override != OverrideMode.None &&
                                        rdFld.Source == FX.Core.Domain.MarketData.MarketSource.User;

                bool rfOverrideActive = rfFld != null &&
                                        rfFld.Override != OverrideMode.None &&
                                        rfFld.Source == FX.Core.Domain.MarketData.MarketSource.User;

                System.Diagnostics.Debug.WriteLine(
                    $"[Presenter.UI.Rates] leg={legId} rd={(rdMid?.ToString("F6") ?? "-")} rf={(rfMid?.ToString("F6") ?? "-")} ovRd={rdOverrideActive} ovRf={rfOverrideActive}");

                // Respektera override i UI-push:
                // - Om override aktiv: visa override-värden (lila), rör inte baseline.
                // - Annars: visa feed-baseline (ingen lila).
                if (rdOverrideActive || rfOverrideActive)
                {
                    _view.ShowRatesOverrideById(
                        legId,
                        rdOverrideActive ? rdMid : (double?)null,
                        rfOverrideActive ? rfMid : (double?)null,
                        staleRd,
                        staleRf
                    );
                }
                else
                {
                    _view.ShowRatesById(
                        legId,
                        rdMid,
                        rfMid,
                        staleRd,
                        staleRf
                    );
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ERR] Presenter.PushRatesUiForLeg: " + ex.Message);
            }
        }

        /// <summary>
        /// Uppdaterar RD/RF i UI för alla ben. Respekterar override per ben (se <see cref="PushRatesUiForLeg(Guid)"/>).
        /// </summary>
        private void PushRatesUiAllLegs()
        {
            try
            {
                for (int i = 0; i < _legStates.Count; i++)
                    PushRatesUiForLeg(_legStates[i].LegId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ERR] Presenter.PushRatesUiAllLegs: " + ex.Message);
            }
        }

        #endregion


        /// <summary>
        /// Beräknar Forward Rate och Forward Points för ett specifikt ben (legId)
        /// utifrån aktuell spot + rd/rf i MarketStore och benets resolverade expiry i UI,
        /// och pushar till vyn (vyn skalar points ×1000 i presentation).
        /// </summary>
        private void PushForwardUiForLeg(Guid legId)
        {
            try
            {
                // 1) Pair + datum (expiry tas från vyns resolverade ISO)
                var pair6 = NormalizePair6(_view.ReadPair6()) ?? "EURSEK";
                var baseCcy = pair6.Substring(0, 3);
                var quoteCcy = pair6.Substring(3, 3);

                var today = DateTime.Today;
                var expIso = _view.TryGetResolvedExpiryIsoById(legId); // finns i din vy
                DateTime expiry = today;
                if (!string.IsNullOrWhiteSpace(expIso))
                    DateTime.TryParse(expIso, out expiry);

                // SpotDate & Settlement via samma tjänst som i runtime
                var dates = _spotSvc.Compute(pair6, today, expiry);
                var spotDate = dates.SpotDate;
                var settlement = dates.SettlementDate;

                // 2) Spot effektiv (respektera ViewMode/Override)
                var snap = _mktStore.Current;
                if (snap == null || !string.Equals(snap.Pair6, pair6, StringComparison.OrdinalIgnoreCase))
                {
                    _view.ShowForwardById(legId, null, null);
                    return;
                }

                var sf = snap.Spot;
                var se = sf.Effective; // TwoWay<double>
                if (se.Bid <= 0.0 && se.Ask <= 0.0)
                {
                    _view.ShowForwardById(legId, null, null);
                    return;
                }

                double spotBid, spotAsk;
                if (sf.ViewMode == ViewMode.Mid || sf.Override == OverrideMode.Mid)
                {
                    var mid = 0.5 * (se.Bid + se.Ask);
                    spotBid = mid; spotAsk = mid;
                }
                else
                {
                    spotBid = se.Bid; spotAsk = se.Ask;
                }
                double S = 0.5 * (spotBid + spotAsk);

                // 3) rd/rf effektivt för just detta ben (leg-GUID som nyckel)
                var key = legId.ToString();
                var rdFld = snap.TryGetRd(key);
                var rfFld = snap.TryGetRf(key);
                if (rdFld == null || rfFld == null)
                {
                    _view.ShowForwardById(legId, null, null);
                    return;
                }

                var rdMid = 0.5 * (rdFld.Effective.Bid + rdFld.Effective.Ask);
                var rfMid = 0.5 * (rfFld.Effective.Bid + rfFld.Effective.Ask);

                // 4) Year fractions enligt MM-konvention (samma som feeder)
                var Tq = YearFracMm(spotDate, settlement, quoteCcy);
                var Tb = YearFracMm(spotDate, settlement, baseCcy);
                if (Tq <= 0.0 && Tb <= 0.0)
                {
                    _view.ShowForwardById(legId, null, null);
                    return;
                }

                // 5) Forward & Points (MM-approx)
                var denom = (1.0 + rfMid * Tb);
                if (denom <= 0.0)
                {
                    _view.ShowForwardById(legId, null, null);
                    return;
                }

                var F = S * (1.0 + rdMid * Tq) / denom;
                var P = F - S;

                _view.ShowForwardById(legId, F, P);

                var ci = System.Globalization.CultureInfo.InvariantCulture;
                System.Diagnostics.Debug.WriteLine(
                    $"[Presenter.UI.Fwd][T{System.Threading.Thread.CurrentThread.ManagedThreadId}] leg={legId} pair={pair6} " +
                    $"S={S.ToString("F6", ci)} rd={rdMid.ToString("F6", ci)} rf={rfMid.ToString("F6", ci)} " +
                    $"spot={spotDate:yyyy-MM-dd} settle={settlement:yyyy-MM-dd} Tq={Tq.ToString("F6", ci)} Tb={Tb.ToString("F6", ci)} " +
                    $"F={F.ToString("F6", ci)} Pts={P.ToString("F6", ci)}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ERR] Presenter.PushForwardUiForLeg: " + ex.Message);
            }
        }



        /// <summary>
        /// Kör PushForwardUiForLeg för alla ben.
        /// </summary>
        private void PushForwardUiAllLegs()
        {
            for (int i = 0; i < _legStates.Count; i++)
                PushForwardUiForLeg(_legStates[i].LegId);
        }

        /// <summary>
        /// Returnerar (wasMid, viewMode) givet hur RD/RF matas in från UI.
        /// Idag matar vi in ett singeltal ⇒ tolka som Mid-låsning och TwoWay-visning.
        /// </summary>
        private (bool wasMid, ViewMode viewMode) InferRateInputModeFromUi()
        {
            // Enkelt läge: singeltal i UI ⇒ lås MID, visa TwoWay i gridden.
            return (wasMid: true, viewMode: ViewMode.TwoWay);
        }


        /// <summary>
        /// True om MarketStore-orsaken handlar om RD/RF (Feed/Override/ViewMode) och
        /// *inte* innehåller spot. Hanterar även batch där endast RD/RF uppdaterats.
        /// </summary>
        private static bool IsRateOnlyReason(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return false;

            // Direkta rate-signaler
            if (reason.StartsWith("FeedRd:", StringComparison.OrdinalIgnoreCase)) return true;
            if (reason.StartsWith("FeedRf:", StringComparison.OrdinalIgnoreCase)) return true;
            if (reason.StartsWith("RdOverride", StringComparison.OrdinalIgnoreCase)) return true;
            if (reason.StartsWith("RfOverride", StringComparison.OrdinalIgnoreCase)) return true;
            if (reason.StartsWith("RdViewMode", StringComparison.OrdinalIgnoreCase)) return true;
            if (reason.StartsWith("RfViewMode", StringComparison.OrdinalIgnoreCase)) return true;

            // Batch: "Batch:Rd=1;Rf=1;Spot=0;Other=0"
            if (reason.StartsWith("Batch:", StringComparison.OrdinalIgnoreCase))
            {
                var s = reason.ToUpperInvariant();
                var hasRd = s.Contains("RD=") && !s.Contains("RD=0");
                var hasRf = s.Contains("RF=") && !s.Contains("RF=0");
                var hasSpot = s.Contains("SPOT=") && !s.Contains("SPOT=0");
                return (hasRd || hasRf) && !hasSpot;
            }

            return false;
        }

        /// <summary>
        /// Försöker extrahera legId (Guid) ur en orsakstext med mönster "FeedRd:{guid}" eller "FeedRf:{guid}".
        /// Returnerar true vid lyckad parse.
        /// </summary>
        private static bool TryParseLegGuidFromReason(string reason, out Guid legId)
        {
            legId = Guid.Empty;
            if (string.IsNullOrEmpty(reason)) return false;

            string guidPart = null;

            if (reason.StartsWith("FeedRd:", StringComparison.OrdinalIgnoreCase))
                guidPart = reason.Substring("FeedRd:".Length).Trim();
            else if (reason.StartsWith("FeedRf:", StringComparison.OrdinalIgnoreCase))
                guidPart = reason.Substring("FeedRf:".Length).Trim();

            if (string.IsNullOrEmpty(guidPart)) return false;
            return Guid.TryParse(guidPart, out legId);
        }



        /// <summary>
        /// Läser UI-räntor (RD/RF) för benets kolumn och skriver User-override till MarketStore
        /// endast för de fält som faktiskt är i override (ovRd/ovRf). Skriver inte “det andra” fältet.
        /// Skippar skrivning om värdet redan matchar Store.Effective. Loggar vad som händer.
        /// Anropas före prisning för att säkra att Store speglar UI-override korrekt.
        /// </summary>
        private void ApplyUiRatesToStore(Guid legId)
        {
            // 1) hitta kolumn-namnet för detta legId via _legStates
            var state = _legStates.Find(s => s.LegId == legId);
            if (state == null || string.IsNullOrWhiteSpace(state.Label))
            {
                System.Diagnostics.Debug.WriteLine($"[Presenter.Rates->Store] leg={legId} saknar kolumn-label.");
                return;
            }
            string col = state.Label;

            // 2) läs RD/RF + override-status från vyn för den kolumnen
            double? uiRd, uiRf; bool ovRd, ovRf;
            if (!_view.TryReadRatesForColumn(col, out uiRd, out ovRd, out uiRf, out ovRf))
            {
                System.Diagnostics.Debug.WriteLine($"[Presenter.Rates->Store] TryReadRatesForColumn misslyckades för col={col} (leg={legId}).");
                return;
            }

            // 3) pair6 att skriva i store
            var pair6 = NormalizePair6(_view.ReadPair6());
            if (string.IsNullOrWhiteSpace(pair6)) pair6 = _mktStore.Current?.Pair6 ?? "EURSEK";

            var now = DateTime.UtcNow;
            var legKey = legId.ToString();

            // Hämta nuvarande fält i Store för jämförelse (undvik onödig writes)
            var snap = _mktStore.Current;
            var rdFld = snap?.TryGetRd(legKey);
            var rfFld = snap?.TryGetRf(legKey);

            // Hjälp: mittvärde ur ett TwoWay
            double Mid(TwoWay<double> tw) => 0.5 * (tw.Bid + tw.Ask);

            // 4) Skriv ENDAST fält som verkligen är override i UI (ov==true).
            // RD
            if (ovRd && uiRd.HasValue)
            {
                // Om Store redan har samma effective-mid för RD, skippa skrivning
                bool sameAsStore = false;
                if (rdFld != null)
                {
                    var eff = rdFld.Effective;
                    sameAsStore = Math.Abs(Mid(eff) - uiRd.Value) <= 1e-12
                                  && rdFld.Source == FX.Core.Domain.MarketData.MarketSource.User
                                  && rdFld.Override != OverrideMode.None;
                }

                if (!sameAsStore)
                {
                    _mktStore.SetRdFromUser(
                        pair6,
                        legKey,
                        new TwoWay<double>(uiRd.Value, uiRd.Value),
                        /*wasMid*/ true,
                        ViewMode.TwoWay,
                        now
                    );

                    System.Diagnostics.Debug.WriteLine(
                        $"[Presenter.Rates->Store] pair={pair6} leg={legId} RD={uiRd.Value:F6} src=User vm=TwoWay ov=Mid");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[Presenter.Rates->Store] RD SKIP (samma override i Store) leg={legId} mid={uiRd.Value:F6}");
                }
            }
            else
            {
                // Viktigt: skriv INTE RD om ovRd=false → lämna RD orörd (feed/ev. tidigare state).
                if (uiRd.HasValue)
                    System.Diagnostics.Debug.WriteLine($"[Presenter.Rates->Store] RD IGNORE (ovRd=false) leg={legId} ui={uiRd.Value:F6}");
            }

            // RF
            if (ovRf && uiRf.HasValue)
            {
                bool sameAsStore = false;
                if (rfFld != null)
                {
                    var eff = rfFld.Effective;
                    sameAsStore = Math.Abs(Mid(eff) - uiRf.Value) <= 1e-12
                                  && rfFld.Source == FX.Core.Domain.MarketData.MarketSource.User
                                  && rfFld.Override != OverrideMode.None;
                }

                if (!sameAsStore)
                {
                    _mktStore.SetRfFromUser(
                        pair6,
                        legKey,
                        new TwoWay<double>(uiRf.Value, uiRf.Value),
                        /*wasMid*/ true,
                        ViewMode.TwoWay,
                        now
                    );

                    System.Diagnostics.Debug.WriteLine(
                        $"[Presenter.Rates->Store] pair={pair6} leg={legId} RF={uiRf.Value:F6} src=User vm=TwoWay ov=Mid");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[Presenter.Rates->Store] RF SKIP (samma override i Store) leg={legId} mid={uiRf.Value:F6}");
                }
            }
            else
            {
                if (uiRf.HasValue)
                    System.Diagnostics.Debug.WriteLine($"[Presenter.Rates->Store] RF IGNORE (ovRf=false) leg={legId} ui={uiRf.Value:F6}");
            }
        }

    }
}
