using System.Collections.Generic;
using FxTradeHub.Domain.Entities;

namespace FxTradeHub.Domain.Interfaces
{
    /// <summary>
    /// Repository-interface för STP-hubben.
    /// Hanterar inskrivning av meddelanden, trades, systemlänkar och workflow-event,
    /// samt läsning av sammanfattande trade/system-data för blottrar.
    /// </summary>
    public interface IStpRepository
    {
        /// <summary>
        /// Infogar ett inkommande meddelande i MessageIn-tabellen.
        /// </summary>
        /// <param name="message">Meddelande att spara.</param>
        /// <returns>Genererat MessageInId.</returns>
        long InsertMessageIn(MessageIn message);

        /// <summary>
        /// Infogar en ny trade i Trade-tabellen.
        /// </summary>
        /// <param name="trade">Trade-objekt att spara.</param>
        /// <returns>Genererat StpTradeId.</returns>
        long InsertTrade(Trade trade);

        /// <summary>
        /// Infogar en ny systemlänk i TradeSystemLink-tabellen.
        /// </summary>
        /// <param name="link">Systemlänk att spara.</param>
        /// <returns>Genererat TradeSystemLinkId.</returns>
        long InsertTradeSystemLink(TradeSystemLink link);

        /// <summary>
        /// Infogar ett nytt workflow-event i TradeWorkflowEvent-tabellen.
        /// </summary>
        /// <param name="evt">Workflow-event att spara.</param>
        /// <returns>Genererat TradeWorkflowEventId.</returns>
        long InsertWorkflowEvent(TradeWorkflowEvent evt);

        /// <summary>
        /// Hämtar alla trades med tillhörande systemlänkar i en sammanfattad vy,
        /// avsedd som grund för blottrar och read-tjänster.
        /// </summary>
        /// <returns>Lista med TradeSystemSummary-rader.</returns>
        IList<TradeSystemSummary> GetAllTradeSystemSummaries();
    }
}
