// ============================================================
// SPRINT 1 – STEG 3: Kontrakt & Meddelanden
// Varför:  BlotterService behöver en enkel domän-DTO för bokade trades.
// Vad:     Immutabel container för ett bokat trade-id och dess data.
// Klar när:Kan användas av IBlotterService utan UI/DB-beroenden.
// ============================================================
using System;
using System.Collections.Generic;

namespace FX.Core.Domain
{
    public sealed class BookedTrade
    {
        public string TradeId { get; }
        public CurrencyPair Pair { get; }
        public IReadOnlyList<OptionLeg> Legs { get; }
        public double Price { get; }
        public DateTime TimeUtc { get; }

        public BookedTrade(string tradeId, CurrencyPair pair, IReadOnlyList<OptionLeg> legs, double price, DateTime timeUtc)
        {
            TradeId = tradeId;
            Pair = pair;
            Legs = legs;
            Price = price;
            TimeUtc = timeUtc;
        }
    }
}
