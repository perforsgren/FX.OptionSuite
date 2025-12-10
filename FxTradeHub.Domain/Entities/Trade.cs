using System;
using FxTradeHub.Domain.Enums;

namespace FxTradeHub.Domain.Entities
{
    /// <summary>
    /// Kärnmodell för en trade i STP-hubben.
    /// Motsvarar tabellen trade_stp.Trade.
    /// </summary>
    public sealed class Trade
    {
        /// <summary>
        /// Primärnyckel (auto-increment).
        /// </summary>
        public long StpTradeId { get; set; }

        /// <summary>
        /// Externt trade-id från källa (t.ex. Volbroker trade id, legacy blotter-id).
        /// Unikt inom Trade-tabellen.
        /// </summary>
        public string TradeId { get; set; }

        /// <summary>
        /// Produkttyp (SPOT, FWD, SWAP, NDF, OPTION_VANILLA, OPTION_NDO).
        /// Mappar mot DB-fältet Trade.ProductType.
        /// </summary>
        public ProductType ProductType { get; set; }

        /// <summary>
        /// Typ av källa: FIX, EMAIL, MANUAL, FILE_IMPORT ...
        /// 1:1 mot Trade.SourceType (VARCHAR).
        /// </summary>
        public string SourceType { get; set; }

        /// <summary>
        /// Kod för källa/venue: VOLBROKER, BLOOMBERG, RTNS ...
        /// 1:1 mot Trade.SourceVenueCode.
        /// </summary>
        public string SourceVenueCode { get; set; }

        /// <summary>
        /// FK till MessageIn (kan vara null om traden skapats manuellt).
        /// </summary>
        public long? MessageInId { get; set; }

        /// <summary>
        /// Intern motpartskod (MX3-namn), efter mapping från rånamn.
        /// 1:1 mot CounterpartyCode.
        /// </summary>
        public string CounterpartyCode { get; set; }

        /// <summary>
        /// Broker-kod (t.ex. VOLBROKER, BGC, TRADITION).
        /// 1:1 mot BrokerCode.
        /// </summary>
        public string BrokerCode { get; set; }

        /// <summary>
        /// Trader-id (Environment.UserName) som ansvarar för traden.
        /// </summary>
        public string TraderId { get; set; }

        /// <summary>
        /// InvId (kan vara trader eller sales person).
        /// </summary>
        public string InvId { get; set; }

        /// <summary>
        /// ReportingEntity-id. Styrs normalt av InvId.
        /// </summary>
        public string ReportingEntityId { get; set; }

        /// <summary>
        /// Valutapar, t.ex. EURSEK, USDNOK.
        /// </summary>
        public string CurrencyPair { get; set; }

        /// <summary>
        /// MIC-kod (marknadsplats), används för MiFID mm.
        /// </summary>
        public string Mic { get; set; }

        /// <summary>
        /// ISIN om relevant (oftast för vissa produkter).
        /// </summary>
        public string Isin { get; set; }

        /// <summary>
        /// Tradedatum (handelsdatum).
        /// </summary>
        public DateTime TradeDate { get; set; }

        /// <summary>
        /// Exekveringstid i UTC (MiFID/handels-tid).
        /// </summary>
        public DateTime ExecutionTimeUtc { get; set; }

        /// <summary>
        /// Buy/Sell (exact string: "Buy" eller "Sell").
        /// </summary>
        public string BuySell { get; set; }

        /// <summary>
        /// Huvudnotional.
        /// </summary>
        public decimal Notional { get; set; }

        /// <summary>
        /// Valuta för notional (t.ex. EUR, USD).
        /// </summary>
        public string NotionalCurrency { get; set; }

        /// <summary>
        /// Settlement date (value date). För swap är detta far date.
        /// </summary>
        public DateTime SettlementDate { get; set; }

        /// <summary>
        /// Near settlement date för swap (annars null).
        /// </summary>
        public DateTime? NearSettlementDate { get; set; }

        /// <summary>
        /// Flagga för NDF/NDO (icke-levererbar).
        /// </summary>
        public bool IsNonDeliverable { get; set; }

        /// <summary>
        /// Fixing date för NDF/NDO.
        /// </summary>
        public DateTime? FixingDate { get; set; }

        /// <summary>
        /// Settlement currency för NDF/NDO (cash-settlement-valuta).
        /// </summary>
        public string SettlementCurrency { get; set; }

        /// <summary>
        /// UTI (reporting).
        /// </summary>
        public string Uti { get; set; }

        /// <summary>
        /// TVTIC (MiFID).
        /// </summary>
        public string Tvtic { get; set; }

        /// <summary>
        /// Margin-belopp för options (och ev. andra produkter vid behov).
        /// </summary>
        public decimal? Margin { get; set; }

        /// <summary>
        /// Hedge rate (pris).
        /// </summary>
        public decimal? HedgeRate { get; set; }

        /// <summary>
        /// Spot rate (för swap/FWD uppdelning).
        /// </summary>
        public decimal? SpotRate { get; set; }

        /// <summary>
        /// Swap points (för swap/FWD).
        /// </summary>
        public decimal? SwapPoints { get; set; }

        /// <summary>
        /// Hedge-typ: SPOT, FWD, SWAP, NDF (string).
        /// </summary>
        public string HedgeType { get; set; }

        /// <summary>
        /// Option direction: "Call" eller "Put" (för optioner).
        /// </summary>
        public string CallPut { get; set; }

        /// <summary>
        /// Strikepris för optioner.
        /// </summary>
        public decimal? Strike { get; set; }

        /// <summary>
        /// Expiry date för optioner.
        /// </summary>
        public DateTime? ExpiryDate { get; set; }

        /// <summary>
        /// Cut (t.ex. "NYC", "TKO").
        /// </summary>
        public string Cut { get; set; }

        /// <summary>
        /// Premiumbelopp för optioner.
        /// </summary>
        public decimal? Premium { get; set; }

        /// <summary>
        /// Valuta för premium.
        /// </summary>
        public string PremiumCurrency { get; set; }

        /// <summary>
        /// Premium date (betalningsdatum för premium).
        /// </summary>
        public DateTime? PremiumDate { get; set; }

        /// <summary>
        /// Soft delete-flagga. true = borttagen från blottern.
        /// </summary>
        public bool IsDeleted { get; set; }

        /// <summary>
        /// Senaste ändringstid (Trade eller TradeSystemLink) i UTC.
        /// Används för polling / synk mot blottern.
        /// </summary>
        public DateTime LastUpdatedUtc { get; set; }

        /// <summary>
        /// Primär MX3-portfölj (kortkod).
        /// Denna är egentligen härledd från lookup, men lagras för enkelhets skull.
        /// </summary>
        public string PortfolioMx3 { get; set; }

        /// <summary>
        /// Calypso-bok som traden ska bokas på i Calypso.
        /// Motsvarar kolumnen trade_stp.trade.CalypsoBook.
        /// Sätts typiskt via mapping-tabellen stp_calypso_book_user baserat på TraderId.
        /// </summary>
        public string CalypsoBook { get; set; }


        /// <summary>
        /// Skapar en ny trade med defaultvärden för strängfält.
        /// Numeriska fält lämnas på sina standardvärden (0 eller null) och
        /// datumfält sätts inte här utan fylls vid parsning/normalisering.
        /// </summary>
        public Trade()
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
            CalypsoBook = string.Empty;
        }
    }
}
