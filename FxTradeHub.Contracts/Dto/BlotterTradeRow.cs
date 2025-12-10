using System;

namespace FxTradeHub.Contracts.Dtos
{
    /// <summary>
    /// Flattenad blotter-rad baserad på Trade + TradeSystemLink.
    /// Detta är den modell som UI binder mot.
    /// </summary>
    public sealed class BlotterTradeRow
    {

        // ----------------------------------------------------
        // Primärnycklar / identitet
        // ----------------------------------------------------

        /// <summary>
        /// Intern primärnyckel för traden (Trade.StpTradeId).
        /// </summary>
        public long StpTradeId { get; set; }

        /// <summary>
        /// Primärnyckel för systemlänken (TradeSystemLink.SystemLinkId).
        /// </summary>
        public long SystemLinkId { get; set; }


        // ----------------------------------------------------
        // Trade – core
        // ----------------------------------------------------

        /// <summary>
        /// Externt/kanoniskt trade-id (Trade.TradeId).
        /// </summary>
        public string TradeId { get; set; }

        /// <summary>
        /// Koppling tillbaka till inkommande meddelande (Trade.MessageInId).
        /// NULL om traden inte är kopplad mot någon MessageIn-rad.
        /// </summary>
        public long? MessageInId { get; set; }

        /// <summary>
        /// SPOT, FWD, SWAP, NDF, OPTION_VANILLA, OPTION_NDO.
        /// (1:1 mot DB-värdena i Trade.ProductType)
        /// </summary>
        public string ProductType { get; set; }

        /// <summary>
        /// FIX, EMAIL, MANUAL, FILE_IMPORT.
        /// (1:1 mot DB-värdena i Trade.SourceType)
        /// </summary>
        public string SourceType { get; set; }

        /// <summary>
        /// VOLBROKER, BLOOMBERG, RTNS.
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
        /// T.ex. "EURSEK", "USDNOK". (1:1 mot Trade.CurrencyPair)
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

        /// <summary>
        /// MiFID/handels-tid i UTC.
        /// </summary>
        public DateTime? ExecutionTimeUtc { get; set; }

        /// <summary>
        /// MIC (marknadsidentifierare), t.ex. XOFF.
        /// </summary>
        public string Mic { get; set; }

        /// <summary>
        /// ISIN (om tillämpligt).
        /// </summary>
        public string Isin { get; set; }


        // ----------------------------------------------------
        // Option / premium
        // ----------------------------------------------------

        public decimal? Premium { get; set; }
        public string PremiumCcy { get; set; }
        public DateTime? PremiumDate { get; set; }

        /// <summary>
        /// Primär MX3-portfölj (kortkod) – Trade.PortfolioMx3.
        /// </summary>
        public string PortfolioMx3 { get; set; }

        /// <summary>
        /// Calypso-bok som traden ska bokas på i Calypso (Trade.CalypsoBook).
        /// Namnet är anpassat för UI men mappar 1:1 mot kolumnen CalypsoBook.
        /// </summary>
        public string CalypsoPortfolio { get; set; }


        // ----------------------------------------------------
        // Linear / hedge / NDF/NDO
        // ----------------------------------------------------

        // Hedge / linear-specifikt
        public string HedgeType { get; set; }
        public decimal? HedgeRate { get; set; }

        /// <summary>
        /// Spotkurs (Trade.SpotRate).
        /// </summary>
        public decimal? SpotRate { get; set; }

        /// <summary>
        /// Swap-punkter (Trade.SwapPoints).
        /// </summary>
        public decimal? SwapPoints { get; set; }

        /// <summary>
        /// Near settlement date (Trade.NearSettlementDate, för SWAP).
        /// </summary>
        public DateTime? NearSettlementDate { get; set; }

        /// <summary>
        /// TRUE om affären är icke levererbar (Trade.IsNonDeliverable).
        /// </summary>
        public bool? IsNonDeliverable { get; set; }

        /// <summary>
        /// Fixingdatum (Trade.FixingDate, NDF/NDO).
        /// </summary>
        public DateTime? FixingDate { get; set; }

        /// <summary>
        /// Cash-settlementvaluta (Trade.SettlementCurrency).
        /// </summary>
        public string SettlementCcy { get; set; }

        /// <summary>
        /// UTI (Unique Trade Identifier, Trade.Uti).
        /// </summary>
        public string Uti { get; set; }

        /// <summary>
        /// TVTIC (Trade Valuation Trade Identifier Code, Trade.Tvtic).
        /// </summary>
        public string Tvtic { get; set; }

        /// <summary>
        /// Marginalbelopp (Trade.Margin).
        /// </summary>
        public decimal? Margin { get; set; }


        // ----------------------------------------------------
        // Trade – meta / audit / polling
        // ----------------------------------------------------

        /// <summary>
        /// Soft delete-flagga på Trade (Trade.IsDeleted).
        /// </summary>
        public bool TradeIsDeleted { get; set; }

        /// <summary>
        /// Senaste uppdateringstid på Trade (Trade.LastUpdatedUtc).
        /// </summary>
        public DateTime? TradeLastUpdatedUtc { get; set; }

        /// <summary>
        /// Senast uppdaterad av (Trade.LastUpdatedBy).
        /// </summary>
        public string TradeLastUpdatedBy { get; set; }

        /// <summary>
        /// Senaste ändringstid på trade/systemlink
        /// (t.ex. max(Trade.LastUpdatedUtc, TradeSystemLink.LastStatusUtc)).
        /// Används som LastChangeUtc i polling.
        /// </summary>
        public DateTime? LastChangeUtc { get; set; }


