using System;
using FxTradeHub.Domain.Enums;

namespace FxTradeHub.Domain.Entities
{
    /// <summary>
    /// Audit-logg / workflowhistorik för en trade.
    /// Motsvarar tabellen trade_stp.TradeWorkflowEvent.
    /// </summary>
    public sealed class TradeWorkflowEvent
    {
        /// <summary>
        /// Primärnyckel.
        /// </summary>
        public long TradeWorkflowEventId { get; set; }

        /// <summary>
        /// FK till Trade.StpTradeId.
        /// </summary>
        public long StpTradeId { get; set; }

        /// <summary>
        /// Vilket system eventet huvudsakligen gäller (kan vara null/okänt).
        /// Exempel: MX3, CALYPSO, VOLBROKER_STP.
        /// </summary>
        public SystemCode? SystemCode { get; set; }

        /// <summary>
        /// Typ av event: t.ex. "FIELD_UPDATE", "BOOK_REQUEST", "BOOK_RESULT", "ACK_SENT".
        /// (Hålls som string i v1, kan enumifieras senare.)
        /// </summary>
        public string EventType { get; set; }

        /// <summary>
        /// Fri text / sammanfattning av eventet.
        ///</summary>
        public string Description { get; set; }

        /// <summary>
        /// Om eventet är en fältändring:
        /// namnet på fältet (t.ex. "PortfolioMx3", "TraderId").
        /// </summary>
        public string FieldName { get; set; }

        /// <summary>
        /// Gamla värdet (text) om fält ändrats.
        /// </summary>
        public string OldValue { get; set; }

        /// <summary>
        /// Nya värdet (text) om fält ändrats.
        /// </summary>
        public string NewValue { get; set; }

        /// <summary>
        /// Tidpunkt då eventet inträffade (UTC).
        /// </summary>
        public DateTime EventTimeUtc { get; set; }

        /// <summary>
        /// Vem initierade eventet (Environment.UserName, systemidentifierare etc).
        /// </summary>
        public string InitiatorId { get; set; }

        public TradeWorkflowEvent()
        {
            EventType = string.Empty;
            Description = string.Empty;
            FieldName = string.Empty;
            OldValue = string.Empty;
            NewValue = string.Empty;
            InitiatorId = string.Empty;
        }
    }
}
