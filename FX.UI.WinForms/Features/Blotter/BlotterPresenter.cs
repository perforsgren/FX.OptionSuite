using System;
using System.ComponentModel;
using FxTradeHub.Contracts.Dtos;
using FxTradeHub.Services;

namespace FX.UI.WinForms.Features.Blotter
{
    /// <summary>
    /// Presenter/view-model för blotter-workspacet.
    /// Håller separata BindingList:or för options- respektive hedge-trades
    /// och anropar IBlotterReadService för att läsa från STP-hubben.
    /// 
    /// v1:
    /// - Engångsladdning via LoadInitial.
    /// - Uppdelning i options/hedge baserat på ProductType.
    /// 
    /// Senare steg:
    /// - Polling/inkrementella uppdateringar.
    /// - Merge av ändringar + hantering av "All"-griden.
    /// </summary>
    public sealed class BlotterPresenter
    {
        private readonly IBlotterReadService _readService;

        /// <summary>
        /// Trades som ska visas i Options-griden
        /// (OPTION_VANILLA, OPTION_NDO).
        /// </summary>
        public BindingList<BlotterTradeRow> OptionsTrades { get; }

        /// <summary>
        /// Trades som ska visas i Hedge/FX Linear-griden
        /// (SPOT, FWD, NDF, SWAP m.fl.).
        /// </summary>
        public BindingList<BlotterTradeRow> HedgeTrades { get; }

        /// <summary>
        /// Skapar en ny presenter för blottern.
        /// </summary>
        /// <param name="readService">
        /// Läs-tjänst mot STP-hubben som returnerar BlotterTradeRow enligt BlotterFilter.
        /// </param>
        public BlotterPresenter(IBlotterReadService readService)
        {
            if (readService == null) throw new ArgumentNullException(nameof(readService));

            _readService = readService;

            OptionsTrades = new BindingList<BlotterTradeRow>();
            HedgeTrades = new BindingList<BlotterTradeRow>();
        }

        /// <summary>
        /// Gör en första laddning av blotter-data från STP-hubben
        /// enligt angivet filter och fördelar raderna på
        /// OptionsTrades respektive HedgeTrades baserat på ProductType.
        /// </summary>
        /// <param name="filter">
        /// Filter med datumintervall, ev. trader/counterparty etc.
        /// Får inte vara null.
        /// </param>
        public void LoadInitial(BlotterFilter filter)
        {
            if (filter == null) throw new ArgumentNullException(nameof(filter));

            var rows = _readService.GetBlotterTrades(filter) ?? new System.Collections.Generic.List<BlotterTradeRow>();

            OptionsTrades.RaiseListChangedEvents = false;
            HedgeTrades.RaiseListChangedEvents = false;

            try
            {
                OptionsTrades.Clear();
                HedgeTrades.Clear();

                foreach (var row in rows)
                {
                    if (IsOptionProduct(row.ProductType))
                    {
                        OptionsTrades.Add(row);
                    }
                    else
                    {
                        HedgeTrades.Add(row);
                    }
                }
            }
            finally
            {
                OptionsTrades.RaiseListChangedEvents = true;
                HedgeTrades.RaiseListChangedEvents = true;

                // Signalera att listorna har uppdaterats så att BindingSource uppdaterar griden.
                OptionsTrades.ResetBindings();
                HedgeTrades.ResetBindings();
            }
        }

        /// <summary>
        /// Returnerar true om produkttypen ska betraktas som option i blottern.
        /// v1: OPTION_VANILLA och OPTION_NDO.
        /// </summary>
        private static bool IsOptionProduct(string productType)
        {
            if (string.IsNullOrWhiteSpace(productType))
            {
                return false;
            }

            var pt = productType.ToUpperInvariant();
            return pt == "OPTION_VANILLA" || pt == "OPTION_NDO";
        }
    }
}
