using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using FxTradeHub.Domain.Entities;
using FxTradeHub.Domain.Enums;
using FxTradeHub.Domain.Interfaces;
using MySql.Data.MySqlClient;

namespace FxTradeHub.Data.MySql.Repositories
{
    /// <summary>
    /// Asynkron MySQL-implementation av IStpRepositoryAsync mot schemat trade_stp.
    /// Använder riktiga async-anrop (OpenAsync/ExecuteReaderAsync/ReadAsync)
    /// för läsning av TradeSystemSummary-readmodellen.
    /// </summary>
    public sealed class MySqlStpRepositoryAsync : IStpRepositoryAsync
    {
        private readonly string _connectionString;

        /// <summary>
        /// Skapar ett nytt asynkront repository med given connection string.
        /// </summary>
        /// <param name="connectionString">Connection string mot trade_stp.</param>
        public MySqlStpRepositoryAsync(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("Connection string must not be empty.", "connectionString");

            _connectionString = connectionString;
        }

        /// <summary>
        /// Skapar en ny MySqlConnection. Används internt per operation.
        /// </summary>
        /// <returns>Ny MySqlConnection instans.</returns>
        private MySqlConnection CreateConnection()
        {
            return new MySqlConnection(_connectionString);
        }

        /// <summary>
        /// Asynkront hämtar en lista av TradeSystemSummary-rader baserat på filtreringsparametrar.
        /// Joinar Trade + TradeSystemLink på serversidan och filtrerar bort soft-deletade
        /// trades (Trade.IsDeleted = 0). Sorterar på TradeDate DESC, StpTradeId DESC, SystemCode ASC
        /// och begränsar antalet rader med LIMIT om maxRows är satt.
        /// </summary>
        public async Task<IList<TradeSystemSummary>> GetTradeSystemSummariesAsync(
            DateTime? fromTradeDate,
            DateTime? toTradeDate,
            string productType,
            string sourceType,
            string sourceVenueCode,
            string counterpartyCode,
            string traderId,
            string currencyPair,
            int? maxRows,
            string currentUserId)
        {
            var results = new List<TradeSystemSummary>();

            using (var connection = CreateConnection())
            {
                await connection.OpenAsync().ConfigureAwait(false);

                var sqlBuilder = new System.Text.StringBuilder();
                sqlBuilder.Append(@"
SELECT
    t.StpTradeId,
    t.TradeId,
    t.ProductType,
    t.SourceType,
    t.SourceVenueCode,
    t.CounterpartyCode,
    t.BrokerCode,
    t.TraderId,
    t.InvId,
    t.ReportingEntityId,
    t.CurrencyPair,
    t.Mic,
    t.Isin,
    t.TradeDate,
    t.ExecutionTimeUtc,
    t.BuySell,
    t.Notional,
    t.NotionalCurrency,
    t.SettlementDate,
    t.NearSettlementDate,
    t.IsNonDeliverable,
    t.FixingDate,
    t.SettlementCurrency,
    t.Uti,
    t.Tvtic,
    t.Margin,
    t.HedgeRate,
    t.SpotRate,
    t.SwapPoints,
    t.HedgeType,
    t.CallPut,
    t.Strike,
    t.ExpiryDate,
    t.Cut,
    t.Premium,
    t.PremiumCurrency,
    t.PremiumDate,
    t.PortfolioMx3,
    t.LastUpdatedUtc AS TradeLastUpdatedUtc,
    t.LastUpdatedBy AS TradeLastUpdatedBy,

    l.SystemLinkId,
    l.SystemCode,
    l.SystemTradeId,
    l.Status,
    l.LastStatusUtc,
    l.LastError,
    l.PortfolioCode,
    l.BookFlag,
    l.StpMode,
    l.ImportedBy,
    l.BookedBy,
    l.FirstBookedUtc,
    l.LastBookedUtc,
    l.StpFlag,
    l.CreatedUtc AS SystemCreatedUtc,
    l.IsDeleted AS SystemLinkIsDeleted
FROM trade_stp.Trade t
INNER JOIN trade_stp.TradeSystemLink l ON l.StpTradeId = t.StpTradeId
WHERE t.IsDeleted = 0
");

                // Dynamiska filter – exakt samma logik som i den synkrona varianten.
                if (fromTradeDate.HasValue)
                {
                    sqlBuilder.Append("  AND t.TradeDate >= @FromTradeDate\n");
                }

                if (toTradeDate.HasValue)
                {
                    sqlBuilder.Append("  AND t.TradeDate <= @ToTradeDate\n");
                }

                if (!string.IsNullOrEmpty(productType))
                {
                    sqlBuilder.Append("  AND t.ProductType = @ProductType\n");
                }

                if (!string.IsNullOrEmpty(sourceType))
                {
                    sqlBuilder.Append("  AND t.SourceType = @SourceType\n");
                }

                if (!string.IsNullOrEmpty(counterpartyCode))
                {
                    sqlBuilder.Append("  AND t.CounterpartyCode = @CounterpartyCode\n");
                }

                if (!string.IsNullOrEmpty(traderId))
                {
                    sqlBuilder.Append("  AND t.TraderId = @TraderId\n");
                }

                // OBS: currencyPair och sourceVenueCode finns i signaturen men används inte i v1-SQL:en.
                // De kan läggas till som filter i en senare iteration om vi vill.

                // Sortering: senaste affärer först
                sqlBuilder.Append("ORDER BY t.TradeDate DESC, t.StpTradeId DESC, l.SystemCode ASC\n");

                // Limit
                if (maxRows.HasValue)
                {
                    sqlBuilder.Append("LIMIT @MaxRows");
                }

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sqlBuilder.ToString();
                    command.CommandType = CommandType.Text;

                    if (fromTradeDate.HasValue)
                    {
                        var p = command.Parameters.Add("@FromTradeDate", MySqlDbType.Date);
                        p.Value = fromTradeDate.Value;
                    }

                    if (toTradeDate.HasValue)
                    {
                        var p = command.Parameters.Add("@ToTradeDate", MySqlDbType.Date);
                        p.Value = toTradeDate.Value;
                    }

                    if (!string.IsNullOrEmpty(productType))
                    {
                        var p = command.Parameters.Add("@ProductType", MySqlDbType.VarChar, 30);
                        p.Value = productType;
                    }

                    if (!string.IsNullOrEmpty(sourceType))
                    {
                        var p = command.Parameters.Add("@SourceType", MySqlDbType.VarChar, 20);
                        p.Value = sourceType;
                    }

                    if (!string.IsNullOrEmpty(counterpartyCode))
                    {
                        var p = command.Parameters.Add("@CounterpartyCode", MySqlDbType.VarChar, 100);
                        p.Value = counterpartyCode;
                    }

                    if (!string.IsNullOrEmpty(traderId))
                    {
                        var p = command.Parameters.Add("@TraderId", MySqlDbType.VarChar, 50);
                        p.Value = traderId;
                    }

                    if (maxRows.HasValue)
                    {
                        var p = command.Parameters.Add("@MaxRows", MySqlDbType.Int32);
                        p.Value = maxRows.Value;
                    }

                    using (var reader = await command.ExecuteReaderAsync().ConfigureAwait(false))
                    {
                        // Slå upp ordningar en gång
                        int ordStpTradeId = reader.GetOrdinal("StpTradeId");
                        int ordTradeId = reader.GetOrdinal("TradeId");
                        int ordProductType = reader.GetOrdinal("ProductType");
                        int ordSourceType = reader.GetOrdinal("SourceType");
                        int ordSourceVenueCode = reader.GetOrdinal("SourceVenueCode");
                        int ordCounterpartyCode = reader.GetOrdinal("CounterpartyCode");
                        int ordBrokerCode = reader.GetOrdinal("BrokerCode");
                        int ordTraderId = reader.GetOrdinal("TraderId");
                        int ordInvId = reader.GetOrdinal("InvId");
                        int ordReportingEntityId = reader.GetOrdinal("ReportingEntityId");
                        int ordCurrencyPair = reader.GetOrdinal("CurrencyPair");
                        int ordMic = reader.GetOrdinal("Mic");
                        int ordIsin = reader.GetOrdinal("Isin");
                        int ordTradeDate = reader.GetOrdinal("TradeDate");
                        int ordExecutionTimeUtc = reader.GetOrdinal("ExecutionTimeUtc");
                        int ordBuySell = reader.GetOrdinal("BuySell");
                        int ordNotional = reader.GetOrdinal("Notional");
                        int ordNotionalCurrency = reader.GetOrdinal("NotionalCurrency");
                        int ordSettlementDate = reader.GetOrdinal("SettlementDate");
                        int ordNearSettlementDate = reader.GetOrdinal("NearSettlementDate");
                        int ordIsNonDeliverable = reader.GetOrdinal("IsNonDeliverable");
                        int ordFixingDate = reader.GetOrdinal("FixingDate");
                        int ordSettlementCurrency = reader.GetOrdinal("SettlementCurrency");
                        int ordUti = reader.GetOrdinal("Uti");
                        int ordTvtic = reader.GetOrdinal("Tvtic");
                        int ordMargin = reader.GetOrdinal("Margin");
                        int ordHedgeRate = reader.GetOrdinal("HedgeRate");
                        int ordSpotRate = reader.GetOrdinal("SpotRate");
                        int ordSwapPoints = reader.GetOrdinal("SwapPoints");
                        int ordHedgeType = reader.GetOrdinal("HedgeType");
                        int ordCallPut = reader.GetOrdinal("CallPut");
                        int ordStrike = reader.GetOrdinal("Strike");
                        int ordExpiryDate = reader.GetOrdinal("ExpiryDate");
                        int ordCut = reader.GetOrdinal("Cut");
                        int ordPremium = reader.GetOrdinal("Premium");
                        int ordPremiumCurrency = reader.GetOrdinal("PremiumCurrency");
                        int ordPremiumDate = reader.GetOrdinal("PremiumDate");
                        int ordPortfolioMx3 = reader.GetOrdinal("PortfolioMx3");
                        int ordTradeLastUpdatedUtc = reader.GetOrdinal("TradeLastUpdatedUtc");
                        int ordTradeLastUpdatedBy = reader.GetOrdinal("TradeLastUpdatedBy");

                        int ordSystemLinkId = reader.GetOrdinal("SystemLinkId");
                        int ordSystemCode = reader.GetOrdinal("SystemCode");
                        int ordSystemTradeId = reader.GetOrdinal("SystemTradeId");
                        int ordStatus = reader.GetOrdinal("Status");
                        int ordLastStatusUtc = reader.GetOrdinal("LastStatusUtc");
                        int ordLastError = reader.GetOrdinal("LastError");
                        int ordPortfolioCode = reader.GetOrdinal("PortfolioCode");
                        int ordBookFlag = reader.GetOrdinal("BookFlag");
                        int ordStpMode = reader.GetOrdinal("StpMode");
                        int ordImportedBy = reader.GetOrdinal("ImportedBy");
                        int ordBookedBy = reader.GetOrdinal("BookedBy");
                        int ordFirstBookedUtc = reader.GetOrdinal("FirstBookedUtc");
                        int ordLastBookedUtc = reader.GetOrdinal("LastBookedUtc");
                        int ordStpFlag = reader.GetOrdinal("StpFlag");
                        int ordSystemCreatedUtc = reader.GetOrdinal("SystemCreatedUtc");
                        int ordSystemLinkIsDeleted = reader.GetOrdinal("SystemLinkIsDeleted");

                        while (await reader.ReadAsync().ConfigureAwait(false))
                        {
                            var summary = new TradeSystemSummary
                            {
                                StpTradeId = reader.GetInt64(ordStpTradeId),
                                TradeId = reader.GetString(ordTradeId),
                                ProductType = MapProductTypeFromDatabaseValue(reader.GetString(ordProductType)),
                                SourceType = reader.IsDBNull(ordSourceType) ? string.Empty : reader.GetString(ordSourceType),
                                SourceVenueCode = reader.IsDBNull(ordSourceVenueCode) ? string.Empty : reader.GetString(ordSourceVenueCode),
                                CounterpartyCode = reader.IsDBNull(ordCounterpartyCode) ? string.Empty : reader.GetString(ordCounterpartyCode),
                                BrokerCode = reader.IsDBNull(ordBrokerCode) ? string.Empty : reader.GetString(ordBrokerCode),
                                TraderId = reader.IsDBNull(ordTraderId) ? string.Empty : reader.GetString(ordTraderId),
                                InvId = reader.IsDBNull(ordInvId) ? string.Empty : reader.GetString(ordInvId),
                                ReportingEntityId = reader.IsDBNull(ordReportingEntityId) ? string.Empty : reader.GetString(ordReportingEntityId),
                                CurrencyPair = reader.IsDBNull(ordCurrencyPair) ? string.Empty : reader.GetString(ordCurrencyPair),
                                Mic = reader.IsDBNull(ordMic) ? string.Empty : reader.GetString(ordMic),
                                Isin = reader.IsDBNull(ordIsin) ? string.Empty : reader.GetString(ordIsin),
                                TradeDate = reader.GetDateTime(ordTradeDate),
                                ExecutionTimeUtc = reader.GetDateTime(ordExecutionTimeUtc),
                                BuySell = reader.IsDBNull(ordBuySell) ? string.Empty : reader.GetString(ordBuySell),
                                Notional = reader.GetDecimal(ordNotional),
                                NotionalCurrency = reader.IsDBNull(ordNotionalCurrency) ? string.Empty : reader.GetString(ordNotionalCurrency),
                                SettlementDate = reader.GetDateTime(ordSettlementDate),
                                NearSettlementDate = reader.IsDBNull(ordNearSettlementDate) ? (DateTime?)null : reader.GetDateTime(ordNearSettlementDate),
                                IsNonDeliverable = reader.IsDBNull(ordIsNonDeliverable) ? (bool?)null : reader.GetBoolean(ordIsNonDeliverable),
                                FixingDate = reader.IsDBNull(ordFixingDate) ? (DateTime?)null : reader.GetDateTime(ordFixingDate),
                                SettlementCurrency = reader.IsDBNull(ordSettlementCurrency) ? string.Empty : reader.GetString(ordSettlementCurrency),
                                Uti = reader.IsDBNull(ordUti) ? string.Empty : reader.GetString(ordUti),
                                Tvtic = reader.IsDBNull(ordTvtic) ? string.Empty : reader.GetString(ordTvtic),
                                Margin = reader.IsDBNull(ordMargin) ? (decimal?)null : reader.GetDecimal(ordMargin),
                                HedgeRate = reader.IsDBNull(ordHedgeRate) ? (decimal?)null : reader.GetDecimal(ordHedgeRate),
                                SpotRate = reader.IsDBNull(ordSpotRate) ? (decimal?)null : reader.GetDecimal(ordSpotRate),
                                SwapPoints = reader.IsDBNull(ordSwapPoints) ? (decimal?)null : reader.GetDecimal(ordSwapPoints),
                                HedgeType = reader.IsDBNull(ordHedgeType) ? string.Empty : reader.GetString(ordHedgeType),
                                CallPut = reader.IsDBNull(ordCallPut) ? string.Empty : reader.GetString(ordCallPut),
                                Strike = reader.IsDBNull(ordStrike) ? (decimal?)null : reader.GetDecimal(ordStrike),
                                ExpiryDate = reader.IsDBNull(ordExpiryDate) ? (DateTime?)null : reader.GetDateTime(ordExpiryDate),
                                Cut = reader.IsDBNull(ordCut) ? string.Empty : reader.GetString(ordCut),
                                Premium = reader.IsDBNull(ordPremium) ? (decimal?)null : reader.GetDecimal(ordPremium),
                                PremiumCurrency = reader.IsDBNull(ordPremiumCurrency) ? string.Empty : reader.GetString(ordPremiumCurrency),
                                PremiumDate = reader.IsDBNull(ordPremiumDate) ? (DateTime?)null : reader.GetDateTime(ordPremiumDate),
                                PortfolioMx3 = reader.IsDBNull(ordPortfolioMx3) ? string.Empty : reader.GetString(ordPortfolioMx3),
                                TradeLastUpdatedUtc = reader.GetDateTime(ordTradeLastUpdatedUtc),
                                TradeLastUpdatedBy = reader.IsDBNull(ordTradeLastUpdatedBy) ? string.Empty : reader.GetString(ordTradeLastUpdatedBy),

                                SystemLinkId = reader.GetInt64(ordSystemLinkId),
                                SystemCode = MapSystemCodeFromDatabaseValue(reader.GetString(ordSystemCode)),
                                Status = MapTradeSystemStatusFromDatabaseValue(reader.GetString(ordStatus)),
                                SystemTradeId = reader.IsDBNull(ordSystemTradeId) ? string.Empty : reader.GetString(ordSystemTradeId),
                                ExternalTradeId = reader.IsDBNull(ordSystemTradeId) ? string.Empty : reader.GetString(ordSystemTradeId),
                                SystemLastStatusUtc = reader.GetDateTime(ordLastStatusUtc),
                                SystemLastError = reader.IsDBNull(ordLastError) ? string.Empty : reader.GetString(ordLastError),
                                SystemPortfolioCode = reader.IsDBNull(ordPortfolioCode) ? string.Empty : reader.GetString(ordPortfolioCode),
                                BookFlag = reader.IsDBNull(ordBookFlag) ? (bool?)null : reader.GetBoolean(ordBookFlag),
                                StpMode = reader.IsDBNull(ordStpMode) ? string.Empty : reader.GetString(ordStpMode),
                                ImportedBy = reader.IsDBNull(ordImportedBy) ? string.Empty : reader.GetString(ordImportedBy),
                                BookedBy = reader.IsDBNull(ordBookedBy) ? string.Empty : reader.GetString(ordBookedBy),
                                FirstBookedUtc = reader.IsDBNull(ordFirstBookedUtc) ? (DateTime?)null : reader.GetDateTime(ordFirstBookedUtc),
                                LastBookedUtc = reader.IsDBNull(ordLastBookedUtc) ? (DateTime?)null : reader.GetDateTime(ordLastBookedUtc),
                                StpFlag = reader.IsDBNull(ordStpFlag) ? (bool?)null : reader.GetBoolean(ordStpFlag),
                                SystemCreatedUtc = reader.GetDateTime(ordSystemCreatedUtc),
                                SystemLinkIsDeleted = reader.GetBoolean(ordSystemLinkIsDeleted)
                            };

                            results.Add(summary);
                        }
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Mappar databaskod (VARCHAR) till ProductType-enum.
        /// </summary>
        private static ProductType MapProductTypeFromDatabaseValue(string value)
        {
            if (string.IsNullOrEmpty(value))
                throw new ArgumentException("ProductType database value cannot be null or empty.", nameof(value));

            switch (value.ToUpperInvariant())
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
                    throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown ProductType database value.");
            }
        }

        /// <summary>
        /// Mappar databaskod (VARCHAR) till SystemCode-enum.
        /// </summary>
        private static SystemCode MapSystemCodeFromDatabaseValue(string value)
        {
            if (string.IsNullOrEmpty(value))
                throw new ArgumentException("SystemCode database value cannot be null or empty.", nameof(value));

            switch (value.ToUpperInvariant())
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
                    throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown SystemCode database value.");
            }
        }

        /// <summary>
        /// Mappar databaskod (VARCHAR) till TradeSystemStatus-enum.
        /// </summary>
        private static TradeSystemStatus MapTradeSystemStatusFromDatabaseValue(string value)
        {
            if (string.IsNullOrEmpty(value))
                throw new ArgumentException("TradeSystemStatus database value cannot be null or empty.", nameof(value));

            switch (value.ToUpperInvariant())
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
                    throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown TradeSystemStatus database value.");
            }
        }
    }
}
