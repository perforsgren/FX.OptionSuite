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

        //public double Rd { get; }
        //public double Rf { get; }

        public double RdAsk { get; }
        public double RdBid { get; }

        public double RfAsk { get; }
        public double RfBid { get; }



        /// <summary>Komfort: RD_mid = (RdBid+RdAsk)/2.</summary>
        //public double Rd => 0.5 * (RdBid + RdAsk);
        /// <summary>Komfort: RF_mid = (RfBid+RfAsk)/2.</summary>
        //public double Rf => 0.5 * (RfBid + RfAsk);




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

        /// <summary>
        /// Övergångs-ctor (bakåtkompatibel): Spot som bid=ask=mid, RD/RF som mid.
        /// - Användbar tills alla anrop skickar tvåväg.
        /// </summary>
        public PricingRequest(
            CurrencyPair pair,
            IReadOnlyList<OptionLeg> legs,
            double spotMid,
            double rdMid,
            double rfMid,
            string surfaceId,
            bool stickyDelta)
            : this(pair, legs,
                   spotMid, spotMid,  // Spot bid/ask = mid
                   rdMid, rdMid,     // RD   bid/ask = mid
                   rfMid, rfMid,     // RF   bid/ask = mid
                   surfaceId, stickyDelta)
        { }

        /// <summary>Sätt/uppdatera tvåvägsvol (valideras av VolQuote).</summary>
        public void SetVol(VolQuote vol)
        {
            if (vol == null) throw new ArgumentNullException(nameof(vol));
            Vol = vol;
        }
    }
}



//using System;
//using System.Collections.Generic;

//namespace FX.Core.Domain
//{
//    public sealed partial class PricingRequest
//    {
//        public CurrencyPair Pair { get; }
//        public IReadOnlyList<OptionLeg> Legs { get; }

//        public double SpotBid { get; }
//        public double SpotAsk { get; }

//        public double Rd { get; }
//        public double Rf { get; }

//        public double RdAsk { get; }
//        public double RdBid { get; }

//        public double RfAsk { get; }
//        public double RfBid { get; }

//        public string SurfaceId { get; }
//        public bool StickyDelta { get; }

//        /// <summary>
//        /// Tvåvägsvol i DECIMAL (0.05 = 5%). Minst en sida måste finnas.
//        /// Om endast en sida finns används den som mid.
//        /// </summary>
//        public VolQuote Vol { get; private set; }

//        // Primär ctor: spot som Bid/Ask
//        public PricingRequest(
//            CurrencyPair pair,
//            IReadOnlyList<OptionLeg> legs,
//            double spotBid,
//            double spotAsk,
//            double rd,
//            double rf,
//            string surfaceId,
//            bool stickyDelta)
//        {
//            Pair = pair;
//            Legs = legs;
//            SpotBid = spotBid;
//            SpotAsk = spotAsk;
//            Rd = rd;
//            Rf = rf;
//            SurfaceId = surfaceId;
//            StickyDelta = stickyDelta;
//        }

//        /// <summary>
//        /// Överlagrad ctor: samma som ovan men med Vol direkt.
//        /// </summary>
//        public PricingRequest(
//            CurrencyPair pair,
//            IReadOnlyList<OptionLeg> legs,
//            double spotBid,
//            double spotAsk,
//            double rd,
//            double rf,
//            string surfaceId,
//            bool stickyDelta,
//            VolQuote vol)
//            : this(pair, legs, spotBid, spotAsk, rd, rf, surfaceId, stickyDelta)
//        {
//            SetVol(vol);
//        }

//        /// <summary>
//        /// Övergångs-ctor: ta emot ett midvärde och sätt Bid=Ask=mid.
//        /// Användbar tills alla anrop skickar tvåväg.
//        /// </summary>
//        public PricingRequest(
//            CurrencyPair pair,
//            IReadOnlyList<OptionLeg> legs,
//            double spot,
//            double rd,
//            double rf,
//            string surfaceId,
//            bool stickyDelta)
//            : this(pair, legs, spot, spot, rd, rf, surfaceId, stickyDelta)
//        { }

//        /// <summary>Sätt/uppdatera tvåvägsvol (valideras av VolQuote).</summary>
//        public void SetVol(VolQuote vol)
//        {
//            if (vol == null) throw new ArgumentNullException(nameof(vol));
//            Vol = vol;
//        }
//    }
//}
