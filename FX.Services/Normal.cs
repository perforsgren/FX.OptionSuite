// ============================================================
// SPRINT 1 – STEG 5: Normal CDF-hjälpare
// Varför:  Behöver N(d) för Black-Scholes/Garman-Kohlhagen.
// Vad:     Snabb approximation av standardnormal CDF.
// Klar när:PriceEngine kan räkna pris/greker.
// ============================================================
using System;

namespace FX.Services
{
    internal static class Normal
    {
        // Abramowitz-Stegun approximation
        public static double Cdf(double x)
        {
            var sign = x < 0 ? -1 : 1;
            x = Math.Abs(x) / Math.Sqrt(2.0);

            double t = 1.0 / (1.0 + 0.3275911 * x);
            double y = 1.0 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t
                               - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
            return 0.5 * (1.0 + sign * y);
        }
    }
}
