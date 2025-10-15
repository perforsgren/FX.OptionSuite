// ============================================================
// SPRINT 1 – STEG 3: Kontrakt & Meddelanden
// Varför:  Skicka volnoder från UI till VolService via bus.
// Vad:     Tenor/Label/Vol i enkel DTO.
// Klar när:RebuildVolSurface kan ta en lista av dessa.
// ============================================================
namespace FX.Messages.Dtos
{
    public sealed class VolNodeDto
    {
        public string Tenor { get; set; }   // "1W","1M","3M","1Y"
        public string Label { get; set; }   // "ATM","25D","10D","RR","BF"
        public double Vol { get; set; }     // 0.12 = 12%
    }
}
