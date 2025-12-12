using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FxTradeHub.Domain.Entities;

namespace FxTradeHub.Domain.Interfaces
{
    /// <summary>
    /// Asynkron variant av IStpRepository för läsning av STP-readmodellen.
    /// Används främst av blotterns read-tjänster för att hämta TradeSystemSummary
    /// utan att blockera UI-tråden.
    /// </summary>
    public interface IStpRepositoryAsync
    {
        /// <summary>
        /// Hämtar en lista av TradeSystemSummary-rader baserat på filtreringsparametrar.
        /// Joinar Trade + TradeSystemLink på serversidan och filtrerar bort
        /// soft-deletade trades (Trade.IsDeleted = 0).
        /// </summary>
        /// <param name="fromTradeDate">Från och med trade-datum (inklusive), eller null för obegränsat.</param>
        /// <param name="toTradeDate">Till och med trade-datum (inklusive), eller null för obegränsat.</param>
        /// <param name="productType">Produktfilter (SPOT/FWD/SWAP/NDF/OPTION_VANILLA/OPTION_NDO), eller null/tomt för alla.</param>
        /// <param name="sourceType">Källtyp (t.ex. FIX/MAIL/MANUAL), eller null/tomt för alla.</param>
        /// <param name="sourceVenueCode">Venue-kod (t.ex. VOLBROKER, DESK), eller null/tomt för alla.</param>
        /// <param name="counterpartyCode">Motpartskod, eller null/tomt för alla.</param>
        /// <param name="traderId">Trader-id, eller null/tomt för alla.</param>
        /// <param name="currencyPair">Valutapar (t.ex. EURSEK), eller null/tomt för alla.</param>
        /// <param name="maxRows">Maximalt antal rader att returnera, eller null för obegränsat.</param>
        /// <param name="currentUserId">
        /// Aktuell användare (för logging/audit i senare versioner). Används inte i v1
        /// men exponeras här för framtida utbyggnad.
        /// </param>
        /// <returns>
        /// En asynkront hämtad lista med TradeSystemSummary-rader som matchar filtret.
        /// </returns>
        Task<IList<TradeSystemSummary>> GetTradeSystemSummariesAsync(
            DateTime? fromTradeDate,
            DateTime? toTradeDate,
            string productType,
            string sourceType,
            string sourceVenueCode,
            string counterpartyCode,
            string traderId,
            string currencyPair,
            int? maxRows,
            string currentUserId);
    }
}
