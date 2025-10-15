using System;

namespace FX.Core.Domain.MarketData
{
    /// <summary>
    /// Per-leg behållare för räntor:
    /// - Rd (domestic/quote-ccy) och Rf (foreign/base-ccy) som MarketField<double>.
    /// - Endast data/metadata; ingen merge/regel här.
    /// - Immutable på referensnivå (fälten har privata set; själv MarketField har Replace/MarkStale).
    /// </summary>
    public sealed class LegRates
    {
        /// <summary>Inhemsk ränta (quote-ccy) för benet.</summary>
        public MarketField<double> Rd { get; }

        /// <summary>Utländsk ränta (base-ccy) för benet.</summary>
        public MarketField<double> Rf { get; }

        public LegRates(MarketField<double> rd, MarketField<double> rf)
        {
            Rd = rd ?? throw new ArgumentNullException(nameof(rd));
            Rf = rf ?? throw new ArgumentNullException(nameof(rf));
        }
    }
}
