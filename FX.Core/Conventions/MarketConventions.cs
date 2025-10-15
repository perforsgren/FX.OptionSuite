using FX.Core.Domain;

namespace FX.Core.Conventions
{
    /// <summary>Håller standardval i projektet (kan göras konfigurerbart senare).</summary>
    public static class MarketConventions
    {
        public static AtmConvention DefaultAtmConvention = AtmConvention.AtmForward;
        public static DeltaConvention DefaultDeltaConvention = DeltaConvention.Forward;
    }
}
