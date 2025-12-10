using System;
using System.Data;
using FxTradeHub.Domain.Entities;
using FxTradeHub.Domain.Interfaces;
using MySql.Data.MySqlClient;

namespace FxTradeHub.Data.MySql.Repositories
{
    /// <summary>
    /// MySQL-implementation av IStpLookupRepository mot schemat trade_stp.
    /// Hanterar lookup-tabeller för expiry cut per valutapar,
    /// Calypso-bok per trader och broker-mapping.
    /// </summary>
    public sealed class MySqlStpLookupRepository : IStpLookupRepository
    {
        private readonly string _connectionString;

        /// <summary>
        /// Skapar ett nytt lookup-repository med given connection string.
        /// Exempel:
        /// "Server=srv78506;Port=3306;Database=trade_stp;User Id=fxopt;Password=...;SslMode=None;TreatTinyAsBoolean=false;"
        /// </summary>
        /// <param name="connectionString">Connection string mot trade_stp-databasen.</param>
        public MySqlStpLookupRepository(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("Connection string must not be empty.", "connectionString");

            _connectionString = connectionString;
        }

        /// <summary>
        /// Skapar en ny MySqlConnection mot trade_stp.
        /// </summary>
        /// <returns>Öppen MySqlConnection (ej öppnad).</returns>
        private MySqlConnection CreateConnection()
        {
            return new MySqlConnection(_connectionString);
        }

        /// <summary>
        /// Hämtar MX3-portföljkod från regeltabellen ccypairportfoliorule
        /// för ett visst system, valutapar och produkttyp.
        /// Om flera regler matchar prioriteras regler med specificerad ProductType
        /// före regler där ProductType är NULL.
        /// </summary>
        /// <param name="systemCode">Systemkod, t.ex. "MX3".</param>
        /// <param name="currencyPair">Valutapar, t.ex. "EURSEK".</param>
        /// <param name="productType">
        /// Produkttyp, t.ex. "OPTION_VANILLA". Kan vara null.
        /// </param>
        /// <returns>PortfolioCode eller null.</returns>
        public string GetPortfolioCode(string systemCode, string currencyPair, string productType)
        {
            if (string.IsNullOrWhiteSpace(systemCode) || string.IsNullOrWhiteSpace(currencyPair))
            {
                return null;
            }

            const string sql = @"
SELECT
    PortfolioCode
FROM trade_stp.ccypairportfoliorule
WHERE SystemCode = @SystemCode
  AND CurrencyPair = @CurrencyPair
  AND IsActive = 1
  AND (@ProductType IS NULL OR ProductType IS NULL OR ProductType = @ProductType)
ORDER BY
    CASE WHEN ProductType IS NULL THEN 1 ELSE 0 END,
    RuleId
LIMIT 1;";

            using (var connection = CreateConnection())
            {
                connection.Open();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    command.CommandType = CommandType.Text;

                    command.Parameters.Add("@SystemCode", MySqlDbType.VarChar, 30).Value = systemCode;
                    command.Parameters.Add("@CurrencyPair", MySqlDbType.VarChar, 20).Value = currencyPair;

                    if (string.IsNullOrWhiteSpace(productType))
                    {
                        command.Parameters.Add("@ProductType", MySqlDbType.VarChar, 30).Value = DBNull.Value;
                    }
                    else
                    {
                        command.Parameters.Add("@ProductType", MySqlDbType.VarChar, 30).Value = productType;
                    }

                    using (var reader = command.ExecuteReader())
                    {
                        if (!reader.Read())
                        {
                            return null;
                        }

                        return reader["PortfolioCode"] as string ?? string.Empty;
                    }
                }
            }
        }

