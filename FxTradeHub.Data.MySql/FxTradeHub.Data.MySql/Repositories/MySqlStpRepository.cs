using System;
using System.Collections.Generic;
using System.Data;
using FxTradeHub.Domain.Entities;
using FxTradeHub.Domain.Enums;
using FxTradeHub.Domain.Interfaces;
using MySql.Data.MySqlClient;

namespace FxTradeHub.Data.MySql.Repositories
{
    /// <summary>
    /// MySQL-implementation av IStpRepository mot schemat trade_stp.
    /// Den här klassen använder ren ADO.NET (MySqlConnection/MySqlCommand).
    /// </summary>
    public sealed class MySqlStpRepository : IStpRepository
    {
        private readonly string _connectionString;

        /// <summary>
        /// Skapar ett nytt repository med given connection string.
        /// Exempel: "Server=srv78506;Port=3306;Database=trade_stp;User Id=fxopt;Password=...;SslMode=None;TreatTinyAsBoolean=false;"
        /// </summary>
        /// <param name="connectionString">Connection string mot trade_stp-databasen.</param>
        public MySqlStpRepository(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("Connection string must not be empty.", "connectionString");

            _connectionString = connectionString;
        }

        /// <summary>
        /// Skapar en ny MySqlConnection. Används internt per operation.
        /// </summary>
        private MySqlConnection CreateConnection()
        {
            return new MySqlConnection(_connectionString);
        }

        /// <summary>
        /// Infogar en ny rad i tabellen trade_stp.MessageIn och returnerar det genererade MessageInId.
        /// (Implementeras i senare steg när FIX/mail-ingest kopplas på.)
        /// </summary>
        public long InsertMessageIn(MessageIn message)
        {
            throw new NotImplementedException("InsertMessageIn is not implemented yet.");
        }

