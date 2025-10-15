// ============================================================
// SPRINT 1 – STEG 3: Kontrakt & Meddelanden
// Varför:  Räntor uppdateras; kan påverka prissättning och ytor.
// Vad:     Pair + expiry + rd/rf + tid.
// Klar när:PriceEngine och ev. surfacekalkyl kan reagera.
// ============================================================
using System;

namespace FX.Messages.Events
{
    public sealed class RatesUpdated
    {
        public string Pair6 { get; set; }
        public DateTime Expiry { get; set; } // UTC
        public double Rd { get; set; }
        public double Rf { get; set; }
        public DateTime TimeUtc { get; set; }
    }
}
