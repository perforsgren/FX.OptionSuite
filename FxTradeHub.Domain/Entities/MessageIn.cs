using System;

namespace FxTradeHub.Domain.Entities
{
    /// <summary>
    /// Rått inkommande meddelande (mail, FIX, API, fil).
    /// Mappar 1:1 mot trade_stp.MessageIn.
    /// </summary>
    public class MessageIn
    {
        public long MessageInId { get; set; }      // PK från DB

        public string SourceType { get; set; }     // MAIL, FIX, API, FILE
        public string SourceVenueCode { get; set; }
        public string SessionKey { get; set; }
        public DateTime ReceivedUtc { get; set; }
        public DateTime? SourceTimestamp { get; set; }
        public bool IsAdmin { get; set; }
        public bool ParsedFlag { get; set; }
        public DateTime? ParsedUtc { get; set; }
        public string ParseError { get; set; }
        public string RawPayload { get; set; }
        public string EmailSubject { get; set; }
        public string EmailFrom { get; set; }
        public string EmailTo { get; set; }
        public string FixMsgType { get; set; }
        public int? FixSeqNum { get; set; }
        public string ExternalCounterpartyName { get; set; }
        public string ExternalTradeKey { get; set; }
    }
}
