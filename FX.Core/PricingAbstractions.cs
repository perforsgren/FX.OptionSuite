// FX.Core/PricingAbstractions.cs
using System;

namespace FX.Core
{


    /// <summary>Resultat av spot/settlement-beräkning för ett FX-par.</summary>
    public sealed class SpotSetDates
    {
        public System.DateTime SpotDate { get; set; }
        public System.DateTime SettlementDate { get; set; }
        public int SpotLagBusinessDays { get; set; }
    }

    public sealed class VolSurface
    {
        // Fylls på senare
    }

}
