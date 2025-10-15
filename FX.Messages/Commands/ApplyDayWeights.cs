// ============================================================
// SPRINT 1 – STEG 3: Kontrakt & Meddelanden
// Varför:  UI/Import sätter dagvikter (events/helger) som påverkar ytan.
// Vad:     ISO-datum -> vikt (0..1).
// Klar när:DayWeightService kan uppdateras och trigga rebuild.
// ============================================================
using System.Collections.Generic;

namespace FX.Messages.Commands
{
    public sealed class ApplyDayWeights
    {
        public string Pair6 { get; set; }
        public Dictionary<string, double> DateWeights { get; set; } = new Dictionary<string, double>();
    }
}
