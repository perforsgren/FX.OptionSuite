using System;
using System.Collections.Generic;

namespace FX.Core.Domain
{
    public sealed partial class PricingRequest
    {
        public CurrencyPair Pair { get; }
        public IReadOnlyList<OptionLeg> Legs { get; }

        public double SpotBid { get; }
        public double SpotAsk { get; }

        public double RdAsk { get; }
        public double RdBid { get; }

        public double RfAsk { get; }
        public double RfBid { get; }

        public string SurfaceId { get; }
        public bool StickyDelta { get; }

        /// <summary>
        /// Tvåvägsvol i DECIMAL (0.05 = 5%). Minst en sida måste finnas.
        /// Om endast en sida finns används den som mid.
        /// </summary>
        public VolQuote Vol { get; private set; }

        // Primär ctor: Spot och RD/RF som tvåväg (bid/ask).
        public PricingRequest(
            CurrencyPair pair,
            IReadOnlyList<OptionLeg> legs,
            double spotBid,
            double spotAsk,
            double rdBid,
            double rdAsk,
            double rfBid,
            double rfAsk,
            string surfaceId,
            bool stickyDelta)
        {
            if (pair == null) throw new ArgumentNullException(nameof(pair));
            if (legs == null) throw new ArgumentNullException(nameof(legs));

            Pair = pair;
            Legs = legs;
            SpotBid = spotBid;
            SpotAsk = spotAsk;
            RdBid = rdBid;
            RdAsk = rdAsk;
            RfBid = rfBid;
            RfAsk = rfAsk;
            SurfaceId = surfaceId;
            StickyDelta = stickyDelta;
        }

        /// <summary>
        /// Överlagrad ctor: tvåvägs Spot + tvåvägs RD/RF + Vol.
        /// </summary>
        public PricingRequest(
            CurrencyPair pair,
            IReadOnlyList<OptionLeg> legs,
            double spotBid,
            double spotAsk,
            double rdBid,
            double rdAsk,
            double rfBid,
            double rfAsk,
            string surfaceId,
            bool stickyDelta,
            VolQuote vol)
            : this(pair, legs, spotBid, spotAsk, rdBid, rdAsk, rfBid, rfAsk, surfaceId, stickyDelta)
        {
            SetVol(vol);
        }

        /// <summary>Sätt/uppdatera tvåvägsvol (valideras av VolQuote).</summary>
        public void SetVol(VolQuote vol)
        {
            if (vol == null) throw new ArgumentNullException(nameof(vol));
            Vol = vol;
        }
    }
}
