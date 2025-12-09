using System;

namespace FxTradeHub.Domain.Entities
{
    /// <summary>
    /// Rått inkommande meddelande (mail, FIX, API, fil).
    /// Mappar 1:1 mot trade_stp.MessageIn.
    /// </summary>
    public class MessageIn
    {
        /// <summary>
        /// Gets or sets the unique identifier of the inbound message.
        /// Maps to trade_stp.MessageIn.MessageInId (PK, bigint).
        /// </summary>
        public long MessageInId { get; set; }

        /// <summary>
        /// Gets or sets the high-level source type of the message.
        /// Typical values: "FIX", "EMAIL", "FILE".
        /// Maps to trade_stp.MessageIn.SourceType (NOT NULL, varchar(20)).
        /// </summary>
        public string SourceType { get; set; }

        /// <summary>
        /// Gets or sets the venue or external system code that produced the message.
        /// Examples: "VOLBROKER", "JPM", "RTNS".
        /// Maps to trade_stp.MessageIn.SourceVenueCode (NOT NULL, varchar(50)).
        /// </summary>
        public string SourceVenueCode { get; set; }

        /// <summary>
        /// Gets or sets an optional session key or technical link to the upstream session.
        /// For FIX, this can be a composed key (SenderCompID/TargetCompID/SessionQualifier).
        /// Maps to trade_stp.MessageIn.SessionKey (NULL, varchar(100)).
        /// </summary>
        public string SessionKey { get; set; }

        /// <summary>
        /// Gets or sets the UTC timestamp when the message was received by the STP hub.
        /// This is the canonical receive time used for ordering and troubleshooting.
        /// Maps to trade_stp.MessageIn.ReceivedUtc (NOT NULL, datetime(3)).
        /// </summary>
        public DateTime ReceivedUtc { get; set; }

        /// <summary>
        /// Gets or sets the original timestamp provided by the source, if available.
        /// For example: FIX SendingTime or mail Date header.
        /// Maps to trade_stp.MessageIn.SourceTimestamp (NULL, datetime(3)).
        /// </summary>
        public DateTime? SourceTimestamp { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the message is an administrative message.
        /// For example FIX session-level messages that should not produce trades.
        /// Maps to trade_stp.MessageIn.IsAdmin (NOT NULL, tinyint(1), default 0).
        /// </summary>
        public bool IsAdmin { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the message has been parsed into
        /// Trade / TradeSystemLink / TradeWorkflowEvent.
        /// Maps to trade_stp.MessageIn.ParsedFlag (NOT NULL, tinyint(1), default 0).
        /// </summary>
        public bool ParsedFlag { get; set; }

        /// <summary>
        /// Gets or sets the UTC timestamp when parsing was attempted or completed.
        /// Maps to trade_stp.MessageIn.ParsedUtc (NULL, datetime(3)).
        /// </summary>
        public DateTime? ParsedUtc { get; set; }

        /// <summary>
        /// Gets or sets the parse error description, if parsing failed.
        /// This is typically a short technical message to aid troubleshooting.
        /// Maps to trade_stp.MessageIn.ParseError (NULL, text).
        /// </summary>
        public string ParseError { get; set; }

        /// <summary>
        /// Gets or sets the raw payload as delivered by the source.
        /// For FIX this is the raw FIX string; for e-mail the mail body;
        /// for files the full file content as text.
        /// Maps to trade_stp.MessageIn.RawPayload (NOT NULL, mediumtext).
        /// </summary>
        public string RawPayload { get; set; }

        /// <summary>
        /// Gets or sets the e-mail subject, if the message originated from an e-mail source.
        /// Maps to trade_stp.MessageIn.EmailSubject (NULL, varchar(255)).
        /// </summary>
        public string EmailSubject { get; set; }

        /// <summary>
        /// Gets or sets the e-mail sender address, if applicable.
        /// Maps to trade_stp.MessageIn.EmailFrom (NULL, varchar(255)).
        /// </summary>
        public string EmailFrom { get; set; }

        /// <summary>
        /// Gets or sets the e-mail recipient(s), if applicable.
        /// Maps to trade_stp.MessageIn.EmailTo (NULL, varchar(255)).
        /// </summary>
        public string EmailTo { get; set; }

        /// <summary>
        /// Gets or sets the FIX message type (tag 35), if the source is FIX.
        /// Examples: "AE" for TradeCaptureReport.
        /// Maps to trade_stp.MessageIn.FixMsgType (NULL, varchar(10)).
        /// </summary>
        public string FixMsgType { get; set; }

        /// <summary>
        /// Gets or sets the FIX sequence number (tag 34), if the source is FIX.
        /// Maps to trade_stp.MessageIn.FixSeqNum (NULL, int).
        /// </summary>
        public int? FixSeqNum { get; set; }

        /// <summary>
        /// Gets or sets the external counterparty name as seen in the inbound message.
        /// This is a free-text field used mainly for troubleshooting and matching.
        /// Maps to trade_stp.MessageIn.ExternalCounterpartyName (NULL, varchar(255)).
        /// </summary>
        public string ExternalCounterpartyName { get; set; }

        /// <summary>
        /// Gets or sets an external trade key or reference linking back to the source.
        /// For example: venue trade id, broker ticket id, or booking reference.
        /// Maps to trade_stp.MessageIn.ExternalTradeKey (NULL, varchar(100)).
        /// </summary>
        public string ExternalTradeKey { get; set; }

        /// <summary>
        /// Hämtar eller sätter en källa-specifik nyckel för meddelandet,
        /// t.ex. ExecID (tag 17) för FIX eller Message-Id för e-post.
        /// Mappar mot kolumnen MessageIn.SourceMessageKey.
        /// </summary>
        public string SourceMessageKey { get; set; }

        /// <summary>
        /// Hämtar eller sätter en enkel instrumentkod som underlättar felsökning,
        /// t.ex. valutapar eller liknande representation från källmeddelandet.
        /// Mappar mot kolumnen MessageIn.InstrumentCode.
        /// </summary>
        public string InstrumentCode { get; set; }

        /// <summary>
        /// Hämtar eller sätter sidan ("Buy"/"Sell") som den uppfattas från källmeddelandet.
        /// Mappar mot kolumnen MessageIn.Side.
        /// </summary>
        public string Side { get; set; }

        /// <summary>
        /// Hämtar eller sätter notional-beloppet som extraherats från källmeddelandet.
        /// Mappar mot kolumnen MessageIn.Notional.
        /// </summary>
        public decimal? Notional { get; set; }

        /// <summary>
        /// Hämtar eller sätter valutakoden för notional-beloppet.
        /// Mappar mot kolumnen MessageIn.NotionalCurrency.
        /// </summary>
        public string NotionalCurrency { get; set; }

        /// <summary>
        /// Hämtar eller sätter tradedatum som extraherats från källmeddelandet.
        /// Mappar mot kolumnen MessageIn.TradeDate.
        /// </summary>
        public DateTime? TradeDate { get; set; }

        /// <summary>
        /// Hämtar eller sätter en hash (t.ex. SHA-256) av råpayloaden.
        /// Kan användas för deduplicering och felsökning.
        /// Mappar mot kolumnen MessageIn.RawPayloadHash.
        /// </summary>
        public string RawPayloadHash { get; set; }



    }
}
