using System.Collections.Generic;

namespace FX.Core.Conventions
{
    /// <summary>ISO-valutalista (kort whitelist för steg 2).</summary>
    public static class CurrencyConventions
    {
        private static readonly HashSet<string> _iso = new HashSet<string>(new[]
        {
            "USD","EUR","SEK","NOK","DKK","GBP","JPY","CHF","CAD","AUD","NZD","CNH","CNY","PLN","CZK","HUF","TRY","ZAR","MXN","SGD","HKD"
        });

        public static bool IsIsoCurrency(string ccy)
        {
            if (string.IsNullOrEmpty(ccy)) return false;
            return _iso.Contains(ccy.ToUpperInvariant());
        }
    }
}
