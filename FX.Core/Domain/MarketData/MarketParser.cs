using System;
using System.Globalization;

namespace FX.Core.Domain.MarketData
{
    /// <summary>
    /// Parser/normaliserare för UI-inmatning till two-way.
    /// Regler:
    /// - Ensam siffra = mid → bid=ask=mid.
    /// - Two-way separeras av '/' eller whitespace (mellanslag/tab).
    /// - Kommatecken tolkas som decimalsymbol (ersätts med '.').
    /// - Om ask < bid → autoswap (debug-logg), ingen exception.
    /// </summary>
    public static class MarketParser
    {
        /// <summary>
        /// Parsar string till two-way (double) och flaggar om det var mid.
        /// Kastar FormatException vid icke-numeriskt innehåll.
        /// </summary>
        public static TwoWay<double> ParseToTwoWay(string raw, out bool wasMid)
        {
            if (raw == null) throw new ArgumentNullException(nameof(raw));

            // 1) Trim + normalisera decimalsymbol: ',' -> '.'
            var s = raw.Trim();
            s = s.Replace(',', '.');

            // 2) Mid: inga two-way-separatorer ('/' eller whitespace) hittades
            var hasSlash = s.IndexOf('/') >= 0;
            var hasWhitespace = HasAnyWhitespace(s);

            if (!hasSlash && !hasWhitespace)
            {
                double mid;
                if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out mid))
                    throw new FormatException($"Kan inte tolka '{raw}' som tal.");
                wasMid = true;
                return new TwoWay<double>(mid, mid);
            }

            // 3) Two-way: välj separator – '/' vinner om den finns, annars whitespace
            string[] parts;
            if (hasSlash)
            {
                parts = s.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            }
            else
            {
                // Split på all whitespace (mellanslag, tabbar etc.)
                parts = SplitOnWhitespace(s);
            }

            if (parts == null || parts.Length != 2)
                throw new FormatException($"Tvåvärdesformat förväntas (bid/ask). Fick '{raw}'.");

            double bid, ask;
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out bid))
                throw new FormatException($"Kan inte tolka bid i '{raw}'.");
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out ask))
                throw new FormatException($"Kan inte tolka ask i '{raw}'.");

            // 4) Monotoni-säkerhet
            if (ask < bid)
            {
                var t = bid; bid = ask; ask = t;
                System.Diagnostics.Debug.WriteLine($"[MarketParser] ask<bid → autoswap: {raw}");
            }

            wasMid = false;
            return new TwoWay<double>(bid, ask);
        }

        private static bool HasAnyWhitespace(string s)
        {
            for (int i = 0; i < s.Length; i++)
                if (char.IsWhiteSpace(s[i])) return true;
            return false;
        }

        private static string[] SplitOnWhitespace(string s)
        {
            // Splitta på följder av whitespace utan att behöva Regex i onödan.
            // (Equivalent till string.Split(null, RemoveEmptyEntries) men explicit.)
            System.Collections.Generic.List<string> parts = new System.Collections.Generic.List<string>(2);
            int i = 0;
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            int start = i;
            for (; i < s.Length; i++)
            {
                if (char.IsWhiteSpace(s[i]))
                {
                    if (i > start) parts.Add(s.Substring(start, i - start));
                    while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                    start = i;
                }
            }
            if (i > start) parts.Add(s.Substring(start, i - start));
            return parts.ToArray();
        }
    }
}
