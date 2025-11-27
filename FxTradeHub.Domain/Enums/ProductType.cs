namespace FxTradeHub.Domain.Enums
{
    /// <summary>
    /// Produkttyp för en trade i STP-flödet.
    /// Mappar mot kolumnen Trade.ProductType i databasen.
    /// </summary>
    public enum ProductType
    {
        /// <summary>
        /// Spot FX.
        /// DB-värde: "SPOT"
        /// </summary>
        Spot,

        /// <summary>
        /// Forward FX.
        /// DB-värde: "FWD"
        /// </summary>
        Fwd,

        /// <summary>
        /// FX swap (near/far).
        /// DB-värde: "SWAP"
        /// </summary>
        Swap,

        /// <summary>
        /// Non-deliverable forward.
        /// DB-värde: "NDF"
        /// </summary>
        Ndf,

        /// <summary>
        /// Vanilla FX option.
        /// DB-värde: "OPTION_VANILLA"
        /// </summary>
        OptionVanilla,

        /// <summary>
        /// Non-deliverable option.
        /// DB-värde: "OPTION_NDO"
        /// </summary>
        OptionNdo
    }
}
