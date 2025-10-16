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
using System.Drawing;

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
        private readonly ISpotFeed _spotFeed;
        private readonly IMarketStore _mktStore;
        private readonly int _spotTimeoutMs = 3000;

        // Debounce/single-flight för Reprice
        private const int RepriceDebounceMs = 50; // justera 10–100 ms efter smak
        private readonly object _repriceGate = new object();
        private Timer _repriceTimer;           // System.Threading.Timer
        private volatile bool _repricePending; // det har kommit events under väntetiden
        private volatile bool _repriceRunning; // en reprice kör just nu
        #endregion

        #region Constructor / Dispose

        /// <summary>
        /// Skapar presenter och ansluter UI- och bus-händelser.
        /// Om <paramref name="spotFeed"/> inte tillhandahålls används <see cref="BloombergSpotFeed"/> med timeout 3000 ms.
        /// </summary>
        public LegacyPricerPresenter(IMessageBus bus, LegacyPricerView view, IMarketStore marketStore)
        {
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));
            _view = view ?? throw new ArgumentNullException(nameof(view));

            // === Ben: se till att vi startar med 1 stabilt ben ("Vanilla 1") ===
            if (_legStates.Count == 0)
            {
                var ls = new LegState(Guid.NewGuid(), "Vanilla 1");
                _legStates.Add(ls);
                _view.BindLegIdToLabel(ls.LegId, ls.Label); // håller UI-label i sync
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

            _view.SpotRefreshRequested += (_, __) => System.Threading.Tasks.Task.Run(() => RefreshSpotSnapshot());

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


            //// Seed expiry + prisa
            //TrySeedDefaultExpiryAndReprice();

            //// Första spot-snapshot
            //RefreshSpotSnapshot();
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
        /// - För Deal: resolve appliceras för samtliga ben (via _legacyColumns) och varje ben prissätts.
        /// - För specifikt ben: resolve + rollback vid fel och prissätt just benet.
        /// </summary>
        private void OnExpiryEditRequested(object sender, LegacyPricerView.ExpiryEditRequestedEventArgs e)
        {
            var pair6 = (e.Pair6 ?? _view.ReadPair6() ?? "EURSEK").Replace("/", "").ToUpperInvariant();
            var holidays = LoadHolidaysForPair(pair6);

            // Deal → alla ben via LegId
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

                        _view.ShowResolvedExpiryById(ls.LegId, r.ExpiryIso, wdEn, rawHint);
                        _view.ShowResolvedSettlementById(ls.LegId, r.SettlementIso);

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

            // Ben-specifik: om e.LegColumn kommer från UI kan vi mappa till LegId,
            // men här antar vi att Presentern redan jobbar med LegId från UI-events framöver.
            // Vi riktar oss därför mot "första benet" som fallback om kolumnnamn saknas.
            var target = _legStates.Count > 0 ? _legStates[0] : null;
            if (target == null) return;

            try
            {
                var res = ExpiryInputResolver.Resolve(e.Raw, pair6, holidays);
                var wd = res.ExpiryDate.ToString("ddd", CultureInfo.GetCultureInfo("en-US"));
                var hint = string.Equals(res.Mode, "Tenor", StringComparison.OrdinalIgnoreCase)
                           ? res.Normalized?.ToUpperInvariant()
                           : null;

                _view.ShowResolvedExpiryById(target.LegId, res.ExpiryIso, wd, hint);
                _view.ShowResolvedSettlementById(target.LegId, res.SettlementIso);

                PriceSingleLeg(target.LegId);
            }
            catch (Exception ex)
            {
                // rollback i dagens UI är label-baserad; tills din vy byter helt till LegId
                // låter vi vyn hantera rollback själv om du vill (annars ingen rollback här)
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
        /// Läser UI-snapshot för benet, löser expiry vid behov och skickar RequestPrice.
        /// </summary>
        private void PriceSingleLeg(Guid legId)
        {
            string reason;
            if (!CanPriceNow(out reason))
            {
                System.Diagnostics.Debug.WriteLine("[Presenter] Skip PriceSingleLeg: " + reason);
                return;
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

            // --- Expiry resolve (best effort) ---
            var iso = _view.TryGetResolvedExpiryIsoById(legId);
            if (string.IsNullOrWhiteSpace(iso) && !string.IsNullOrWhiteSpace(snap.ExpiryRaw))
            {
                try
                {
                    var pair6x = (snap.Pair6 ?? "EURSEK").Replace("/", "").ToUpperInvariant();
                    var holidays = LoadHolidaysForPair(pair6x);
                    var r = ExpiryInputResolver.Resolve(snap.ExpiryRaw, pair6x, holidays);
                    var wd = r.ExpiryDate.ToString("ddd", CultureInfo.GetCultureInfo("en-US"));
                    var hint = string.Equals(r.Mode, "Tenor", StringComparison.OrdinalIgnoreCase)
                                ? r.Normalized?.ToUpperInvariant()
                                : null;

                    _view.ShowResolvedExpiryById(legId, r.ExpiryIso, wd, hint);
                    _view.ShowResolvedSettlementById(legId, r.SettlementIso);
                    iso = r.ExpiryIso;
                }
                catch { /* best effort */ }
            }

            // --- Spot från MarketStore (samma logik som tidigare) ---
            var storeSnap = _mktStore.Current;

            var pair6 =
                NormalizePair6(snap.Pair6) ??
                NormalizePair6(_mktStore.Current?.Pair6) ??
                NormalizePair6(_view.ReadPair6());

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
                var mid = snap.SpotMid > 0.0 ? snap.SpotMid : snap.Spot;
                sb = mid; sa = mid;
            }

            if (sb <= 0.0 || sa <= 0.0)
            {
                System.Diagnostics.Debug.WriteLine("[Presenter] Skip pricing: Spot not set.");
                return;
            }

            int dp = 4;
            try { dp = _view.GetSpotUiDecimals(); } catch { dp = 4; }

            sb = Math.Round(sb, dp, MidpointRounding.AwayFromZero);
            sa = Math.Round(sa, dp, MidpointRounding.AwayFromZero);

            // --- Vol (decimal → procent) ---
            double? volBidPct = (snap.VolBid > 0.0) ? (double?)(snap.VolBid * 100.0) : null;
            double? volAskPct = (snap.VolHasTwoWay && snap.VolAsk > 0.0) ? (double?)(snap.VolAsk * 100.0) : null;

            // --- Publisera kommandot ---
            var corr = Guid.NewGuid();
            _corrToLegId[corr] = legId;

            var cmd = new FX.Messages.Commands.RequestPrice
            {
                CorrelationId = corr,
                Pair6 = pair6,
                SpotBidOverride = sb,
                SpotAskOverride = sa,
                RdOverride = snap.Rd,
                RfOverride = snap.Rf,
                SurfaceId = "default",
                StickyDelta = false,
                VolBidPct = volBidPct,
                VolAskPct = volAskPct,
                Legs = new System.Collections.Generic.List<FX.Messages.Commands.RequestPrice.Leg>
        {
            new FX.Messages.Commands.RequestPrice.Leg
            {
                Side      = snap.Side,
                Type      = snap.Type,
                Strike    = snap.Strike,
                ExpiryIso = string.IsNullOrWhiteSpace(iso) ? "2030-12-31" : iso,
                Notional  = snap.Notional
            }
        }
            };

            System.Threading.Tasks.Task.Run(() => _bus.Publish(cmd));
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
            });
        }

        /// <summary>
        /// Tar emot fel från bus och loggar/visar basinformation.
        /// </summary>
        /// <summary>Tar emot fel från bus och loggar/visar basinformation.</summary>
        private void OnError(FX.Messages.Events.ErrorOccurred e)
        {
            _corrToLegId.Remove(e.CorrelationId);   // ändrat namn
            System.Diagnostics.Debug.WriteLine($"[ERR] {e.Source}: {e.Message}");
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
        /// Hämtar spot (TryGetTwoWay) från feed och skriver endast till MarketStore (FeedSpot).
        /// UI uppdateras därefter via OnMarketChanged (store-driven). Loggar feedvärden om DebugFlags.SpotFeed=true.
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


                System.Diagnostics.Debug.WriteLine($"[RefreshSpotSnapshot][T{System.Threading.Thread.CurrentThread.ManagedThreadId}] {p6} FEED raw {bid:F6}/{ask:F6}");


                // Skriv ENDAST till store; OnMarketChanged tar UI-visningen
                _mktStore.SetSpotFromFeed(
                    p6,
                    new TwoWay<double>(bid, ask),
                    DateTime.UtcNow,
                    isStale: false
                );
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
                        System.Diagnostics.Debug.WriteLine("[Presenter] Skip reprice: " + reason);
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
        /// Reagerar på MarketStore-ändringar: uppdaterar spot-UI vid spot-relaterad reason
        /// och schemalägger prisning via debounce/single-flight (ScheduleRepriceDebounced()).
        /// Inget tungt arbete görs på UI-tråden direkt.
        /// </summary>
        private void OnMarketChanged(object sender, FX.Core.Domain.MarketData.MarketChangedEventArgs e)
        {
            try
            {
                var snap = _mktStore.Current;
                var effOpt = snap?.Spot?.Effective; // TwoWay<double>?

                // 1) Uppdatera SPOT i UI endast vid spot-relaterat reason
                if (IsSpotReason(e?.Reason) && effOpt.HasValue)
                {
                    var bid = effOpt.Value.Bid;
                    var ask = effOpt.Value.Ask;

                    OnUi(() => _view.ShowSpotFeedFixed4(bid, ask));
                }

                // 2) Reprice: debounce + single-flight
                ScheduleRepriceDebounced();
            }
            catch (Exception ex)
            {
                _bus.Publish(new ErrorOccurred
                {
                    Source = "Presenter.OnMarketChanged",
                    Message = ex.Message,
                    Detail = ex.ToString(),
                    CorrelationId = Guid.Empty
                });
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

        #endregion

        #region Helpers

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
        /// Skapar ett nytt ben sist i listan med ett nytt stabilt <see cref="Guid"/> och
        /// etikett "Vanilla N". Returnerar det skapade LegId.
        /// </summary>
        private Guid AddNewLeg()
        {
            var newId = Guid.NewGuid();
            var newLabel = $"Vanilla {_legStates.Count + 1}";

            _legStates.Add(new LegState(newId, newLabel));

            // UI-hook (no-op tills du kopplar mot vyn)
            NotifyViewLegAdded(newId, newLabel);

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

        /// <summary>Hook för att låta vyn lägga in en ny rad för ett ben.</summary>
        private void NotifyViewLegAdded(Guid legId, string label)
        {
            _view.BindLegIdToLabel(legId, label);
            // Här kan du senare be vyn skapa en kolumn/tab etc.
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

    }
}
