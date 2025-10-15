// ============================================================
// SPRINT 1 – STEG 3: Kontrakt & Meddelanden
// Varför:  Signalera att volytan för ett par/byttes version.
// Vad:     Pair + SurfaceId + tidpunkt.
// Klar när:UI/PriceEngine kan reagera (auto-reprice).
// ============================================================
using System;

namespace FX.Messages.Events
{
    public sealed class SurfaceUpdated
    {
        public string Pair6 { get; set; }
        public string SurfaceId { get; set; }
        public DateTime TimeUtc { get; set; }
    }
}
