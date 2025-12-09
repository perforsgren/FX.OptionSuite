using System;
using System.Data;
using MySql.Data.MySqlClient;
using FxTradeHub.Domain.Entities;
using FxTradeHub.Domain.Repositories;
using System.Collections.Generic;

namespace FxTradeHub.Data.MySql.Repositories
{
    /// <summary>
    /// Provides MySQL-based persistence for inbound messages stored in the
    /// trade_stp.MessageIn table. Supports inserting new raw messages and
    /// retrieving messages by their identifier.
    /// </summary>
    public class MessageInRepository : IMessageInRepository
    {
        private readonly string _connectionString;

        /// <summary>
        /// Initializes a new instance of the MessageInRepository using the
        /// specified STP connection string.
        /// </summary>
        public MessageInRepository(string connectionString)
        {
            _connectionString = connectionString;
        }

        /// <summary>
        /// Infogar en ny post i MessageIn-tabellen baserat på angivet MessageIn-objekt.
        /// Returnerar genererat primärnyckelvärde (MessageInId).
        /// </summary>
        public long Insert(MessageIn message)
        {
            const string sql = @"
INSERT INTO trade_stp.MessageIn
(
    SourceType,
    SourceVenueCode,
    SessionKey,
    ReceivedUtc,
    SourceTimestamp,
    IsAdmin,
    ParsedFlag,
    ParsedUtc,
    ParseError,
    RawPayload,
    EmailSubject,
    EmailFrom,
    EmailTo,
    FixMsgType,
    FixSeqNum,
    ExternalCounterpartyName,
    ExternalTradeKey,
    SourceMessageKey,
    InstrumentCode,
    Side,
    Notional,
    NotionalCurrency,
    TradeDate,
    RawPayloadHash
)
VALUES
(
    @SourceType,
    @SourceVenueCode,
    @SessionKey,
    @ReceivedUtc,
    @SourceTimestamp,
    @IsAdmin,
    @ParsedFlag,
    @ParsedUtc,
    @ParseError,
    @RawPayload,
    @EmailSubject,
    @EmailFrom,
    @EmailTo,
    @FixMsgType,
    @FixSeqNum,
    @ExternalCounterpartyName,
    @ExternalTradeKey,
    @SourceMessageKey,
    @InstrumentCode,
    @Side,
    @Notional,
    @NotionalCurrency,
    @TradeDate,
    @RawPayloadHash
);
SELECT LAST_INSERT_ID();
";

            using (var conn = new MySqlConnection(_connectionString))
            using (var cmd = new MySqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@SourceType", message.SourceType);
                cmd.Parameters.AddWithValue("@SourceVenueCode", message.SourceVenueCode);
                cmd.Parameters.AddWithValue("@SessionKey", (object)message.SessionKey ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ReceivedUtc", message.ReceivedUtc);
                cmd.Parameters.AddWithValue("@SourceTimestamp", (object)message.SourceTimestamp ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@IsAdmin", message.IsAdmin ? 1 : 0);
                cmd.Parameters.AddWithValue("@ParsedFlag", message.ParsedFlag ? 1 : 0);
                cmd.Parameters.AddWithValue("@ParsedUtc", (object)message.ParsedUtc ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ParseError", (object)message.ParseError ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@RawPayload", message.RawPayload);
                cmd.Parameters.AddWithValue("@EmailSubject", (object)message.EmailSubject ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@EmailFrom", (object)message.EmailFrom ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@EmailTo", (object)message.EmailTo ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@FixMsgType", (object)message.FixMsgType ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@FixSeqNum", message.FixSeqNum.HasValue ? (object)message.FixSeqNum.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("@ExternalCounterpartyName", (object)message.ExternalCounterpartyName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ExternalTradeKey", (object)message.ExternalTradeKey ?? DBNull.Value);

                cmd.Parameters.AddWithValue("@SourceMessageKey", (object)message.SourceMessageKey ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@InstrumentCode", (object)message.InstrumentCode ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Side", (object)message.Side ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Notional", message.Notional.HasValue ? (object)message.Notional.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("@NotionalCurrency", (object)message.NotionalCurrency ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@TradeDate", message.TradeDate.HasValue ? (object)message.TradeDate.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("@RawPayloadHash", (object)message.RawPayloadHash ?? DBNull.Value);

                conn.Open();
                var result = cmd.ExecuteScalar();
                return Convert.ToInt64(result);
            }
        }


        /// <summary>
        /// Retrieves a MessageIn by its identifier, or null if the record does not exist.
        /// </summary>
        public MessageIn GetById(long messageInId)
        {
            const string sql = @"
SELECT *
FROM trade_stp.MessageIn
WHERE MessageInId = @MessageInId;
";

            using (var conn = new MySqlConnection(_connectionString))
            using (var cmd = new MySqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@MessageInId", messageInId);
                conn.Open();

                using (var reader = cmd.ExecuteReader(CommandBehavior.SingleRow))
                {
                    if (!reader.Read())
                        return null;

                    return Map(reader);
                }
            }
        }

        /// <summary>
        /// Mappar en databassrad (IDataRecord) till ett MessageIn-objekt,
        /// inklusive de utökade metadatafälten för felsökning och analys.
        /// </summary>
        private static MessageIn Map(IDataRecord r)
        {
            return new MessageIn
            {
                MessageInId = r.GetInt64(r.GetOrdinal("MessageInId")),
                SourceType = r.GetString(r.GetOrdinal("SourceType")),
                SourceVenueCode = r.GetString(r.GetOrdinal("SourceVenueCode")),
                SessionKey = r["SessionKey"] as string,
                ReceivedUtc = r.GetDateTime(r.GetOrdinal("ReceivedUtc")),
                SourceTimestamp = r["SourceTimestamp"] == DBNull.Value
                    ? (DateTime?)null
                    : Convert.ToDateTime(r["SourceTimestamp"]),
                IsAdmin = Convert.ToInt32(r["IsAdmin"]) == 1,
                ParsedFlag = Convert.ToInt32(r["ParsedFlag"]) == 1,
                ParsedUtc = r["ParsedUtc"] == DBNull.Value
                    ? (DateTime?)null
                    : Convert.ToDateTime(r["ParsedUtc"]),
                ParseError = r["ParseError"] as string,
                RawPayload = r.GetString(r.GetOrdinal("RawPayload")),
                EmailSubject = r["EmailSubject"] as string,
                EmailFrom = r["EmailFrom"] as string,
                EmailTo = r["EmailTo"] as string,
                FixMsgType = r["FixMsgType"] as string,
                FixSeqNum = r["FixSeqNum"] == DBNull.Value
                    ? (int?)null
                    : Convert.ToInt32(r["FixSeqNum"]),
                ExternalCounterpartyName = r["ExternalCounterpartyName"] as string,
                ExternalTradeKey = r["ExternalTradeKey"] as string,
                SourceMessageKey = r["SourceMessageKey"] as string,
                InstrumentCode = r["InstrumentCode"] as string,
                Side = r["Side"] as string,
                Notional = r["Notional"] == DBNull.Value
                    ? (decimal?)null
                    : Convert.ToDecimal(r["Notional"]),
                NotionalCurrency = r["NotionalCurrency"] as string,
                TradeDate = r["TradeDate"] == DBNull.Value
                    ? (DateTime?)null
                    : Convert.ToDateTime(r["TradeDate"]),
                RawPayloadHash = r["RawPayloadHash"] as string
            };
        }

        public void UpdateParsingState(MessageIn message)
        {
            const string sql = @"
UPDATE trade_stp.MessageIn
SET
    ParsedFlag = @ParsedFlag,
    ParsedUtc = @ParsedUtc,
    ParseError = @ParseError
WHERE
    MessageInId = @MessageInId;
";

            using (var conn = new MySqlConnection(_connectionString))
            using (var cmd = new MySqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@ParsedFlag", message.ParsedFlag ? 1 : 0);
                cmd.Parameters.AddWithValue("@ParsedUtc", (object)message.ParsedUtc ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ParseError", (object)message.ParseError ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@MessageInId", message.MessageInId);

                conn.Open();
                cmd.ExecuteNonQuery();
            }
        }

        public List<MessageIn> GetUnparsedMessages(int maxCount)
        {
            const string sql = @"
SELECT *
FROM trade_stp.MessageIn
WHERE ParsedFlag = 0
ORDER BY MessageInId
LIMIT @Limit;
";

            var list = new List<MessageIn>();

            using (var conn = new MySqlConnection(_connectionString))
            using (var cmd = new MySqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@Limit", maxCount);

                conn.Open();

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        list.Add(Map(reader));
                    }
                }
            }

            return list;
        }


    }
}
