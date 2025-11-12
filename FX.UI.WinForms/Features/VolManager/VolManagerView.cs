using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using FX.Core.Domain;

namespace FX.UI.WinForms.Features.VolManager
{
    /// <summary>
    /// WinForms-vy för att inspektera volytor. Stödjer två lägen:
    /// 1) Enkelvy (textbox + "Hämta senaste" + grid) – bra för snabb test.
    /// 2) Flikvy ("pinned pairs") där flera valutapar kan visas samtidigt under en TabControl.
    ///
    /// Vyn är endast read-only i detta steg (MVP-1). Ingen koppling till prismotorn.
    /// Anropa SetPresenter(...) innan du laddar data. För flikläget: anropa InitializeTabbedLayout().
    /// </summary>
    public sealed class VolManagerView : UserControl
    {
        // --- Enkelvy-fält (enkel testlayout) ---
        private TextBox _txtPair;
        private Button _btnFetch;
        private Label _lblSnapshot;
        private DataGridView _grid;
        private BindingSource _bs;

        // --- Flikvy-fält (pinned pairs) ---
        private TabControl _tabs;

        // --- Presenter (obligatorisk innan laddning) ---
        private VolManagerPresenter _presenter;

        /// <summary>
        /// Skapar vykomponenterna. Efter konstruktion:
        ///  - Anropa SetPresenter(...) med den presenter som ska användas.
        ///  - För flikläge: anropa InitializeTabbedLayout() för att byta till TabControl-layout.
        /// </summary>
        public VolManagerView()
        {
            InitializeComponents(); // startar i enkelvy-läge
        }

        /// <summary>
        /// Sätter presentern som vyn använder för att läsa voldata från databasen.
        /// Måste anropas innan någon Load*/Pin*-metod körs.
        /// </summary>
        /// <param name="presenter">Instans av VolManagerPresenter.</param>
        public void SetPresenter(VolManagerPresenter presenter)
        {
            _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        }

        /// <summary>
        /// Enkelvy: Läser senaste snapshot för angivet par och binder resultatet till gridet.
        /// Visar även snapshot-id i en label. Om inget snapshot finns rensas gridet.
        /// </summary>
        /// <param name="pairSymbol">Valutapar, t.ex. "EUR/USD" eller "USD/SEK".</param>
        public void LoadLatestFromPair(string pairSymbol)
        {
            if (_presenter == null)
                throw new InvalidOperationException("SetPresenter måste anropas innan LoadLatestFromPair.");

            if (_lblSnapshot != null) _lblSnapshot.Text = "Snapshot: –";
            if (_bs != null) _bs.DataSource = null;

            if (string.IsNullOrWhiteSpace(pairSymbol))
                return;

            var result = _presenter.LoadLatestWithHeader(pairSymbol);
            var rows = (IList<VolSurfaceRow>)(result.Rows ?? new List<VolSurfaceRow>());

            if (_bs != null) _bs.DataSource = rows;
            if (_lblSnapshot != null)
            {
                _lblSnapshot.Text = result.SnapshotId.HasValue
                    ? $"Snapshot: {result.SnapshotId.Value}"
                    : "Snapshot: –";
            }
        }

        /// <summary>
        /// Initierar den enkla testlayouten (textbox + knapp + label + grid).
        /// Denna layout ersätts om InitializeTabbedLayout() anropas.
        /// </summary>
        private void InitializeComponents()
        {
            // Textbox för par
            _txtPair = new TextBox
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Left,
                Width = 120,
                Text = "USD/SEK"
            };

            // Hämta-knapp
            _btnFetch = new Button
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Left,
                Left = _txtPair.Right + 8,
                Width = 110,
                Text = "Hämta senaste"
            };

