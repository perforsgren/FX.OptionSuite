using System.Collections.Generic;
using FxTradeHub.Contracts.Dtos;

namespace FxTradeHub.Services
{
    /// <summary>
    /// Läser blotter-data från STP-hubben och mappar till BlotterTradeRow.
    /// </summary>
    public interface IBlotterReadService
    {
        /// <summary>
        /// Hämtar trades för blottern enligt BlotterFilter.
        /// </summary>
        /// <param name="filter">Filter för datum, produkt, counterparty, trader, max-rader etc.</param>
        /// <returns>Lista av blotter-rader.</returns>
        List<BlotterTradeRow> GetBlotterTrades(BlotterFilter filter);
    }
}
