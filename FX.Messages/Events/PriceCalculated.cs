// ============================================================
// SPRINT 1 – STEG 3: Kontrakt & Meddelanden
// Varför:  Asynkront svar på RequestPrice, UI renderar resultat.
// Vad:     Pris + greker eller felmeddelande.
// Klar när:UI kan lyssna och visa pris.
// ============================================================
using System;

namespace FX.Messages.Events
{
    /// <summary>
    /// Resultat från prissättning. Behåller Price för bakåtkomp (vald sida),
    /// men skickar även ut tvåväg + mid-greker (position).
    /// </summary>
    public sealed class PriceCalculated
    {
        public System.Guid CorrelationId { get; set; }

        // Bakåtkomp: valt pris (BUY→Ask, SELL→Bid). Presentern kan ignorera om den visar tvåväg.
        public double Price { get; set; }

        // Nytt: tvåvägs-premium per unit
        public double PremiumBid { get; set; }
        public double PremiumMid { get; set; }
        public double PremiumAsk { get; set; }

        // Vilken sida som ska fetmarkeras i UI ("BID" eller "ASK" eller "MID" när bara mid finns)
        public string BoldSide { get; set; }

        // Positionella greker (mid-greker * notional * side)
        public double Delta { get; set; }
        public double Gamma { get; set; }
        public double Vega { get; set; }
        public double Theta { get; set; }
    }
}
