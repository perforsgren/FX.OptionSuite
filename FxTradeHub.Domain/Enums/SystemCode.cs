namespace FxTradeHub.Domain.Enums
{
    /// <summary>
    /// Kod för vilket externt system en TradeSystemLink gäller.
    /// Mappar mot TradeSystemLink.SystemCode i databasen.
    /// </summary>
    public enum SystemCode
    {
        /// <summary>
        /// Murex MX.3.
        /// DB-värde: "MX3"
        /// </summary>
        Mx3,

        /// <summary>
        /// Calypso.
        /// DB-värde: "CALYPSO"
        /// </summary>
        Calypso,

        /// <summary>
        /// Volbroker STP-koppling.
        /// DB-värde: "VOLBROKER_STP"
        /// </summary>
        VolbrokerStp,

        /// <summary>
        /// RTNS / drop copy (t.ex. från JPM).
        /// DB-värde: "RTNS"
        /// </summary>
        Rtns
    }
}
