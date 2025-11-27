namespace FxTradeHub.Domain.Enums
{
    /// <summary>
    /// Status för en trade i ett visst system (MX3, Calypso, Volbroker STP etc).
    /// Mappar mot TradeSystemLink.Status i databasen.
    /// </summary>
    public enum TradeSystemStatus
    {
        /// <summary>
        /// Traden är skapad men inte skickad till systemet.
        /// DB-värde: "NEW"
        /// </summary>
        New,

        /// <summary>
        /// Traden är skickad / under behandling.
        /// DB-värde: "PENDING"
        /// </summary>
        Pending,

        /// <summary>
        /// Traden är bokad / accepterad i systemet.
        /// DB-värde: "BOOKED"
        /// </summary>
        Booked,

        /// <summary>
        /// Ett fel har inträffat (t.ex. bokningsfel).
        /// DB-värde: "ERROR"
        /// </summary>
        Error,

        /// <summary>
        /// Traden är annullerad.
        /// DB-värde: "CANCELLED"
        /// </summary>
        Cancelled,

        /// <summary>
        /// Gäller Volbroker STP: redo att ackas när FO-bokning är klar.
        /// DB-värde: "READY_TO_ACK"
        /// </summary>
        ReadyToAck,

        /// <summary>
        /// Gäller Volbroker STP: ACK skickad.
        /// DB-värde: "ACK_SENT"
        /// </summary>
        AckSent,

        /// <summary>
        /// Gäller Volbroker STP: fel vid ack (t.ex. kommunikationsfel).
        /// DB-värde: "ACK_ERROR"
        /// </summary>
        AckError
    }
}
