// FX.Services/FxRuntime.cs
// (Uppdaterad: valfri MarketStore-ref + hjälpare för rd/rf-feeding)
using System;
using System.Collections.Generic;
using System.Threading;
using FX.Core.Interfaces;
using FX.Messages.Commands;
using FX.Messages.Events;
using FX.Core.Domain;
using FX.Core.Domain.MarketData;   // IMarketStore
using FX.Services.MarketData;      // UsdAnchoredRateFeeder (och ev. orchestrator senare)
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Tasks;


namespace FX.Services
{
    public sealed class FxRuntime : IDisposable
    {
        private readonly IMessageBus _bus;
        private readonly IPriceEngine _price;
        private readonly ISpotSetDateService _spotSvc;
        private readonly IMarketStore _marketStore; // valfri, för rd/rf/spot via store
        private readonly List<IDisposable> _subscriptions = new List<IDisposable>();
        private readonly AppStateStore _state;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();


        /// <summary>
        /// Enda publika konstruktorn – kräver både MarketStore (för spot/rd/rf)
        /// och SpotSetDateService (för spot/settlement-datum).
        /// </summary>
        [ActivatorUtilitiesConstructor]
        public FxRuntime(IMessageBus bus, IPriceEngine price, AppStateStore state, IMarketStore marketStore, ISpotSetDateService spotSvc)
        {
            if (bus == null) throw new ArgumentNullException(nameof(bus));
            if (price == null) throw new ArgumentNullException(nameof(price));
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (marketStore == null) throw new ArgumentNullException(nameof(marketStore));
            if (spotSvc == null) throw new ArgumentNullException(nameof(spotSvc));

            _bus = bus;
            _price = price;
            _state = state;
            _marketStore = marketStore;
            _spotSvc = spotSvc;

            // Bus-subscriptions
            _subscriptions.Add(_bus.Subscribe<RequestPrice>(HandleRequestPrice));
            // _subscriptions.Add(_bus.Subscribe<RebuildVolSurface>(HandleRebuildSurface)); // om/när du aktiverar
        }

        private void HandleRequestPrice(RequestPrice cmd)
        {
            _ = Task.Run(() => HandleRequestPriceWorkerAsync(cmd));
        }

