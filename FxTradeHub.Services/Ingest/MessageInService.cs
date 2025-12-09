using System;
using FxTradeHub.Domain.Entities;
using FxTradeHub.Domain.Repositories;
using FxTradeHub.Domain.Services;

namespace FxTradeHub.Services.Ingest
{
    /// <summary>
    /// Provides high-level capture operations for inbound messages.
    /// Upstream components (FIX gateways, mail readers, file watchers)
    /// use this service to persist raw incoming data into the STP hub.
    /// </summary>
    public class MessageInService : IMessageInService
    {
        private readonly IMessageInRepository _repository;

        /// <summary>
        /// Initializes a new instance of the MessageInService with the specified repository.
        /// </summary>
        public MessageInService(IMessageInRepository repository)
        {
            _repository = repository;
        }

        /// <summary>
        /// Inserts a new inbound message into the STP hub, applying safe defaults
        /// and returning the generated MessageInId.
        /// </summary>
        public long InsertMessage(MessageIn message)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));

            if (string.IsNullOrWhiteSpace(message.SourceType))
                throw new ArgumentException("SourceType must be set.", nameof(message));

            if (string.IsNullOrWhiteSpace(message.SourceVenueCode))
                throw new ArgumentException("SourceVenueCode must be set.", nameof(message));

            if (string.IsNullOrWhiteSpace(message.RawPayload))
                throw new ArgumentException("RawPayload must be set.", nameof(message));

            // Apply safe defaults if caller did not set them.
            if (message.ReceivedUtc == default(DateTime))
                message.ReceivedUtc = DateTime.UtcNow;

            // Ensure parsing fields start clean.
            message.ParsedFlag = false;
            message.ParsedUtc = null;
            message.ParseError = null;

            // Ensure admin flag defaults to false unless explicitly set.
            // Caller can override by setting message.IsAdmin = true.
            // (Do not modify if user explicitly set true.)
            // Default bool = false → OK.

            // Persist via repository.
            var id = _repository.Insert(message);
            message.MessageInId = id;

            return id;
        }
    }
}
