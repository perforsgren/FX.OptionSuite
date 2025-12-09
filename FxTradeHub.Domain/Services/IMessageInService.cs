using FxTradeHub.Domain.Entities;

namespace FxTradeHub.Domain.Services
{
    /// <summary>
    /// Defines the high-level operations for capturing inbound messages
    /// into the STP hub. This is the main entry point used by upstream
    /// components such as FIX gateways, e-mail ingestors and file watchers.
    /// </summary>
    public interface IMessageInService
    {
        /// <summary>
        /// Inserts a new inbound message into the STP hub.
        /// The message is persisted to the MessageIn table and assigned
        /// a unique MessageInId.
        /// </summary>
        /// <param name="message">
        /// The MessageIn instance to capture. Implementations may enforce
        /// defaults for fields such as ReceivedUtc, ParsedFlag and IsAdmin
        /// if they are not explicitly set by the caller.
        /// </param>
        /// <returns>
        /// The generated primary key (MessageInId) after successful insertion.
        /// </returns>
        long InsertMessage(MessageIn message);
    }
}
