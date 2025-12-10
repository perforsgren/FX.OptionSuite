using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace FX.UI.WinForms.Features.Blotter
{
    /// <summary>
    /// Anger i vilka blotter-grids en kolumn kan vara synlig.
    /// </summary>
    [Flags]
    public enum BlotterGridVisibility
    {
        /// <summary>Kolumnen används inte i något grid.</summary>
        None = 0,

        /// <summary>Kolumnen kan visas i Options-griden.</summary>
        Options = 1,

        /// <summary>Kolumnen kan visas i Hedge/FX Linear-griden.</summary>
        Hedge = 2,

        /// <summary>Kolumnen kan visas i All-griden.</summary>
        All = 4
    }

    /// <summary>
    /// Etiketter för kolumner som hör ihop med vissa settings,
    /// t.ex. "Show MiFID details" eller "Show MX3/Calypso IDs".
    /// </summary>
    [Flags]
    public enum BlotterColumnTag
    {
        /// <summary>Inga särskilda taggar.</summary>
        None = 0,

        /// <summary>Kolumnen ingår i "Show MiFID details".</summary>
        MiFID = 1,

        /// <summary>Kolumnen ingår i "Show margin field".</summary>
        Margin = 2,

        /// <summary>Kolumnen ingår i "Show MX3/Calypso IDs".</summary>
        SystemIds = 4
    }

    /// <summary>
    /// Vilken typ av editor som ska användas för kolumnen i griden.
    /// </summary>
    public enum BlotterEditorType
    {
        /// <summary>Ingen speciell editor – behandlas som vanlig text.</summary>
        None = 0,

        /// <summary>Textcell som kan vara läs- eller skrivbar.</summary>
        Text = 1,

        /// <summary>ComboBox med fördefinierade val.</summary>
        Combo = 2,

        /// <summary>Checkbox (t.ex. STP-flagga).</summary>
        CheckBox = 3
    }


    /// <summary>
    /// Beskriver en blotter-kolumn oberoende av grid:
    /// rubrik, binding mot Trade/DTO, visningsformat, editor-typ och var den används.
    /// All grid-konfiguration utgår från dessa definitioner.
    /// </summary>
    public sealed class BlotterColumnDefinition
    {
        /// <summary>
        /// Intern nyckel för kolumnen (t.ex. "TradeId").
        /// Används i settings, persistering och vid kolumn-toggling.
        /// </summary>
        public string Key { get; set; }

        /// <summary>
        /// Kolumnrubrik som visas i DataGridView.
        /// </summary>
        public string HeaderText { get; set; }

        /// <summary>
        /// Binding-path mot Trade/DTO, t.ex. "TradeId" eller "NotionalCcy".
        /// Själva bindingen implementeras i presenter/VM-lager.
        /// </summary>
        public string BindingPath { get; set; }

        /// <summary>
        /// Anger i vilka grids kolumnen kan synas och därmed skapas.
        /// </summary>
        public BlotterGridVisibility VisibleIn { get; set; }

        /// <summary>
        /// Anger i vilka grids kolumnen är synlig som standard
        /// (användaren kan ändra detta via Settings → Show columns).
        /// </summary>
        public BlotterGridVisibility DefaultVisibleIn { get; set; }

        /// <summary>
        /// Ordningstal för hur kolumner sorteras inom ett grid.
        /// Lågt värde = långt till vänster.
        /// </summary>
        public int DisplayOrder { get; set; }

        /// <summary>
        /// Standardjustering för cellinnehållet.
        /// </summary>
        public DataGridViewContentAlignment Alignment { get; set; }

        /// <summary>
        /// Formatsträng för värden (t.ex. "N0", "N2", "yyyy-MM-dd HH:mm:ss").
        /// Tom sträng innebär att standardformat används.
        /// </summary>
        public string Format { get; set; }

        /// <summary>
        /// Etiketter som kopplar kolumnen till vissa meny-val
        /// (MiFID, margin, system-IDs etc.).
        /// </summary>
        public BlotterColumnTag Tags { get; set; }

        /// <summary>
        /// Lista med produkt-typer där kolumnen är relevant, t.ex.
        /// "SPOT", "FWD", "NDF", "SWAP", "OPTION_VANILLA".
        /// Tom/null = gäller alla produkter.
        /// </summary>
        public IReadOnlyList<string> ProductTypes { get; set; }

        /// <summary>
        /// Tooltip-text för kolumnhuvudet (kan lämnas tomt).
        /// </summary>
        public string HeaderToolTip { get; set; }

        /// <summary>
        /// Vilken typ av editor kolumnen ska ha i griden
        /// (None/Text/Combo/CheckBox).
        /// </summary>
        public BlotterEditorType EditorType { get; set; }

        /// <summary>
        /// Anger om kolumnen överhuvudtaget får editeras.
        /// Kombineras senare med radens CanEdit-flagga.
        /// </summary>
        public bool IsEditable { get; set; }

        /// <summary>
        /// Nyckel för att hämta uppslag (lookup) till ComboBox-kolumner,
        /// t.ex. "BuySell", "CallPut", "Trader", "PortfolioMx3".
        /// Tom/null för kolumner som inte använder lookup.
        /// </summary>
        public string LookupKey { get; set; }
    }


    /// <summary>
    /// Håller alla blotter-kolumn-definitioner. CreateColumnsX-metoderna
    /// och Settings-menyn kommer att utgå från denna lista.
    /// </summary>
    public static class BlotterColumnMetadata
    {
        /// <summary>
        /// Statisk lista med samtliga tillgängliga kolumner i blottern.
        /// Namn och binding-path bör spegla Trade STP-datamodellen.
        /// </summary>
        public static readonly IReadOnlyList<BlotterColumnDefinition> All =
            new List<BlotterColumnDefinition>
            {
                // === Core-identitet ===

                // 1. Trade ID
                new BlotterColumnDefinition
                {
                    Key = "TradeId",
                    HeaderText = "Trade ID",
                    BindingPath = "TradeId",
                    VisibleIn = BlotterGridVisibility.Options | BlotterGridVisibility.Hedge | BlotterGridVisibility.All,
                    DefaultVisibleIn = BlotterGridVisibility.Options | BlotterGridVisibility.Hedge | BlotterGridVisibility.All,
                    DisplayOrder = 10,
                    Alignment = DataGridViewContentAlignment.MiddleLeft,
                    Format = string.Empty,
                    Tags = BlotterColumnTag.None,
                    ProductTypes = new[] { "OPTION_VANILLA", "SPOT", "FWD", "NDF", "SWAP", "OPTION_NDO" },
                    HeaderToolTip = "STP Trade identifier"
                },

                // Produkt / venue är inte default i Options – de hamnar efter Options-blocket
                new BlotterColumnDefinition
                {
                    Key = "ProductType",
                    HeaderText = "Product",
                    BindingPath = "ProductType",
                    VisibleIn = BlotterGridVisibility.Options | BlotterGridVisibility.Hedge | BlotterGridVisibility.All,
                    DefaultVisibleIn = BlotterGridVisibility.All,
                    DisplayOrder = 210,
                    Alignment = DataGridViewContentAlignment.MiddleLeft,
                    Format = string.Empty,
                    Tags = BlotterColumnTag.None,
                    ProductTypes = new[] { "OPTION_VANILLA", "SPOT", "FWD", "NDF", "SWAP", "OPTION_NDO" },
                    HeaderToolTip = "Product type (Option/Spot/Fwd/NDF/Swap/OPTION_NDO)"
                },
                new BlotterColumnDefinition
                {
                    Key = "SourceVenue",
                    HeaderText = "Venue",
                    BindingPath = "SourceVenueCode",
                    VisibleIn = BlotterGridVisibility.All | BlotterGridVisibility.Options | BlotterGridVisibility.Hedge,
                    DefaultVisibleIn = BlotterGridVisibility.All,
                    DisplayOrder = 220,
                    Alignment = DataGridViewContentAlignment.MiddleLeft,
                    Format = string.Empty,
                    Tags = BlotterColumnTag.None,
                    ProductTypes = new[] { "OPTION_VANILLA", "SPOT", "FWD", "NDF", "SWAP", "OPTION_NDO" },
                    HeaderToolTip = "Source venue / platform"
                },

                // === Motpart & trader ===

                // 2. Counterpart
                new BlotterColumnDefinition
                {
                    Key = "Counterparty",
                    HeaderText = "Counterpart",
                    BindingPath = "CounterpartyCode",
                    VisibleIn = BlotterGridVisibility.Options | BlotterGridVisibility.Hedge | BlotterGridVisibility.All,
                    DefaultVisibleIn = BlotterGridVisibility.Options | BlotterGridVisibility.Hedge | BlotterGridVisibility.All,
                    DisplayOrder = 20,
                    Alignment = DataGridViewContentAlignment.MiddleLeft,
                    Format = string.Empty,
                    Tags = BlotterColumnTag.None,
                    ProductTypes = new[] { "OPTION_VANILLA", "SPOT", "FWD", "NDF", "SWAP", "OPTION_NDO" },
                    HeaderToolTip = "Counterparty short code"
                },

                // 16. Trader (men vi lägger displayordern här för att dela blocket logiskt)
                new BlotterColumnDefinition
                {
                    Key = "Trader",
                    HeaderText = "Trader",
                    BindingPath = "TraderId",
                    VisibleIn = BlotterGridVisibility.Options | BlotterGridVisibility.Hedge | BlotterGridVisibility.All,
                    DefaultVisibleIn = BlotterGridVisibility.Options | BlotterGridVisibility.Hedge | BlotterGridVisibility.All,
                    DisplayOrder = 160,
                    Alignment = DataGridViewContentAlignment.MiddleLeft,
                    Format = string.Empty,
                    Tags = BlotterColumnTag.None,
                    ProductTypes = new[] { "OPTION_VANILLA", "SPOT", "FWD", "NDF", "SWAP", "OPTION_NDO" },
                    HeaderToolTip = "Trader / user",
                    EditorType = BlotterEditorType.Combo,
                    IsEditable = true,
                    LookupKey = "Trader",
                },

                // === Direction & pair ===

                // 3. Buy/Sell
                new BlotterColumnDefinition
                {
                    Key = "BuySell",
                    HeaderText = "Buy/Sell",
                    BindingPath = "BuySell",
                    VisibleIn = BlotterGridVisibility.Options | BlotterGridVisibility.Hedge | BlotterGridVisibility.All,
                    DefaultVisibleIn = BlotterGridVisibility.Options | BlotterGridVisibility.Hedge | BlotterGridVisibility.All,
                    DisplayOrder = 40,
                    Alignment = DataGridViewContentAlignment.MiddleCenter,
                    Format = string.Empty,
                    Tags = BlotterColumnTag.None,
                    ProductTypes = new[] { "OPTION_VANILLA", "SPOT", "FWD", "NDF", "SWAP", "OPTION_NDO" },
                    HeaderToolTip = "Buy or Sell",
                    EditorType = BlotterEditorType.Combo,
                    IsEditable = true,
                    LookupKey = "BuySell",
                },

                // 5. Ccy Pair
                new BlotterColumnDefinition
                {
                    Key = "CcyPair",
                    HeaderText = "Ccy Pair",
                    BindingPath = "CcyPair",
                    VisibleIn = BlotterGridVisibility.Options | BlotterGridVisibility.Hedge | BlotterGridVisibility.All,
                    DefaultVisibleIn = BlotterGridVisibility.Options | BlotterGridVisibility.Hedge | BlotterGridVisibility.All,
                    DisplayOrder = 30,
                    Alignment = DataGridViewContentAlignment.MiddleCenter,
                    Format = string.Empty,
                    Tags = BlotterColumnTag.None,
                    ProductTypes = new[] { "OPTION_VANILLA", "SPOT", "FWD", "NDF", "SWAP", "OPTION_NDO" },
                    HeaderToolTip = "Currency pair"
                },

                // === Notional ===

                // 10. Notional
                new BlotterColumnDefinition
                {
                    Key = "Notional",
                    HeaderText = "Notional",
                    BindingPath = "Notional",
                    VisibleIn = BlotterGridVisibility.Options | BlotterGridVisibility.Hedge | BlotterGridVisibility.All,
                    DefaultVisibleIn = BlotterGridVisibility.Options | BlotterGridVisibility.Hedge | BlotterGridVisibility.All,
                    DisplayOrder = 60,
                    Alignment = DataGridViewContentAlignment.MiddleRight,
                    Format = "N0",
                    Tags = BlotterColumnTag.None,
                    ProductTypes = new[] { "OPTION_VANILLA", "SPOT", "FWD", "NDF", "SWAP", "OPTION_NDO" },
                    HeaderToolTip = "Trade notional amount"
                },

                // 11. Notional Ccy
                new BlotterColumnDefinition
                {
                    Key = "NotionalCcy",
                    HeaderText = "Notional Ccy",
                    BindingPath = "NotionalCcy",
                    VisibleIn = BlotterGridVisibility.Options | BlotterGridVisibility.Hedge | BlotterGridVisibility.All,
                    DefaultVisibleIn = BlotterGridVisibility.Options | BlotterGridVisibility.Hedge | BlotterGridVisibility.All,
                    DisplayOrder = 70,
                    Alignment = DataGridViewContentAlignment.MiddleCenter,
                    Format = string.Empty,
                    Tags = BlotterColumnTag.None,
                    ProductTypes = new[] { "OPTION_VANILLA", "SPOT", "FWD", "NDF", "SWAP", "OPTION_NDO" },
                    HeaderToolTip = "Notional currency"
                },

                // === Options-specifika fält (Options-grid) ===

                // 4. Call/Put
                new BlotterColumnDefinition
                {
                    Key = "CallPut",
                    HeaderText = "Call/Put",
                    BindingPath = "CallPut",
                    VisibleIn = BlotterGridVisibility.Options,
                    DefaultVisibleIn = BlotterGridVisibility.Options,
                    DisplayOrder = 40,
                    Alignment = DataGridViewContentAlignment.MiddleCenter,
                    Format = string.Empty,
                    Tags = BlotterColumnTag.None,
                    ProductTypes = new[] { "OPTION_VANILLA", "OPTION_NDO" },
                    HeaderToolTip = "Call or Put",
                    EditorType = BlotterEditorType.Combo,
                    IsEditable = true,
                    LookupKey = "CallPut",

                },

                // 6. Strike
                new BlotterColumnDefinition
                {
                    Key = "Strike",
                    HeaderText = "Strike",
                    BindingPath = "Strike",
                    VisibleIn = BlotterGridVisibility.Options,
                    DefaultVisibleIn = BlotterGridVisibility.Options,
                    DisplayOrder = 55,
                    Alignment = DataGridViewContentAlignment.MiddleRight,
                    Format = "N4",
                    Tags = BlotterColumnTag.None,
                    ProductTypes = new[] { "OPTION_VANILLA", "OPTION_NDO" },
                    HeaderToolTip = "Option strike"
                },

                // 7. Cut
                new BlotterColumnDefinition
                {
                    Key = "Cut",
                    HeaderText = "Cut",
                    BindingPath = "Cut",
                    VisibleIn = BlotterGridVisibility.Options,
                    DefaultVisibleIn = BlotterGridVisibility.Options,
                    DisplayOrder = 70,
                    Alignment = DataGridViewContentAlignment.MiddleCenter,
                    Format = string.Empty,
                    Tags = BlotterColumnTag.None,
                    ProductTypes = new[] { "OPTION_VANILLA", "OPTION_NDO" },
                    HeaderToolTip = "Fixing cut (e.g. NYC, TKY)"
                },

                // 8. Expiry Date
                new BlotterColumnDefinition
                {
                    Key = "ExpiryDate",
                    HeaderText = "Expiry Date",
                    BindingPath = "ExpiryDate",
                    VisibleIn = BlotterGridVisibility.Options,
                    DefaultVisibleIn = BlotterGridVisibility.Options,
                    DisplayOrder = 80,
                    Alignment = DataGridViewContentAlignment.MiddleCenter,
                    Format = "yyyy-MM-dd",
                    Tags = BlotterColumnTag.None,
                    ProductTypes = new[] { "OPTION_VANILLA", "OPTION_NDO" },
                    HeaderToolTip = "Option expiry date"
                },

                // 9. Settlement Date
                new BlotterColumnDefinition
                {
                    Key = "SettlementDate",
                    HeaderText = "Settlement Date",
                    BindingPath = "SettlementDate",
                    VisibleIn = BlotterGridVisibility.Options | BlotterGridVisibility.Hedge,
                    DefaultVisibleIn = BlotterGridVisibility.Options | BlotterGridVisibility.Hedge,
                    DisplayOrder = 90,
                    Alignment = DataGridViewContentAlignment.MiddleCenter,
                    Format = "yyyy-MM-dd",
                    Tags = BlotterColumnTag.None,
                    ProductTypes = new[] { "OPTION_VANILLA", "SPOT", "FWD", "NDF", "SWAP", "OPTION_NDO" },
                    HeaderToolTip = "Settlement date"
                },

                // 12. Premium
                new BlotterColumnDefinition
                {
                    Key = "Premium",
                    HeaderText = "Premium",
                    BindingPath = "Premium",
                    VisibleIn = BlotterGridVisibility.Options,
                    DefaultVisibleIn = BlotterGridVisibility.Options,
                    DisplayOrder = 120,
                    Alignment = DataGridViewContentAlignment.MiddleRight,
                    Format = "N2",
                    Tags = BlotterColumnTag.None,
                    ProductTypes = new[] { "OPTION_VANILLA", "OPTION_NDO" },
                    HeaderToolTip = "Premium amount"
                },

                // 13. Premium Ccy
                new BlotterColumnDefinition
                {
                    Key = "PremiumCcy",
                    HeaderText = "Premium Ccy",
                    BindingPath = "PremiumCcy",
                    VisibleIn = BlotterGridVisibility.Options,
                    DefaultVisibleIn = BlotterGridVisibility.Options,
                    DisplayOrder = 130,
                    Alignment = DataGridViewContentAlignment.MiddleCenter,
                    Format = string.Empty,
                    Tags = BlotterColumnTag.None,
                    ProductTypes = new[] { "OPTION_VANILLA", "OPTION_NDO" },
                    HeaderToolTip = "Premium currency"
                },

                // 14. Premium Date
                new BlotterColumnDefinition
                {
                    Key = "PremiumDate",
                    HeaderText = "Premium Date",
                    BindingPath = "PremiumDate",
                    VisibleIn = BlotterGridVisibility.Options,
                    DefaultVisibleIn = BlotterGridVisibility.Options,
                    DisplayOrder = 140,
                    Alignment = DataGridViewContentAlignment.MiddleCenter,
                    Format = "yyyy-MM-dd",
                    Tags = BlotterColumnTag.None,
                    ProductTypes = new[] { "OPTION_VANILLA", "OPTION_NDO" },
                    HeaderToolTip = "Premium settlement date"
                },

                // 15. Portfolio MX3 & Calypso
                new BlotterColumnDefinition
                {
                    Key = "PortfolioMx3",
                    HeaderText = "Portfolio MX3",
                    BindingPath = "PortfolioMx3",
                    VisibleIn = BlotterGridVisibility.Options | BlotterGridVisibility.Hedge | BlotterGridVisibility.All,
                    DefaultVisibleIn = BlotterGridVisibility.Options | BlotterGridVisibility.Hedge,
                    DisplayOrder = 150,
                    Alignment = DataGridViewContentAlignment.MiddleLeft,
                    Format = string.Empty,
                    Tags = BlotterColumnTag.None,
                    ProductTypes = new[] { "OPTION_VANILLA", "SPOT", "FWD", "NDF", "SWAP", "OPTION_NDO" },
                    HeaderToolTip = "MX.3 portfolio / book",
                    EditorType = BlotterEditorType.Combo,
                    IsEditable = true,
                    LookupKey = "PortfolioMx3",
                },

                new BlotterColumnDefinition
                {
                    Key = "CalypsoPortfolio",
                    HeaderText = "Book Calypso",
                    BindingPath = "CalypsoPortfolio",
                    VisibleIn = BlotterGridVisibility.Hedge | BlotterGridVisibility.All,
                    DefaultVisibleIn = BlotterGridVisibility.Hedge,
                    DisplayOrder = 155,
                    Alignment = DataGridViewContentAlignment.MiddleLeft,
                    Format = string.Empty,
                    Tags = BlotterColumnTag.None,
                    ProductTypes = new[] { "SPOT", "FWD", "NDF", "SWAP" },
                    HeaderToolTip = "Calypso portfolio / book",
                    EditorType = BlotterEditorType.Combo,
                    IsEditable = true,
                    LookupKey = "CalypsoPortfolio",
                },

                // 17. Status MX3
                new BlotterColumnDefinition
                {
                    Key = "Mx3Status",
                    HeaderText = "Status MX3",
                    BindingPath = "Mx3Status",
                    VisibleIn = BlotterGridVisibility.Options | BlotterGridVisibility.Hedge | BlotterGridVisibility.All,
                    DefaultVisibleIn = BlotterGridVisibility.Options | BlotterGridVisibility.Hedge,
                    DisplayOrder = 170,
                    Alignment = DataGridViewContentAlignment.MiddleLeft,
                    Format = string.Empty,
                    Tags = BlotterColumnTag.None,
                    ProductTypes = new[] { "OPTION_VANILLA", "SPOT", "FWD", "NDF", "SWAP", "OPTION_NDO" },
                    HeaderToolTip = "Booking status in MX.3"
                },

                // === Hedge/linear-specifika fält (rate, hedge type) – används bara i Hedge-grid ===
                new BlotterColumnDefinition
                {
                    Key = "HedgeType",
                    HeaderText = "Hedge Type",
                    BindingPath = "HedgeType",
                    VisibleIn = BlotterGridVisibility.Hedge,
                    DefaultVisibleIn = BlotterGridVisibility.Hedge,
                    DisplayOrder = 86,
                    Alignment = DataGridViewContentAlignment.MiddleLeft,
                    Format = string.Empty,
                    Tags = BlotterColumnTag.None,
                    ProductTypes = new[] { "SPOT", "FWD", "NDF", "SWAP" },
                    HeaderToolTip = "Spot/Fwd/NDF/Swap etc."
                },
                new BlotterColumnDefinition
                {
                    Key = "HedgeRate",
                    HeaderText = "Hedge Rate",
                    BindingPath = "HedgeRate",
                    VisibleIn = BlotterGridVisibility.Hedge,
                    DefaultVisibleIn = BlotterGridVisibility.Hedge,
                    DisplayOrder = 85,
                    Alignment = DataGridViewContentAlignment.MiddleRight,
                    Format = "N6",
                    Tags = BlotterColumnTag.None,
                    ProductTypes = new[] { "SPOT", "FWD", "NDF", "SWAP" },
                    HeaderToolTip = "Execution rate"
                },

                // === Tidsstämplar ===
                new BlotterColumnDefinition
                {
                    Key = "ExecutionTime",
                    HeaderText = "Execution Time",
                    BindingPath = "ExecutionTime",
                    VisibleIn = BlotterGridVisibility.Options | BlotterGridVisibility.Hedge | BlotterGridVisibility.All,
                    DefaultVisibleIn = BlotterGridVisibility.All,
                    DisplayOrder = 250,
                    Alignment = DataGridViewContentAlignment.MiddleCenter,
                    Format = "yyyy-MM-dd HH:mm:ss",
                    Tags = BlotterColumnTag.None,
                    ProductTypes = new[] { "OPTION_VANILLA", "SPOT", "FWD", "NDF", "SWAP", "OPTION_NDO" },
                    HeaderToolTip = "Execution timestamp"
                },

                // === System-IDs / status (kopplade till Settings-taggar) ===
                new BlotterColumnDefinition
                {
                    Key = "Mx3TradeId",
                    HeaderText = "MX3 ID",
                    BindingPath = "Mx3TradeId",
                    VisibleIn = BlotterGridVisibility.Options | BlotterGridVisibility.Hedge | BlotterGridVisibility.All,
                    DefaultVisibleIn = BlotterGridVisibility.None,
                    DisplayOrder = 260,
                    Alignment = DataGridViewContentAlignment.MiddleLeft,
                    Format = string.Empty,
                    Tags = BlotterColumnTag.SystemIds,
                    ProductTypes = new[] { "OPTION_VANILLA", "SPOT", "FWD", "NDF", "SWAP", "OPTION_NDO" },
                    HeaderToolTip = "Trade identifier in MX.3"
                },
                new BlotterColumnDefinition
                {
                    Key = "CalypsoTradeId",
                    HeaderText = "Calypso ID",
                    BindingPath = "CalypsoTradeId",
                    VisibleIn = BlotterGridVisibility.Options | BlotterGridVisibility.Hedge | BlotterGridVisibility.All,
                    DefaultVisibleIn = BlotterGridVisibility.None,
                    DisplayOrder = 270,
                    Alignment = DataGridViewContentAlignment.MiddleLeft,
                    Format = string.Empty,
                    Tags = BlotterColumnTag.SystemIds,
                    ProductTypes = new[] { "OPTION_VANILLA", "SPOT", "FWD", "NDF", "SWAP", "OPTION_NDO" },
                    HeaderToolTip = "Trade identifier in Calypso"
                },
                new BlotterColumnDefinition
                {
                    Key = "CalypsoStatus",
                    HeaderText = "Status Calypso",
                    BindingPath = "CalypsoStatus",
                    VisibleIn = BlotterGridVisibility.Hedge | BlotterGridVisibility.Options | BlotterGridVisibility.All,
                    DefaultVisibleIn = BlotterGridVisibility.Hedge,
                    DisplayOrder = 280,
                    Alignment = DataGridViewContentAlignment.MiddleLeft,
                    Format = string.Empty,
                    Tags = BlotterColumnTag.None,
                    ProductTypes = new[] { "SPOT", "FWD", "NDF", "SWAP" },
                    HeaderToolTip = "Booking status in Calypso"
                }

                // MiFID/margin/NDF/Swap-specifika kolumner lägger vi till här senare.
            };
    }
}
