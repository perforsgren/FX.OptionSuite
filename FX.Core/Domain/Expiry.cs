using System;
using System.Globalization;

namespace FX.Core.Domain
{
    /// <summary>Förfallodatum för en option (datumdel utnyttjas).</summary>
    public sealed class Expiry
    {
        public DateTime Date { get; }

        public Expiry(DateTime date)
        {
            Date = date.Date;
        }

        public override string ToString()
        {
            // t.ex. 23-OCT-2025
            return Date.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture).ToUpperInvariant();
        }
    }
}
