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

        private bool _suppressUiRatesWriteOnce;

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
            _mktStore = marketStore ?? throw new ArgumentNullException(nameof(marketStore));

            // === Ben: se till att vi startar med 1 stabilt ben ("Vanilla 1") ===
            if (_legStates.Count == 0)
            {
                var ls = new LegState(Guid.NewGuid(), "Vanilla 1");
                _legStates.Add(ls);
                _view.BindLegIdToLabel(ls.LegId, ls.Label); // håller UI-label i sync
                //Debug.WriteLine($"[Presenter.AddNewLeg] Added leg Vanilla {_legStates.Count} ({ls.LegId})");
            }

            // Spot-feed kör vi lokalt (ingen DI för den i detta steg)
            _spotFeed = new BloombergSpotFeed(_spotTimeoutMs);

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

            _view.ForwardTargetEdited += OnForwardTargetEdited;

            // NYTT: reagera på Forward MID/FULL (ingen reload – endast ViewMode på RD/RF)
            _view.ForwardModeChanged += OnForwardModeChanged;

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
        /// - Deal: resolve + apply för samtliga ben, nolla RD/RF-override, invaliddera räntor,
        ///   avpinna forward-visning (Rate & Points) och uppdatera forward-celler omedelbart,
        ///   därefter enkel (debouncad) prisning per ben.
        /// - Enskilt ben: samma som ovan fast för det benet.
        /// 
        /// Nytt: Avpinnar aktiv PIN på Forward-celler (Rate/Points) vid expiry-byte så att gammal råtext
        /// inte ligger kvar i UI. Efter override-reset + invalidation pushas forward-visningen direkt så att
        /// UI omedelbart speglar aktuellt läge (utan att invänta nästa bus-prisning).
        /// </summary>
        void OnExpiryEditRequested(object sender, LegacyPricerView.ExpiryEditRequestedEventArgs e)
        {
            var pair6 = (e.Pair6 ?? _view.ReadPair6() ?? "EURSEK").Replace("/", "").ToUpperInvariant();
            var holidays = LoadHolidaysForPair(pair6);
            var nowUtc = DateTime.UtcNow;

            // === Deal → alla ben ===
            if (string.Equals(e.LegColumn, "Deal", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var ls in _legStates)
                {
                    try
                    {
                        var r = ExpiryInputResolver.Resolve(e.Raw, pair6, holidays);
                        var wdEn = r.ExpiryDate.ToString("ddd", CultureInfo.GetCultureInfo("en-US"));
                        var rawHint = string.Equals(r.Mode, "Tenor", StringComparison.OrdinalIgnoreCase)
                                      ? r.Normalized?.ToUpperInvariant()
                                      : null;

                        // Visa resolved expiry/settlement i UI
                        _view.ShowResolvedExpiryById(ls.LegId, r.ExpiryIso, wdEn, rawHint);
                        _view.ShowResolvedSettlementById(ls.LegId, r.SettlementIso);

                        // 1) Nolla ev. overrides för RD/RF på benet (återgå till baseline)
                        _mktStore.SetRdOverride(pair6, ls.LegId.ToString(), OverrideMode.None, nowUtc);
                        _mktStore.SetRfOverride(pair6, ls.LegId.ToString(), OverrideMode.None, nowUtc);

                        // 2) Invaliddera RD/RF → cache-only re-derive vid prisning (ingen force)
                        _mktStore.InvalidateRatesForLeg(pair6, ls.LegId.ToString(), nowUtc);

                        // 3) Avpinna forward-visning (Rate & Points) så att gammal råtext inte hänger kvar
                        _view.UnpinForwardDisplay(ls.LegId, null);

                        // 4) Uppdatera forward-celler direkt (bygger på resolved expiry i View + nuvarande Store)
                        //    så UI slutar visa gammalt pinned-värde omedelbart.
                        PushForwardUiForLeg(ls.LegId);

                        // 5) Skippa UI→Store i nästföljande prisanrop (engång)
                        _suppressUiRatesWriteOnce = true;

                        // 6) Prissätt benet (cache/TTL + ingen återinförd override)
                        PriceSingleLeg(ls.LegId, forceRefreshRates: false);
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
                Debug.WriteLine($"[Presenter.Expiry] Okänd legkolumn '{e.LegColumn}', avbryter.");
                return;
            }

            try
            {
                var res = ExpiryInputResolver.Resolve(e.Raw, pair6, holidays);
                var wd = res.ExpiryDate.ToString("ddd", CultureInfo.GetCultureInfo("en-US"));
                var hint = string.Equals(res.Mode, "Tenor", StringComparison.OrdinalIgnoreCase)
                           ? res.Normalized?.ToUpperInvariant()
                           : null;

                // Visa resolved expiry/settlement i UI
                _view.ShowResolvedExpiryById(target.LegId, res.ExpiryIso, wd, hint);
                _view.ShowResolvedSettlementById(target.LegId, res.SettlementIso);

                // 1) Nolla ev. overrides för RD/RF (återgå till baseline)
                _mktStore.SetRdOverride(pair6, target.LegId.ToString(), OverrideMode.None, nowUtc);
                _mktStore.SetRfOverride(pair6, target.LegId.ToString(), OverrideMode.None, nowUtc);

                // 2) Invaliddera RD/RF (ingen force)
                _mktStore.InvalidateRatesForLeg(pair6, target.LegId.ToString(), nowUtc);

                // 3) Avpinna Forward (Rate & Points) för detta ben
                _view.UnpinForwardDisplay(target.LegId, null);

                // 4) Omedelbar UI-push av forward-värden (utan att vänta på bus-prisning)
                PushForwardUiForLeg(target.LegId);

                // 5) Nästa prisning ska inte skriva UI-rates → Store (engång)
                _suppressUiRatesWriteOnce = true;

                // 6) Prissätt benet (cache/TTL)
                PriceSingleLeg(target.LegId, forceRefreshRates: false);
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
            var conn = Environment.GetEnvironmentVariable("AHS_SQL_CONN") ?? "Data Source=AHSKvant-prod-db;Initial Catalog=AHS;Integrated Security=True;Connect Timeout=15";
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
        /// NYTT: Om _suppressUiRatesWriteOnce är satt (t.ex. efter expiry-byte) så skippar vi
        ///       också UI->Store i just det här anropet och nollställer flaggan (ingen force).
        /// </summary>
        private void PriceSingleLeg(Guid legId, bool forceRefreshRates)
        {
            string reason;
            if (!CanPriceNow(out reason))
            {
                //System.Diagnostics.Debug.WriteLine("[Presenter] Skip PriceSingleLeg: " + reason);
                return;
            }

            var skipUiToStore = forceRefreshRates || _suppressUiRatesWriteOnce;

            // Viktigt: vid force refresh eller engångssuppress ska vi INTE skriva UI->Store
            if (!skipUiToStore)
            {
                ApplyUiRatesToStore(legId);
            }
            else
            {
                if (_suppressUiRatesWriteOnce)
                    _suppressUiRatesWriteOnce = false;
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

                //System.Diagnostics.Debug.WriteLine(
                //    $"[RefreshSpotSnapshot][T{System.Threading.Thread.CurrentThread.ManagedThreadId}] {p6} FEED raw {bid:F6}/{ask:F6}");

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
                    //else
                        //Debug.WriteLine("[Presenter] Skip reprice: " + reason);
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
        /// Detekterar om en MarketStore-reason signalerar ändrat Forward-läge,
        /// antingen som direkt "ForwardMode" eller i batchsträng "Batch:...;Forward=n;...".
        /// </summary>
        private static bool IsForwardReason(string reason)
        {
            if (string.IsNullOrEmpty(reason))
                return false;

            if (string.Equals(reason, "ForwardMode", StringComparison.OrdinalIgnoreCase))
                return true;

            if (reason.StartsWith("Batch:", StringComparison.OrdinalIgnoreCase))
            {
                // Sök Forward=NN i batchsträngen
                var key = "Forward=";
                var i = reason.IndexOf(key, StringComparison.OrdinalIgnoreCase);
                if (i >= 0)
                {
                    int s = i + key.Length;
                    int e = s;
                    while (e < reason.Length && char.IsDigit(reason[e])) e++;
                    var num = reason.Substring(s, e - s);
                    int val;
                    if (int.TryParse(num, out val) && val > 0)
                        return true;
                }
            }
            return false;
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

                // =========== ForwardMode toggled ===========
                if (IsForwardReason(reason))
                {
                    var snap2 = _mktStore.Current;
                    var p6snap = snap2?.Pair6 ?? _view.ReadPair6();
                    var p6 = NormalizePair6(p6snap);

                    if (!string.IsNullOrWhiteSpace(p6) && _legStates != null && _legStates.Count > 0)
                    {
                        var fpm = _mktStore.GetForwardPricingMode();
                        var vm = (fpm == FX.Core.Domain.MarketData.ForwardPricingMode.Mid)
                                 ? ViewMode.Mid
                                 : ViewMode.TwoWay; // Full/Net → TwoWay

                        var nowUtc = DateTime.UtcNow;

                        // Sätt endast om ändring krävs per ben → undvik loops/extra events
                        for (int i = 0; i < _legStates.Count; i++)
                        {
                            var legIdStr = _legStates[i].LegId.ToString();

                            var rdFld = snap2.TryGetRd(legIdStr);
                            if (rdFld != null && rdFld.ViewMode != vm)
                                _mktStore.SetRdViewMode(p6, legIdStr, vm, nowUtc);

                            var rfFld = snap2.TryGetRf(legIdStr);
                            if (rfFld != null && rfFld.ViewMode != vm)
                                _mktStore.SetRfViewMode(p6, legIdStr, vm, nowUtc);
                        }

                        // UI-push + reprice (debounced) — inuti UI-eventskydd för att undvika spök-override
                        OnUi(() =>
                        {
                            PushRatesUiAllLegs();
                            PushForwardUiAllLegs();
                        });
                        ScheduleRepriceDebounced();
                        return;
                    }
                }

                // =========== [User-override RD/RF (rate-only user)] ===========
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

                            // UI-push inom eventskydd → inga edit/commit-handlers
                            OnUi(() =>
                            {

                                // Visa override i UI (Guid) utan att röra baseline → lila stannar kvar
                                _view.ShowRatesOverrideById(gid, rdMid, rfMid, staleRd, staleRf);

                                // Forward påverkas av RD/RF → uppdatera för just detta ben
                                PushForwardUiForLeg(gid);

                            });

                            // Reprice debounced (rate-only user)
                            ScheduleRepriceDebounced();
                            return;
                        }
                    }
                }

                // =========== 1) Direkta spot-reasons ===========
                if (IsSpotReason(reason))
                {
                    // Skydda spot + forward UI-push inom eventskydd
                    OnUi(() =>
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

                    });

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
                        // Skydda spot + forward UI-push inom eventskydd
                        OnUi(() =>
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

                        });

                        ScheduleRepriceDebounced();
                        return;
                    }

                    // Någon rate ändrades → uppdatera RD/RF + forward
                    if (rdCnt > 0 || rfCnt > 0)
                    {
                        // Skydda programmatiska uppdateringar i rate-only-batch
                        OnUi(() =>
                        {
                            PushRatesUiAllLegs();
                            PushForwardUiAllLegs();
                        });

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
                    reason.StartsWith("UserRd", StringComparison.OrdinalIgnoreCase) ||
                    reason.StartsWith("UserRf", StringComparison.OrdinalIgnoreCase) ||
                    reason.StartsWith("RdViewMode", StringComparison.OrdinalIgnoreCase) ||
                    reason.StartsWith("RfViewMode", StringComparison.OrdinalIgnoreCase) ||
                    reason.StartsWith("RdOverride", StringComparison.OrdinalIgnoreCase) ||
                    reason.StartsWith("RfOverride", StringComparison.OrdinalIgnoreCase);

                if (isRateOnly)
                {
                    Guid gid;
                    if (TryParseLegGuidFromReason(reason, out gid) && gid != Guid.Empty)
                    {
                        // Skydda programmatiska uppdateringar (per ben)
                        OnUi(() =>
                        {
                            PushRatesUiForLeg(gid);
                            PushForwardUiForLeg(gid);

                        });
                    }
                    else
                    {
                        // Skydda programmatiska uppdateringar (alla ben)
                        OnUi(() =>
                        {
                            PushRatesUiAllLegs();
                            PushForwardUiAllLegs();

                        });
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
        /// Hanterar forced refresh (F7) av räntor för alla ben.
        /// Nytt: Återställer även alla manuella DP-preferenser för Forward-visningen i vyn
        /// så att Points återgår till 1 dp och Rate till minst 5 dp innan vi hämtar nya RD/RF.
        /// Därefter nollas RD/RF-overrides i Store och varje ben prissätts med forceRefreshRates=true.
        /// </summary>
        private void OnRatesRefreshRequested(object sender, EventArgs e)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[Presenter.F7] Force refresh rates for all legs…");

                // 0) Återställ DP-preferenser för Forward before refresh (Points=1 dp, Rate≥5 dp).
                //    Detta påverkar endast UI-visningen; Store/engine påverkas inte av detta anrop.
                _view?.ResetForwardDpToDefaults();

                var pair6 = NormalizePair6(_view.ReadPair6()) ?? _mktStore.Current?.Pair6 ?? "EURSEK";
                var nowUtc = DateTime.UtcNow;

                // 1) Nolla overrides i Store
                for (int i = 0; i < _legStates.Count; i++)
                {
                    var legIdStr = _legStates[i].LegId.ToString();
                    _mktStore.SetRdOverride(pair6, legIdStr, OverrideMode.None, nowUtc);
                    _mktStore.SetRfOverride(pair6, legIdStr, OverrideMode.None, nowUtc);
                    //System.Diagnostics.Debug.WriteLine($"[Presenter.F7] Cleared overrides (RD/RF) leg={legIdStr}");
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


        /// <summary>
        /// Hanterar byte av Forward-läge via UI-pill (0=Mid,1=Full,2=Net) utan att bryta user-input.
        /// - Sätter endast policyn i Store.
        /// - Visuell transform av pins (pair↔mid) i vyn.
        /// - Därefter re-solve:ar vi RD mot det transformerade målet (per ben) så att prisningen matchar visningen.
        /// - Debouncad reprice på slutet.
        /// </summary>
        void OnForwardModeChanged(object sender, EventArgs e)
        {
            try
            {
                var p6 = (_view.ReadPair6() ?? "EURSEK").Replace("/", "").ToUpperInvariant();

                int sel = _view.GetForwardMode(); // 0=Mid, 1=Full, 2=Net
                var fpm = (sel == 0) ? ForwardPricingMode.Mid
                         : (sel == 1) ? ForwardPricingMode.Full
                                      : ForwardPricingMode.Net;

                // Sätt policy i Store
                _mktStore.SetForwardPricingMode(p6, fpm, DateTime.UtcNow);

                // VISUELL transform av befintliga pins enligt 5d-C (bryter inte pin/override)
                _view.TransformPinnedForwardDisplayForMode(sel);

                // VIKTIGT: re-solve efter transform så RD/forward följer nya målet (ej gamla pin)
                ReSolvePinnedTargetsForMode(sel);

                // Debouncad reprice
                ScheduleRepriceDebounced();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Presenter.OnForwardModeChanged] " + ex.Message);
            }
        }




        /// <summary>
        /// Reagerar på editering av Forward-target (Points/Rate) från vyn – utan autoswitch.
        /// Lägget (FULL/MID/NET) är det som gäller; toggling hanteras separat.
        /// 
        /// Regler:
        /// • FULL/NET + single  → behandla som value/value (lös RD lika på båda sidor).
        /// • FULL/NET + pair    → lös RD per sida (bid/ask).
        /// • MID + single       → lös RD för mid (engine använder bid=ask=mid).
        /// • MID + pair         → ska inte nå hit (View rollbackar i 5b-1), men guard finns.
        /// 
        /// Endast solver och debouncad reprice. Inga mode-byten, inga unpins, ingen reset.
        /// </summary>
        void OnForwardTargetEdited(object sender, LegacyPricerView.ForwardTargetEditedEventArgs e)
        {
            if (e == null) return;

            // Läs aktuellt UI-läge: 0=Mid, 1=Full, 2=Net
            int sel = _view.GetForwardMode();
            var mode = (sel == 0) ? ForwardPricingMode.Mid
                     : (sel == 1) ? ForwardPricingMode.Full
                                  : ForwardPricingMode.Net;

            // Safety: i MID får par inte förekomma (View rollbackade redan i 5b-1)
            if (mode == ForwardPricingMode.Mid && e.IsPair)
            {
                return;
            }

            // Förhindra att nästa marknadsuppdatering skriver tillbaka UI→Store (engång)
            _suppressUiRatesWriteOnce = true;

            // Transformera input enligt gällande mode innan vi löser RD
            LegacyPricerView.ForwardTargetEditedEventArgs toSolve = e;

            if (mode == ForwardPricingMode.Mid)
            {
                // MID + single: lös mid (engine kör bid=ask=mid). Låt e vara som den är (single).
                // Inget att göra här.
            }
            else
            {
                // FULL eller NET
                if (!e.IsPair)
                {
                    // Single ska tolkas som value/value (båda sidor lika) i FULL/NET.
                    toSolve = new LegacyPricerView.ForwardTargetEditedEventArgs
                    {
                        LegId = e.LegId,
                        ColumnName = e.ColumnName,
                        TargetType = e.TargetType,
                        IsPair = true,
                        Bid = e.Single, // båda sidor lika
                        Ask = e.Single,
                        Single = null,
                        RawDisplay = e.RawDisplay,
                        Decimals = e.Decimals,
                        WasMidStyle = true // single ursprungligen
                    };
                }
                // FULL/NET + pair: använd e rakt av (lös per sida).
            }

            // Lös RD (Hold RF) enligt transformerad target
            SolveRdFromForwardTarget(toSolve);

            // Debouncad reprice
            ScheduleRepriceDebounced();
        }


        /// <summary>
        /// Bestämmer vilket ForwardPricingMode som ska gälla baserat på användarens forward-mål.
        /// Singelvärde ⇒ Mid; parvärde (bid/ask) ⇒ Full.
        /// </summary>
        private static ForwardPricingMode InferForwardModeForTarget(ForwardTargetEditedEventArgs e)
        {
            if (e == null) return ForwardPricingMode.Mid;
            return e.IsPair ? ForwardPricingMode.Full
                            : ForwardPricingMode.Mid;
        }

        #endregion

        #region Helpers

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

        #region UI sync (Store→UI & UI→Store)

        /// <summary>
        /// Pushar RD/RF till UI enligt ForwardPricingMode:
        /// - MID  → visar singel (mid) och lila om override.
        /// - FULL/NET → visar bid/ask (tvåväg) och lila om override.
        /// </summary>
        private void PushRatesUiForLeg(Guid legId)
        {
            var snap = _mktStore?.Current;
            if (snap == null) return;

            var key = legId.ToString();
            var rdFld = snap.TryGetRd(key);
            var rfFld = snap.TryGetRf(key);
            if (rdFld == null || rfFld == null) return;

            var rdEff = rdFld.Effective;
            var rfEff = rfFld.Effective;

            var rdMid = 0.5 * (rdEff.Bid + rdEff.Ask);
            var rfMid = 0.5 * (rfEff.Bid + rfEff.Ask);

            bool rdOv = rdFld.Source == MarketSource.User && rdFld.Override != OverrideMode.None;
            bool rfOv = rfFld.Source == MarketSource.User && rfFld.Override != OverrideMode.None;

            var staleRd = rdFld.IsStale;
            var staleRf = rfFld.IsStale;

            var mode = _mktStore.GetForwardPricingMode();

            // === MID ===
            if (mode == ForwardPricingMode.Mid)
            {
                if (rdOv || rfOv)
                    _view.ShowRatesOverrideById(legId, rdMid, rfMid, staleRd, staleRf);
                else
                    _view.ShowRatesById(legId, rdMid, rfMid, staleRd, staleRf);

                return;
            }

            // === FULL / NET ===
            _view.ShowRatesTwoWayById(
                legId,
                rdEff.Bid, rdEff.Ask,
                rfEff.Bid, rfEff.Ask,
                staleRd, staleRf
            );

            // Sätt lila på override-celler (utan att ändra text)
            //_view.MarkOverride(L.Rd, legId.ToString(), rdOv);
            //_view.MarkOverride(L.Rf, legId.ToString(), rfOv);
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



        /// <summary>
        /// Uppdaterar Forward-rate och -points för ett enskilt ben baserat på Store.
        /// Logik:
        /// - Alltid definiera points relativt S_mid (stabila pips när spot-pill växlas).
        /// - MID (räntor i Mid): visa F_mid och P_mid (singel).
        /// - FULL (räntor i TwoWay): beräkna P_bid/P_ask från sided räntor (mot S_mid).
        ///   Forward rate visas med enkel aritmetik:
        ///      F_bid = S_bid + P_bid, F_ask = S_ask + P_ask.
        ///   Points visas då tvåvägs (inte "bid=ask").
        /// Vid ogiltig data döljs forward-fältet för benet.
        /// </summary>
        private void PushForwardUiForLeg(Guid legId)
        {
            try
            {
                // 1) Pair + datum
                var pair6 = NormalizePair6(_view.ReadPair6()) ?? "EURSEK";
                var baseCcy = pair6.Substring(0, 3);
                var quoteCcy = pair6.Substring(3, 3);

                var today = DateTime.Today;
                var expIso = _view.TryGetResolvedExpiryIsoById(legId);
                DateTime expiry = today;
                if (!string.IsNullOrWhiteSpace(expIso))
                    DateTime.TryParse(expIso, out expiry);

                var dates = _spotSvc.Compute(pair6, today, expiry);
                var spotDate = dates.SpotDate;
                var settlement = dates.SettlementDate;

                // 2) Snapshot
                var snap = _mktStore.Current;
                if (snap == null || !string.Equals(snap.Pair6, pair6, StringComparison.OrdinalIgnoreCase))
                {
                    _view.ShowForwardById(legId, null, null);
                    return;
                }

                var sf = snap.Spot;
                if (sf == null)
                {
                    _view.ShowForwardById(legId, null, null);
                    return;
                }
                var se = sf.Effective;

                // Spot: S_mid för points; S_bid/S_ask för rate om spot-view är TwoWay
                var S_mid = 0.5 * (se.Bid + se.Ask);
                double S_bid_for_rate, S_ask_for_rate;
                if (sf.ViewMode == ViewMode.Mid || sf.Override == OverrideMode.Mid)
                {
                    S_bid_for_rate = S_mid;
                    S_ask_for_rate = S_mid;
                }
                else
                {
                    S_bid_for_rate = se.Bid;
                    S_ask_for_rate = se.Ask;
                }

                // 3) RD/RF per ben
                var key = legId.ToString();
                var rdFld = snap.TryGetRd(key);
                var rfFld = snap.TryGetRf(key);
                if (rdFld == null || rfFld == null)
                {
                    _view.ShowForwardById(legId, null, null);
                    return;
                }
                var rdEff = rdFld.Effective;
                var rfEff = rfFld.Effective;

                // 4) Year fractions
                var Tq = YearFracMm(spotDate, settlement, quoteCcy);
                var Tb = YearFracMm(spotDate, settlement, baseCcy);
                if (Tq <= 0.0 && Tb <= 0.0)
                {
                    _view.ShowForwardById(legId, null, null);
                    return;
                }

                // 5) Points relativt S_mid
                // Mid-points:
                var rd_mid = 0.5 * (rdEff.Bid + rdEff.Ask);
                var rf_mid = 0.5 * (rfEff.Bid + rfEff.Ask);
                var denom_mid = 1.0 + rf_mid * Tb;
                if (denom_mid <= 0.0)
                {
                    _view.ShowForwardById(legId, null, null);
                    return;
                }
                var F_mid = S_mid * (1.0 + rd_mid * Tq) / denom_mid;
                var P_mid = F_mid - S_mid;

                // Är räntor i TwoWay? (proxy för FULL-läge)
                bool isFull =
                    (rdFld.ViewMode == ViewMode.TwoWay || rfFld.ViewMode == ViewMode.TwoWay) &&
                    (rdFld.Override != OverrideMode.Mid && rfFld.Override != OverrideMode.Mid);

                if (!isFull)
                {
                    // MID: singel rate & singel points (mot S_mid)
                    _view.ShowForwardById(legId, F_mid, P_mid);
                    return;
                }

                // FULL: ta fram sided points (fortfarande mot S_mid), sedan F_side = S_side + P_side.
                // Sided points: använd ordningen rf_ask i bid-denominator, rf_bid i ask-denominator (samma som engine-sidning).
                var rd_bid = rdEff.Bid; var rd_ask = rdEff.Ask;
                var rf_bid = rfEff.Bid; var rf_ask = rfEff.Ask;

                var denom_bid = 1.0 + rf_ask * Tb;
                var denom_ask = 1.0 + rf_bid * Tb;
                if (denom_bid <= 0.0 || denom_ask <= 0.0)
                {
                    _view.ShowForwardById(legId, null, null);
                    return;
                }

                // Fwd från S_mid för att få points per sida (stabila mot spot-pill):
                var F_from_mid_bid = S_mid * (1.0 + rd_bid * Tq) / denom_bid;
                var F_from_mid_ask = S_mid * (1.0 + rd_ask * Tq) / denom_ask;
                var P_bid = F_from_mid_bid - S_mid;
                var P_ask = F_from_mid_ask - S_mid;

                // Forward RATE enligt "enkel matematik": F_side = S_side + P_side
                var F_bid = S_bid_for_rate + P_bid;
                var F_ask = S_ask_for_rate + P_ask;
                if (F_bid > F_ask)
                {
                    var tmp = F_bid; F_bid = F_ask; F_ask = tmp; // monotoni-vakt
                }

                _view.ShowForwardTwoWayById(legId, F_bid, F_ask, P_bid, P_ask);
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

        private void ApplyUiRatesToStore(Guid legId)
        {
            var state = _legStates.Find(s => s.LegId == legId);
            if (state == null || string.IsNullOrWhiteSpace(state.Label))
            {
                System.Diagnostics.Debug.WriteLine($"[Presenter.Rates->Store] leg={legId} saknar kolumn-label.");
                return;
            }
            string col = state.Label;

            double? uiRd, uiRf; bool ovRd, ovRf;
            if (!_view.TryReadRatesForColumn(col, out uiRd, out ovRd, out uiRf, out ovRf))
                return;

            var pair6 = NormalizePair6(_view.ReadPair6());
            if (string.IsNullOrWhiteSpace(pair6)) pair6 = _mktStore.Current?.Pair6 ?? "EURSEK";

            var now = DateTime.UtcNow;
            var legKey = legId.ToString();

            var snap = _mktStore.Current;
            var rdFld = snap?.TryGetRd(legKey);
            var rfFld = snap?.TryGetRf(legKey);

            double Mid(TwoWay<double> tw) => 0.5 * (tw.Bid + tw.Ask);

            // ---- RD ----
            if (ovRd && uiRd.HasValue)
            {
                bool skipWrite = false;
                if (rdFld != null)
                {
                    var eff = rdFld.Effective;
                    // Tolerans: några 1e-6 (≈0.5–1 pip på 4–5 decimaltal) för att undvika “nästan lika” = ny skrivning
                    const double tol = 5e-6;
                    var isTwoWayUser = rdFld.Source == FX.Core.Domain.MarketData.MarketSource.User
                                       && rdFld.Override != OverrideMode.None
                                       && Math.Abs(eff.Bid - eff.Ask) > tol;

                    var sameMidAsStore = Math.Abs(Mid(eff) - uiRd.Value) <= tol
                                         && rdFld.Source == FX.Core.Domain.MarketData.MarketSource.User
                                         && rdFld.Override != OverrideMode.None;

                    // Viktigt:
                    // - Om Store redan bär en user two-way (solver) → skriv INTE tillbaka en mid som klappar ihop den.
                    // - Om mid i UI == mid i Store (inom tol) → onödig skrivning, hoppa.
                    skipWrite = isTwoWayUser || sameMidAsStore;
                }

                if (!skipWrite)
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
                        $"[Presenter.Rates->Store] pair={pair6} leg={legId} RD={uiRd.Value:F6} src=User vm=TwoWay ov=Mid (UI→Store)");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[Presenter.Rates->Store] RD SKIP (two-way i Store eller same mid) leg={legId} ui={uiRd.Value:F6}");
                }
            }
            else
            {
                if (uiRd.HasValue)
                    System.Diagnostics.Debug.WriteLine($"[Presenter.Rates->Store] RD IGNORE (ovRd=false) leg={legId} ui={uiRd.Value:F6}");
            }

            // ---- RF (oförändrat beteende – skriv endast om ovRf=true) ----
            if (ovRf && uiRf.HasValue)
            {
                bool sameAsStore = false;
                if (rfFld != null)
                {
                    const double tol = 5e-6;
                    var eff = rfFld.Effective;
                    sameAsStore = Math.Abs(Mid(eff) - uiRf.Value) <= tol
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
                        $"[Presenter.Rates->Store] pair={pair6} leg={legId} RF={uiRf.Value:F6} src=User vm=TwoWay ov=Mid (UI→Store)");
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


        #endregion

        #region Forward Solver

        /// <summary>
        /// Läser transformerade pins (per ben) från vyn och löser RD på nytt mot dessa mål,
        /// utan att bryta pin (ingen reset). Tolkning per läge:
        /// - MID: singel förväntas (pair accepteras ej av vyn).
        /// - FULL/NET: singel tolkas som value/value (båda sidor lika) inför solve.
        /// Notering: RF-mid vid single i FULL (Alt B) införs i nästa steg i engine/adapter.
        /// </summary>
        private void ReSolvePinnedTargetsForMode(int modeIdx)
        {
            var ids = _view.GetAllLegIds();
            if (ids == null || ids.Count == 0) return;

            var mode = (modeIdx == 0) ? ForwardPricingMode.Mid
                     : (modeIdx == 1) ? ForwardPricingMode.Full
                                      : ForwardPricingMode.Net;

            foreach (var legId in ids)
            {
                if (!_view.TryBuildForwardArgsFromPin(legId, modeIdx, out var arg) || arg == null)
                    continue;

                LegacyPricerView.ForwardTargetEditedEventArgs toSolve = arg;

                if (mode == ForwardPricingMode.Mid)
                {
                    // MID ska vara singel (vyn har redan gjort pair→mid), inget att transformera.
                    if (toSolve.IsPair)
                    {
                        // Säkerhetsvakt: kollapsa till mid om det ändå råkat bli pair.
                        double mid = 0.5 * ((toSolve.Bid ?? 0.0) + (toSolve.Ask ?? 0.0));
                        toSolve = new LegacyPricerView.ForwardTargetEditedEventArgs
                        {
                            LegId = arg.LegId,
                            ColumnName = arg.ColumnName,
                            TargetType = arg.TargetType,
                            IsPair = false,
                            Single = mid,
                            Bid = null,
                            Ask = null,
                            RawDisplay = arg.RawDisplay,
                            Decimals = arg.Decimals,
                            WasMidStyle = true
                        };
                    }
                }
                else
                {
                    // FULL/NET: singel ska behandlas som value/value vid solve
                    if (!toSolve.IsPair && toSolve.Single.HasValue)
                    {
                        toSolve = new LegacyPricerView.ForwardTargetEditedEventArgs
                        {
                            LegId = arg.LegId,
                            ColumnName = arg.ColumnName,
                            TargetType = arg.TargetType,
                            IsPair = true,
                            Bid = toSolve.Single,
                            Ask = toSolve.Single,
                            Single = null,
                            RawDisplay = arg.RawDisplay,
                            Decimals = arg.Decimals,
                            WasMidStyle = true
                        };
                    }
                }

                // Lös RD mot det transformerade målet
                SolveRdFromForwardTarget(toSolve);
            }
        }


        /// <summary>
        /// Löser RD från användarens forward-target (Hold RF) med policy inkl. Alternativ B.
        /// Policy:
        /// - MID + single: solve mot RF_mid, skriv RD lika (bid=ask).
        /// - FULL/NET + single: tolka single som value/value, FRYS RF på mid (solve + prisning), skriv RD lika.
        /// - FULL/NET + pair:
        ///     * Om F_bid≈F_ask (inom tajt tolerans) → behandla som single (RF_mid, RD lika).
        ///     * Annars lös sida-för-sida (sided) och släpp ev. tidigare RF-mid-override.
        /// - Points i UI är pips → konverteras till rate-delta före addition till S_mid.
        /// </summary>
        private void SolveRdFromForwardTarget(ForwardTargetEditedEventArgs e)
        {
            // Guards
            var legId = e.LegId;
            if (legId == Guid.Empty) return;

            var pair6 = NormalizePair6(_view.ReadPair6()) ?? _mktStore.Current?.Pair6 ?? "EURSEK";
            var baseCcy = pair6.Substring(0, 3);
            var quoteCcy = pair6.Substring(3, 3);

            // Datum/Tq/Tb
            var today = DateTime.Today;
            var expIso = _view.TryGetResolvedExpiryIsoById(legId);
            DateTime expiry = today;
            if (!string.IsNullOrWhiteSpace(expIso)) DateTime.TryParse(expIso, out expiry);

            var dates = _spotSvc.Compute(pair6, today, expiry);
            var spotDate = dates.SpotDate;
            var settlement = dates.SettlementDate;

            var Tq = YearFracMm(spotDate, settlement, quoteCcy);
            var Tb = YearFracMm(spotDate, settlement, baseCcy);
            if (Tq <= 0.0) throw new InvalidOperationException("Kan ej lösa RD: Tq≈0 (för kort tenor).");

            // Snapshot
            var snap = _mktStore.Current;
            if (snap == null || !string.Equals(snap.Pair6, pair6, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("MarketSnapshot saknas eller fel par.");

            // Spot: vi använder S_mid i solver (stabilt ihop med pips→rate)
            var se = snap.Spot?.Effective ?? default(TwoWay<double>);
            var S_mid = 0.5 * (se.Bid + se.Ask);
            if (double.IsNaN(S_mid) || double.IsInfinity(S_mid) || S_mid <= 0.0)
                throw new InvalidOperationException("Ogiltig spot i snapshot.");

            // RF two-way (kan frysas till mid i vissa fall)
            var key = legId.ToString();
            var rfFd = snap.TryGetRf(key);
            if (rfFd == null) throw new InvalidOperationException("RF saknas i snapshot.");
            var rfEff = rfFd.Effective;
            var rf_mid = 0.5 * (rfEff.Bid + rfEff.Ask);
            var rf_bid = rfEff.Bid;
            var rf_ask = rfEff.Ask;

            // Bygg F-target
            double? F_single = null, F_bid = null, F_ask = null;
            if (!e.IsPair)
            {
                if (!e.Single.HasValue) return;
                F_single = (e.TargetType == ForwardTargetType.Points)
                    ? S_mid + PointsPipsToRateDelta(pair6, e.Single.Value)
                    : e.Single.Value;
            }
            else
            {
                if (!e.Bid.HasValue && !e.Ask.HasValue) return;

                if (e.TargetType == ForwardTargetType.Points)
                {
                    if (e.Bid.HasValue) F_bid = S_mid + PointsPipsToRateDelta(pair6, e.Bid.Value);
                    if (e.Ask.HasValue) F_ask = S_mid + PointsPipsToRateDelta(pair6, e.Ask.Value);
                }
                else
                {
                    F_bid = e.Bid; F_ask = e.Ask;
                }

                if (F_bid.HasValue && F_ask.HasValue)
                {
                    var fb = F_bid.Value; var fa = F_ask.Value;
                    EnsureBidAskMonotonic(ref fb, ref fa);
                    F_bid = fb; F_ask = fa;
                }
            }

            double Denom(double rf) => 1.0 + rf * Tb;
            var nowUtc = DateTime.UtcNow;
            var mode = _mktStore.GetForwardPricingMode();

            // Hjälpare: skriv RD lika (bid=ask) + ev. RF-mid override
            void WriteSingleLikeWithRfMid(double Fstar, bool freezeRfMid)
            {
                if (freezeRfMid)
                {
                    _mktStore.SetRfFromUser(
                        pair6,
                        key,
                        new TwoWay<double>(rf_mid, rf_mid),
                        /*wasMid*/ true,
                        ViewMode.TwoWay,
                        nowUtc
                    );
                }

                var denom = Denom(rf_mid);
                if (denom <= 0.0) throw new InvalidOperationException("Ogiltigt rf·Tb (denominator≤0).");

                var rd_mid = ((Fstar / S_mid) * denom - 1.0) / Tq;

                _mktStore.SetRdFromUser(
                    pair6,
                    key,
                    new TwoWay<double>(rd_mid, rd_mid),
                    /*wasMid*/ true,
                    ViewMode.TwoWay,
                    nowUtc
                );
            }

            if (!e.IsPair)
            {
                // SINGLE target
                bool freezeRf = (mode != ForwardPricingMode.Mid) && e.WasMidStyle; // FULL/NET + single
                WriteSingleLikeWithRfMid(F_single.Value, freezeRf);

                System.Diagnostics.Debug.WriteLine(
                    (mode == ForwardPricingMode.Mid ? "[Solver] MID/single" : "[Solver] FULL/NET single RF(mid)") +
                    " pair=" + pair6 +
                    " leg=" + legId +
                    " F*=" + F_single.Value.ToString("F8", System.Globalization.CultureInfo.InvariantCulture) +
                    " rf_mid=" + rf_mid.ToString("F8", System.Globalization.CultureInfo.InvariantCulture) +
                    " Tq=" + Tq.ToString("F6", System.Globalization.CultureInfo.InvariantCulture) +
                    " Tb=" + Tb.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)
                );
            }
            else
            {
                // PAIR target
                // Om paren i praktiken är lika → behandla som single-ekvivalent (freeze RF mid i FULL/NET)
                bool both = F_bid.HasValue && F_ask.HasValue;
                bool effectivelySingle = both && Math.Abs(F_bid.Value - F_ask.Value) < 1e-12;

                if (mode != ForwardPricingMode.Mid && effectivelySingle)
                {
                    // RF-mid + RD lika
                    var Fstar = 0.5 * (F_bid.Value + F_ask.Value);
                    WriteSingleLikeWithRfMid(Fstar, freezeRfMid: true);

                    System.Diagnostics.Debug.WriteLine(
                        "[Solver] FULL/NET pair(eq)→treat-as-single RF(mid)" +
                        " pair=" + pair6 +
                        " leg=" + legId +
                        " Fbid=" + F_bid.Value.ToString("F8", System.Globalization.CultureInfo.InvariantCulture) +
                        " Fask=" + F_ask.Value.ToString("F8", System.Globalization.CultureInfo.InvariantCulture)
                    );
                }
                else if (mode == ForwardPricingMode.Mid)
                {
                    // MID hamnar inte här (vyn avvisar pair), men säkerhetsmässigt: behandla som single på mid
                    var Fstar = both ? 0.5 * (F_bid.Value + F_ask.Value) : (F_bid ?? F_ask ?? S_mid);
                    WriteSingleLikeWithRfMid(Fstar, freezeRfMid: false);

                    System.Diagnostics.Debug.WriteLine(
                        "[Solver] MID/pair→treat-as-single" +
                        " pair=" + pair6 +
                        " leg=" + legId
                    );
                }
                else
                {
                    // FULL/NET med riktig tvåvägs-input (F_bid ≠ F_ask) → sided solve
                    _mktStore.SetRfOverride(pair6, key, OverrideMode.None, nowUtc); // släpp ev. tidigare RF-mid-override

                    double? rd_bid = null, rd_ask = null;

                    if (F_bid.HasValue)
                    {
                        var denom_bid = Denom(rf_ask); // sided: rf_ask i bid-denominator
                        if (denom_bid <= 0.0) throw new InvalidOperationException("Ogiltigt rf_ask·Tb i bid-sida.");
                        rd_bid = ((F_bid.Value / S_mid) * denom_bid - 1.0) / Tq;
                    }

                    if (F_ask.HasValue)
                    {
                        var denom_ask = Denom(rf_bid); // sided: rf_bid i ask-denominator
                        if (denom_ask <= 0.0) throw new InvalidOperationException("Ogiltigt rf_bid·Tb i ask-sida.");
                        rd_ask = ((F_ask.Value / S_mid) * denom_ask - 1.0) / Tq;
                    }

                    var rdEff = snap.TryGetRd(key)?.Effective ?? new TwoWay<double>(0, 0);
                    var outBid = rd_bid ?? rdEff.Bid;
                    var outAsk = rd_ask ?? rdEff.Ask;

                    _mktStore.SetRdFromUser(
                        pair6,
                        key,
                        new TwoWay<double>(outBid, outAsk),
                        /*wasMid*/ false,
                        ViewMode.TwoWay,
                        nowUtc
                    );

                    System.Diagnostics.Debug.WriteLine(
                        "[Solver] FULL/NET pair(sided)" +
                        " pair=" + pair6 +
                        " leg=" + legId +
                        " F_bid=" + (F_bid.HasValue ? F_bid.Value.ToString("F8", System.Globalization.CultureInfo.InvariantCulture) : "null") +
                        " F_ask=" + (F_ask.HasValue ? F_ask.Value.ToString("F8", System.Globalization.CultureInfo.InvariantCulture) : "null") +
                        " rf_bid=" + rf_bid.ToString("F8", System.Globalization.CultureInfo.InvariantCulture) +
                        " rf_ask=" + rf_ask.ToString("F8", System.Globalization.CultureInfo.InvariantCulture)
                    );
                }
            }
        }




        /// <summary>
        /// Returnerar pip-storlek (rate-delta per 1 pip) givet ett par i format "EURSEK".
        /// Basregel: JPY som quote ⇒ 0.01, annars 0.0001.
        /// </summary>
        private static double PipSizeFromPair6(string pair6)
        {
            if (string.IsNullOrWhiteSpace(pair6) || pair6.Length < 6) return 0.0001;
            var quote = pair6.Substring(pair6.Length - 3).ToUpperInvariant();
            return (quote == "JPY") ? 0.01 : 0.0001;
        }

        /// <summary>
        /// Konverterar Forward Points angivna i pips (så som användaren skriver i UI)
        /// till rate-delta (samma enhet som spot/forward) för givet par.
        /// Ex: EURSEK, 10 pips ⇒ 10 × 0.0001 = 0.0010.
        /// Ex: USDJPY, 10 pips ⇒ 10 × 0.01   = 0.10.
        /// </summary>
        private static double PointsPipsToRateDelta(string pair6, double pointsInPips)
        {
            var pip = PipSizeFromPair6(pair6);
            return pointsInPips * pip;
        }

        /// <summary>
        /// Säkerställer monotonin för bid/ask efter beräkning.
        /// Om ask < bid byts de (robust normalisering).
        /// </summary>
        private static void EnsureBidAskMonotonic(ref double bid, ref double ask)
        {
            if (ask < bid)
            {
                var tmp = bid; bid = ask; ask = tmp;
            }
        }




        #endregion






    }
}
