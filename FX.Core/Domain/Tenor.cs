using System;

namespace FX.Core.Domain
{
    /// <summary>Tenor som 1W, 2W, 1M, 3M, 1Y. Enkel datumframmatning (inga helg-/bankdagsregler här).</summary>
    public sealed class Tenor
    {
        public int Value { get; }
        public char Unit { get; } // 'D','W','M','Y'

        public Tenor(int value, char unit)
        {
            if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
            Unit = char.ToUpperInvariant(unit);
            if (Unit != 'D' && Unit != 'W' && Unit != 'M' && Unit != 'Y')
                throw new ArgumentException("Enhet måste vara D, W, M eller Y.");
            Value = value;
        }

        public static Tenor Parse(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) throw new ArgumentNullException(nameof(s));
            s = s.Trim().ToUpperInvariant();
            if (s.Length < 2) throw new ArgumentException("Ogiltig tenor.");
            var unit = s[s.Length - 1];
            var numPart = s.Substring(0, s.Length - 1);
            int val;
            if (!int.TryParse(numPart, out val)) throw new ArgumentException("Ogiltig tenor.");
            return new Tenor(val, unit);
        }

        public DateTime AddTo(DateTime start)
        {
            // Enkel logik: inga helgdagar/swap-konventioner i steg 2.
            if (Unit == 'D') return start.AddDays(Value);
            if (Unit == 'W') return start.AddDays(7 * Value);
            if (Unit == 'M') return start.AddMonths(Value);
            return start.AddYears(Value); // 'Y'
        }

        public override string ToString() => Value.ToString() + Unit;
    }
}
