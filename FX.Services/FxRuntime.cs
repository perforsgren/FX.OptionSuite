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
        /// Flöde:
        /// 1) Normalisera indata och räkna datum (expiry via cmd, spot/settle via ISpotSetDateService).
        /// 2) Säkerställ RD/RF i MarketStore för aktuellt legId (hedrar ForceRefreshRates).
        /// 3) Läs Spot enligt Store (respektera ViewMode/Override) → SpotBid/SpotAsk (MID ⇒ bid=ask=mid).
        /// 4) Läs RD/RF enligt Store (respektera ViewMode/Override) → RdBid/RdAsk/RfBid/RfAsk (MID ⇒ bid=ask=mid).
        /// 5) Bygg PricingRequest med tvåvägs Spot + tvåvägs RD/RF, sätt Vol, kör motorn och publicera PriceCalculated.
        /// </summary>
        private async Task HandleRequestPriceWorkerAsync(RequestPrice cmd)
        {
            try
            {
                try
                {
                    // 0) Indata
                    string pair6 = (cmd.Pair6 ?? "EURSEK").Replace("/", "").ToUpperInvariant();
                    if (cmd.Legs == null || cmd.Legs.Count == 0)
                        throw new InvalidOperationException("Request saknar ben.");
                    var leg0 = cmd.Legs[0];
                    if (leg0.LegId == Guid.Empty)
                        throw new InvalidOperationException("LegId saknas i RequestPrice.Leg.");
                    string legId = leg0.LegId.ToString();

                    // Säkerställ att Store står på rätt par
                    var current = _marketStore.Current;
                    if (current == null || !string.Equals(current.Pair6, pair6, StringComparison.OrdinalIgnoreCase))
                        _marketStore.SetSpotFromFeed(pair6, new TwoWay<double>(0d, 0d), DateTime.UtcNow, true);

                    // 1) Datum
                    var today = DateTime.Today;
                    var expiry = today;
                    if (!string.IsNullOrWhiteSpace(leg0.ExpiryIso))
                        DateTime.TryParse(leg0.ExpiryIso, out expiry);

                    var dates = _spotSvc.Compute(pair6, today, expiry);
                    var spotDate = dates.SpotDate;
                    var settlement = dates.SettlementDate;

                    // 2) Säkerställ RD/RF (cache-först; endast forceRefresh triggar ny hämtning)
                    using (var feeder = new UsdAnchoredRateFeeder(_marketStore))
                    {
                        feeder.EnsureRdRfFor(pair6, legId, today, spotDate, settlement, /*forceRefresh:*/ cmd.ForceRefreshRates);
                    }


                    bool EnableSnapshotDump = false;
                    if (EnableSnapshotDump)
                    {
                        using (var feeder = new UsdAnchoredRateFeeder(_marketStore))
                            feeder.DumpCrossRfSnapshot(pair6.Substring(0, 3), pair6.Substring(3, 3), spotDate, settlement, true, false);
                    }

                    // 3) Spot enligt Store (respektera ViewMode/Override)
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


                    // 4) RD/RF enligt Store (respektera ViewMode/Override) → tvåväg
                    var rdFld = snap.TryGetRd(legId);
                    var rfFld = snap.TryGetRf(legId);
                    if (rdFld == null || rfFld == null)
                        throw new InvalidOperationException("RD/RF saknas i Store efter ensure.");

                    var rdEff = rdFld.Effective;
                    var rfEff = rfFld.Effective;

                    bool rdZero = Math.Abs(rdEff.Bid) <= 1e-15 && Math.Abs(rdEff.Ask) <= 1e-15;
                    bool rfZero = Math.Abs(rfEff.Bid) <= 1e-15 && Math.Abs(rfEff.Ask) <= 1e-15;
                    if (rdZero || rfZero)
                    {
                        using (var feeder = new UsdAnchoredRateFeeder(_marketStore))
                        {
                            feeder.EnsureRdRfFor(pair6, legId, today, spotDate, settlement, /*forceRefresh:*/ true);
                        }
                        snap = _marketStore.Current;
                        rdFld = snap.TryGetRd(legId); rfFld = snap.TryGetRf(legId);
                        if (rdFld == null || rfFld == null)
                            throw new InvalidOperationException("RD/RF saknas efter retry-ensure.");
                        rdEff = rdFld.Effective; rfEff = rfFld.Effective;

                        rdZero = Math.Abs(rdEff.Bid) <= 1e-15 && Math.Abs(rdEff.Ask) <= 1e-15;
                        rfZero = Math.Abs(rfEff.Bid) <= 1e-15 && Math.Abs(rfEff.Ask) <= 1e-15;
                        if (rdZero || rfZero)
                            throw new InvalidOperationException("RD/RF är 0/0 efter ensure (retry).");
                    }

                    // Bygg RD tvåväg med hänsyn till ViewMode/Override (MID ⇒ bid=ask=mid)
                    double rdBid, rdAsk;
                    {
                        double b = rdEff.Bid, a = rdEff.Ask, m = 0.5 * (b + a);
                        if (rdFld.ViewMode == ViewMode.Mid || rdFld.Override == OverrideMode.Mid) { rdBid = m; rdAsk = m; }
                        else { rdBid = b; rdAsk = a; }
                    }

                    // Bygg RF tvåväg med hänsyn till ViewMode/Override (MID ⇒ bid=ask=mid)
                    double rfBid, rfAsk;
                    {
                        double b = rfEff.Bid, a = rfEff.Ask, m = 0.5 * (b + a);
                        if (rfFld.ViewMode == ViewMode.Mid || rfFld.Override == OverrideMode.Mid) { rfBid = m; rfAsk = m; }
                        else { rfBid = b; rfAsk = a; }
                    }

                    //System.Diagnostics.Debug.WriteLine(
                    //    "[FxRuntime.Rates][T" + System.Threading.Thread.CurrentThread.ManagedThreadId + "] " +
                    //    "pair=" + pair6 + " legId=" + legId +
                    //    " rdEff=" + rdEff.Bid.ToString("F6") + "/" + rdEff.Ask.ToString("F6") +
                    //    " rfEff=" + rfEff.Bid.ToString("F6") + "/" + rfEff.Ask.ToString("F6") +
                    //    " rdUse=" + rdBid.ToString("F6") + "/" + rdAsk.ToString("F6") +
                    //    " rfUse=" + rfBid.ToString("F6") + "/" + rfAsk.ToString("F6"));


                    // 5) Prissättning
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

                            legs.Add(new OptionLeg(pair, sideEnum, typeEnum, strikeDom, domExpiry, src.Notional));
                        }
                    }

                    // PricingRequest – nu tvåvägs Spot + tvåvägs RD/RF
                    var domainReq = new PricingRequest(
                        pair: new CurrencyPair(pair6.Substring(0, 3), pair6.Substring(3, 3)),
                        legs: legs,
                        spotBid: spotBid,
                        spotAsk: spotAsk,
                        rdBid: rdBid,
                        rdAsk: rdAsk,
                        rfBid: rfBid,
                        rfAsk: rfAsk,
                        surfaceId: cmd.SurfaceId ?? "default",
                        stickyDelta: cmd.StickyDelta
                    );

                    // Vol (tvåvägsprocent)
                    double? bidDec = cmd.VolBidPct.HasValue ? (cmd.VolBidPct.Value / 100.0) : (double?)null;
                    double? askDec = cmd.VolAskPct.HasValue ? (cmd.VolAskPct.Value / 100.0) : (double?)null;
                    domainReq.SetVol(new VolQuote(bidDec, askDec));


                    // === DEBUG: radbruten sammanställning av den marknadsdata som används i prisningen ===
                    try
                    {
                        bool spotIsMid = (spotField.ViewMode == ViewMode.Mid || spotField.Override == OverrideMode.Mid);
                        bool rdIsMid = (rdFld.ViewMode == ViewMode.Mid || rdFld.Override == OverrideMode.Mid);
                        bool rfIsMid = (rfFld.ViewMode == ViewMode.Mid || rfFld.Override == OverrideMode.Mid);

                        string dbg = BuildMarketDebugBlock(
                            pair6,
                            legId,
                            spotIsMid, spotBid, spotAsk,
                            rdIsMid, rdBid, rdAsk,
                            rfIsMid, rfBid, rfAsk,
                            bidDec, askDec
                        );
                        System.Diagnostics.Debug.WriteLine(dbg);
                    }
                    catch { /* best-effort debug */ }


                    var unit = await _price.PriceAsync(domainReq, _cts.Token).ConfigureAwait(false);

                    string firstSide = ((cmd.Legs != null && cmd.Legs.Count > 0) ? cmd.Legs[0].Side : "BUY") ?? "BUY";
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

        /// <summary>
        /// Bygger en radbruten debug-text som visar vilken marknadsdata som faktiskt används i prisningen.
        /// Format:
        /// Spot:  singel [MID]  eller  bid / ask [FULL]
        /// RD/RF: singel [MID]  eller  bid / ask [FULL]   (värden i decimal, F6)
        /// Vol:   singel [MID]  eller  bid / ask [FULL]   (värden i %)
        /// </summary>
        private static string BuildMarketDebugBlock(
            string pair6, string legId,
            bool spotIsMid, double spotBid, double spotAsk,
            bool rdIsMid, double rdBid, double rdAsk,
            bool rfIsMid, double rfBid, double rfAsk,
            double? volBidDec, double? volAskDec)
        {
            var nl = Environment.NewLine;

            string spotLine = spotIsMid
                ? $"Spot:  {spotBid.ToString("F6")} [MID]"
                : $"Spot:  {spotBid.ToString("F6")} / {spotAsk.ToString("F6")} [FULL]";

            string rdLine = rdIsMid
                ? $"RD:    {rdBid.ToString("F6")} [MID]"
                : $"RD:    {rdBid.ToString("F6")} / {rdAsk.ToString("F6")} [FULL]";

            string rfLine = rfIsMid
                ? $"RF:    {rfBid.ToString("F6")} [MID]"
                : $"RF:    {rfBid.ToString("F6")} / {rfAsk.ToString("F6")} [FULL]";

            string volLine;
            if (volBidDec.HasValue && volAskDec.HasValue && Math.Abs(volBidDec.Value - volAskDec.Value) > 1e-12)
            {
                volLine = $"Vol:   {ToPct(volBidDec.Value)} / {ToPct(volAskDec.Value)} [FULL]";
            }
            else
            {
                // om bara en sida finns, eller lika ⇒ behandla som MID
                double mid = 0.0;
                if (volBidDec.HasValue && volAskDec.HasValue)
                    mid = 0.5 * (volBidDec.Value + volAskDec.Value);
                else if (volBidDec.HasValue) mid = volBidDec.Value;
                else if (volAskDec.HasValue) mid = volAskDec.Value;

                volLine = (volBidDec.HasValue || volAskDec.HasValue)
                    ? $"Vol:   {ToPct(mid)} [MID]"
                    : "Vol:   –";
            }

            return "[FxRuntime.MarketUsed]" + nl +
                   $"pair={pair6} leg={legId}" + nl +
                   spotLine + nl + rdLine + nl + rfLine + nl + volLine;
        }

        /// <summary>
        /// Formatterar decimal (0.0123) till procentsats med 4 d.p. → "1.2300%".
        /// </summary>
        private static string ToPct(double x)
        {
            return (x * 100.0).ToString("F4") + "%";
        }

    }
}
