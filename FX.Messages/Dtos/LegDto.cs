// ============================================================
// SPRINT 1 – STEG 3: Kontrakt & Meddelanden
// Varför:  Skicka ben via bus/GUI utan Core-beroende.
// Vad:     Enkel wire-DTO med strängar/tal (parsas till Core i services).
// Klar när:UI kan skapa RequestPrice med denna typ.
// ============================================================
namespace FX.Messages.Dtos
{
    public sealed class LegDto
    {
        public string Side { get; set; }     // "BUY"/"SELL"
        public string Type { get; set; }     // "CALL"/"PUT"
        public string Strike { get; set; }   // "25D" eller "10.1234"
        public string ExpiryIso { get; set; } // "yyyy-MM-dd"
        public double Notional { get; set; }
    }
}
