using System;
using FX.Core.Conventions;

namespace FX.Core.Domain
{
    /// <summary>Representerar ett valutapar, t.ex. EURSEK.</summary>
    public sealed class CurrencyPair
    {
        public string BaseCurrency { get; }
        public string QuoteCurrency { get; }
        public string Pair6 => BaseCurrency + QuoteCurrency;

        public CurrencyPair(string baseCurrency, string quoteCurrency)
        {
            if (string.IsNullOrWhiteSpace(baseCurrency)) throw new ArgumentNullException(nameof(baseCurrency));
            if (string.IsNullOrWhiteSpace(quoteCurrency)) throw new ArgumentNullException(nameof(quoteCurrency));

            baseCurrency = baseCurrency.Trim().ToUpperInvariant();
            quoteCurrency = quoteCurrency.Trim().ToUpperInvariant();

            if (!CurrencyConventions.IsIsoCurrency(baseCurrency))
                throw new ArgumentException("Ogiltig ISO-valuta (base).", nameof(baseCurrency));
            if (!CurrencyConventions.IsIsoCurrency(quoteCurrency))
                throw new ArgumentException("Ogiltig ISO-valuta (quote).", nameof(quoteCurrency));

            if (baseCurrency == quoteCurrency)
                throw new ArgumentException("Base och quote får inte vara samma.");

            BaseCurrency = baseCurrency;
            QuoteCurrency = quoteCurrency;
        }

        public static CurrencyPair FromPair6(string pair6)
        {
            if (string.IsNullOrWhiteSpace(pair6) || pair6.Length != 6)
                throw new ArgumentException("Förväntar exakt 6 tecken (t.ex. EURSEK).", nameof(pair6));

            var b = pair6.Substring(0, 3);
            var q = pair6.Substring(3, 3);
            return new CurrencyPair(b, q);
        }

        public override string ToString() => Pair6;

        public override bool Equals(object obj)
        {
            var other = obj as CurrencyPair;
            return other != null && other.BaseCurrency == BaseCurrency && other.QuoteCurrency == QuoteCurrency;
        }

        public override int GetHashCode() => Pair6.GetHashCode();
    }
}
