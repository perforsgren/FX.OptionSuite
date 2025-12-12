using System.Windows.Forms;
using FxTradeHub.Contracts.Dtos;

namespace FX.UI.WinForms.Features.Blotter
{
    /// <summary>
    /// Kontrakt för FX Trade Blotter-vyn.
    /// Exponeras mot <see cref="BlotterPresenter"/> så att all logik
    /// kan ligga i presentern medan vyn endast ansvarar för WinForms-layout.
    /// </summary>
    public interface IBlotterView
    {
        /// <summary>
        /// Grid som visar options-trades (OPTION_VANILLA, OPTION_NDO).
        /// Presentern binder sina options-rader mot denna grid.
        /// </summary>
        DataGridView OptionsGrid { get; }

        /// <summary>
        /// Grid som visar linjära/hedge-trades (SPOT, FWD, SWAP, NDF).
        /// Presentern binder sina hedge-rader mot denna grid.
        /// </summary>
        DataGridView HedgeGrid { get; }

        /// <summary>
        /// Grid som visar alla trades (alla produkt-typer).
        /// Används i separat All-tab.
        /// </summary>
        DataGridView AllGrid { get; }
    }
}
