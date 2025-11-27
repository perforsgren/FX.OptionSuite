using System;
using System.Collections.Generic;

namespace FxTradeHub.Contracts.Dtos
{
    /// <summary>
    /// Resultat från polling mot STP-hubben:
    /// - nya eller uppdaterade trades
    /// - vilka trades som har soft-deletats
    /// </summary>
    public sealed class TradeChangeSet
    {
        /// <summary>
        /// Nya eller uppdaterade trades (flattenade blotter-rader).
        /// </summary>
        public List<BlotterTradeRow> NewOrUpdatedTrades { get; set; }

        /// <summary>
        /// StpTradeId:n för trades som nu är IsDeleted = 1 (ska tas bort ur UI).
        /// </summary>
        public List<long> DeletedStpTradeIds { get; set; }

        /// <summary>
        /// Senaste ändringstid på servern (Trade/TradeSystemLink).
        /// Blottern sparar detta värde och skickar in som ”sinceUtc” vid nästa poll.
        /// </summary>
        public DateTime LastChangeUtc { get; set; }

        public TradeChangeSet()
        {
            NewOrUpdatedTrades = new List<BlotterTradeRow>();
            DeletedStpTradeIds = new List<long>();
        }
    }
}
