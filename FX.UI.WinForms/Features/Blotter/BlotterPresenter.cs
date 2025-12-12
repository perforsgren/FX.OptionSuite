using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Forms;
using FxTradeHub.Contracts.Dtos;
using FxTradeHub.Services;
using System.Threading.Tasks;

namespace FX.UI.WinForms.Features.Blotter
{
    /// <summary>
    /// Presenter för FX Trade Blotter.
    /// Äger blotter-state (BindingLists) och hämtar data via IBlotterReadService.
    /// View-lagret är en tunn WinForms-kontroll som implementerar IBlotterView.
    /// </summary>
    public sealed class BlotterPresenter
    {
        private readonly IBlotterReadService _readService;

        /// <summary>
        /// Asynkron läs-tjänst för blottern (UI-friendly).
        /// Används av LoadInitialAsync/RefreshAsync för att inte blockera UI.
        /// </summary>
        private readonly IBlotterReadServiceAsync _readServiceAsync;

        private IBlotterView _view;

        private readonly BindingList<BlotterTradeRow> _optionsTrades;
        private readonly BindingList<BlotterTradeRow> _hedgeTrades;
        private readonly BindingList<BlotterTradeRow> _allTrades;

        private BlotterFilter _currentFilter;

        /// <summary>
        /// Markerar om initial dataladdning redan gjorts.
        /// Förhindrar att vi laddar om varje gång blottern aktiveras.
        /// </summary>
        private bool _initialLoadDone;

        /// <summary>
        /// BindingList med blotter-rader för Options-vyn
        /// (OPTION_VANILLA, OPTION_NDO).
        /// Ägs av presentern och binds mot viewns OptionsGrid.
        /// </summary>
        public BindingList<BlotterTradeRow> OptionsTrades
        {
            get { return _optionsTrades; }
        }

        /// <summary>
        /// BindingList med blotter-rader för Hedge/FX Linear-vyn
        /// (SPOT, FWD, SWAP, NDF).
        /// Ägs av presentern och binds mot viewns HedgeGrid.
        /// </summary>
        public BindingList<BlotterTradeRow> HedgeTrades
        {
            get { return _hedgeTrades; }
        }

        /// <summary>
        /// BindingList med blotter-rader för All-vyn
        /// (alla produkt-typer).
        /// Ägs av presentern och binds mot viewns AllGrid.
        /// </summary>
        public BindingList<BlotterTradeRow> AllTrades
        {
            get { return _allTrades; }
        }


        /// <summary>
        /// Skapar en ny instans av BlotterPresenter.
        /// </summary>
        /// <param name="readService">
        /// Synkron tjänst som läser blotter-data från STP-hubben och mappar till BlotterTradeRow.
        /// </param>
        /// <param name="readServiceAsync">
        /// Asynkron tjänst som läser blotter-data från STP-hubben utan att blockera UI.
        /// </param>
        public BlotterPresenter(IBlotterReadService readService, IBlotterReadServiceAsync readServiceAsync)
        {
            _readService = readService ?? throw new ArgumentNullException(nameof(readService));
            _readServiceAsync = readServiceAsync ?? throw new ArgumentNullException(nameof(readServiceAsync));

            _optionsTrades = new BindingList<BlotterTradeRow>();
            _hedgeTrades = new BindingList<BlotterTradeRow>();
            _allTrades = new BindingList<BlotterTradeRow>();
        }


        /// <summary>
        /// Kopplar presentern till en konkret vy.
        /// Sätter upp data-bindning mellan presenter-state och vy-grids.
        /// </summary>
        /// <param name="view">Vyn som ska drivas av presentern.</param>
        public void AttachView(IBlotterView view)
        {
            _view = view ?? throw new ArgumentNullException(nameof(view));

            // Koppla presenter-listor till vy-grids om de finns.
            if (_view.OptionsGrid != null)
            {
                _view.OptionsGrid.AutoGenerateColumns = false;
                _view.OptionsGrid.DataSource = _optionsTrades;
            }

            if (_view.HedgeGrid != null)
            {
                _view.HedgeGrid.AutoGenerateColumns = false;
                _view.HedgeGrid.DataSource = _hedgeTrades;
            }

            if (_view.AllGrid != null)
            {
                // All-grid kan initialt vara dold eller inte färdigkonfigurerad,
                // men vi kopplar ändå DataSource för framtida bruk.
                _view.AllGrid.AutoGenerateColumns = false;
                _view.AllGrid.DataSource = _allTrades;
            }
        }

