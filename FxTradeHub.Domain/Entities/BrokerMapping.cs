using System;

namespace FxTradeHub.Domain.Entities
{
    /// <summary>
    /// Mappning mellan extern brokerkod per venue och intern normaliserad brokerkod.
    /// Motsvarar raden i tabellen trade_stp.stp_broker_mapping.
    /// </summary>
    public sealed class BrokerMapping
    {
        /// <summary>
        /// Primärnyckel i tabellen.
        /// </summary>
        public long Id { get; set; }

        /// <summary>
        /// Venue-/källkod, t.ex. VOLBROKER, RTNS, BBGFX.
        /// Matchar normalt Trade.SourceVenueCode.
        /// </summary>
        public string SourceVenueCode { get; set; }

        /// <summary>
        /// Extern rå brokerkod från FIX/email/etc.
        /// </summary>
        public string ExternalBrokerCode { get; set; }

        /// <summary>
        /// Intern normaliserad brokerkod som används i Trade.BrokerCode.
        /// </summary>
        public string NormalizedBrokerCode { get; set; }

        /// <summary>
        /// Anger om mappningen är aktiv.
        /// Inaktiva rader ska ignoreras av lookup-logiken.
        /// </summary>
        public bool IsActive { get; set; }

        /// <summary>
        /// Fri kommentar/beskrivning av mappningen.
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
        /// Skapar en ny instans av BrokerMapping med
        /// strängfält initialiserade till tomma strängar.
        /// </summary>
        public BrokerMapping()
        {
            SourceVenueCode = string.Empty;
            ExternalBrokerCode = string.Empty;
            NormalizedBrokerCode = string.Empty;
            Comment = string.Empty;
            UpdatedBy = string.Empty;
        }
    }
}
