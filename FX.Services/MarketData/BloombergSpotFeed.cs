// FX.Services/MarketData/BloombergSpotFeed.cs
// C# 7.3
using System;

namespace FX.Services.MarketData
{
    /// <summary>
    /// Adapter mot BloombergStaticData för snapshot av spot, tvåvägs (bid/ask).
    /// Källan arbetar i decimal; adaptern castar till double.
    /// RUNDAR alltid till 4 d.p. (feed-policy) innan värdet lämnar adaptern.
    /// </summary>
    public sealed class BloombergSpotFeed : ISpotFeed
    {
        private readonly int _timeoutMs;

        /// <summary>
        /// Skapar en adapter. Timeout används av underliggande Bloomberg-anrop.
        /// </summary>
        public BloombergSpotFeed(int timeoutMs)
        {
            _timeoutMs = timeoutMs > 0 ? timeoutMs : 3000;
        }

        /// <summary>
        /// Försöker hämta tvåvägs spot för ett valutapar (t.ex. "EURSEK" / "EUR/SEK").
        /// Returnerar true om minst en sida > 0.0.
        /// Feed-policy: bid/ask är alltid avrundade till 4 d.p. (AwayFromZero).
        /// </summary>
        public bool TryGetTwoWay(string pair6, out double bid, out double ask)
        {
            bid = 0.0; ask = 0.0;

            var p = NormalizePair6(pair6);
            if (string.IsNullOrEmpty(p)) return false;

            try
            {
                decimal dbid, dask;
                if (!BloombergStaticData.TryGetFxSpotTwoWay(p, out dbid, out dask, _timeoutMs))
                    return false;

                // cast till double för resten av appen
                bid = (double)dbid;
                ask = (double)dask;

                return (bid > 0.0) || (ask > 0.0);
            }
            catch
            {
                // Inga exceptions utåt
                bid = 0.0; ask = 0.0;
                return false;
            }
        }

        private static string NormalizePair6(string pair)
        {
            if (string.IsNullOrWhiteSpace(pair)) return null;
            var s = pair.Trim().Replace("/", "").Replace("\\", "").ToUpperInvariant();
            return (s.Length >= 6) ? s.Substring(0, 6) : null;
        }
    }
}