        /// <summary>
        /// Anropas när blotterns fönster aktiveras i shellen.
        /// Säkerställer att en initial dataladdning sker en gång,
        /// preferens: async-kedja om IBlotterReadServiceAsync finns.
        /// </summary>
        public void OnActivated()
        {
            if (_initialLoadDone)
            {
                return;
            }

            EnsureDefaultFilter();

            // Om vi har en async-tjänst använder vi den, annars sync som fallback.
            if (_readServiceAsync != null)
            {
                // Fire-and-forget på UI-tråden; continuation sker på UI-tråden
                // eftersom vi INTE använder ConfigureAwait(false) i LoadInitialAsync.
                var _ = LoadInitialAsync();
            }
            else
            {
                LoadInitial();
            }
        }





        /// <summary>
        /// Anropas när blotter-fönstret tappar fokus.
        /// I v1 görs inget här – används senare för polling/ timers.
        /// </summary>
        public void OnDeactivated()
        {
            // Placeholder för framtida logik (polling, timers etc.).
        }

        /// <summary>
        /// Säkerställer att det finns ett default BlotterFilter för initial laddning.
        /// V1: Ingen datumfiltrering, men en övre gräns på antal rader för att skydda UI.
        /// </summary>
        private void EnsureDefaultFilter()
        {
            if (_currentFilter != null)
            {
                // Någon (t.ex. ett framtida filter-UI) har redan satt filter – respektera det.
                return;
            }

            _currentFilter = new BlotterFilter
            {
                // Ingen datumfiltrering i v1 – repository tolkar null som "ingen gräns".
                FromTradeDate = null,
                ToTradeDate = null,

                // Alla produkter, källor, motparter och traders som default.
                ProductType = null,
                SourceType = null,
                CounterpartyCode = null,
                TraderId = null,
                CurrentUserId = null,

                // Rimlig övre gräns för antal rader i initial vy.
                MaxRows = 200
            };
        }

        /// <summary>
        /// Initial laddning via async-tjänsten.
        /// Anropas från OnActivated när IBlotterReadServiceAsync finns.
        /// </summary>
        private async Task LoadInitialAsync()
        {
            if (_initialLoadDone)
            {
                return;
            }

            EnsureDefaultFilter();
            await LoadDataAsync();   // OBS: inget ConfigureAwait(false)
            _initialLoadDone = true;
        }

        /// <summary>
        /// Gemensam async-laddning som hämtar trades från IBlotterReadServiceAsync,
        /// delar upp i Options/Hedge/All och uppdaterar BindingLists.
        /// Måste köras på UI-tråden eftersom BindingList är bunden mot DataGridView.
        /// </summary>
        private async Task LoadDataAsync()
        {
            if (_readServiceAsync == null)
            {
                throw new InvalidOperationException("Async read service is not configured.");
            }

            if (_currentFilter == null)
            {
                throw new InvalidOperationException("Blotter-filter saknas vid asynkron dataladdning.");
            }

            List<BlotterTradeRow> trades;

            try
            {
                // Hämta data från STP-hubben (kan blocka IO-tråd, därför async).
                trades = await _readServiceAsync.GetBlotterTradesAsync(_currentFilter);
            }
            catch (Exception ex)
            {
                // TODO: Byt till logging/statusrad när vi har ett mönster för felvisning.
                MessageBox.Show(
                    "Fel vid asynkron läsning av blotter-data från STP-hubben:\r\n" + ex.Message,
                    "Blotter – läsfel (async)",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);

                trades = new List<BlotterTradeRow>();
            }

            // Här är vi tillbaka på UI-tråden (ingen ConfigureAwait(false)),
            // så det är säkert att uppdatera BindingList som är databindad mot grids.
            _optionsTrades.Clear();
            _hedgeTrades.Clear();
            _allTrades.Clear();

            foreach (var row in trades)
            {
                _allTrades.Add(row);

                if (IsOption(row))
                {
                    _optionsTrades.Add(row);
                }
                else if (IsHedge(row))
                {
                    _hedgeTrades.Add(row);
                }
            }

            // Om view finns, trigga uppdatering av rad-räknare.
            //_view?.UpdateRowCounts();
        }

