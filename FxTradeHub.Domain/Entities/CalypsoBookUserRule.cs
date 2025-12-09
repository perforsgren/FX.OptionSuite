using System;

namespace FxTradeHub.Domain.Entities
{
    /// <summary>
    /// Regel för Calypso-bok per trader/användare.
    /// Motsvarar raden i tabellen trade_stp.stp_calypso_book_user.
    /// </summary>
    public sealed class CalypsoBookUserRule
    {
        /// <summary>
        /// TraderId / användar-id.
        /// Matchar normalt Trade.TraderId.
        /// </summary>
        public string TraderId { get; set; }

        /// <summary>
        /// Namn/kod på Calypso-boken som ska användas för denna trader.
        /// </summary>
        public string CalypsoBook { get; set; }

        /// <summary>
        /// Anger om regeln är aktiv.
        /// Inaktiva rader ska ignoreras av lookup-logiken.
        /// </summary>
        public bool IsActive { get; set; }

        /// <summary>
        /// Fri kommentar/beskrivning av regeln.
        /// </summary>
        public string Comment { get; set; }

        /// <summary>
        /// När posten skapades i databasen (UTC).
        /// </summary>
        public DateTime CreatedUtc { get; set; }

        /// <summary>
        /// När posten senast uppdaterades i databasen (UTC), om någonsin.
        /// </summary>
        public DateTime? UpdatedUtc { get; set; }

        /// <summary>
        /// Tekniskt användar-id som senast uppdaterade posten.
        /// </summary>
        public string UpdatedBy { get; set; }

        /// <summary>
        /// Skapar en ny instans av CalypsoBookUserRule med
        /// strängfält initialiserade till tomma strängar.
        /// </summary>
        public CalypsoBookUserRule()
        {
            TraderId = string.Empty;
            CalypsoBook = string.Empty;
            Comment = string.Empty;
            UpdatedBy = string.Empty;
        }
    }
}

