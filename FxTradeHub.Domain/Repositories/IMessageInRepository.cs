using System.Collections.Generic;
using FxTradeHub.Domain.Entities;

namespace FxTradeHub.Domain.Repositories
{
    /// <summary>
    /// Defines data access operations for the MessageIn entity.
    /// Implementations are responsible for persisting and retrieving
    /// raw inbound messages in the trade_stp.MessageIn table.
    /// </summary>
    public interface IMessageInRepository
    {
        /// <summary>
        /// Inserts a new inbound message into the MessageIn table.
        /// Returns the generated MessageInId.
        /// </summary>
        /// <param name="message">
        /// The MessageIn instance to insert. The MessageInId property
        /// should be ignored by the implementation and populated from
        /// the database identity value after insertion.
        /// </param>
        /// <returns>
        /// The generated primary key (MessageInId) from the database.
        /// </returns>
        long Insert(MessageIn message);

        /// <summary>
        /// Retrieves a single MessageIn by its primary key.
        /// </summary>
        /// <param name="messageInId">The identifier of the inbound message.</param>
        /// <returns>
        /// The matching MessageIn entity, or null if no record exists
        /// for the specified identifier.
        /// </returns>
        MessageIn GetById(long messageInId);

        /// <summary>
        /// Updates parsing-related fields (ParsedFlag, ParsedUtc, ParseError)
        /// for an existing MessageIn record.
        /// </summary>
        void UpdateParsingState(MessageIn message);

        /// <summary>
        /// Returns a list of pending (unparsed) messages up to the specified limit.
        /// ParsedFlag = 0
        /// </summary>
        List<MessageIn> GetUnparsedMessages(int maxCount);

    }
}
