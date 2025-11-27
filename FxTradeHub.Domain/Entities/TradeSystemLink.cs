using System;
using FxTradeHub.Domain.Enums;

namespace FxTradeHub.Domain.Entities
{
    /// <summary>
    /// Länk mellan en intern trade och ett externt system (MX3, Calypso, Volbroker STP, RTNS).
    /// Motsvarar tabellen trade_stp.TradeSystemLink.
    /// </summary>
    public sealed class TradeSystemLink
    {
        /// <summary>
        /// Primärnyckel.
        /// </summary>
        public long TradeSystemLinkId { get; set; }

        /// <summary>
        /// FK till Trade.StpTradeId.
        /// </summary>
        public long StpTradeId { get; set; }

        /// <summary>
        /// Vilket system länken gäller (MX3, CALYPSO, VOLBROKER_STP, RTNS).
        /// </summary>
        public SystemCode SystemCode { get; set; }

        /// <summary>
        /// Systemets egna trade-id (t.ex. MX3 deal number, Calypso trade id).
        /// </summary>
        public string ExternalTradeId { get; set; }

        /// <summary>
        /// Status i detta system: NEW, PENDING, BOOKED, ERROR, CANCELLED, READY_TO_ACK, ACK_SENT, ACK_ERROR.
        /// </summary>
        public TradeSystemStatus Status { get; set; }

        /// <summary>
        /// Senaste felkod om status = ERROR / ACK_ERROR (kan vara tom).
        /// </summary>
        public string ErrorCode { get; set; }

        /// <summary>
        /// Senaste felmeddelande (kort text, för blotter / logg).
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Skapad-tid i UTC.
        /// </summary>
        public DateTime CreatedUtc { get; set; }

        /// <summary>
        /// Senast uppdaterad-tid i UTC.
        /// </summary>
        public DateTime LastUpdatedUtc { get; set; }

        /// <summary>
        /// Soft delete-flagga även här (om vi vill kunna droppa gamla länkar).
        /// </summary>
        public bool IsDeleted { get; set; }

        public TradeSystemLink()
        {
            ExternalTradeId = string.Empty;
            ErrorCode = string.Empty;
            ErrorMessage = string.Empty;
        }
    }
}
