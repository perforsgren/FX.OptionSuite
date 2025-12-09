using System;
using FxTradeHub.Domain.Enums;

namespace FxTradeHub.Domain.Entities
{
    /// <summary>
    /// Sammanfattning av en trade och dess länk mot ett specifikt system.
    /// Kombinerar centrala fält från Trade och TradeSystemLink till en read-modell
    /// som kan användas av blotter/servicelagret.
    /// </summary>
    public sealed class TradeSystemSummary
    {
        // ----------------------------------------------------
        // Trade-del
        // ----------------------------------------------------

        /// <summary>
        /// Intern primärnyckel för traden (Trade.StpTradeId).
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
        /// Källa för traden (MAIL, FIX, API, FILE).
        /// </summary>
        public string SourceType { get; set; }

        /// <summary>
        /// Venue / källa (VOLBROKER, EMAIL_BROKER, RTNS, osv.).
        /// </summary>
        public string SourceVenueCode { get; set; }

        /// <summary>
        /// Normaliserad motpartskod.
        /// </summary>
        public string CounterpartyCode { get; set; }

        /// <summary>
        /// Brokerkod (om någon).
        /// </summary>
        public string BrokerCode { get; set; }

        /// <summary>
        /// Trader-id (UserProfile.UserId).
        /// </summary>
        public string TraderId { get; set; }

        /// <summary>
        /// InvId / säljare / sales (UserProfile.UserId).
        /// </summary>
        public string InvId { get; set; }

        /// <summary>
        /// Rapporteringsenhet (ReportingEntity.ReportingEntityId).
        /// </summary>
        public string ReportingEntityId { get; set; }

        /// <summary>
        /// Valutapar, t.ex. EURSEK.
        /// </summary>
        public string CurrencyPair { get; set; }

        /// <summary>
        /// MIC (marknadsidentifierare), t.ex. XOFF.
        /// </summary>
        public string Mic { get; set; }

        /// <summary>
        /// ISIN (om tillämpligt).
        /// </summary>
        public string Isin { get; set; }

        /// <summary>
        /// Traddatum (handelsdatum).
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
        /// Slutlig settlement-dag.
        /// </summary>
        public DateTime SettlementDate { get; set; }

        /// <summary>
        /// Near settlement-dag (för SWAP).
        /// </summary>
        public DateTime? NearSettlementDate { get; set; }

        /// <summary>
        /// NDF-flagga (icke levererbar).
        /// </summary>
        public bool? IsNonDeliverable { get; set; }

        /// <summary>
        /// Fixingdatum (för NDF/NDO).
        /// </summary>
        public DateTime? FixingDate { get; set; }

        /// <summary>
        /// Cash-settlementvaluta (för NDF/NDO).
        /// </summary>
        public string SettlementCurrency { get; set; }

        /// <summary>
        /// UTI (Unique Trade Identifier).
        /// </summary>
        public string Uti { get; set; }

        /// <summary>
        /// TVTIC (Trade Valuation Trade Identifier Code).
        /// </summary>
        public string Tvtic { get; set; }

        /// <summary>
        /// Marginalbelopp (om tillämpligt).
        /// </summary>
        public decimal? Margin { get; set; }

        /// <summary>
        /// Hedge-rate (deal rate / all-in rate).
        /// </summary>
        public decimal? HedgeRate { get; set; }

        /// <summary>
        /// Spotkurs (för SWAP).
        /// </summary>
        public decimal? SpotRate { get; set; }

        /// <summary>
        /// Swap-punkter (för SWAP).
        /// </summary>
        public decimal? SwapPoints { get; set; }

        /// <summary>
        /// Hedge-typ (t.ex. HEDGE/TRADE).
        /// </summary>
        public string HedgeType { get; set; }

        /// <summary>
        /// CALL eller PUT (för optioner).
        /// </summary>
        public string CallPut { get; set; }

        /// <summary>
        /// Strike (för optioner).
        /// </summary>
        public decimal? Strike { get; set; }

        /// <summary>
        /// Expirydatum (för optioner).
        /// </summary>
        public DateTime? ExpiryDate { get; set; }

        /// <summary>
        /// Cut (t.ex. STO, NYC, TKY).
        /// </summary>
        public string Cut { get; set; }

        /// <summary>
        /// Optionspremie.
        /// </summary>
        public decimal? Premium { get; set; }

        /// <summary>
        /// Premievaluta.
        /// </summary>
        public string PremiumCurrency { get; set; }

        /// <summary>
        /// Premiedatum (valutadag för premien).
        /// </summary>
        public DateTime? PremiumDate { get; set; }

        /// <summary>
        /// MX3-portfölj (Trade.PortfolioMx3).
        /// </summary>
        public string PortfolioMx3 { get; set; }

        /// <summary>
        /// Soft delete-flagga på Trade.
        /// </summary>
        public bool TradeIsDeleted { get; set; }

