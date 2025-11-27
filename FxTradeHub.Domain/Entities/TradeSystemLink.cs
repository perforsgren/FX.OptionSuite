using System;
using FxTradeHub.Domain.Enums;

namespace FxTradeHub.Domain.Entities
{
    /// <summary>
    /// Länk mellan en intern trade och ett externt system (MX3, Calypso, Volbroker STP, RTNS).
    /// Motsvarar tabellen trade_stp.tradesystemlink.
    /// </summary>
    public sealed class TradeSystemLink
    {
        /// <summary>
        /// Primärnyckel. Motsvarar SystemLinkId i databasen.
        /// </summary>
        public long TradeSystemLinkId { get; set; }

        /// <summary>
        /// FK till Trade.StpTradeId.
        /// </summary>
        public long StpTradeId { get; set; }

        /// <summary>
        /// Vilket system länken gäller (MX3, CALYPSO, VOLBROKER_STP, RTNS).
        /// Lagra som upper-case string i databasen.
        /// </summary>
        public SystemCode SystemCode { get; set; }

        /// <summary>
        /// Systemets egna trade-id (t.ex. MX3 deal number, Calypso trade id).
        /// Motsvarar SystemTradeId i databasen.
        /// </summary>
        public string ExternalTradeId { get; set; }

        /// <summary>
        /// Status i detta system: NEW, PENDING, BOOKED, ERROR, CANCELLED, READY_TO_ACK, ACK_SENT, ACK_ERROR.
        /// Lagra som upper-case string i databasen.
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
        /// Portföljkod för routing / booking (t.ex. MX3-portfolio).
        /// Motsvarar PortfolioCode i databasen.
        /// </summary>
        public string PortfolioCode { get; set; }

        /// <summary>
        /// Flagga för om traden ska bokas i detta system (true/false).
        /// Motsvarar BookFlag i databasen (tinyint).
        /// </summary>
        public bool? BookFlag { get; set; }

        /// <summary>
        /// STP-läge, t.ex. AUTO, MANUAL.
        /// Motsvarar StpMode i databasen.
        /// </summary>
        public string StpMode { get; set; }

        /// <summary>
        /// Vem som importerade / skapade länken (användarnamn, systemnamn).
        /// Motsvarar ImportedBy i databasen.
        /// </summary>
        public string ImportedBy { get; set; }

        /// <summary>
        /// Vem som bokade traden i målsystemet (om tillämpligt).
        /// Motsvarar BookedBy i databasen.
        /// </summary>
        public string BookedBy { get; set; }

        /// <summary>
        /// Första gången traden bokades i målsystemet (UTC).
        /// Motsvarar FirstBookedUtc i databasen.
        /// </summary>
        public DateTime? FirstBookedUtc { get; set; }

        /// <summary>
        /// Senaste gång traden bokades/ändrades i målsystemet (UTC).
        /// Motsvarar LastBookedUtc i databasen.
        /// </summary>
        public DateTime? LastBookedUtc { get; set; }

        /// <summary>
        /// Flagga för om STP-flödet är aktivt / påslaget.
        /// Motsvarar StpFlag i databasen (tinyint).
        /// </summary>
        public bool? StpFlag { get; set; }

        /// <summary>
        /// Skapad-tid i UTC för länken.
        /// Motsvarar CreatedUtc i databasen.
        /// </summary>
        public DateTime CreatedUtc { get; set; }

        /// <summary>
        /// Senast uppdaterad-tid i UTC för länken (status, fel etc).
        /// Mappas mot LastStatusUtc i databasen.
        /// </summary>
        public DateTime LastUpdatedUtc { get; set; }

        /// <summary>
        /// Soft delete-flagga för länken.
        /// Motsvarar IsDeleted i databasen.
        /// </summary>
        public bool IsDeleted { get; set; }

        public TradeSystemLink()
        {
            ExternalTradeId = string.Empty;
            ErrorCode = string.Empty;
            ErrorMessage = string.Empty;
            PortfolioCode = string.Empty;
            StpMode = string.Empty;
            ImportedBy = string.Empty;
            BookedBy = string.Empty;
        }
    }
}
