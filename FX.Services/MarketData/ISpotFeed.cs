using System;

namespace FX.Services.MarketData
{
    /// <summary>
    /// Abstraktion för snapshot-feed av FX spot.
    /// Returnerar tvåvägspris (bid/ask). Mid kan härledas som (bid+ask)/2.
    /// </summary>
    public interface ISpotFeed
    {
        /// <summary>
        /// Försöker hämta tvåvägs spot för ett valutapar (t.ex. "EURSEK").
        /// Returnerar true om lyckat; bid/ask > 0 vid framgång.
        /// </summary>
        bool TryGetTwoWay(string pair6, out double bid, out double ask);
    }
}