        /// <summary>
        /// Infogar en ny rad i tabellen trade_stp.Trade och returnerar det genererade StpTradeId.
        /// </summary>
        /// <param name="trade">Trade-objekt att spara.</param>
        /// <returns>Genererat StpTradeId från databasen.</returns>
        public long InsertTrade(Trade trade)
        {
            if (trade == null)
                throw new ArgumentNullException("trade");

            // Sätt LastUpdatedUtc om den inte är satt.
            if (trade.LastUpdatedUtc == default(DateTime))
                trade.LastUpdatedUtc = DateTime.UtcNow;

            const string sql = @"
INSERT INTO trade
(
    TradeId,
    ProductType,
    SourceType,
    SourceVenueCode,
    MessageInId,
    CounterpartyCode,
    BrokerCode,
    TraderId,
    InvId,
    ReportingEntityId,
    CurrencyPair,
    Mic,
    Isin,
    TradeDate,
    ExecutionTimeUtc,
    BuySell,
    Notional,
    NotionalCurrency,
    SettlementDate,
    NearSettlementDate,
    IsNonDeliverable,
    FixingDate,
    SettlementCurrency,
    Uti,
    Tvtic,
    Margin,
    HedgeRate,
    SpotRate,
    SwapPoints,
    HedgeType,
    CallPut,
    Strike,
    ExpiryDate,
    Cut,
    Premium,
    PremiumCurrency,
    PremiumDate,
    IsDeleted,
    LastUpdatedUtc,
    PortfolioMx3
)
VALUES
(
    @TradeId,
    @ProductType,
    @SourceType,
    @SourceVenueCode,
    @MessageInId,
    @CounterpartyCode,
    @BrokerCode,
    @TraderId,
    @InvId,
    @ReportingEntityId,
    @CurrencyPair,
    @Mic,
    @Isin,
    @TradeDate,
    @ExecutionTimeUtc,
    @BuySell,
    @Notional,
    @NotionalCurrency,
    @SettlementDate,
    @NearSettlementDate,
    @IsNonDeliverable,
    @FixingDate,
    @SettlementCurrency,
    @Uti,
    @Tvtic,
    @Margin,
    @HedgeRate,
    @SpotRate,
    @SwapPoints,
    @HedgeType,
    @CallPut,
    @Strike,
    @ExpiryDate,
    @Cut,
    @Premium,
    @PremiumCurrency,
    @PremiumDate,
    @IsDeleted,
    @LastUpdatedUtc,
    @PortfolioMx3
);
SELECT LAST_INSERT_ID();";

            using (var connection = CreateConnection())
            {
                connection.Open();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    command.CommandType = CommandType.Text;

                    // Obligatoriska fält
                    command.Parameters.Add("@TradeId", MySqlDbType.VarChar, 50).Value = trade.TradeId;
                    command.Parameters.Add("@ProductType", MySqlDbType.VarChar, 30).Value = MapProductTypeToDatabaseValue(trade.ProductType);
                    command.Parameters.Add("@SourceType", MySqlDbType.VarChar, 20).Value = trade.SourceType;
                    command.Parameters.Add("@SourceVenueCode", MySqlDbType.VarChar, 50).Value = trade.SourceVenueCode;
                    command.Parameters.Add("@CounterpartyCode", MySqlDbType.VarChar, 100).Value = trade.CounterpartyCode;
                    command.Parameters.Add("@TraderId", MySqlDbType.VarChar, 50).Value = trade.TraderId;
                    command.Parameters.Add("@CurrencyPair", MySqlDbType.VarChar, 20).Value = trade.CurrencyPair;
                    command.Parameters.Add("@TradeDate", MySqlDbType.Date).Value = trade.TradeDate;
                    command.Parameters.Add("@ExecutionTimeUtc", MySqlDbType.DateTime, 3).Value = trade.ExecutionTimeUtc;
                    command.Parameters.Add("@BuySell", MySqlDbType.VarChar, 10).Value = trade.BuySell;
                    command.Parameters.Add("@Notional", MySqlDbType.Decimal).Value = trade.Notional;
                    command.Parameters.Add("@NotionalCurrency", MySqlDbType.VarChar, 10).Value = trade.NotionalCurrency;
                    command.Parameters.Add("@SettlementDate", MySqlDbType.Date).Value = trade.SettlementDate;

                    // Nullable / optional fält
                    command.Parameters.Add("@MessageInId", MySqlDbType.Int64).Value =
                        trade.MessageInId.HasValue ? (object)trade.MessageInId.Value : DBNull.Value;

                    command.Parameters.Add("@BrokerCode", MySqlDbType.VarChar, 50).Value =
                        string.IsNullOrEmpty(trade.BrokerCode) ? (object)DBNull.Value : trade.BrokerCode;

                    command.Parameters.Add("@InvId", MySqlDbType.VarChar, 50).Value =
                        string.IsNullOrEmpty(trade.InvId) ? (object)DBNull.Value : trade.InvId;

                    command.Parameters.Add("@ReportingEntityId", MySqlDbType.VarChar, 50).Value =
                        string.IsNullOrEmpty(trade.ReportingEntityId) ? (object)DBNull.Value : trade.ReportingEntityId;

                    command.Parameters.Add("@Mic", MySqlDbType.VarChar, 20).Value =
                        string.IsNullOrEmpty(trade.Mic) ? (object)DBNull.Value : trade.Mic;

                    command.Parameters.Add("@Isin", MySqlDbType.VarChar, 50).Value =
                        string.IsNullOrEmpty(trade.Isin) ? (object)DBNull.Value : trade.Isin;

                    command.Parameters.Add("@NearSettlementDate", MySqlDbType.Date).Value =
                        trade.NearSettlementDate.HasValue ? (object)trade.NearSettlementDate.Value : DBNull.Value;

                    command.Parameters.Add("@IsNonDeliverable", MySqlDbType.Bit).Value = trade.IsNonDeliverable;

                    command.Parameters.Add("@FixingDate", MySqlDbType.Date).Value =
                        trade.FixingDate.HasValue ? (object)trade.FixingDate.Value : DBNull.Value;

                    command.Parameters.Add("@SettlementCurrency", MySqlDbType.VarChar, 10).Value =
                        string.IsNullOrEmpty(trade.SettlementCurrency) ? (object)DBNull.Value : trade.SettlementCurrency;

                    command.Parameters.Add("@Uti", MySqlDbType.VarChar, 100).Value =
                        string.IsNullOrEmpty(trade.Uti) ? (object)DBNull.Value : trade.Uti;

                    command.Parameters.Add("@Tvtic", MySqlDbType.VarChar, 100).Value =
                        string.IsNullOrEmpty(trade.Tvtic) ? (object)DBNull.Value : trade.Tvtic;

                    command.Parameters.Add("@Margin", MySqlDbType.Decimal).Value =
                        trade.Margin.HasValue ? (object)trade.Margin.Value : DBNull.Value;

                    command.Parameters.Add("@HedgeRate", MySqlDbType.Decimal).Value =
                        trade.HedgeRate.HasValue ? (object)trade.HedgeRate.Value : DBNull.Value;

                    command.Parameters.Add("@SpotRate", MySqlDbType.Decimal).Value =
                        trade.SpotRate.HasValue ? (object)trade.SpotRate.Value : DBNull.Value;

                    command.Parameters.Add("@SwapPoints", MySqlDbType.Decimal).Value =
                        trade.SwapPoints.HasValue ? (object)trade.SwapPoints.Value : DBNull.Value;

                    command.Parameters.Add("@HedgeType", MySqlDbType.VarChar, 20).Value =
                        string.IsNullOrEmpty(trade.HedgeType) ? (object)DBNull.Value : trade.HedgeType;

                    command.Parameters.Add("@CallPut", MySqlDbType.VarChar, 4).Value =
                        string.IsNullOrEmpty(trade.CallPut) ? (object)DBNull.Value : trade.CallPut;

                    command.Parameters.Add("@Strike", MySqlDbType.Decimal).Value =
                        trade.Strike.HasValue ? (object)trade.Strike.Value : DBNull.Value;

                    command.Parameters.Add("@ExpiryDate", MySqlDbType.Date).Value =
                        trade.ExpiryDate.HasValue ? (object)trade.ExpiryDate.Value : DBNull.Value;

                    command.Parameters.Add("@Cut", MySqlDbType.VarChar, 20).Value =
                        string.IsNullOrEmpty(trade.Cut) ? (object)DBNull.Value : trade.Cut;

                    command.Parameters.Add("@Premium", MySqlDbType.Decimal).Value =
                        trade.Premium.HasValue ? (object)trade.Premium.Value : DBNull.Value;

                    command.Parameters.Add("@PremiumCurrency", MySqlDbType.VarChar, 10).Value =
                        string.IsNullOrEmpty(trade.PremiumCurrency) ? (object)DBNull.Value : trade.PremiumCurrency;

                    command.Parameters.Add("@PremiumDate", MySqlDbType.Date).Value =
                        trade.PremiumDate.HasValue ? (object)trade.PremiumDate.Value : DBNull.Value;

                    command.Parameters.Add("@IsDeleted", MySqlDbType.Bit).Value = trade.IsDeleted;

                    command.Parameters.Add("@LastUpdatedUtc", MySqlDbType.DateTime, 3).Value = trade.LastUpdatedUtc;

                    command.Parameters.Add("@PortfolioMx3", MySqlDbType.VarChar, 50).Value =
                        string.IsNullOrEmpty(trade.PortfolioMx3) ? (object)DBNull.Value : trade.PortfolioMx3;

                    object result = command.ExecuteScalar();

                    long id;
                    if (result == null || result == DBNull.Value || !long.TryParse(result.ToString(), out id))
                        throw new InvalidOperationException("Failed to retrieve LAST_INSERT_ID() for Trade.");

                    trade.StpTradeId = id;
                    return id;
                }
            }
        }

