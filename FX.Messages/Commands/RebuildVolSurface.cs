// ============================================================
// SPRINT 1 – STEG 3: Kontrakt & Meddelanden
// Varför:  Användaren/Import triggar rebuild av ytan för ett par.
// Vad:     Skickar volnoder och valfri orsak (loggning).
// Klar när:VolService kan lyssna och bygga om ytan.
// ============================================================
using System.Collections.Generic;
using FX.Messages.Dtos;

namespace FX.Messages.Commands
{
    public sealed class RebuildVolSurface
    {
        public string Pair6 { get; set; }
        public List<VolNodeDto> Nodes { get; set; } = new List<VolNodeDto>();
        public string Reason { get; set; }
    }
}
