// ============================================================
// SPRINT 1 – STEG 3: Kontrakt & Meddelanden
// Varför:  Marknadsticker för spot; UI/PriceEngine kan reagera.
// Vad:     Pair + Spot + tid.
// Klar när:Subscriptions kan testas (mockad feed).
// ============================================================
using System;

namespace FX.Messages.Events
{
    public sealed class SpotUpdated
    {
        public string Pair6 { get; set; }
        public double Spot { get; set; }
        public DateTime TimeUtc { get; set; }
    }
}