        /// <summary>
        /// Infogar en ny rad i tabellen trade_stp.tradesystemlink och returnerar det genererade SystemLinkId.
        /// Full 1:1-mappning mot TradeSystemLink-entiteten.
        /// </summary>
        /// <param name="link">TradeSystemLink-objekt att spara.</param>
        /// <returns>Genererat SystemLinkId från databasen.</returns>
        public long InsertTradeSystemLink(TradeSystemLink link)
        {
            if (link == null)
                throw new ArgumentNullException("link");

            // Sätt default-tider om de inte är satta.
            if (link.CreatedUtc == default(DateTime))
                link.CreatedUtc = DateTime.UtcNow;

            if (link.LastUpdatedUtc == default(DateTime))
                link.LastUpdatedUtc = link.CreatedUtc;

            const string sql = @"
INSERT INTO tradesystemlink
(
    StpTradeId,
    SystemCode,
    SystemTradeId,
    Status,
    LastStatusUtc,
    LastError,
    CreatedUtc,
    PortfolioCode,
    BookFlag,
    StpMode,
    ImportedBy,
    BookedBy,
    FirstBookedUtc,
    LastBookedUtc,
    StpFlag,
    IsDeleted
)
VALUES
(
    @StpTradeId,
    @SystemCode,
    @SystemTradeId,
    @Status,
    @LastStatusUtc,
    @LastError,
    @CreatedUtc,
    @PortfolioCode,
    @BookFlag,
    @StpMode,
    @ImportedBy,
    @BookedBy,
    @FirstBookedUtc,
    @LastBookedUtc,
    @StpFlag,
    @IsDeleted
);
SELECT LAST_INSERT_ID();";

            using (var connection = CreateConnection())
            {
                connection.Open();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    command.CommandType = CommandType.Text;

                    command.Parameters.Add("@StpTradeId", MySqlDbType.Int64).Value = link.StpTradeId;

                    // SystemCode/Status via centraliserade mapping-metoder.
                    string systemCode = MapSystemCodeToDatabaseValue(link.SystemCode);
                    string status = MapTradeSystemStatusToDatabaseValue(link.Status);

                    command.Parameters.Add("@SystemCode", MySqlDbType.VarChar, 30).Value = systemCode;

                    // ExternalTradeId -> SystemTradeId
                    command.Parameters.Add("@SystemTradeId", MySqlDbType.VarChar, 100).Value =
                        string.IsNullOrEmpty(link.ExternalTradeId) ? (object)DBNull.Value : link.ExternalTradeId;

                    command.Parameters.Add("@Status", MySqlDbType.VarChar, 20).Value = status;

                    // LastStatusUtc <- LastUpdatedUtc
                    command.Parameters.Add("@LastStatusUtc", MySqlDbType.DateTime, 3).Value = link.LastUpdatedUtc;

                    // LastError: kombinera ErrorCode + ErrorMessage
                    string lastError = null;
                    if (!string.IsNullOrEmpty(link.ErrorCode) && !string.IsNullOrEmpty(link.ErrorMessage))
                    {
                        lastError = link.ErrorCode + ": " + link.ErrorMessage;
                    }
                    else if (!string.IsNullOrEmpty(link.ErrorMessage))
                    {
                        lastError = link.ErrorMessage;
                    }
                    else if (!string.IsNullOrEmpty(link.ErrorCode))
                    {
                        lastError = link.ErrorCode;
                    }

                    command.Parameters.Add("@LastError", MySqlDbType.Text).Value =
                        string.IsNullOrEmpty(lastError) ? (object)DBNull.Value : lastError;

                    command.Parameters.Add("@CreatedUtc", MySqlDbType.DateTime, 3).Value = link.CreatedUtc;

                    // PortfolioCode
                    command.Parameters.Add("@PortfolioCode", MySqlDbType.VarChar, 50).Value =
                        string.IsNullOrEmpty(link.PortfolioCode) ? (object)DBNull.Value : link.PortfolioCode;

                    // BookFlag (nullable bool -> tinyint)
                    if (link.BookFlag.HasValue)
                    {
                        command.Parameters.Add("@BookFlag", MySqlDbType.Bit).Value = link.BookFlag.Value ? 1 : 0;
                    }
                    else
                    {
                        command.Parameters.Add("@BookFlag", MySqlDbType.Bit).Value = DBNull.Value;
                    }

                    // StpMode, ImportedBy, BookedBy
                    command.Parameters.Add("@StpMode", MySqlDbType.VarChar, 20).Value =
                        string.IsNullOrEmpty(link.StpMode) ? (object)DBNull.Value : link.StpMode;

                    command.Parameters.Add("@ImportedBy", MySqlDbType.VarChar, 50).Value =
                        string.IsNullOrEmpty(link.ImportedBy) ? (object)DBNull.Value : link.ImportedBy;

                    command.Parameters.Add("@BookedBy", MySqlDbType.VarChar, 50).Value =
                        string.IsNullOrEmpty(link.BookedBy) ? (object)DBNull.Value : link.BookedBy;

                    // First/LastBookedUtc
                    command.Parameters.Add("@FirstBookedUtc", MySqlDbType.DateTime, 3).Value =
                        link.FirstBookedUtc.HasValue ? (object)link.FirstBookedUtc.Value : DBNull.Value;

                    command.Parameters.Add("@LastBookedUtc", MySqlDbType.DateTime, 3).Value =
                        link.LastBookedUtc.HasValue ? (object)link.LastBookedUtc.Value : DBNull.Value;

                    // StpFlag
                    if (link.StpFlag.HasValue)
                    {
                        command.Parameters.Add("@StpFlag", MySqlDbType.Bit).Value = link.StpFlag.Value ? 1 : 0;
                    }
                    else
                    {
                        command.Parameters.Add("@StpFlag", MySqlDbType.Bit).Value = DBNull.Value;
                    }

                    // IsDeleted (bool -> tinyint)
                    command.Parameters.Add("@IsDeleted", MySqlDbType.Bit).Value = link.IsDeleted ? 1 : 0;

                    object result = command.ExecuteScalar();

                    long id;
                    if (result == null || result == DBNull.Value || !long.TryParse(result.ToString(), out id))
                        throw new InvalidOperationException("Failed to retrieve LAST_INSERT_ID() for TradeSystemLink.");

                    link.TradeSystemLinkId = id;
                    return id;
                }
            }
        }







        /// <summary>
        /// Infogar en ny rad i tabellen trade_stp.tradeworkflowevent och returnerar det genererade WorkflowEventId.
        /// Mappning:
        /// - TradeWorkflowEvent.TradeWorkflowEventId &lt;--&gt; tradeworkflowevent.WorkflowEventId
        /// - StpTradeId &lt;--&gt; StpTradeId
        /// - EventTimeUtc &lt;--&gt; TimestampUtc
        /// - EventType &lt;--&gt; EventType
        /// - SystemCode? (enum) lagras som upper-case string i SystemCode (eller NULL)
        /// - InitiatorId &lt;--&gt; UserId
        /// - Description/FieldName/OldValue/NewValue packas ihop till Details.
        /// </summary>
        /// <param name="evt">TradeWorkflowEvent-objekt att spara.</param>
        /// <returns>Genererat WorkflowEventId från databasen.</returns>
        public long InsertWorkflowEvent(TradeWorkflowEvent evt)
        {
            if (evt == null)
                throw new ArgumentNullException("evt");

            if (evt.EventTimeUtc == default(DateTime))
                evt.EventTimeUtc = DateTime.UtcNow;

            const string sql = @"
INSERT INTO tradeworkflowevent
(
    StpTradeId,
    TimestampUtc,
    EventType,
    SystemCode,
    UserId,
    Details
)
VALUES
(
    @StpTradeId,
    @TimestampUtc,
    @EventType,
    @SystemCode,
    @UserId,
    @Details
);
SELECT LAST_INSERT_ID();";

            using (var connection = CreateConnection())
            {
                connection.Open();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    command.CommandType = CommandType.Text;

                    command.Parameters.Add("@StpTradeId", MySqlDbType.Int64).Value = evt.StpTradeId;
                    command.Parameters.Add("@TimestampUtc", MySqlDbType.DateTime, 3).Value = evt.EventTimeUtc;

                    command.Parameters.Add("@EventType", MySqlDbType.VarChar, 50).Value =
                        string.IsNullOrEmpty(evt.EventType) ? (object)DBNull.Value : evt.EventType;

                    if (evt.SystemCode.HasValue)
                    {
                        string systemCode = evt.SystemCode.Value.ToString().ToUpperInvariant();
                        command.Parameters.Add("@SystemCode", MySqlDbType.VarChar, 30).Value = systemCode;
                    }
                    else
                    {
                        command.Parameters.Add("@SystemCode", MySqlDbType.VarChar, 30).Value = DBNull.Value;
                    }

                    command.Parameters.Add("@UserId", MySqlDbType.VarChar, 50).Value =
                        string.IsNullOrEmpty(evt.InitiatorId) ? (object)DBNull.Value : evt.InitiatorId;

                    // Bygg Details: Description + ev. fält-info
                    string details = string.IsNullOrEmpty(evt.Description) ? null : evt.Description;

                    bool hasFieldChange =
                        !string.IsNullOrEmpty(evt.FieldName) ||
                        !string.IsNullOrEmpty(evt.OldValue) ||
                        !string.IsNullOrEmpty(evt.NewValue);

                    if (hasFieldChange)
                    {
                        string fieldPart =
                            "Field=" + (evt.FieldName ?? "<null>") +
                            ", Old=" + (evt.OldValue ?? "<null>") +
                            ", New=" + (evt.NewValue ?? "<null>");

                        details = string.IsNullOrEmpty(details)
                            ? fieldPart
                            : details + " | " + fieldPart;
                    }

                    command.Parameters.Add("@Details", MySqlDbType.Text).Value =
                        string.IsNullOrEmpty(details) ? (object)DBNull.Value : details;

                    object result = command.ExecuteScalar();

                    long id;
                    if (result == null || result == DBNull.Value || !long.TryParse(result.ToString(), out id))
                        throw new InvalidOperationException("Failed to retrieve LAST_INSERT_ID() for TradeWorkflowEvent.");

                    evt.TradeWorkflowEventId = id;
                    return id;
                }
            }
        }

        /// <summary>
        /// Läser alla trades med deras systemlänkar från databasen
        /// och returnerar en sammanfattad vy per (trade, system).
        /// </summary>
        /// <returns>Lista med TradeSystemSummary-instancer.</returns>
        public IList<TradeSystemSummary> GetAllTradeSystemSummaries()
        {
            const string sql = @"
SELECT
    t.StpTradeId,
    t.TradeId,
    t.ProductType,
    t.CurrencyPair,
    t.TradeDate,
    t.ExecutionTimeUtc,
    t.BuySell,
    t.Notional,
    t.NotionalCurrency,
    l.SystemLinkId,
    l.SystemCode,
    l.Status
FROM trade t
INNER JOIN tradesystemlink l ON l.StpTradeId = t.StpTradeId
WHERE t.IsDeleted = 0
  AND l.IsDeleted = 0;";

            var result = new List<TradeSystemSummary>();

            using (var connection = CreateConnection())
            {
                connection.Open();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    command.CommandType = CommandType.Text;

                    using (var reader = command.ExecuteReader())
                    {
                        var stpTradeIdOrdinal = reader.GetOrdinal("StpTradeId");
                        var tradeIdOrdinal = reader.GetOrdinal("TradeId");
                        var productTypeOrdinal = reader.GetOrdinal("ProductType");
                        var currencyPairOrdinal = reader.GetOrdinal("CurrencyPair");
                        var tradeDateOrdinal = reader.GetOrdinal("TradeDate");
                        var executionTimeUtcOrdinal = reader.GetOrdinal("ExecutionTimeUtc");
                        var buySellOrdinal = reader.GetOrdinal("BuySell");
                        var notionalOrdinal = reader.GetOrdinal("Notional");
                        var notionalCurrencyOrdinal = reader.GetOrdinal("NotionalCurrency");
                        var systemLinkIdOrdinal = reader.GetOrdinal("SystemLinkId");
                        var systemCodeOrdinal = reader.GetOrdinal("SystemCode");
                        var statusOrdinal = reader.GetOrdinal("Status");

                        while (reader.Read())
                        {
                            var summary = new TradeSystemSummary
                            {
                                StpTradeId = reader.GetInt64(stpTradeIdOrdinal),
                                TradeId = reader.GetString(tradeIdOrdinal),
                                ProductType = MapProductTypeFromDatabaseValue(reader.GetString(productTypeOrdinal)),
                                CurrencyPair = reader.GetString(currencyPairOrdinal),
                                TradeDate = reader.GetDateTime(tradeDateOrdinal),
                                ExecutionTimeUtc = reader.GetDateTime(executionTimeUtcOrdinal),
                                BuySell = reader.GetString(buySellOrdinal),
                                Notional = reader.GetDecimal(notionalOrdinal),
                                NotionalCurrency = reader.GetString(notionalCurrencyOrdinal),
                                SystemLinkId = reader.GetInt64(systemLinkIdOrdinal),
                                SystemCode = MapSystemCodeFromDatabaseValue(reader.GetString(systemCodeOrdinal)),
                                Status = MapTradeSystemStatusFromDatabaseValue(reader.GetString(statusOrdinal))
                            };

                            result.Add(summary);
                        }
                    }
                }
            }

            return result;
        }



        /// <summary>
        /// Mappar ProductType-enum till databaskod (VARCHAR) i Trade.ProductType.
        /// </summary>
        private static string MapProductTypeToDatabaseValue(ProductType productType)
        {
            switch (productType)
            {
                case ProductType.Spot:
                    return "SPOT";
                case ProductType.Fwd:
                    return "FWD";
                case ProductType.Swap:
                    return "SWAP";
                case ProductType.Ndf:
                    return "NDF";
                case ProductType.OptionVanilla:
                    return "OPTION_VANILLA";
                case ProductType.OptionNdo:
                    return "OPTION_NDO";
                default:
                    throw new ArgumentOutOfRangeException("productType", productType, "Unknown ProductType value.");
            }
        }

        /// <summary>
        /// Mappar databaskod (VARCHAR) i Trade.ProductType till ProductType-enum.
        /// </summary>
        private static ProductType MapProductTypeFromDatabaseValue(string dbValue)
        {
            if (string.IsNullOrEmpty(dbValue))
                throw new ArgumentException("ProductType database value must not be null or empty.", "dbValue");

            switch (dbValue.ToUpperInvariant())
            {
                case "SPOT":
                    return ProductType.Spot;
                case "FWD":
                    return ProductType.Fwd;
                case "SWAP":
                    return ProductType.Swap;
                case "NDF":
                    return ProductType.Ndf;
                case "OPTION_VANILLA":
                    return ProductType.OptionVanilla;
                case "OPTION_NDO":
                    return ProductType.OptionNdo;
                default:
                    throw new InvalidOperationException("Unknown ProductType database value: " + dbValue);
            }
        }

        /// <summary>
        /// Mappar databaskod (VARCHAR) i TradeSystemLink.SystemCode till SystemCode-enum.
        /// </summary>
        private static SystemCode MapSystemCodeFromDatabaseValue(string dbValue)
        {
            if (string.IsNullOrEmpty(dbValue))
                throw new ArgumentException("SystemCode database value must not be null or empty.", "dbValue");

            switch (dbValue.ToUpperInvariant())
            {
                case "MX3":
                    return SystemCode.Mx3;
                case "CALYPSO":
                    return SystemCode.Calypso;
                case "VOLBROKER_STP":
                    return SystemCode.VolbrokerStp;
                case "RTNS":
                    return SystemCode.Rtns;
                default:
                    throw new InvalidOperationException("Unknown SystemCode database value: " + dbValue);
            }
        }

        /// <summary>
        /// Mappar databaskod (VARCHAR) i TradeSystemLink.Status till TradeSystemStatus-enum.
        /// </summary>
        private static TradeSystemStatus MapTradeSystemStatusFromDatabaseValue(string dbValue)
        {
            if (string.IsNullOrEmpty(dbValue))
                throw new ArgumentException("Status database value must not be null or empty.", "dbValue");

            switch (dbValue.ToUpperInvariant())
            {
                case "NEW":
                    return TradeSystemStatus.New;
                case "PENDING":
                    return TradeSystemStatus.Pending;
                case "BOOKED":
                    return TradeSystemStatus.Booked;
                case "ERROR":
                    return TradeSystemStatus.Error;
                case "CANCELLED":
                    return TradeSystemStatus.Cancelled;
                case "READY_TO_ACK":
                    return TradeSystemStatus.ReadyToAck;
                case "ACK_SENT":
                    return TradeSystemStatus.AckSent;
                case "ACK_ERROR":
                    return TradeSystemStatus.AckError;
                default:
                    throw new InvalidOperationException("Unknown TradeSystemStatus database value: " + dbValue);
            }
        }


        /// <summary>
        /// Mappar SystemCode-enum till databaskod (VARCHAR) i TradeSystemLink.SystemCode.
        /// </summary>
        /// <param name="systemCode">SystemCode-värde att mappa.</param>
        /// <returns>Övre-case string representation (t.ex. "MX3").</returns>
        private static string MapSystemCodeToDatabaseValue(SystemCode systemCode)
        {
            switch (systemCode)
            {
                case SystemCode.Mx3:
                    return "MX3";
                case SystemCode.Calypso:
                    return "CALYPSO";
                case SystemCode.VolbrokerStp:
                    return "VOLBROKER_STP";
                case SystemCode.Rtns:
                    return "RTNS";
                default:
                    throw new ArgumentOutOfRangeException(
                        "systemCode",
                        systemCode,
                        "Unknown SystemCode value.");
            }
        }

        /// <summary>
        /// Mappar TradeSystemStatus-enum till databaskod (VARCHAR) i TradeSystemLink.Status.
        /// </summary>
        /// <param name="status">Status-värde att mappa.</param>
        /// <returns>Övre-case string representation (t.ex. "NEW").</returns>
        private static string MapTradeSystemStatusToDatabaseValue(TradeSystemStatus status)
        {
            switch (status)
            {
                case TradeSystemStatus.New:
                    return "NEW";
                case TradeSystemStatus.Pending:
                    return "PENDING";
                case TradeSystemStatus.Booked:
                    return "BOOKED";
                case TradeSystemStatus.Error:
                    return "ERROR";
                case TradeSystemStatus.Cancelled:
                    return "CANCELLED";
                case TradeSystemStatus.ReadyToAck:
                    return "READY_TO_ACK";
                case TradeSystemStatus.AckSent:
                    return "ACK_SENT";
                case TradeSystemStatus.AckError:
                    return "ACK_ERROR";
                default:
                    throw new ArgumentOutOfRangeException(
                        "status",
                        status,
                        "Unknown TradeSystemStatus value.");
            }
        }



    }
}
