using System;

namespace FxTradeHub.Contracts.Dtos
{
    /// <summary>
    /// Flattenad blotter-rad baserad på Trade + TradeSystemLink.
    /// Detta är den modell som UI binder mot.
    /// </summary>
    public sealed class BlotterTradeRow
    {
        // Primärnyckel från Trade (StpTradeId)
        public long StpTradeId { get; set; }

        // Trade (core)
        public string TradeId { get; set; }
        /// <summary>
        /// SPOT, FWD, SWAP, NDF, OPTION_VANILLA, OPTION_NDO...
        /// (1:1 mot DB-värdena i Trade.ProductType)
        /// </summary>
        public string ProductType { get; set; }
        /// <summary>
        /// FIX, EMAIL, MANUAL, FILE_IMPORT...
        /// (1:1 mot DB-värdena i Trade.SourceType)
        /// </summary>
        public string SourceType { get; set; }
        /// <summary>
        /// VOLBROKER, BLOOMBERG, RTNS...
        /// (1:1 mot Trade.SourceVenueCode)
        /// </summary>
        public string SourceVenueCode { get; set; }

        public string CounterpartyCode { get; set; }
        public string BrokerCode { get; set; }

        /// <summary>
        /// Intern trader-id (Environment.UserName).
        /// </summary>
        public string TraderId { get; set; }
        /// <summary>
        /// InvId (kan vara trader eller sales).
        /// </summary>
        public string InvId { get; set; }
        /// <summary>
        /// ReportingEntity-id (styr marginal mm).
        /// </summary>
        public string ReportingEntityId { get; set; }

        /// <summary>
        /// T.ex. "EURSEK", "USDNOK".
        /// (1:1 mot Trade.CurrencyPair)
        /// </summary>
        public string CcyPair { get; set; }

        /// <summary>
        /// "Buy" / "Sell".
        /// </summary>
        public string BuySell { get; set; }

        /// <summary>
        /// "Call" / "Put" (för optioner).
        /// </summary>
        public string CallPut { get; set; }

        public decimal Notional { get; set; }
        public string NotionalCcy { get; set; }

        public decimal? Strike { get; set; }
        public string Cut { get; set; }

        public DateTime? TradeDate { get; set; }
        public DateTime? ExpiryDate { get; set; }
        public DateTime? SettlementDate { get; set; }

        public decimal? Premium { get; set; }
        public string PremiumCcy { get; set; }
        public DateTime? PremiumDate { get; set; }

        /// <summary>
        /// Primär MX3-portfölj (kortkod).
        /// </summary>
        public string PortfolioMx3 { get; set; }

        /// <summary>
        /// MiFID/handels-tid i UTC.
        /// </summary>
        public DateTime? ExecutionTimeUtc { get; set; }

        // Hedge / linear-specifikt
        public string HedgeType { get; set; }
        public decimal? HedgeRate { get; set; }

        // Systemlink MX3 (flattenat från TradeSystemLink)
        public string Mx3TradeId { get; set; }
        /// <summary>
        /// NEW, PENDING, BOOKED, ERROR, CANCELLED, READY_TO_ACK, ACK_SENT, ACK_ERROR.
        /// (1:1 mot TradeSystemLink.Status för SystemCode = MX3)
        /// </summary>
        public string Mx3Status { get; set; }

        // Systemlink Calypso (flattenat)
        public string CalypsoTradeId { get; set; }
        public string CalypsoStatus { get; set; }

        /// <summary>
        /// Styrs av STP-regler.
        /// false = raden är låst för edit i blottern.
        /// </summary>
        public bool CanEdit { get; set; }

        public BlotterTradeRow()
        {
            TradeId = string.Empty;
            ProductType = string.Empty;
            SourceType = string.Empty;
            SourceVenueCode = string.Empty;
            CounterpartyCode = string.Empty;
            BrokerCode = string.Empty;
            TraderId = string.Empty;
            InvId = string.Empty;
            ReportingEntityId = string.Empty;
            CcyPair = string.Empty;
            BuySell = string.Empty;
            CallPut = string.Empty;
            NotionalCcy = string.Empty;
            Cut = string.Empty;
            PremiumCcy = string.Empty;
            PortfolioMx3 = string.Empty;
            HedgeType = string.Empty;
            Mx3TradeId = string.Empty;
            Mx3Status = string.Empty;
            CalypsoTradeId = string.Empty;
            CalypsoStatus = string.Empty;
        }
    }
}
