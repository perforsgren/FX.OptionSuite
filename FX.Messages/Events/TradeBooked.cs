// ============================================================
// SPRINT 1 – STEG 3: Kontrakt & Meddelanden
// Varför:  Bekräftelse på att en trade persisterats.
// Vad:     TradeId + pair + tid.
// Klar när:Blotter-vyn kan uppdatera tabellen direkt.
// ============================================================
using System;

namespace FX.Messages.Events
{
    public sealed class TradeBooked
    {
        public string TradeId { get; set; }
        public string Pair6 { get; set; }
        public DateTime TimeUtc { get; set; }
    }
}
