using System;

namespace FX.Core.Domain
{
    /// <summary>Ett ben i en FX-optionsstruktur.</summary>
    public sealed class OptionLeg
    {
        public CurrencyPair Pair { get; }
        public BuySell Side { get; }
        public OptionType Type { get; }
        public Strike Strike { get; }
        public Expiry Expiry { get; }
        public double Notional { get; }

        public OptionLeg(CurrencyPair pair, BuySell side, OptionType type, Strike strike, Expiry expiry, double notional)
        {
            Pair = pair ?? throw new ArgumentNullException(nameof(pair));
            Side = side;
            Type = type;
            Strike = strike ?? throw new ArgumentNullException(nameof(strike));
            Expiry = expiry ?? throw new ArgumentNullException(nameof(expiry));
            Notional = notional;
        }

        public override string ToString()
        {
            return string.Format("{0} {1} {2} {3} {4}", Pair, Side, Type, Strike, Expiry);
        }
    }
}
