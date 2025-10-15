// ============================================================
// SPRINT 1 – STEG 3: Kontrakt & Meddelanden
// Varför:  UI bokar affär efter lyckad prissättning.
// Vad:     Ben + dealpris; svar kommer som TradeBooked.
// Klar när:BlotterService lyssnar och persisterar.
// ============================================================
using System.Collections.Generic;
using FX.Messages.Dtos;

namespace FX.Messages.Commands
{
    public sealed class BookTrade
    {
        public string CorrelationId { get; set; }
        public string Pair6 { get; set; }
        public List<LegDto> Legs { get; set; } = new List<LegDto>();
        public double DealPrice { get; set; }
    }
}
