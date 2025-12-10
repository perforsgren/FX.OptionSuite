using System;

namespace FxTradeHub.Domain.Entities
{
    /// <summary>
    /// Resultat av trader-mappning från venue-traderkod till intern användare.
    /// Används av Volbroker-parsningen för att sätta TraderId, InvId och ReportingEntityId.
    /// </summary>
    public sealed class TraderRoutingInfo
    {
        /// <summary>
        /// Internt användar-id (UserId) som representerar tradern.
        /// Mappas från stp_venue_trader_mapping.InternalUserId.
        /// </summary>
        public string InternalUserId { get; set; }

        /// <summary>
        /// Murex-trader-id (Mx3Id) som används som InvId på traden.
        /// Hämtas från userprofile.Mx3Id.
        /// </summary>
        public string InvId { get; set; }

        /// <summary>
        /// ReportingEntityId för tradern.
        /// Hämtas från userprofile.ReportingEntityId.
        /// </summary>
        public string ReportingEntityId { get; set; }

        /// <summary>
        /// Skapar en ny instans av TraderRoutingInfo med tomma fält.
        /// </summary>
        public TraderRoutingInfo()
        {
            InternalUserId = string.Empty;
            InvId = string.Empty;
            ReportingEntityId = string.Empty;
        }
    }
}
