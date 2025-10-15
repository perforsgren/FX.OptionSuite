// ============================================================
// SPRINT 1 – STEG 3: Kontrakt & Meddelanden
// Varför:  Enhetlig felkanal för UI/loggning.
// Vad:     Källa + kort meddelande + ev. detaljer + korrelations-id.
// Klar när:UI kan visa banner/toast och logga till fil.
// ============================================================
using System;

namespace FX.Messages.Events
{
    public sealed class ErrorOccurred
    {
        public string Source { get; set; }         // "PriceEngine", "VolService", etc.
        public string Message { get; set; }
        public string Detail { get; set; }
        public Guid CorrelationId { get; set; }  // koppling till request om finns
    }
}
