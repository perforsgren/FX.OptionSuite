using System;

namespace FX.Core.Domain.MarketData
{
    /// <summary>
    /// Minimal payload till prismotorn med enbart nödvändiga numeriska fält.
    /// Hålls enkel med bara bid/ask för Spot/rd/rf (mid skickas inte till motorn).
    /// </summary>
    public sealed class EngineRatesPayload
    {
        public string Pair6 { get; set; }            // "EURSEK"
        public DateTime Today { get; set; }
        public DateTime Expiry { get; set; }
        public DateTime SpotDate { get; set; }
        public DateTime Settlement { get; set; }

        public double SpotBid { get; set; }
        public double SpotAsk { get; set; }

        public double RdBid { get; set; }           // domestic (quote ccy)
        public double RdAsk { get; set; }

        public double RfBid { get; set; }           // foreign  (base ccy)
        public double RfAsk { get; set; }

        public LockMode LockMode { get; set; }      // skickas med för spårbarhet/loggning
    }

    /// <summary>
    /// Ett litet paket för UI så vi kan visa vad som faktiskt användes/härleddes.
    /// </summary>
    public sealed class UiDerivedView
    {
        // Forward & Swaps
        public double? ForwardBid { get; set; }
        public double? ForwardAsk { get; set; }
        public double? ForwardMid { get; set; }

        public double? SwapBid { get; set; }
        public double? SwapAsk { get; set; }
        public double? SwapMid { get; set; }

        // Expiry-DFs (för diskontering av premie/greker)
        public double? DFdExpiryBid { get; set; }
        public double? DFdExpiryAsk { get; set; }
        public double? DFdExpiryMid { get; set; }
        public double? DFfExpiryBid { get; set; }
        public double? DFfExpiryAsk { get; set; }
        public double? DFfExpiryMid { get; set; }
    }

    /// <summary>
    /// Samlar ihop motordata och UI-derivat i ett resultat.
    /// </summary>
    public sealed class PricingBuildResult
    {
        public EngineRatesPayload EnginePayload { get; set; }
        public UiDerivedView UiView { get; set; }
    }

    /// <summary>
    /// Adapter som tar MarketInputs (+ datum & par), normaliserar till sided (ev. mid-läge),
    /// kör legacy-kalkyl och returnerar:
    ///  - EngineRatesPayload (rd/rf/spot i bid/ask till prismotorn)
    ///  - UiDerivedView (forward/swaps/expiry-DFs för visning och spårbarhet).
    /// 
    /// Inga externa beroenden; all matte sker i FxCurveCalculator (legacy default).
    /// </summary>
    public sealed class MarketPricingAdapter
    {
        private readonly FxCurveCalculator _calc = new FxCurveCalculator();

        /// <summary>
        /// Bygger en motorklar payload + UI-derivat från råa inputs.
        /// </summary>
        /// <param name="pair6">Valutapar, t.ex. "EURSEK". (Base=foreign, Quote=domestic)</param>
        /// <param name="today">Dagens datum.</param>
        /// <param name="expiry">Optionens förfallodag.</param>
        /// <param name="spotDate">Spot date.</param>
        /// <param name="settlement">Settlement-datum för hedge/forward.</param>
        /// <param name="raw">Rå inputs (UI/feed). Kan innehålla Mid/Spread och IsOverride.</param>
        /// <param name="useMid">True → tvinga bid=ask=mid på Spot/rd/rf inför prissättning.</param>
        public PricingBuildResult BuildEnginePayload(
            string pair6,
            DateTime today,
            DateTime expiry,
            DateTime spotDate,
            DateTime settlement,
            MarketInputs raw,
            bool useMid)
        {
            if (raw == null) throw new ArgumentNullException(nameof(raw));

            // 1) Normalisera till "effective" sided (eller mid-forced) + validera monotoni
            var eff = raw.ToEffectiveSided(useMid);

            // 2) Build legacy-kurva (DF exp med MM 360/365; expiry-DFs + forward-DFs)
            var curve = _calc.BuildLegacyExp(pair6, today, expiry, spotDate, settlement, eff);

            // 3) Packa “rena siffror” till motorn
            var engine = new EngineRatesPayload
            {
                Pair6 = pair6,
                Today = today,
                Expiry = expiry,
                SpotDate = spotDate,
                Settlement = settlement,

                SpotBid = eff.Spot.Bid.Value,
                SpotAsk = eff.Spot.Ask.Value,

                RdBid = eff.Rd.Bid.Value,
                RdAsk = eff.Rd.Ask.Value,

                RfBid = eff.Rf.Bid.Value,
                RfAsk = eff.Rf.Ask.Value,

                LockMode = eff.LockMode
            };

            // 4) UI-derivat som speglar vad som faktiskt användes
            var ui = new UiDerivedView
            {
                ForwardBid = curve.F_bid,
                ForwardAsk = curve.F_ask,
                ForwardMid = curve.F_mid,

                SwapBid = curve.Swap_bid,
                SwapAsk = curve.Swap_ask,
                SwapMid = curve.Swap_mid,

                DFdExpiryBid = curve.DF_d_bid,
                DFdExpiryAsk = curve.DF_d_ask,
                DFdExpiryMid = curve.DF_d_mid,

                DFfExpiryBid = curve.DF_f_bid,
                DFfExpiryAsk = curve.DF_f_ask,
                DFfExpiryMid = curve.DF_f_mid
            };

            return new PricingBuildResult
            {
                EnginePayload = engine,
                UiView = ui
            };
        }
    }
}
