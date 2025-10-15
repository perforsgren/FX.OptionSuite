// ============================================================
// SPRINT 1 – STEG 5: DayWeightService (stub)
// Varför:  Hålla plats för dagvikter utan att blockera Steg 5.
// Vad:     Returnerar Uniform() tills riktig kalender/helglogik kopplas på.
// Klar när:IVolService kan anropas med en kurva även om den ignoreras.
// ============================================================
using System;
using System.Collections.Generic;
using FX.Core.Domain;
using FX.Core.Interfaces;

namespace FX.Services
{
    public sealed class DayWeightService : IDayWeightService
    {
        public double Weight(DateTime today, DateTime expiry)
        {
            return 1.0; // placeholder
        }
    }
}