        /// <summary>
        /// Hämtar expiry cut-regel för ett givet valutapar.
        /// Returnerar null om ingen aktiv regel finns.
        /// </summary>
        /// <param name="currencyPair">Valutapar, t.ex. EURSEK.</param>
        /// <returns>ExpiryCutCcyRule eller null.</returns>
        public ExpiryCutCcyRule GetExpiryCutByCurrencyPair(string currencyPair)
        {
            if (string.IsNullOrWhiteSpace(currencyPair))
            {
                return null;
            }

            const string sql = @"
SELECT
    CurrencyPair,
    ExpiryCut,
    IsActive,
    Comment,
    CreatedUtc,
    UpdatedUtc,
    UpdatedBy
FROM trade_stp.stp_expiry_cut_ccy
WHERE CurrencyPair = @CurrencyPair
LIMIT 1;";

            using (var connection = CreateConnection())
            {
                connection.Open();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    command.CommandType = CommandType.Text;

                    command.Parameters.Add("@CurrencyPair", MySqlDbType.VarChar, 20).Value = currencyPair;

                    using (var reader = command.ExecuteReader())
                    {
                        if (!reader.Read())
                        {
                            return null;
                        }

                        return MapExpiryCutCcyRule(reader);
                    }
                }
            }
        }

        /// <summary>
        /// Hämtar Calypso-bok-regel för en given trader.
        /// Returnerar null om ingen aktiv regel finns.
        /// </summary>
        /// <param name="traderId">TraderId / användar-id.</param>
        /// <returns>CalypsoBookUserRule eller null.</returns>
        public CalypsoBookUserRule GetCalypsoBookByTraderId(string traderId)
        {
            if (string.IsNullOrWhiteSpace(traderId))
            {
                return null;
            }

            const string sql = @"
SELECT
    TraderId,
    CalypsoBook,
    IsActive,
    Comment,
    CreatedUtc,
    UpdatedUtc,
    UpdatedBy
FROM trade_stp.stp_calypso_book_user
WHERE TraderId = @TraderId
LIMIT 1;";

            using (var connection = CreateConnection())
            {
                connection.Open();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    command.CommandType = CommandType.Text;

                    command.Parameters.Add("@TraderId", MySqlDbType.VarChar, 50).Value = traderId;

                    using (var reader = command.ExecuteReader())
                    {
                        if (!reader.Read())
                        {
                            return null;
                        }

                        return MapCalypsoBookUserRule(reader);
                    }
                }
            }
        }

        /// <summary>
        /// Hämtar broker-mapping för en given venue och extern brokerkod.
        /// Returnerar null om ingen aktiv mappning finns.
        /// </summary>
        /// <param name="sourceVenueCode">Venue/källa, t.ex. VOLBROKER.</param>
        /// <param name="externalBrokerCode">Extern brokerkod från meddelandet.</param>
        /// <returns>BrokerMapping eller null.</returns>
        public BrokerMapping GetBrokerMapping(string sourceVenueCode, string externalBrokerCode)
        {
            if (string.IsNullOrWhiteSpace(sourceVenueCode) || string.IsNullOrWhiteSpace(externalBrokerCode))
            {
                return null;
            }

            const string sql = @"
SELECT
    Id,
    SourceVenueCode,
    ExternalBrokerCode,
    NormalizedBrokerCode,
    IsActive,
    Comment,
    CreatedUtc,
    UpdatedUtc,
    UpdatedBy
FROM trade_stp.stp_broker_mapping
WHERE SourceVenueCode = @SourceVenueCode
  AND ExternalBrokerCode = @ExternalBrokerCode
LIMIT 1;";

            using (var connection = CreateConnection())
            {
                connection.Open();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    command.CommandType = CommandType.Text;

                    command.Parameters.Add("@SourceVenueCode", MySqlDbType.VarChar, 50).Value = sourceVenueCode;
                    command.Parameters.Add("@ExternalBrokerCode", MySqlDbType.VarChar, 100).Value = externalBrokerCode;

                    using (var reader = command.ExecuteReader())
                    {
                        if (!reader.Read())
                        {
                            return null;
                        }

                        return MapBrokerMapping(reader);
                    }
                }
            }
        }

        /// <summary>
        /// Försöker mappa ett externt motpartsnamn/kod till internt CounterpartyCode
        /// via tabellen trade_stp.counterpartynamepattern.
        /// </summary>
        /// <param name="sourceType">Källa, t.ex. "FIX".</param>
        /// <param name="sourceVenueCode">Venue, t.ex. "VOLBROKER".</param>
        /// <param name="externalName">Externt motparts-id/namn, t.ex. "DB".</param>
        /// <returns>CounterpartyCode eller null om ingen aktiv mappning hittas.</returns>
        public string ResolveCounterpartyCode(string sourceType, string sourceVenueCode, string externalName)
        {
            if (string.IsNullOrWhiteSpace(externalName))
            {
                return null;
            }

            const string sql = @"
SELECT
    CounterpartyCode
FROM trade_stp.counterpartynamepattern
WHERE IsActive = 1
  AND Pattern = @Pattern
  AND (@SourceType IS NULL OR SourceType IS NULL OR SourceType = @SourceType)
  AND (@SourceVenueCode IS NULL OR SourceVenueCode IS NULL OR SourceVenueCode = @SourceVenueCode)
ORDER BY Priority ASC
LIMIT 1;";

            using (var connection = CreateConnection())
            {
                connection.Open();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    command.CommandType = CommandType.Text;

                    command.Parameters.Add("@Pattern", MySqlDbType.VarChar, 200).Value = externalName;

                    if (string.IsNullOrWhiteSpace(sourceType))
                        command.Parameters.Add("@SourceType", MySqlDbType.VarChar, 20).Value = DBNull.Value;
                    else
                        command.Parameters.Add("@SourceType", MySqlDbType.VarChar, 20).Value = sourceType;

                    if (string.IsNullOrWhiteSpace(sourceVenueCode))
                        command.Parameters.Add("@SourceVenueCode", MySqlDbType.VarChar, 50).Value = DBNull.Value;
                    else
                        command.Parameters.Add("@SourceVenueCode", MySqlDbType.VarChar, 50).Value = sourceVenueCode;

                    var result = command.ExecuteScalar();
                    if (result == null || result == DBNull.Value)
                    {
                        return null;
                    }

                    return Convert.ToString(result);
                }
            }
        }

        /// <summary>
        /// Hämtar trader-routinginformation för en given venue-traderkod.
        /// Bygger på tabellen trade_stp.stp_venue_trader_mapping och userprofile.
        /// Returnerar null om ingen aktiv mappning hittas eller om användaren saknas.
        /// </summary>
        /// <param name="sourceVenueCode">Venue/källa, t.ex. "VOLBROKER".</param>
        /// <param name="venueTraderCode">Traderkod från AE, t.ex. "FORSPE".</param>
        /// <returns>TraderRoutingInfo eller null.</returns>
        public TraderRoutingInfo GetTraderRoutingInfo(string sourceVenueCode, string venueTraderCode)
        {
            if (string.IsNullOrWhiteSpace(sourceVenueCode) || string.IsNullOrWhiteSpace(venueTraderCode))
                return null;

            const string sql = @"
SELECT
    m.InternalUserId,
    u.Mx3Id,
    u.ReportingEntityId
FROM trade_stp.stp_venue_trader_mapping m
JOIN trade_stp.userprofile u
  ON u.UserId = m.InternalUserId
WHERE m.SourceVenueCode = @SourceVenueCode
  AND m.VenueTraderCode = @VenueTraderCode
  AND m.IsActive = 1
  AND u.IsActive = 1
LIMIT 1;";

            using (var connection = CreateConnection())
            {
                connection.Open();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    command.CommandType = CommandType.Text;

                    command.Parameters.Add("@SourceVenueCode", MySqlDbType.VarChar, 50).Value = sourceVenueCode;
                    command.Parameters.Add("@VenueTraderCode", MySqlDbType.VarChar, 50).Value = venueTraderCode;

                    using (var reader = command.ExecuteReader())
                    {
                        if (!reader.Read())
                            return null;

                        var info = new TraderRoutingInfo
                        {
                            InternalUserId = reader["InternalUserId"] as string ?? string.Empty,
                            InvId = reader["Mx3Id"] as string ?? string.Empty,
                            ReportingEntityId = reader["ReportingEntityId"] as string ?? string.Empty
                        };

                        return info;
                    }
                }
            }
        }

        /// <summary>
        /// Mappar en datareader-rad till ExpiryCutCcyRule.
        /// </summary>
        /// <param name="reader">Datareader positionerad på en rad.</param>
        /// <returns>ExpiryCutCcyRule-instans.</returns>
        private static ExpiryCutCcyRule MapExpiryCutCcyRule(IDataRecord reader)
        {
            var rule = new ExpiryCutCcyRule
            {
                CurrencyPair = reader["CurrencyPair"] as string ?? string.Empty,
                ExpiryCut = reader["ExpiryCut"] as string ?? string.Empty,
                IsActive = Convert.ToBoolean(reader["IsActive"]),
                Comment = reader["Comment"] as string ?? string.Empty,
                CreatedUtc = reader.GetDateTime(reader.GetOrdinal("CreatedUtc")),
                UpdatedBy = reader["UpdatedBy"] as string ?? string.Empty
            };

            int indexUpdatedUtc = reader.GetOrdinal("UpdatedUtc");
            if (reader.IsDBNull(indexUpdatedUtc))
            {
                rule.UpdatedUtc = null;
            }
            else
            {
                rule.UpdatedUtc = reader.GetDateTime(indexUpdatedUtc);
            }

            return rule;
        }

        /// <summary>
        /// Mappar en datareader-rad till CalypsoBookUserRule.
        /// </summary>
        /// <param name="reader">Datareader positionerad på en rad.</param>
        /// <returns>CalypsoBookUserRule-instans.</returns>
        private static CalypsoBookUserRule MapCalypsoBookUserRule(IDataRecord reader)
        {
            var rule = new CalypsoBookUserRule
            {
                TraderId = reader["TraderId"] as string ?? string.Empty,
                CalypsoBook = reader["CalypsoBook"] as string ?? string.Empty,
                IsActive = Convert.ToBoolean(reader["IsActive"]),
                Comment = reader["Comment"] as string ?? string.Empty,
                CreatedUtc = reader.GetDateTime(reader.GetOrdinal("CreatedUtc")),
                UpdatedBy = reader["UpdatedBy"] as string ?? string.Empty
            };

            int indexUpdatedUtc = reader.GetOrdinal("UpdatedUtc");
            if (reader.IsDBNull(indexUpdatedUtc))
            {
                rule.UpdatedUtc = null;
            }
            else
            {
                rule.UpdatedUtc = reader.GetDateTime(indexUpdatedUtc);
            }

            return rule;
        }

        /// <summary>
        /// Mappar en datareader-rad till BrokerMapping.
        /// </summary>
        /// <param name="reader">Datareader positionerad på en rad.</param>
        /// <returns>BrokerMapping-instans.</returns>
        private static BrokerMapping MapBrokerMapping(IDataRecord reader)
        {
            var mapping = new BrokerMapping
            {
                Id = Convert.ToInt64(reader["Id"]),
                SourceVenueCode = reader["SourceVenueCode"] as string ?? string.Empty,
                ExternalBrokerCode = reader["ExternalBrokerCode"] as string ?? string.Empty,
                NormalizedBrokerCode = reader["NormalizedBrokerCode"] as string ?? string.Empty,
                IsActive = Convert.ToBoolean(reader["IsActive"]),
                Comment = reader["Comment"] as string ?? string.Empty,
                CreatedUtc = reader.GetDateTime(reader.GetOrdinal("CreatedUtc")),
                UpdatedBy = reader["UpdatedBy"] as string ?? string.Empty
            };

            int indexUpdatedUtc = reader.GetOrdinal("UpdatedUtc");
            if (reader.IsDBNull(indexUpdatedUtc))
            {
                mapping.UpdatedUtc = null;
            }
            else
            {
                mapping.UpdatedUtc = reader.GetDateTime(indexUpdatedUtc);
            }

            return mapping;
        }
    }
}
