using FxTradeHub.Domain.Entities;

namespace FxTradeHub.Domain.Interfaces
{
    /// <summary>
    /// Abstraktion för att läsa och skriva mot trade_stp-schemat.
    /// Implementeras i FxTradeHub.Data.MySql eller annan data-access.
    /// </summary>
    public interface IStpRepository
    {
        /// <summary>
        /// Infogar ett nytt MessageIn och returnerar genererat MessageInId.
        /// </summary>
        long InsertMessageIn(MessageIn message);

        /// <summary>
        /// Infogar en ny Trade och returnerar genererat StpTradeId.
        /// </summary>
        long InsertTrade(Trade trade);

        /// <summary>
        /// Infogar en ny TradeSystemLink och returnerar genererat TradeSystemLinkId.
        /// </summary>
        long InsertTradeSystemLink(TradeSystemLink link);

        /// <summary>
        /// Infogar en ny TradeWorkflowEvent och returnerar genererat TradeWorkflowEventId.
        /// </summary>
        long InsertWorkflowEvent(TradeWorkflowEvent evt);
    }
}