        // ----------------------------------------------------
        // SystemLink – generella fält
        // ----------------------------------------------------

        /// <summary>
        /// Systemet denna rad avser (MX3, CALYPSO, VOLBROKER_STP, RTNS).
        /// Enum-värdet som string.
        /// </summary>
        public string SystemCode { get; set; }

        /// <summary>
        /// Status i detta system (NEW, PENDING, BOOKED, ERROR, CANCELLED, ...).
        /// Enum-värdet som string.
        /// </summary>
        public string Status { get; set; }

        /// <summary>
        /// Alias för Status om blottern vill särskilja systemstatus från andra statusar.
        /// </summary>
        public string SystemStatus { get; set; }

        /// <summary>
        /// Systemets egna trade-id (deal-id i MX3, Calypso-id, osv.).
        /// </summary>
        public string SystemTradeId { get; set; }

        /// <summary>
        /// Mer neutral benämning för systemets trade-id (samma som SystemTradeId i v1).
        /// </summary>
        public string ExternalTradeId { get; set; }

        /// <summary>
        /// Senaste status-tid i detta system (TradeSystemLink.LastStatusUtc).
        /// </summary>
        public DateTime? SystemLastStatusUtc { get; set; }

        /// <summary>
        /// Senaste feltext (TradeSystemLink.LastError).
        /// </summary>
        public string SystemLastError { get; set; }

        /// <summary>
        /// Systemets portföljkod (TradeSystemLink.PortfolioCode).
        /// </summary>
        public string SystemPortfolioCode { get; set; }

        /// <summary>
        /// TRUE om traden ska bokas i detta system (TradeSystemLink.BookFlag).
        /// </summary>
        public bool? BookFlag { get; set; }

        /// <summary>
        /// STP-läge (AUTO/MANUAL, TradeSystemLink.StpMode).
        /// </summary>
        public string StpMode { get; set; }

        /// <summary>
        /// Vem som importerade traden i detta system (TradeSystemLink.ImportedBy).
        /// </summary>
        public string ImportedBy { get; set; }

        /// <summary>
        /// Vem som bokade traden i systemet (TradeSystemLink.BookedBy).
        /// </summary>
        public string BookedBy { get; set; }

        /// <summary>
        /// Första gången traden bokades i systemet (TradeSystemLink.FirstBookedUtc).
        /// </summary>
        public DateTime? FirstBookedUtc { get; set; }

        /// <summary>
        /// Senaste gången traden bokades i systemet (TradeSystemLink.LastBookedUtc).
        /// </summary>
        public DateTime? LastBookedUtc { get; set; }

        /// <summary>
        /// Intern STP-flagga (TradeSystemLink.StpFlag).
        /// </summary>
        public bool? StpFlag { get; set; }

        /// <summary>
        /// Skapad-tid på systemlänken (TradeSystemLink.CreatedUtc).
        /// </summary>
        public DateTime? SystemCreatedUtc { get; set; }

        /// <summary>
        /// Soft delete-flagga på systemlänken (TradeSystemLink.IsDeleted).
        /// </summary>
        public bool SystemLinkIsDeleted { get; set; }



        // ----------------------------------------------------
        // SystemLink – flattenade per-system-fält (befintlig design)
        // ----------------------------------------------------

        /// <summary>
        /// MX3 deal-id (flattenat från TradeSystemLink.SystemTradeId).
        /// </summary>
        public string Mx3TradeId { get; set; }

        /// <summary>
        /// MX3-status:
        /// NEW, PENDING, BOOKED, ERROR, CANCELLED, READY_TO_ACK, ACK_SENT, ACK_ERROR.
        /// (1:1 mot TradeSystemLink.Status för SystemCode = MX3)
        /// </summary>
        public string Mx3Status { get; set; }

        /// <summary>
        /// Calypso trade-id (flattenat från TradeSystemLink.SystemTradeId).
        /// </summary>
        public string CalypsoTradeId { get; set; }

        /// <summary>
        /// Calypso-status (1:1 mot TradeSystemLink.Status för SystemCode = CALYPSO).
        /// </summary>
        public string CalypsoStatus { get; set; }


        // ----------------------------------------------------
        // UI-hjälp
        // ----------------------------------------------------

        /// <summary>
        /// Styrs av STP-regler.
        /// false = raden är låst för edit i blottern.
        /// </summary>
        public bool CanEdit { get; set; }



        /// <summary>
        /// Initierar strängar till tom sträng för att minimera null-hantering i UI-lagret.
        /// </summary>
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
            Mic = string.Empty;
            Isin = string.Empty;
            PortfolioMx3 = string.Empty;
            CalypsoPortfolio = string.Empty;
            PremiumCcy = string.Empty;
            SettlementCcy = string.Empty;
            Uti = string.Empty;
            Tvtic = string.Empty;
            HedgeType = string.Empty;
            TradeLastUpdatedBy = string.Empty;
            SystemCode = string.Empty;
            Status = string.Empty;
            SystemStatus = string.Empty;
            SystemTradeId = string.Empty;
            ExternalTradeId = string.Empty;
            SystemLastError = string.Empty;
            SystemPortfolioCode = string.Empty;
            StpMode = string.Empty;
            ImportedBy = string.Empty;
            BookedBy = string.Empty;
            Mx3TradeId = string.Empty;
            Mx3Status = string.Empty;
            CalypsoTradeId = string.Empty;
            CalypsoStatus = string.Empty;
        }

    }
}
