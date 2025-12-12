using System.Collections.Generic;
using System.Threading.Tasks;
using FxTradeHub.Contracts.Dtos;

namespace FxTradeHub.Services
{
    /// <summary>
    /// Asynkront läs-API mot STP-hubben för blottern.
    /// Används för att hämta blotter-rader utan att blockera UI-tråden.
    /// </summary>
    public interface IBlotterReadServiceAsync
    {
        /// <summary>
        /// Hämtar blotter-rader från STP-hubben baserat på angivet filter.
        /// Används av UI/presenter för asynkron initial load och refresh.
        /// </summary>
        /// <param name="filter">
        /// Filter för datum, produkt, källa, motpart, trader, max-rader etc.
        /// </param>
        /// <returns>
        /// En asynkron operation som returnerar en lista med blotter-rader
        /// (en rad per trade/system-kombination v1).
        /// </returns>
        Task<List<BlotterTradeRow>> GetBlotterTradesAsync(BlotterFilter filter);
    }
}
