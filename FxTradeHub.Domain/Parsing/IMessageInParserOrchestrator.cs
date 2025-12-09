using System.Collections.Generic;
using FxTradeHub.Domain.Entities;

namespace FxTradeHub.Domain.Parsing
{
    /// <summary>
    /// Defines the orchestration logic responsible for locating unparsed messages,
    /// selecting the appropriate parser, executing parsing, and persisting results.
    /// </summary>
    public interface IMessageInParserOrchestrator
    {
        /// <summary>
        /// Processes all pending inbound messages (ParsedFlag = false).
        /// </summary>
        void ProcessPendingMessages();

        /// <summary>
        /// Processes a single MessageIn record by id.
        /// </summary>
        void ProcessMessage(long messageInId);
    }
}