            // Snapshot-label
            _lblSnapshot = new Label
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Left,
                Left = _btnFetch.Right + 12,
                AutoSize = true,
                Text = "Snapshot: –"
            };

            // Grid + binding
            _grid = new DataGridView
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Left = 0,
                Top = _txtPair.Bottom + 8,
                Width = this.Width,
                Height = this.Height - (_txtPair.Bottom + 8),
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AutoGenerateColumns = false,
                RowHeadersVisible = false
            };

            _bs = new BindingSource();
            _grid.DataSource = _bs;

            EnsureGridColumns(_grid);

            // Lägg till kontroller
            this.Controls.Add(_txtPair);
            this.Controls.Add(_btnFetch);
            this.Controls.Add(_lblSnapshot);
            this.Controls.Add(_grid);

            // Event
            _btnFetch.Click += OnClickFetchLatest;

            // Startstorlek (om kontrollen värdas fristående)
            this.Width = 900;
            this.Height = 500;
        }

        /// <summary>
        /// Säkerställer att ett angivet DataGridView har kolumner för tenor och vol-fält (ATM bid/mid/ask samt RR/BF).
        /// </summary>
        /// <param name="grid">Grid som ska få kolumner.</param>
        private static void EnsureGridColumns(DataGridView grid)
        {
            grid.Columns.Clear();

            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(VolSurfaceRow.TenorCode),
                HeaderText = "Tenor",
                Width = 70,
                ReadOnly = true
            });

            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(VolSurfaceRow.TenorDaysNominal),
                HeaderText = "Days (nom.)",
                Width = 90,
                ReadOnly = true
            });

            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(VolSurfaceRow.AtmBid),
                HeaderText = "ATM Bid",
                Width = 90,
                ReadOnly = true,
                DefaultCellStyle = { Format = "N6" }
            });

            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(VolSurfaceRow.AtmMid),
                HeaderText = "ATM Mid",
                Width = 90,
                ReadOnly = true,
                DefaultCellStyle = { Format = "N6" }
            });

            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(VolSurfaceRow.AtmAsk),
                HeaderText = "ATM Ask",
                Width = 90,
                ReadOnly = true,
                DefaultCellStyle = { Format = "N6" }
            });

            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(VolSurfaceRow.Rr25Mid),
                HeaderText = "RR25 Mid",
                Width = 90,
                ReadOnly = true,
                DefaultCellStyle = { Format = "N6" }
            });

            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(VolSurfaceRow.Bf25Mid),
                HeaderText = "BF25 Mid",
                Width = 90,
                ReadOnly = true,
                DefaultCellStyle = { Format = "N6" }
            });

            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(VolSurfaceRow.Rr10Mid),
                HeaderText = "RR10 Mid",
                Width = 90,
                ReadOnly = true,
                DefaultCellStyle = { Format = "N6" }
            });

            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(VolSurfaceRow.Bf10Mid),
                HeaderText = "BF10 Mid",
                Width = 90,
                ReadOnly = true,
                DefaultCellStyle = { Format = "N6" }
            });
        }

        /// <summary>
        /// Klick på "Hämta senaste" i enkelvy-läge – läser in par från textbox och laddar gridet.
        /// </summary>
        private void OnClickFetchLatest(object sender, EventArgs e)
        {
            var pair = (_txtPair.Text ?? string.Empty).Trim();
            LoadLatestFromPair(pair);
        }

        /// <summary>
        /// Växlar vyn till flikläge med TabControl för "pinned pairs".
        /// Bygger vänster pin-panel (placeholder) och höger TabControl. Rensar enkelvy-kontrollerna.
        /// </summary>
        public void InitializeTabbedLayout()
        {
            // Vänster: enkel pin-panel (placeholder – kan ersättas av "Pair Explorer" senare)
            var pnlLeft = new Panel { Dock = DockStyle.Left, Width = 180 };
            var lblLeft = new Label { Text = "Pair", Dock = DockStyle.Top, Height = 18 };
            _txtPair = new TextBox { Dock = DockStyle.Top, Text = "USD/SEK" };
            var btnPin = new Button { Dock = DockStyle.Top, Text = "Pin" };
            btnPin.Click += (s, e) =>
            {
                var p = (_txtPair.Text ?? string.Empty).Trim();
                if (!string.IsNullOrEmpty(p))
                    PinPair(p);
            };
            pnlLeft.Controls.Add(btnPin);
            pnlLeft.Controls.Add(_txtPair);
            pnlLeft.Controls.Add(lblLeft);

            // Höger: TabControl för pinned pairs
            _tabs = new TabControl { Dock = DockStyle.Fill };

            // Rensa tidigare enkelvy-kontroller
            this.Controls.Clear();
            this.Controls.Add(_tabs);
            this.Controls.Add(pnlLeft);

            // (Valfritt) starta med att pinna textboxens par:
            // PinPair(_txtPair.Text?.Trim());
        }

        /// <summary>
        /// Skapar eller aktiverar en underflik för angivet valutapar och laddar "senaste + header + rows".
        /// </summary>
        /// <param name="pairSymbol">Valutapar som ska visas i fliken.</param>
        public void PinPair(string pairSymbol)
        {
            if (string.IsNullOrWhiteSpace(pairSymbol))
                return;

            if (_tabs == null)
                throw new InvalidOperationException("InitializeTabbedLayout måste anropas innan PinPair.");

            // Finns redan?
            for (int i = 0; i < _tabs.TabPages.Count; i++)
            {
                var existing = _tabs.TabPages[i];
                if (string.Equals(existing.Text, pairSymbol, StringComparison.OrdinalIgnoreCase))
                {
                    _tabs.SelectedTab = existing;
                    LoadLatestForPairTab(existing, pairSymbol);
                    return;
                }
            }

            // Annars: skapa ny tab
            var tab = CreatePairTab(pairSymbol);
            _tabs.TabPages.Add(tab);
            _tabs.SelectedTab = tab;

            LoadLatestForPairTab(tab, pairSymbol);
        }

        /// <summary>
        /// Skapar en TabPage (underflik) för ett valutapar inklusive en header-panel (chips) och ett grid.
        /// UI-referenser sparas i TabPage.Tag (PairTabUi) för senare uppdatering.
        /// </summary>
        /// <param name="pairSymbol">Valutapar (används som tabbrubrik).</param>
        /// <returns>Ny TabPage redo att laddas.</returns>
        private TabPage CreatePairTab(string pairSymbol)
        {
            var tab = new TabPage(pairSymbol);

            // Header med "chips"
            var header = new Panel { Dock = DockStyle.Top, Height = 28 };
            var lblSnap = new Label { AutoSize = true, Left = 4, Top = 6, Text = "Snapshot: –" };
            var lblDelta = new Label { AutoSize = true, Left = 200, Top = 6, Text = "Δ: –" };
            var lblPA = new Label { AutoSize = true, Left = 280, Top = 6, Text = "PA: –" };
            var lblTs = new Label { AutoSize = true, Left = 340, Top = 6, Text = "TS: –" };

            header.Controls.Add(lblSnap);
            header.Controls.Add(lblDelta);
            header.Controls.Add(lblPA);
            header.Controls.Add(lblTs);

            // Grid + binding
            var grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AutoGenerateColumns = false,
                RowHeadersVisible = false
            };
            var bs = new BindingSource();
            grid.DataSource = bs;

            EnsureGridColumns(grid);

            // Layout i tabben
            tab.Controls.Add(grid);
            tab.Controls.Add(header);

            // Spara UI-refs i Tag
            tab.Tag = new PairTabUi
            {
                Grid = grid,
                Binding = bs,
                LabelSnapshot = lblSnap,
                LabelDelta = lblDelta,
                LabelPremiumAdj = lblPA,
                LabelTs = lblTs
            };

            return tab;
        }

        /// <summary>
        /// Laddar "senaste + header + rows" för en given TabPage och uppdaterar grid och header-chipsen.
        /// </summary>
        /// <param name="tab">TabPage vars Tag ska innehålla PairTabUi.</param>
        /// <param name="pairSymbol">Valutapar som fliken representerar.</param>
        private void LoadLatestForPairTab(TabPage tab, string pairSymbol)
        {
            if (_presenter == null)
                throw new InvalidOperationException("SetPresenter måste anropas innan laddning.");

            if (tab == null || !(tab.Tag is PairTabUi ui))
                return;

            var result = _presenter.LoadLatestWithHeader(pairSymbol);

            // Grid
            ui.Binding.DataSource = result.Rows ?? new List<VolSurfaceRow>();

            // Header-chips
            if (result.SnapshotId.HasValue)
                ui.LabelSnapshot.Text = $"Snapshot: {result.SnapshotId.Value}";
            else
                ui.LabelSnapshot.Text = "Snapshot: –";

            if (result.Header != null)
            {
                ui.LabelDelta.Text = $"Δ: {result.Header.DeltaConvention}";
                ui.LabelPremiumAdj.Text = $"PA: {(result.Header.PremiumAdjusted ? "On" : "Off")}";
                ui.LabelTs.Text = $"TS: {result.Header.TsUtc:yyyy-MM-dd HH:mm:ss}Z";
            }
            else
            {
                ui.LabelDelta.Text = "Δ: –";
                ui.LabelPremiumAdj.Text = "PA: –";
                ui.LabelTs.Text = "TS: –";
            }
        }

        /// <summary>
        /// Enkel behållare för UI-kontroller som hör till en underflik (pair-tab).
        /// Används för att slippa leta i Controls-samlingar vid uppdatering.
        /// </summary>
        private sealed class PairTabUi
        {
            public DataGridView Grid { get; set; }
            public BindingSource Binding { get; set; }
            public Label LabelSnapshot { get; set; }
            public Label LabelDelta { get; set; }
            public Label LabelPremiumAdj { get; set; }
            public Label LabelTs { get; set; }
        }
    }
}
