using System;

namespace FX.Core
{
    public sealed class ExpiryResolution
    {
        public DateTime ExpiryDate { get; set; }
        public DateTime SettlementDate { get; set; }
        public string ExpiryIso { get; set; }       // "yyyy-MM-dd"
        public string SettlementIso { get; set; }   // "yyyy-MM-dd"
        public string Mode { get; set; }            // "Tenor" eller "Date"
        public string Normalized { get; set; }      // t.ex. "1m", "on", "2026-02-01"
        public string ExpiryWeekday { get; set; }   // "Mon (Mån)"
    }
}
