// ============================================================
// SPRINT 1 – STEG 3 (uppdaterad): Kontrakt & Meddelanden
// Spot som tvåväg (Bid/Ask) + SpotMid helper. I övrigt oförändrat.
// ============================================================
using System;
using System.Collections.Generic;

namespace FX.Core.Domain
{
    public sealed partial class PricingRequest
    {
        public CurrencyPair Pair { get; }
        public IReadOnlyList<OptionLeg> Legs { get; }

        // --- NYTT: tvåvägs-spot ---
        public double SpotBid { get; }
        public double SpotAsk { get; }
        //public double SpotMid => 0.5 * (SpotBid + SpotAsk);

        public double Rd { get; }
        public double Rf { get; }
        public string SurfaceId { get; }
        public bool StickyDelta { get; }

        /// <summary>
        /// Tvåvägsvol i DECIMAL (0.05 = 5%). Minst en sida måste finnas.
        /// Om endast en sida finns används den som mid.
        /// </summary>
        public VolQuote Vol { get; private set; }

        // Primär ctor: spot som Bid/Ask
        public PricingRequest(
            CurrencyPair pair,
            IReadOnlyList<OptionLeg> legs,
            double spotBid,
            double spotAsk,
            double rd,
            double rf,
            string surfaceId,
            bool stickyDelta)
        {
            Pair = pair;
            Legs = legs;
            SpotBid = spotBid;
            SpotAsk = spotAsk;
            Rd = rd;
            Rf = rf;
            SurfaceId = surfaceId;
            StickyDelta = stickyDelta;
        }

        /// <summary>
        /// Överlagrad ctor: samma som ovan men med Vol direkt.
        /// </summary>
        public PricingRequest(
            CurrencyPair pair,
            IReadOnlyList<OptionLeg> legs,
            double spotBid,
            double spotAsk,
            double rd,
            double rf,
            string surfaceId,
            bool stickyDelta,
            VolQuote vol)
            : this(pair, legs, spotBid, spotAsk, rd, rf, surfaceId, stickyDelta)
        {
            SetVol(vol);
        }

        /// <summary>
        /// Övergångs-ctor: ta emot ett midvärde och sätt Bid=Ask=mid.
        /// Användbar tills alla anrop skickar tvåväg.
        /// </summary>
        public PricingRequest(
            CurrencyPair pair,
            IReadOnlyList<OptionLeg> legs,
            double spot,
            double rd,
            double rf,
            string surfaceId,
            bool stickyDelta)
            : this(pair, legs, spot, spot, rd, rf, surfaceId, stickyDelta)
        { }

        /// <summary>Sätt/uppdatera tvåvägsvol (valideras av VolQuote).</summary>
        public void SetVol(VolQuote vol)
        {
            if (vol == null) throw new ArgumentNullException(nameof(vol));
            Vol = vol;
        }
    }
}
