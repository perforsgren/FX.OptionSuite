using System;

namespace FxTradeHub.Domain.Entities
{
    /// <summary>
    /// Regel för expiry cut per valutapar.
    /// Motsvarar raden i tabellen trade_stp.stp_expiry_cut_ccy.
    /// </summary>
    public sealed class ExpiryCutCcyRule
    {
        /// <summary>
        /// Valutapar, t.ex. EURSEK eller USDNOK.
        /// Primärnyckel i tabellen.
        /// </summary>
        public string CurrencyPair { get; set; }

        /// <summary>
        /// Expiry cut-kod, t.ex. NYC_10, TKO_15 etc.
        /// </summary>
        public string ExpiryCut { get; set; }

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
        /// Skapar en ny instans av ExpiryCutCcyRule med
        /// strängfält initialiserade till tomma strängar.
        /// </summary>
        public ExpiryCutCcyRule()
        {
            CurrencyPair = string.Empty;
            ExpiryCut = string.Empty;
            Comment = string.Empty;
            UpdatedBy = string.Empty;
        }
    }
}
