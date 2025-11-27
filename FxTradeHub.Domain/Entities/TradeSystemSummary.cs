using System;
using FxTradeHub.Domain.Enums;

namespace FxTradeHub.Domain.Entities
{
    /// <summary>
    /// Sammanfattning av en trade + dess systemlänk för ett visst system.
    /// Används som read-modell mot blotter/servicelagret.
    /// </summary>
    public sealed class TradeSystemSummary
    {
        /// <summary>
        /// Primärnyckel för traden (Trade.StpTradeId).
        /// </summary>
        public long StpTradeId { get; set; }

        /// <summary>
        /// Externt/kanoniskt trade-id (Trade.TradeId).
        /// </summary>
        public string TradeId { get; set; }

        /// <summary>
        /// Produkt-typ (SPOT, FWD, SWAP, NDF, OPTION_VANILLA, OPTION_NDO).
        /// </summary>
        public ProductType ProductType { get; set; }

        /// <summary>
        /// Valutapar, t.ex. EURSEK.
        /// </summary>
        public string CurrencyPair { get; set; }

        /// <summary>
        /// Traddatum.
        /// </summary>
        public DateTime TradeDate { get; set; }

        /// <summary>
        /// Exekveringstidpunkt i UTC.
        /// </summary>
        public DateTime ExecutionTimeUtc { get; set; }

        /// <summary>
        /// BUY eller SELL.
        /// </summary>
        public string BuySell { get; set; }

        /// <summary>
        /// Nominellt belopp.
        /// </summary>
        public decimal Notional { get; set; }

        /// <summary>
        /// Valuta för nominellt belopp.
        /// </summary>
        public string NotionalCurrency { get; set; }

        /// <summary>
        /// Primärnyckel för systemlänken (TradeSystemLink.SystemLinkId).
        /// </summary>
        public long SystemLinkId { get; set; }

        /// <summary>
        /// Vilket system länken gäller (MX3, CALYPSO, VOLBROKER_STP, RTNS).
        /// </summary>
        public SystemCode SystemCode { get; set; }

        /// <summary>
        /// Status i detta system (NEW, PENDING, BOOKED, ERROR, CANCELLED, osv).
        /// </summary>
        public TradeSystemStatus Status { get; set; }

        public TradeSystemSummary()
        {
            TradeId = string.Empty;
            CurrencyPair = string.Empty;
            BuySell = string.Empty;
            NotionalCurrency = string.Empty;
        }
    }
}
