using System;

namespace FX.Core.Domain
{
    /// <summary>
    /// Representerar en rad i en volyta för en specifik tenor (som lagras i vol_surface_expiry).
    /// Alla volatiliteter är absoluta (t.ex. 0.115000 = 11.5%).
    /// RR/BF lagras på mid enligt vår modell.
    /// </summary>
    public sealed class VolSurfaceRow
    {
        /// <summary>Tenor-kod, t.ex. "ON", "1W", "1M", ...</summary>
        public string TenorCode { get; set; }

        /// <summary>
        /// Nominalt antal dagar för sortering/interpolation (kan vara null om ej satt).
        /// Används inte för dag-/eventjustering i prissättningen.
        /// </summary>
        public int? TenorDaysNominal { get; set; }

        /// <summary>ATM bid volatilitet.</summary>
        public decimal? AtmBid { get; set; }

        /// <summary>ATM ask volatilitet.</summary>
        public decimal? AtmAsk { get; set; }

        /// <summary>ATM mid volatilitet (kan vara beräknad kolumn i DB).</summary>
        public decimal? AtmMid { get; set; }

        /// <summary>25D Risk Reversal (mid).</summary>
        public decimal? Rr25Mid { get; set; }

        /// <summary>25D Butterfly (mid).</summary>
        public decimal? Bf25Mid { get; set; }

        /// <summary>10D Risk Reversal (mid).</summary>
        public decimal? Rr10Mid { get; set; }

        /// <summary>10D Butterfly (mid).</summary>
        public decimal? Bf10Mid { get; set; }
    }
}
