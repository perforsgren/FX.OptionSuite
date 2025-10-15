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


        /// <summary>
        /// Huvudhandler för prisförfrågningar. Variant 1:
        /// 1) Kör PricingOrchestrator.Build(...) först (autofetch + cache) så rd/rf säkerställs i MarketStore.
        /// 2) Läs tillbaka rd/rf (effektiva, med overrides) ur MarketStore (TwoWay<double>? → HasValue/Value).
        /// 3) Kör befintliga motorn (_price.PriceAsync) så premien beräknas på samma rd/rf.
        /// 4) Publicera PriceCalculated (oförändrat).
        /// </summary>
        private async void HandleRequestPrice(RequestPrice cmd)
        {
            try
            {
                // === 0) Normalize pair & enkla defaults ===
                string pair6 = (cmd.Pair6 ?? "EURSEK").Replace("/", "").ToUpperInvariant();
                string legId = "A"; // nuvarande iteration använder ett leg-id; utöka vid behov

                // Säkerställ att store är på rätt par innan Build
                var current = _marketStore.Current;
                if (current == null || !string.Equals(current.Pair6, pair6, StringComparison.OrdinalIgnoreCase))
                {
                    // Byt par på ett "neutralt" sätt: lägg in en stämplad, stale spot = 0/0
                    // (Bytet triggar Changed så UI kan uppdatera rubriker/viewmode korrekt.)
                    _marketStore.SetSpotFromFeed(
                        pair6,
                        new TwoWay<double>(0d, 0d),
                        DateTime.UtcNow,
                        isStale: true
                    );
                }

                // === 1) Bestäm datum ===
                var src1 = cmd.Legs[0];
                var today = DateTime.Today;
                var expiry = DateTime.Today;
                if (!string.IsNullOrWhiteSpace(src1.ExpiryIso))
                    DateTime.TryParse(src1.ExpiryIso, out expiry);
                //var expiry = …; // som du redan plockar från cmd

                // Kalla din legacy-tjänst för datum, precis som i adaptern
                var dates = _spotSvc.Compute(pair6, today, expiry);
                var spotDate = dates.SpotDate;
                var settlement = dates.SettlementDate;


                // === 2) Kör Orchestrator.Build(...) först (autofetch via feeder om rd/rf saknas) ===
                var orchestrator = FX.Services.MarketData.OrchestratorFactory.Create(_marketStore);
                var build = orchestrator.Build(pair6, legId, today, expiry, spotDate, settlement, useMid: false);

                // === 3) Läs tillbaka rd/rf (effektiva) ur MarketStore så premien räknas på samma nivåer ===
                var snap = _marketStore.Current;

                // TwoWay<double>? eftersom ?.Effective på en struct ger Nullable<T> i C# 7.3
                var rdEff = snap.TryGetRd(legId)?.Effective; // TwoWay<double>?
                var rfEff = snap.TryGetRf(legId)?.Effective; // TwoWay<double>?

                // === 4) Spot (bid/ask) – använd overrides om de skickades, annars feed från MarketStore.Spot ===
                double feedSpotMid = snap?.Spot != null ? 0.5 * (snap.Spot.Effective.Bid + snap.Spot.Effective.Ask) : 0.0;
                double spotBid = cmd.SpotBidOverride ?? cmd.SpotOverride ?? (snap?.Spot?.Effective.Bid ?? feedSpotMid);
                double spotAsk = cmd.SpotAskOverride ?? cmd.SpotOverride ?? (snap?.Spot?.Effective.Ask ?? feedSpotMid);

                // === 5) rd/rf att stoppa in i motorns PricingRequest ===
                // FIX: rdEff/rfEff är Nullable → kontrollera HasValue och använd .Value.Bid/.Value.Ask
                double rdMid = rdEff.HasValue ? 0.5 * (rdEff.Value.Bid + rdEff.Value.Ask) : 0.02;
                double rfMid = rfEff.HasValue ? 0.5 * (rfEff.Value.Bid + rfEff.Value.Ask) : 0.01;

                double rd = cmd.RdOverride ?? rdMid;
                double rf = cmd.RfOverride ?? rfMid;

                // === 6) Mappa legs och bygg domänens PricingRequest (oförändrat från din baseline) ===
                var legs = new System.Collections.Generic.List<OptionLeg>();
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

                // === 7) Kör din befintliga motor (premium) – nu på samma rd/rf som i MarketStore ===
                var unit = await _price.PriceAsync(domainReq, _cts.Token).ConfigureAwait(false);

                // === 8) Välj sida för per-unit premium (BUY→Ask, SELL→Bid; fallback Mid i engine) ===
                string firstSide = (cmd.Legs != null && cmd.Legs.Count > 0 ? cmd.Legs[0].Side : "BUY")
                                   ?.ToUpperInvariant() ?? "BUY";
                bool isBuy = string.Equals(firstSide, "BUY", StringComparison.OrdinalIgnoreCase);

                double pricePerUnit = isBuy ? unit.PremiumAsk : unit.PremiumBid;
                string boldSide = (unit.PremiumBid == unit.PremiumAsk)
                                    ? "MID"
                                    : (isBuy ? "ASK" : "BID");

                // === 9) Publicera resultat (oförändrat) ===
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