        /// <summary>
        /// Kör en full omladdning av blotter-datat via async-tjänsten.
        /// Förbereder filter och delegerar till LoadDataAsync.
        /// </summary>
        public async Task RefreshAsync()
        {
            if (_readServiceAsync == null)
            {
                // Fallback: sync-refresh om async-tjänst saknas.
                Refresh();
                return;
            }

            if (_currentFilter == null)
            {
                EnsureDefaultFilter();
            }

            await LoadDataAsync();   // OBS: inget ConfigureAwait(false)
        }



        /// <summary>
        /// Synkron wrapper runt den asynkrona initiala dataladdningen.
        /// Behålls för bakåtkompatibilitet mot befintligt UI som ännu inte
        /// använder async/await. Anropar <see cref="LoadInitialAsync"/> och
        /// blockerar tills laddningen är klar.
        /// </summary>
        public void LoadInitial()
        {
            LoadInitialAsync().GetAwaiter().GetResult();
        }



        /// <summary>
        /// Synkron wrapper runt den asynkrona refresh-laddningen.
        /// Behålls till dess att hela UI:t är migrerat till async/await.
        /// </summary>
        public void Refresh()
        {
            RefreshAsync().GetAwaiter().GetResult();
        }





        /// <summary>
        /// Normaliserar produkt-typ-strängen till de koder vi använder i blottern,
        /// t.ex. SPOT, FWD, SWAP, NDF, OPTION_VANILLA, OPTION_NDO.
        /// 
        /// Syftet är att hantera ev. skillnader mellan hur ProductType lagras i DB,
        /// i domänlagret (enum.ToString) och hur blottern klassificerar rader.
        /// </summary>
        /// <param name="productType">Rå produkt-typ från BlotterTradeRow.</param>
        /// <returns>Normaliserad produkt-typkod eller tom sträng om saknas.</returns>
        private static string NormalizeProductType(string productType)
        {
            if (string.IsNullOrWhiteSpace(productType))
            {
                return string.Empty;
            }

            var code = productType.Trim().ToUpperInvariant();

            // Här kan vi hantera olika varianter/skrivningar om det skulle behövas.
            // Vi mappar allt till de koder som blottern förväntar sig.
            switch (code)
            {
                case "SPOT":
                    return "SPOT";

                case "FWD":
                case "FWD_OUTRIGHT":
                case "FORWARD":
                    return "FWD";

                case "SWAP":
                    return "SWAP";

                case "NDF":
                    return "NDF";

                case "OPTION_VANILLA":
                case "OPTIONVANILLA":
                //case "OPTION-VANILLA":
                //case "VANILLA_OPTION":
                    return "OPTION_VANILLA";

                case "OPTION_NDO":
                case "OPTIONNDO":
                case "OPTION-NDO":
                case "NDO_OPTION":
                    return "OPTION_NDO";

                default:
                    // Okänd/övrig typ – behåll uppercased kod så den ändå syns i All-vyn.
                    return code;
            }
        }


        /// <summary>
        /// Hjälpmetod för att avgöra om en blotter-rad är en optionsprodukt.
        /// OPTION_VANILLA eller OPTION_NDO (case-insensitive, normaliserad).
        /// </summary>
        /// <param name="row">Blotter-rad som ska klassificeras.</param>
        /// <returns>true om det är en optionsprodukt, annars false.</returns>
        private static bool IsOption(BlotterTradeRow row)
        {
            if (row == null)
            {
                return false;
            }

            var pt = NormalizeProductType(row.ProductType);

            return pt == "OPTION_VANILLA"
                   || pt == "OPTION_NDO";
        }


        /// <summary>
        /// Hjälpmetod för att avgöra om en blotter-rad är en hedge/linjär produkt.
        /// SPOT, FWD, SWAP, NDF (via normaliserad produkt-typ).
        /// </summary>
        /// <param name="row">Blotter-rad som ska klassificeras.</param>
        /// <returns>true om det är en hedge/linjär produkt, annars false.</returns>
        private static bool IsHedge(BlotterTradeRow row)
        {
            if (row == null)
            {
                return false;
            }

            var pt = NormalizeProductType(row.ProductType);

            switch (pt)
            {
                case "SPOT":
                case "FWD":
                case "SWAP":
                case "NDF":
                    return true;

                default:
                    return false;
            }
        }

    }
}