        /// <summary>
        /// Huvudhandler för prisförfrågningar.
        /// 1) Säkerställ rd/rf i MarketStore (via orchestrator) för BENETS Guid.
        /// 2) Läs effektiv rd/rf ur MarketStore (samma nyckel).
        /// 3) Välj spot enligt store (respektera ViewMode/Override).
        /// 4) Kör motorn och publicera PriceCalculated.
        /// </summary>
        private async System.Threading.Tasks.Task HandleRequestPriceWorkerAsync(RequestPrice cmd)
        {
            try
            {
                try
                {
                    // === 0) Normalisera indata ===
                    string pair6 = (cmd.Pair6 ?? "EURSEK").Replace("/", "").ToUpperInvariant();

                    if (cmd.Legs == null || cmd.Legs.Count == 0)
                        throw new InvalidOperationException("Request saknar ben.");

                    var leg0 = cmd.Legs[0];
                    if (leg0.LegId == Guid.Empty)
                        throw new InvalidOperationException("LegId saknas i RequestPrice.Leg.");

                    // Stabil ben-nyckel = Guid-string
                    string legId = leg0.LegId.ToString();

                    // Säkerställ att store är på rätt par innan Build
                    var current = _marketStore.Current;
                    if (current == null || !string.Equals(current.Pair6, pair6, StringComparison.OrdinalIgnoreCase))
                    {
                        _marketStore.SetSpotFromFeed(pair6, new TwoWay<double>(0d, 0d), DateTime.UtcNow, isStale: true);
                    }

                    // === 1) Datum (expiry från cmd) + Spot/Settle via tjänsten ===
                    var today = DateTime.Today;
                    var expiry = today;
                    if (!string.IsNullOrWhiteSpace(leg0.ExpiryIso))
                        DateTime.TryParse(leg0.ExpiryIso, out expiry);

                    var dates = _spotSvc.Compute(pair6, today, expiry);
                    var spotDate = dates.SpotDate;
                    var settlement = dates.SettlementDate;

                    // === 2) Spot enligt store (respektera ViewMode/Override) ===
                    var snap = _marketStore.Current;
                    var spotField = snap.Spot;
                    var spotEff = spotField.Effective;

                    double spotBid, spotAsk;
                    if (spotField.ViewMode == ViewMode.Mid || spotField.Override == OverrideMode.Mid)
                    {
                        var mid = 0.5 * (spotEff.Bid + spotEff.Ask);
                        spotBid = mid; spotAsk = mid;
                    }
                    else
                    {
                        spotBid = spotEff.Bid; spotAsk = spotEff.Ask;
                    }

                    System.Diagnostics.Debug.WriteLine(
                        $"[FxRuntime.Request][T{Thread.CurrentThread.ManagedThreadId}] pair={pair6} legId={legId} spotEff={spotEff.Bid:F6}/{spotEff.Ask:F6} vm={spotField.ViewMode} ov={spotField.Override} exp={expiry:yyyy-MM-dd} spotDate={spotDate:yyyy-MM-dd} settle={settlement:yyyy-MM-dd}");

                    // === 3) Kör Orchestratorn för att säkerställa RD/RF för just detta legId ===
                    var orchestrator = FX.Services.MarketData.OrchestratorFactory.Create(_marketStore);
                    var build = orchestrator.Build(pair6, legId, today, expiry, spotDate, settlement, useMid: false);

                    // === 4) Läs tillbaka effektiv RD/RF (TwoWay<double>?) ur MarketStore med samma legId ===
                    snap = _marketStore.Current; // hämta igen efter build
                    var rdEff = snap.TryGetRd(legId)?.Effective;
                    var rfEff = snap.TryGetRf(legId)?.Effective;

                    if (!rdEff.HasValue || !rfEff.HasValue)
                        throw new InvalidOperationException("Effektiv RD/RF saknas efter orchestrator.Build().");

                    System.Diagnostics.Debug.WriteLine(
                        $"[FxRuntime.Rates][T{Thread.CurrentThread.ManagedThreadId}] pair={pair6} legId={legId} rdEff={rdEff.Value.Bid:F6}/{rdEff.Value.Ask:F6} rfEff={rfEff.Value.Bid:F6}/{rfEff.Value.Ask:F6}");

                    // === 5) rd/rf-mid (om overrides ej satta i cmd) ===
                    double rdMid = 0.5 * (rdEff.Value.Bid + rdEff.Value.Ask);
                    double rfMid = 0.5 * (rfEff.Value.Bid + rfEff.Value.Ask);

                    double rd = cmd.RdOverride ?? rdMid;
                    double rf = cmd.RfOverride ?? rfMid;

                    // === 6) Bygg domänens PricingRequest (samma mapping som tidigare) ===
                    var legs = new List<OptionLeg>();
                    if (cmd.Legs != null)
                    {
                        for (int i = 0; i < cmd.Legs.Count; i++)
                        {
                            var src = cmd.Legs[i];
                            var sideEnum = ParseSide(src.Side);
                            var typeEnum = ParseType(src.Type);

                            DateTime legExpiry = expiry;
                            if (!string.IsNullOrWhiteSpace(src.ExpiryIso))
                                DateTime.TryParse(src.ExpiryIso, out legExpiry);

                            var spotMidForStrike = (spotBid > 0 && spotAsk > 0)
                                ? 0.5 * (spotBid + spotAsk)
                                : (spotBid > 0 ? spotBid : (spotAsk > 0 ? spotAsk : 0));

                            var strikeDom = FX.Core.Domain.Strike.ParseStrict(src.Strike, (decimal)spotMidForStrike);
                            var domExpiry = new Expiry(legExpiry);
                            var pair = new CurrencyPair(pair6.Substring(0, 3), pair6.Substring(3, 3));

                            legs.Add(new OptionLeg(
                                pair: pair,
                                side: sideEnum,
                                type: typeEnum,
                                strike: strikeDom,
                                expiry: domExpiry,
                                notional: src.Notional
                            ));
                        }
                    }

                    var domainReq = new PricingRequest(
                        pair: new CurrencyPair(pair6.Substring(0, 3), pair6.Substring(3, 3)),
                        legs: legs,
                        spotBid: spotBid,
                        spotAsk: spotAsk,
                        rd: rd,
                        rf: rf,
                        surfaceId: cmd.SurfaceId ?? "default",
                        stickyDelta: cmd.StickyDelta
                    );

                    // Tvåvägsvol: procent → decimal
                    double? bidDec = cmd.VolBidPct.HasValue ? cmd.VolBidPct.Value / 100.0 : (double?)null;
                    double? askDec = cmd.VolAskPct.HasValue ? cmd.VolAskPct.Value / 100.0 : (double?)null;
                    domainReq.SetVol(new VolQuote(bidDec, askDec));

                    // === 7) Kör prisning och publicera ===
                    var unit = await _price.PriceAsync(domainReq, _cts.Token).ConfigureAwait(false);

                    string firstSide = (cmd.Legs != null && cmd.Legs.Count > 0 ? cmd.Legs[0].Side : "BUY")
                                       ?.ToUpperInvariant() ?? "BUY";
                    bool isBuy = string.Equals(firstSide, "BUY", StringComparison.OrdinalIgnoreCase);

                    double pricePerUnit = isBuy ? unit.PremiumAsk : unit.PremiumBid;
                    string boldSide = (unit.PremiumBid == unit.PremiumAsk) ? "MID" : (isBuy ? "ASK" : "BID");

                    _bus.Publish(new PriceCalculated
                    {
                        CorrelationId = cmd.CorrelationId,
                        Price = pricePerUnit,
                        PremiumBid = unit.PremiumBid,
                        PremiumMid = unit.PremiumMid,
                        PremiumAsk = unit.PremiumAsk,
                        BoldSide = boldSide,
                        Delta = unit.Delta,
                        Vega = unit.Vega,
                        Gamma = unit.Gamma,
                        Theta = unit.Theta
                    });

                    System.Diagnostics.Debug.WriteLine(
                        $"[FxRuntime.Done][T{System.Threading.Thread.CurrentThread.ManagedThreadId}] pair={pair6} legId={legId} premMid={unit.PremiumMid:F6}");
                }
                catch (Exception ex)
                {
                    _bus.Publish(new ErrorOccurred
                    {
                        Source = "PriceEngine",
                        Message = ex.Message,
                        Detail = ex.ToString(),
                        CorrelationId = cmd.CorrelationId
                    });
                }
            }
            catch (Exception ex)
            {
                _bus.Publish(new FX.Messages.Events.ErrorOccurred
                {
                    Source = "FxRuntime.HandleRequestPrice",
                    Message = ex.Message,
                    Detail = ex.ToString(),
                    CorrelationId = cmd?.CorrelationId ?? Guid.Empty
                });
            }
        }


        /// <summary>
        /// Liten hjälpare: hämta/bygg rd/rf för (pair6, legId) med feedern en gång.
        /// Anropa när du har today/spotDate/settlement från dina kalender-tjänster.
        /// </summary>
        public void EnsureRdRfOnce(string pair6, string legId, DateTime today, DateTime spotDate, DateTime settlement)
        {
            if (_marketStore == null) return;
            using (var feeder = new UsdAnchoredRateFeeder(_marketStore))
            {
                feeder.EnsureRdRfFor(pair6, legId, today, spotDate, settlement);
            }
        }

        private static BuySell ParseSide(string s)
        {
            var v = (s ?? "BUY").Trim().ToUpperInvariant();
            return v == "SELL" ? BuySell.Sell : BuySell.Buy;
        }

        private static OptionType ParseType(string s)
        {
            var v = (s ?? "CALL").Trim().ToUpperInvariant();
            return v == "PUT" ? OptionType.Put : OptionType.Call;
        }

        public void Dispose()
        {
            _cts.Cancel();
            for (int i = 0; i < _subscriptions.Count; i++) _subscriptions[i].Dispose();
            _subscriptions.Clear();
            _cts.Dispose();
        }
    }
}
