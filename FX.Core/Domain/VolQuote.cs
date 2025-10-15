using System;

namespace FX.Core.Domain
{
    /// <summary>
    /// Tvåvägsvol i decimal (0.05 = 5%). Minst en sida måste finnas.
    /// Bid <= Ask måste gälla om båda finns.
    /// </summary>
    public sealed class VolQuote
    {
        public double? Bid { get; }
        public double? Ask { get; }

        public VolQuote(double? bid, double? ask)
        {
            if (!bid.HasValue && !ask.HasValue)
                throw new ArgumentException("VolQuote: minst en av Bid/Ask måste anges.");
            if (bid.HasValue && ask.HasValue && bid.Value > ask.Value)
                throw new ArgumentException("VolQuote: Bid måste vara <= Ask.");

            Bid = bid;
            Ask = ask;
        }

        public bool HasTwoSided => Bid.HasValue && Ask.HasValue;

        /// <summary>
        /// Mid = (Bid+Ask)/2 när båda finns; annars den sida som finns.
        /// </summary>
        public double Mid
        {
            get
            {
                if (Bid.HasValue && Ask.HasValue) return (Bid.Value + Ask.Value) / 2.0;
                if (Bid.HasValue) return Bid.Value;
                if (Ask.HasValue) return Ask.Value;
                return 0.0; // når ej hit pga ctor-vakt
            }
        }
    }
}
