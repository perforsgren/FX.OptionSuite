using System;
using System.Collections.Generic;
using FxTradeHub.Domain.Entities;

namespace FxTradeHub.Domain.Interfaces
{
    /// <summary>
    /// Repository-interface för STP-hubben.
    /// Hanterar inskrivning av inkommande meddelanden, trades,
    /// systemlänkar och workflow-events samt läsning av sammanfattningar
    /// för blottern.
    /// </summary>
    public interface IStpRepository
    {
        /// <summary>
        /// Infogar en ny rad i tabellen trade_stp.MessageIn
        /// och returnerar genererat MessageInId.
        /// </summary>
        long InsertMessageIn(MessageIn message);

        /// <summary>
        /// Infogar en ny rad i tabellen trade_stp.Trade
        /// och returnerar genererat StpTradeId.
        /// </summary>
        long InsertTrade(Trade trade);

        /// <summary>
        /// Infogar en ny rad i tabellen trade_stp.TradeSystemLink
        /// och returnerar genererat SystemLinkId.
        /// </summary>
        long InsertTradeSystemLink(TradeSystemLink link);

        /// <summary>
        /// Infogar en ny rad i tabellen trade_stp.TradeWorkflowEvent
        /// och returnerar genererat WorkflowEventId.
        /// </summary>
        long InsertTradeWorkflowEvent(TradeWorkflowEvent evt);

        /// <summary>
        /// Hämtar en lista av TradeSystemSummary-rader baserat på
        /// enkla filter (datumintervall, produkt, källa, motpart, trader).
        /// Används av blotter-lagret (D1) för att bygga BlotterTradeRow.
        /// </summary>
        /// <param name="fromTradeDate">Nedre gräns för TradeDate (inklusive). Null = ingen nedre gräns.</param>
        /// <param name="toTradeDate">Övre gräns för TradeDate (inklusive). Null = ingen övre gräns.</param>
        /// <param name="productType">Produktkod (SPOT/FWD/SWAP/NDF/OPTION_VANILLA/OPTION_NDO) eller null/tomt = alla.</param>
        /// <param name="sourceType">Källa (MAIL/FIX/API/FILE) eller null/tomt = alla.</param>
        /// <param name="counterpartyCode">Normaliserad motpartskod, eller null/tomt = alla.</param>
        /// <param name="traderId">TraderId, eller null/tomt = alla.</param>
        /// <param name="maxRows">Max antal rader att returnera. Null = ingen limit.</param>
        /// <returns>Lista med TradeSystemSummary, en rad per (trade, system)-kombination.</returns>
        IList<TradeSystemSummary> GetTradeSystemSummaries(
            DateTime? fromTradeDate,
            DateTime? toTradeDate,
            string productType,
            string sourceType,
            string counterpartyCode,
            string traderId,
            int? maxRows);
    }


 

}