        /// <summary>
        /// Senaste uppdateringstid på Trade.
        /// </summary>
        public DateTime TradeLastUpdatedUtc { get; set; }

        /// <summary>
        /// Senast uppdaterad av (Trade.LastUpdatedBy).
        /// </summary>
        public string TradeLastUpdatedBy { get; set; }

        // ----------------------------------------------------
        // SystemLink-del (TradeSystemLink)
        // ----------------------------------------------------

        /// <summary>
        /// Primärnyckel för systemlänken (TradeSystemLink.SystemLinkId).
        /// </summary>
        public long SystemLinkId { get; set; }

        /// <summary>
        /// Systemet länken gäller (MX3, CALYPSO, VOLBROKER_STP, RTNS).
        /// </summary>
        public SystemCode SystemCode { get; set; }

        /// <summary>
        /// Status i detta system (NEW, PENDING, BOOKED, ERROR, CANCELLED, osv.).
        /// </summary>
        public TradeSystemStatus Status { get; set; }

        /// <summary>
        /// Systemets egna trade-id (deal-id i MX3, Calypso-id, osv.).
        /// </summary>
        public string SystemTradeId { get; set; }

        /// <summary>
        /// Alias för SystemTradeId, används som mer neutral benämning i tjänster/DTO:er.
        /// </summary>
        public string ExternalTradeId { get; set; }

        /// <summary>
        /// Senaste status-tid i detta system (TradeSystemLink.LastStatusUtc).
        /// </summary>
        /// 
        public DateTime SystemLastStatusUtc { get; set; }

        /// <summary>
        /// Senaste feltext (TradeSystemLink.LastError).
        /// </summary>
        public string SystemLastError { get; set; }

        /// <summary>
        /// Systemets portföljkod (TradeSystemLink.PortfolioCode).
        /// </summary>
        public string SystemPortfolioCode { get; set; }

        /// <summary>
        /// TRUE om traden ska bokas i detta system (BookFlag).
        /// </summary>
        public bool? BookFlag { get; set; }

        /// <summary>
        /// STP-läge (AUTO/MANUAL).
        /// </summary>
        public string StpMode { get; set; }

        /// <summary>
        /// Vem som importerade traden i detta system.
        /// </summary>
        public string ImportedBy { get; set; }

        /// <summary>
        /// Vem som bokade traden i systemet.
        /// </summary>
        public string BookedBy { get; set; }

        /// <summary>
        /// Första gången traden bokades i systemet.
        /// </summary>
        public DateTime? FirstBookedUtc { get; set; }

        /// <summary>
        /// Senaste gången traden bokades i systemet.
        /// </summary>
        public DateTime? LastBookedUtc { get; set; }

        /// <summary>
        /// Intern STP-flagga (TradeSystemLink.StpFlag).
        /// </summary>
        public bool? StpFlag { get; set; }

        /// <summary>
        /// Skapad-tid på TradeSystemLink (CreatedUtc).
        /// </summary>
        public DateTime SystemCreatedUtc { get; set; }

        /// <summary>
        /// Soft delete-flagga på TradeSystemLink.
        /// </summary>
        public bool SystemLinkIsDeleted { get; set; }

        // ----------------------------------------------------
        // Sammanfattnings-/pollingfält
        // ----------------------------------------------------

        /// <summary>
        /// Senaste ändringstid, beräknad som max(Trade.LastUpdatedUtc, TradeSystemLink.LastStatusUtc).
        /// Används som LastChangeUtc i polling/push.
        /// </summary>
        public DateTime LastChangeUtc { get; set; }

        /// <summary>
        /// Initierar strängar till tomma strängar för att slippa null-checks i UI-lagret.
        /// </summary>
        public TradeSystemSummary()
        {
            TradeId = string.Empty;
            SourceType = string.Empty;
            SourceVenueCode = string.Empty;
            CounterpartyCode = string.Empty;
            BrokerCode = string.Empty;
            TraderId = string.Empty;
            InvId = string.Empty;
            ReportingEntityId = string.Empty;
            CurrencyPair = string.Empty;
            Mic = string.Empty;
            Isin = string.Empty;
            BuySell = string.Empty;
            NotionalCurrency = string.Empty;
            SettlementCurrency = string.Empty;
            Uti = string.Empty;
            Tvtic = string.Empty;
            HedgeType = string.Empty;
            CallPut = string.Empty;
            Cut = string.Empty;
            PremiumCurrency = string.Empty;
            PortfolioMx3 = string.Empty;
            TradeLastUpdatedBy = string.Empty;

            SystemTradeId = string.Empty;
            ExternalTradeId = string.Empty;
            SystemLastError = string.Empty;
            SystemPortfolioCode = string.Empty;
            StpMode = string.Empty;
            ImportedBy = string.Empty;
            BookedBy = string.Empty;
        }
    }
}
