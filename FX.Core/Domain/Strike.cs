using System;
using System.Globalization;

namespace FX.Core.Domain
{
    public sealed class Strike
    {
        public decimal? Absolute { get; }
        public double? Delta { get; }   // 0..100 för "25D" etc.

        public bool IsAbsolute => Absolute.HasValue;
        public bool IsDelta => Delta.HasValue;

        public Strike(decimal absolute)
        {
            Absolute = absolute;
            Delta = null;
        }

        public Strike(double deltaPercent)
        {
            if (deltaPercent <= 0 || deltaPercent >= 100)
                throw new ArgumentOutOfRangeException(nameof(deltaPercent), "Delta i procent (0..100).");
            Absolute = null;
            Delta = deltaPercent;
        }

        // --- NYTT: strikt parsning från UI/extern källa ---
        public static Strike ParseStrict(string raw, decimal spot)
        {
            var s = (raw ?? "").Trim();

            // Tomt eller ATM => bind strike till spot (absolut)
            if (s.Length == 0 || s.Equals("ATM", StringComparison.OrdinalIgnoreCase))
                return new Strike(spot);

            // Delta ENDAST om 'D' uttryckligen skrivs
            if (s.EndsWith("D", StringComparison.OrdinalIgnoreCase))
            {
                var num = s.Substring(0, s.Length - 1).Trim()
                           .Replace("\u00A0", " ").Replace(" ", "").Replace(',', '.');

                if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var dPct))
                    throw new FormatException($"Ogiltig delta-strike: '{raw}'");
                return new Strike(dPct); // valideras i konstruktorn (0..100)
            }

            // Annars: absolut strike
            var absTxt = s.Replace("\u00A0", " ").Replace(" ", "").Replace(',', '.');
            if (!decimal.TryParse(absTxt, NumberStyles.Float, CultureInfo.InvariantCulture, out var abs))
                throw new FormatException($"Ogiltig absolut strike: '{raw}'");
            return new Strike(abs);
        }

        public override string ToString()
        {
            if (IsAbsolute) return Absolute.Value.ToString("0.########", CultureInfo.InvariantCulture);
            if (IsDelta) return Delta.Value.ToString("0.##", CultureInfo.InvariantCulture) + "D";
            return "?";
        }
    }
}
