// FX.Services/MarketData/PricingOrchestrator.cs
// C# 7.3
using System;
using FX.Core.Domain.MarketData;

namespace FX.Services.MarketData
{
    /// <summary>
    /// Tunn orchestrator mellan MarketStore (read model) och prismotor/adapter.
    /// - Läser Spot/rd/rf ur MarketStore.Current för ett par + leg.
    /// - Bygger MarketInputs och kör MarketPricingAdapter (legacy-beräkningar).
    /// - Har en valfri callback (_ensureRdRf) som kan anropas om rd/rf saknas.
    /// </summary>
    public sealed class PricingOrchestrator
    {
        private readonly IMarketStore _store;
        private readonly MarketPricingAdapter _adapter = new MarketPricingAdapter();

        /// <summary>
        /// Valfri callback som hämtar/bygger rd/rf om de saknas.
        /// Signatur: (pair6, legId, today, spotDate, settlement) => void
        /// </summary>
        private readonly Action<string, string, DateTime, DateTime, DateTime> _ensureRdRf;

        /// <summary>
        /// Skapa orchestrator. Skicka in ensureRdRf om du vill att Build() ska
        /// försöka hämta rd/rf automatiskt när de saknas.
        /// </summary>
        public PricingOrchestrator(
            IMarketStore store,
            Action<string, string, DateTime, DateTime, DateTime> ensureRdRf = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _ensureRdRf = ensureRdRf; // kan vara null
        }

        /// <summary>
        /// Bygger motorklar payload + härledda visningsvärden (Forward/Swaps/DF) utifrån MarketStore.
        /// - Läser Spot/rd/rf från <see cref="_store"/>. 
        /// - Om rd/rf saknas och en callback finns (<see cref="_ensureRdRf"/>), anropas den för att hämta/bygga dem
        ///   (autofetch). Därefter läses snapshot om och bygget försöker igen.
        /// - Använder MarketPricingAdapter (legacy-metodik) för att skapa payload till motorn.
        /// 
        /// Parametrar:
        ///   pair6     : "EURSEK" etc. (utan snedstreck)
        ///   legId     : Leg-id (t.ex. "A")
        ///   today     : Valuation date (T0)
        ///   expiry    : Optionens förfallodatum
        ///   spotDate  : Spot-datum för paret (enligt dina konventioner)
        ///   settlement: Delivery/settlement (för forwardperioden)
        ///   useMid    : True ⇒ tvinga mid (bid=ask=mid) för Spot/rd/rf när inputs byggs
        /// 
        /// Kastar:
        ///   InvalidOperationException om pair6 inte matchar snapshot, eller om Spot/rd/rf fortfarande saknas efter autofetch.
        /// </summary>
        public PricingBuildResult Build(
            string pair6, string legId,
            DateTime today, DateTime expiry, DateTime spotDate, DateTime settlement,
            bool useMid)
        {
            // 1) Normalisera indata och hämta snapshot
            pair6 = (pair6 ?? "").Replace("/", "").ToUpperInvariant();
            var snap = _store.Current;

            if (!string.Equals(snap.Pair6, pair6, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Snapshot-paret matchar inte angivet pair6.");

            // 2) Läs fält (kan vara null första gången för rd/rf)
            var spotField = snap.Spot;
            var rdField = snap.TryGetRd(legId);
            var rfField = snap.TryGetRf(legId);

            // 3) Autofetch: om rd/rf saknas och vi har en callback – hämta/bygg en gång och läs om
            if ((rdField == null || rfField == null) && _ensureRdRf != null)
            {
                _ensureRdRf(pair6, legId, today, spotDate, settlement);

                // Läs om snapshot efter att feedern skrivit tillbaka rd/rf
                snap = _store.Current;
                rdField = snap.TryGetRd(legId);
                rfField = snap.TryGetRf(legId);
            }

            // 4) Slutlig validering
            if (spotField == null)
                throw new InvalidOperationException("Spot saknas i snapshot.");
            if (rdField == null)
                throw new InvalidOperationException($"rd saknas för leg '{legId}'.");
            if (rfField == null)
                throw new InvalidOperationException($"rf saknas för leg '{legId}'.");

            // 5) Mappa till sided quotes
            var spot = ToSided(spotField);
            var rd = ToSided(rdField);
            var rf = ToSided(rfField);

            // 6) Inputs till motorn
            var inputs = new MarketInputs
            {
                Spot = spot,
                Rd = rd,
                Rf = rf,
                LockMode = LockMode.HoldRd
            };

            // 7) Kör adapter – använd POSITIONELLA argument (inte namngivna)
            var result = _adapter.BuildEnginePayload(
                pair6,
                today,
                expiry,
                spotDate,
                settlement,
                inputs,
                useMid
            );

            // 8) Resultat
            return result;
        }

        /// <summary>
        /// Mappar MarketField&lt;double&gt; → SidedQuote (bid/ask/mid/spread + source/override-flag).
        /// </summary>
        private static SidedQuote ToSided(MarketField<double> f)
        {
            if (f == null) return null;
            var eff = f.Effective; // TwoWay<double>
            var mid = 0.5 * (eff.Bid + eff.Ask);

            return new SidedQuote
            {
                Bid = eff.Bid,
                Ask = eff.Ask,
                Mid = mid,
                Spread = eff.Ask - eff.Bid,
                Source = f.Source == MarketSource.User ? QuoteSource.User :
                         f.Source == MarketSource.Feed ? QuoteSource.Feed : QuoteSource.Unknown,
                IsOverride = f.Override == OverrideMode.Mid ||
                             f.Override == OverrideMode.Bid ||
                             f.Override == OverrideMode.Ask ||
                             f.Override == OverrideMode.Both
            };
        }
    }
}
