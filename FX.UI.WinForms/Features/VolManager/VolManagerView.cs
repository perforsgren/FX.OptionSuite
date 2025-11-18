using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Forms;
using FX.Core.Domain;
using FX.Core.Interfaces;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.Window;

namespace FX.UI.WinForms.Features.VolManager
{
    /// <summary>
    /// WinForms-vy för att inspektera volytor. Stödjer två lägen:
    /// 1) Enkelvy (textbox + "Hämta senaste" + grid) – bra för snabb test.
    /// 2) Flikvy ("pinned pairs") där flera valutapar kan visas samtidigt under en TabControl.
    /// Anropa SetPresenter(...) innan du laddar data. För flikläget: anropa InitializeTabbedLayout().
    /// </summary>
    public sealed class VolManagerView : UserControl
    {
        #region Fält – enkelvy (snabbtest)
        private TextBox _txtPair;
        private Button _btnFetch;
        private Label _lblSnapshot;
        private DataGridView _grid;
        private BindingSource _bs;
        #endregion

        #region Fält

        private TabControl _pairTabs;
        private FlowLayoutPanel _tilesPanel;

        /// <summary>
        /// Kolumnläge för Tiles: ATM-only = superkompakt, Compact = fler nyckelfält.
        /// </summary>
        private enum TileColumnsMode { AtmOnly, Compact }

        // Default = Compact (som idag)
        private TileColumnsMode _tileColumnsMode = TileColumnsMode.Compact;

        // Hotkey-filter (globalt på applikationens meddelandepump, scoped till aktuellt Form)
        private HotkeysMessageFilter _hotkeysFilter;

        // Draft-lager: per pair (UPPER), per tenor
        private readonly Dictionary<string, Dictionary<string, VolDraftRow>> _draftStore = new Dictionary<string, Dictionary<string, VolDraftRow>>(StringComparer.OrdinalIgnoreCase);

        private sealed class VolDraftRow
        {
            public string TenorCode { get; set; }
            public decimal? Rr25Mid { get; set; }
            public decimal? Bf25Mid { get; set; }
            public decimal? Rr10Mid { get; set; }
            public decimal? Bf10Mid { get; set; }
            public decimal? AtmSpread { get; set; } // icke-ankrat
            public decimal? AtmOffset { get; set; } // ankrat
            public decimal? AtmMid { get; set; }
        }

        /// <summary>
        /// Enkel diff-rad för review: Tenor, Field, Old, New.
        /// </summary>
        private sealed class VolDraftChange
        {
            public string Tenor { get; set; }
            public string Field { get; set; }
            public decimal? Old { get; set; }
            public decimal? New { get; set; }
        }


        #endregion

        #region Fält – presenter
        private VolManagerPresenter _presenter;
        #endregion

        #region Lifecycle & init (konstruktor, presenter, dispose)

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
        /// När handtaget skapas kopplar vi in en global hotkey-lyssnare (IMessageFilter)
        /// så F5/Ctrl+F5 fångas även när fokus ligger i child-kontroller.
        /// </summary>
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                if (_hotkeysFilter == null)
                {
                    _hotkeysFilter = new HotkeysMessageFilter(this);
                    Application.AddMessageFilter(_hotkeysFilter);
                }
            }
            catch { /* best effort */ }
        }

        /// <summary>
        /// När kontrollen förstörs avregistrerar vi hotkey-lyssnaren.
        /// </summary>
        protected override void OnHandleDestroyed(EventArgs e)
        {
            try
            {
                if (_hotkeysFilter != null)
                {
                    Application.RemoveMessageFilter(_hotkeysFilter);
                    _hotkeysFilter = null;
                }
            }
            catch { /* best effort */ }

            base.OnHandleDestroyed(e);
        }

        /// <summary>
        /// Fångar F5 och Ctrl+F5 oavsett vilket barn som har fokus.
        /// F5 = Refresh (soft cache), Ctrl+F5 = Refresh (force/bypass cache).
        /// </summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F5)
            {
                TriggerRefreshFromHotkey(force: false);
                return true; // markerar som hanterad
            }
            if (keyData == (Keys.Control | Keys.F5))
            {
                TriggerRefreshFromHotkey(force: true);
                return true; // markerar som hanterad
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }


        #endregion


        #region Public API (presenter, laddning)

        /// <summary>
        /// Fired när sessionens UI-state (Pinned/Recent/View/TileColumns) har ändrats,
        /// så att workspace kan persistenta direkt.
        /// </summary>
        public event EventHandler UiStateChanged;

        /// <summary>
        /// Helper för att raisa <see cref="UiStateChanged"/> säkert.
        /// </summary>
        private void OnUiStateChanged()
        {
            var h = UiStateChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        /// <summary>
        /// Representerar minsta UI-state för en session (Pinned/Recent + vyläge/tiles).
        /// </summary>
        public sealed class VolManagerUiState
        {
            public List<string> Pinned { get; set; } = new List<string>();
            public List<string> Recent { get; set; } = new List<string>();
            public string View { get; set; } = "Tabs";          // "Tabs" | "Tiles"
            public string TileColumns { get; set; } = "Compact"; // "Compact" | "AtmOnly"
        }

        /// <summary>
        /// Exporterar UI-state direkt från kontrollerna (LstPinned/LstRecent + vy/tiles).
        /// </summary>
        public VolManagerUiState ExportUiState()
        {
            var lstPinned = FindChild<ListBox>(this, "LstPinned");
            var lstRecent = FindChild<ListBox>(this, "LstRecent");

            var pinned = (lstPinned != null)
                ? lstPinned.Items.Cast<object>()
                    .Select(x => x as string)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : new List<string>();

            var recent = (lstRecent != null)
                ? lstRecent.Items.Cast<object>()
                    .Select(x => x as string)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : new List<string>();

            var isTiles = _tilesPanel != null && _tilesPanel.Visible;
            var tileCols = (_tileColumnsMode == TileColumnsMode.AtmOnly) ? "AtmOnly" : "Compact";

            return new VolManagerUiState
            {
                Pinned = pinned,
                Recent = recent,
                View = isTiles ? "Tiles" : "Tabs",
                TileColumns = tileCols
            };
        }


        /// <summary>
        /// Applicerar sparat UI-state till kontrollerna (LstPinned/LstRecent + vy/tiles).
        /// </summary>
        public void ApplyUiState(VolManagerUiState state)
        {
            if (state == null) return;

            // Tile-kolumner först (så rebuild blir rätt ifall Tiles är aktivt)
            _tileColumnsMode = string.Equals(state.TileColumns, "AtmOnly", StringComparison.OrdinalIgnoreCase)
                ? TileColumnsMode.AtmOnly
                : TileColumnsMode.Compact;

            var rbAtmOnly = FindChild<RadioButton>(this, "RbTileAtmOnly");
            var rbCompact = FindChild<RadioButton>(this, "RbTileCompact");
            if (rbAtmOnly != null) rbAtmOnly.Checked = (_tileColumnsMode == TileColumnsMode.AtmOnly);
            if (rbCompact != null) rbCompact.Checked = (_tileColumnsMode == TileColumnsMode.Compact);

            // Listor
            var lstPinned = FindChild<ListBox>(this, "LstPinned");
            var lstRecent = FindChild<ListBox>(this, "LstRecent");
            if (lstPinned != null)
            {
                lstPinned.Items.Clear();
                foreach (var p in state.Pinned ?? Enumerable.Empty<string>())
                    if (!string.IsNullOrWhiteSpace(p)) lstPinned.Items.Add(p);
            }
            if (lstRecent != null)
            {
                lstRecent.Items.Clear();
                foreach (var r in state.Recent ?? Enumerable.Empty<string>())
                    if (!string.IsNullOrWhiteSpace(r)) lstRecent.Items.Add(r);
            }

            // Skapa flikar för Pinned (utan “force load” från DB – bara UI)
            if (lstPinned != null)
            {
                foreach (var it in lstPinned.Items.Cast<object>())
                {
                    var pair = it as string;
                    if (!string.IsNullOrWhiteSpace(pair))
                        PinPair(pair);
                }
            }

            // Vy
            if (string.Equals(state.View, "Tiles", StringComparison.OrdinalIgnoreCase))
                ShowTilesView(forceInitialLoad: false);
            else
                ShowTabsView();

            // Rebuild tiles om aktiv
            if (_tilesPanel != null && _tilesPanel.Visible)
                RebuildTilesFromPinned(force: false);
        }

        /// <summary>
        /// Läser Pinned/Recent/View/TileColumns från UI, sparar till presentern
        /// och signalerar till workspace att persist sker (UiStateChanged).
        /// </summary>
        private void SaveUiStateFromControls()
        {
            if (_presenter == null)
            {
                // Även om presenter saknas kan vi åtminstone meddela workspace om förändring.
                OnUiStateChanged();
                return;
            }

            var lstPinned = FindChild<ListBox>(this, "LstPinned");
            var lstRecent = FindChild<ListBox>(this, "LstRecent");

            var pinned = (lstPinned != null)
                ? lstPinned.Items.Cast<object>()
                    .Select(x => x as string)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : new List<string>();

            var recent = (lstRecent != null)
                ? lstRecent.Items.Cast<object>()
                    .Select(x => x as string)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : new List<string>();

            var view = (_tilesPanel != null && _tilesPanel.Visible) ? "Tiles" : "Tabs";
            var tileCols = (_tileColumnsMode == TileColumnsMode.AtmOnly) ? "AtmOnly" : "Compact";

            // 1) spara till presenter (intern kopia om du använder den)
            _presenter.SaveUiState(pinned, recent, view, tileCols);

            // 2) signalera workspace → SaveWorkspaceStateToDisk() (via VolWorkspaceControl-subscribe)
            OnUiStateChanged();
        }

        /// <summary>
        /// Triggar en Refresh från hotkey: i Tiles-läge uppdateras ALLA tiles,
        /// i Tabs-läge uppdateras enbart den aktiva pair-fliken.
        /// <paramref name="force"/> = true bypassar cachen (Ctrl+F5).
        /// </summary>
        public void TriggerRefreshFromHotkey(bool force)
        {
            if (_tilesPanel != null && _tilesPanel.Visible)
            {
                RefreshAllTiles(force);
            }
            else
            {
                RefreshActivePair(force);
            }
        }

        /// <summary>
        /// Uppdaterar alla tiles i Tiles-vyn (om den är aktiv).
        /// </summary>
        /// <param name="force">True = bypass cache.</param>
        public void RefreshAllTiles(bool force = false)
        {
            if (_tilesPanel == null || !_tilesPanel.Visible) return;

            foreach (Control c in _tilesPanel.Controls)
            {
                var ui = c.Tag as TileUi;
                if (ui == null || string.IsNullOrWhiteSpace(ui.PairSymbol)) continue;
                LoadLatestForTile((Panel)c, ui.PairSymbol, force);
                AddToRecent(ui.PairSymbol, 10);
            }
        }

        /// <summary>
        /// Laddar om aktiv pair-flik via presentern. force=true bypassar cache (Ctrl+F5).
        /// </summary>
        public void RefreshActivePair(bool force)
        {
            if (_pairTabs == null || _pairTabs.TabPages.Count == 0) return;

            var tab = _pairTabs.SelectedTab;
            var pair = tab?.Text?.Trim();
            if (string.IsNullOrWhiteSpace(pair)) return;

            if (_presenter != null)
                _presenter.RefreshPairAndBindAsync(pair, force).ConfigureAwait(false);

            AddToRecent(pair, 10);
        }

        /// <summary>
        /// Laddar om data (senaste snapshot) för den aktiva pair-fliken.
        /// Uppdaterar både grid och header-chips.
        /// (Delegerar till overloaden med force=false.)
        /// </summary>
        public void RefreshActivePair()
        {
            RefreshActivePair(force: false);
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
        #endregion

        #region Public API – Databindning (från Presenter)

        /// <summary>
        /// Binder en volyta till parflikens grid. Sätter cellvärden *per kolumnnamn* (inte via index)
        /// för att undvika kolumnförskjutningar när ATM-justeringskolumnen (Spread/Offset) finns.
        /// Bygger även baseline-kartan (row.Tag) för Review (ATM_bid/ask/mid, RR/BF, ATM_adj).
        /// </summary>
        public void BindPairSurface(string pair, DateTime tsUtc, IList<VolSurfaceRow> rows, bool fromCache)
        {
            if (string.IsNullOrWhiteSpace(pair) || _pairTabs == null) return;
            pair = pair.Trim().ToUpperInvariant();

            var grid = EnsurePairTabGrid(pair);
            if (grid == null) return;

            // Header
            var page = grid.Parent as TabPage ?? grid.Parent?.Parent as TabPage;
            var host = page?.Controls[0] as Panel ?? page;
            var lbl = FindChild<Label>(host, "PairHeaderLabel");
            if (lbl != null)
                lbl.Text = $"{pair} | TS: {FormatTimeUtc(tsUtc)} | {(fromCache ? "Cached" : "Fresh")}";

            // Är paret ankrat? (styr hur vi sätter ATM_adj baseline)
            var isAnchored = _presenter != null && _presenter.IsAnchoredPair(pair);

            // Rensa och fyll rader
            grid.Rows.Clear();
            if (rows == null) rows = Array.Empty<VolSurfaceRow>();

            // Lokal hjälp: sätt cell via kolumnnamn (Name eller HeaderText). Ingen throw vid saknad kolumn.
            int ColIndexByNameOrHeader(DataGridView g, string nameOrHeader)
            {
                foreach (DataGridViewColumn c in g.Columns)
                    if (string.Equals(c.Name, nameOrHeader, StringComparison.OrdinalIgnoreCase)) return c.Index;
                foreach (DataGridViewColumn c in g.Columns)
                    if (string.Equals(c.HeaderText, nameOrHeader, StringComparison.OrdinalIgnoreCase)) return c.Index;
                return -1;
            }
            void SetCell(DataGridViewRow r, string colKey, object value)
            {
                var idx = ColIndexByNameOrHeader(grid, colKey);
                if (idx >= 0) r.Cells[idx].Value = value ?? "";
            }

            foreach (var r in rows)
            {
                var ri = grid.Rows.Add();
                var row = grid.Rows[ri];

                // Tenor + nominal days
                SetCell(row, "Tenor", r.TenorCode ?? "");
                SetCell(row, "DaysNom", r.TenorDaysNominal.HasValue ? (object)r.TenorDaysNominal.Value : "");

                // ATM Bid / Ask / Mid
                SetCell(row, "ATM_bid", r.AtmBid);
                SetCell(row, "ATM_ask", r.AtmAsk);
                SetCell(row, "ATM_mid", r.AtmMid);

                // RR/BF mids
                SetCell(row, "RR25_mid", r.Rr25Mid);
                SetCell(row, "RR10_mid", r.Rr10Mid);
                SetCell(row, "BF25_mid", r.Bf25Mid);
                SetCell(row, "BF10_mid", r.Bf10Mid);

                // DB-baseline (“Before”) för Review
                // För icke-ankrat par: ATM_adj = ask − bid; för ankrat: null (hanteras i senare steg).
                decimal? atmAdjBefore = null;
                if (!isAnchored && r.AtmAsk.HasValue && r.AtmBid.HasValue)
                    atmAdjBefore = r.AtmAsk.Value - r.AtmBid.Value;

                row.Tag = new Dictionary<string, decimal?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ATM_bid"] = r.AtmBid,
                    ["ATM_ask"] = r.AtmAsk,
                    ["ATM_mid"] = r.AtmMid,
                    ["RR25"] = r.Rr25Mid,
                    ["RR10"] = r.Rr10Mid,
                    ["BF25"] = r.Bf25Mid,
                    ["BF10"] = r.Bf10Mid,
                    ["ATM_adj"] = atmAdjBefore
                };
            }

            // Lägg/uppd. ATM-justeringskolumn efter ATM Mid (Spread för icke-ankrat, Offset för ankrat)
            EnsureAtmAdjustColumn(grid, isAnchoredPair: isAnchored);

            // Gör kolumner editerbara + koppla handlers (en gång)
            EnableEditingForPairGrid(grid);

            // Lägg på draft-värden (inkl. ATM_adj) ovanpå DB-värdena
            ApplyDraftToGrid(pair, grid);

            // Uppdatera "Edits: n"
            UpdateDraftEditsCounter(pair);
        }


        /// <summary>
        /// Visar/döljer en enkel busy-overlay för ett valutapar i aktuell vy.
        /// Tabs: overlay i tab-sidans host-panel. Tiles: overlay i parets tile-panel.
        /// </summary>
        public void ShowPairBusy(string pair, bool isBusy)
        {
            if (string.IsNullOrWhiteSpace(pair)) return;

            if (_pairTabs != null && _pairTabs.Visible)
            {
                TabPage page = null;
                foreach (TabPage p in _pairTabs.TabPages)
                    if (string.Equals(p.Text, pair, StringComparison.OrdinalIgnoreCase)) { page = p; break; }
                if (page != null)
                {
                    var host = page.Controls.Count > 0 ? page.Controls[0] : (Control)page;
                    var overlay = GetOrCreateBusyOverlay(host, "BusyOverlay");
                    overlay.Visible = isBusy;
                    if (isBusy) overlay.BringToFront();
                }
                return;
            }

            if (_tilesPanel != null && _tilesPanel.Visible)
            {
                Panel tile = null;
                foreach (Control c in _tilesPanel.Controls)
                    if (c is Panel p && string.Equals(p.Tag as string, pair, StringComparison.OrdinalIgnoreCase)) { tile = p; break; }
                if (tile != null)
                {
                    var overlay = GetOrCreateBusyOverlay(tile, "BusyOverlay");
                    overlay.Visible = isBusy;
                    if (isBusy) overlay.BringToFront();
                }
            }
        }

        /// <summary>
        /// Visar ett felmeddelande för ett visst par i aktuell vy (Tabs eller Tiles) som overlay.
        /// </summary>
        public void ShowPairError(string pair, string message)
        {
            if (string.IsNullOrWhiteSpace(pair)) return;
            message = string.IsNullOrWhiteSpace(message) ? "Error" : message.Trim();

            if (_pairTabs != null && _pairTabs.Visible)
            {
                var page = default(TabPage);
                foreach (TabPage p in _pairTabs.TabPages)
                    if (string.Equals(p.Text, pair, StringComparison.OrdinalIgnoreCase)) { page = p; break; }

                if (page != null)
                {
                    var host = page.Controls.Count > 0 ? page.Controls[0] : (Control)page;
                    var overlay = GetOrCreateMessageOverlay(host, "ErrorOverlay", message, Color.IndianRed);
                    overlay.Visible = true;
                    overlay.BringToFront();
                }
                return;
            }

            if (_tilesPanel != null && _tilesPanel.Visible)
            {
                Panel tile = null;
                foreach (Control c in _tilesPanel.Controls)
                    if (c is Panel p && string.Equals(p.Tag as string, pair, StringComparison.OrdinalIgnoreCase)) { tile = p; break; }

                if (tile != null)
                {
                    var overlay = GetOrCreateMessageOverlay(tile, "ErrorOverlay", message, Color.IndianRed);
                    overlay.Visible = true;
                    overlay.BringToFront();
                }
            }
        }

        #endregion

        #region Public API – State access

        /// <summary>
        /// Hämtar aktuellt UI-state från vyn (konservativ variant):
        /// - Pinned = alla parflikar i _pairTabs
        /// - View = "Tabs" om flikläget är aktivt, annars "Tiles"
        /// - TileColumns = "Compact" (placeholder tills full wiring finns)
        /// - Recent = tom lista (placeholder)
        /// </summary>
        private VolManagerUiState GetUiState()
        {
            var pinned = new List<string>();
            if (_pairTabs != null)
            {
                foreach (TabPage tp in _pairTabs.TabPages)
                {
                    if (!string.IsNullOrWhiteSpace(tp.Text))
                        pinned.Add(tp.Text);
                }
            }

            var view = (_pairTabs != null && _pairTabs.Visible) ? "Tabs" : "Tiles";
            var tileCols = "Compact"; // TODO: ersätt med verklig tiles-kolumnpolicy när den är på plats

            return new VolManagerUiState
            {
                Pinned = pinned,
                Recent = new List<string>(),
                View = view,
                TileColumns = tileCols
            };
        }

        /// <summary>
        /// True om Tabs-läget är aktivt (pair-flikar ska visas/bindas).
        /// </summary>
        public bool IsTabsModeActive() => _pairTabs != null && _pairTabs.Visible;

        /// <summary>
        /// True om Tiles-läget är aktivt (kort/tile-layout ska visas/bindas).
        /// </summary>
        public bool IsTilesModeActive() => _tilesPanel != null && _tilesPanel.Visible;

        /// <summary>
        /// Returnerar en fryst kopia av aktuella pinned-par för den här sessionens vy.
        /// </summary>
        public IReadOnlyList<string> SnapshotPinnedPairs()
        {
            // Om du redan har en UiState-modell: använd den.
            var st = GetUiState();              // existerar i din vy
            var list = st?.Pinned ?? new List<string>();
            return list.ToList();               // fryst kopia
        }

        /// <summary>
        /// Returnerar symbolen för aktiv par-flik i Tabs-läget, annars null.
        /// </summary>
        public string GetActivePairTabSymbolOrNull()
        {
            return _pairTabs != null && _pairTabs.Visible
                ? _pairTabs.SelectedTab?.Text
                : null;
        }

        #endregion

        #region Enkelvy – init & event (kan bantas bort när Pair Tabs är standard)
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
            Controls.Add(_txtPair);
            Controls.Add(_btnFetch);
            Controls.Add(_lblSnapshot);
            Controls.Add(_grid);

            // Event
            _btnFetch.Click += OnClickFetchLatest;

            // Startstorlek (om kontrollen värdas fristående)
            Width = 900;
            Height = 500;
        }

        /// <summary>
        /// Klick på "Hämta senaste" i enkelvy-läge – läser in par från textbox och laddar gridet.
        /// </summary>
        private void OnClickFetchLatest(object sender, EventArgs e)
        {
            var pair = (_txtPair.Text ?? string.Empty).Trim();
            LoadLatestFromPair(pair);
        }
        #endregion

        #region Grid & bindning (kolumnsetup)

        /// <summary>
        /// Väljer kolumnuppsättning för en tile utifrån valt TileColumnsMode.
        /// </summary>
        /// <param name="grid">Tile-grid.</param>
        /// <param name="mode">Kolumnläge.</param>
        private static void EnsureTileColumnsByMode(DataGridView grid, TileColumnsMode mode)
        {
            switch (mode)
            {
                case TileColumnsMode.AtmOnly:
                    EnsureTileAtmOnlyColumns(grid);
                    break;
                case TileColumnsMode.Compact:
                default:
                    EnsureTileGridColumns(grid); // befintlig "Compact"
                    break;
            }
        }

        /// <summary>
        /// Superkompakt kolumnuppsättning för Tiles: endast Tenor och ATM Mid (4 dp).
        /// </summary>
        private static void EnsureTileAtmOnlyColumns(DataGridView grid)
        {
            if (grid == null) return;

            grid.Columns.Clear();
            grid.ReadOnly = true;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.AllowUserToOrderColumns = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = false;
            grid.RowHeadersVisible = false;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.ColumnHeadersHeight = 22;
            grid.RowTemplate.Height = 18;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;

            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(VolSurfaceRow.TenorCode),
                HeaderText = "Tenor",
                Width = 80,
                ReadOnly = true,
                DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleLeft }
            });

            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(VolSurfaceRow.AtmMid),
                HeaderText = "ATM",
                Width = 90,
                ReadOnly = true,
                DefaultCellStyle = new DataGridViewCellStyle { Format = "0.0000", Alignment = DataGridViewContentAlignment.MiddleRight }
            });
        }

        /// <summary>
        /// Kompakt tile-layout: Tenor + ATM/RR25/BF25 (4 dp).
        /// </summary>
        private static void EnsureTileGridColumns(DataGridView grid)
        {
            if (grid == null) return;

            grid.Columns.Clear();
            grid.ReadOnly = true;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.AllowUserToOrderColumns = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = false;
            grid.RowHeadersVisible = false;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.ColumnHeadersHeight = 22;
            grid.RowTemplate.Height = 18;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;

            var volStyle = new DataGridViewCellStyle { Format = "0.0000", Alignment = DataGridViewContentAlignment.MiddleRight };

            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(VolSurfaceRow.TenorCode),
                HeaderText = "Tenor",
                Width = 64,
                ReadOnly = true,
                DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleLeft }
            });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(VolSurfaceRow.AtmMid),
                HeaderText = "ATM",
                Width = 74,
                ReadOnly = true,
                DefaultCellStyle = volStyle
            });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(VolSurfaceRow.Rr25Mid),
                HeaderText = "RR25",
                Width = 70,
                ReadOnly = true,
                DefaultCellStyle = volStyle
            });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(VolSurfaceRow.Bf25Mid),
                HeaderText = "BF25",
                Width = 70,
                ReadOnly = true,
                DefaultCellStyle = volStyle
            });
        }

        /// <summary>
        /// Sätter upp kolumner för huvudgrid i Tabs-läget.
        /// Voltal renderas med 4 decimaler (0.0000).
        /// </summary>
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

            // 4 dp på alla vol-kolumner
            var volStyle = new DataGridViewCellStyle { Format = "0.0000", Alignment = DataGridViewContentAlignment.MiddleRight };

            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(VolSurfaceRow.AtmBid),
                HeaderText = "ATM Bid",
                Width = 90,
                ReadOnly = true,
                DefaultCellStyle = volStyle
            });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(VolSurfaceRow.AtmMid),
                HeaderText = "ATM Mid",
                Width = 90,
                ReadOnly = true,
                DefaultCellStyle = volStyle
            });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(VolSurfaceRow.AtmAsk),
                HeaderText = "ATM Ask",
                Width = 90,
                ReadOnly = true,
                DefaultCellStyle = volStyle
            });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(VolSurfaceRow.Rr25Mid),
                HeaderText = "RR25 Mid",
                Width = 90,
                ReadOnly = true,
                DefaultCellStyle = volStyle
            });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(VolSurfaceRow.Bf25Mid),
                HeaderText = "BF25 Mid",
                Width = 90,
                ReadOnly = true,
                DefaultCellStyle = volStyle
            });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(VolSurfaceRow.Rr10Mid),
                HeaderText = "RR10 Mid",
                Width = 90,
                ReadOnly = true,
                DefaultCellStyle = volStyle
            });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(VolSurfaceRow.Bf10Mid),
                HeaderText = "BF10 Mid",
                Width = 90,
                ReadOnly = true,
                DefaultCellStyle = volStyle
            });
        }

        #endregion

        #region Tabs-läge (grid & header)

        /// <summary>
        /// Räknar ATM Mid och ATM Bid/Ask för en grid-rad utifrån aktuella cellvärden + baseline,
        /// med stöd för anchored och non-anchored:
        /// - Non-anchored:   Mid = ATM_mid (cell→baseline).
        /// - Anchored:       Mid = AnchorMid + Offset, där AnchorMid ≈ (baseline ATM_mid − baseline ATM_adj), Offset = ATM_adj (cell→baseline).
        /// - Spread:         ATM_spread (cell) → (ATM_ask − ATM_bid) (cell/header) → baseline (ATM_ask − ATM_bid).
        /// Sätter cellerna "ATM_mid", "ATM_bid", "ATM_ask" om underlag finns.
        /// </summary>
        private void RecomputeAtmForRow(DataGridView grid, DataGridViewRow row, bool anchored)
        {
            if (grid == null || row == null) return;

            // Hjälpare: hämta cellvärde via Name eller HeaderText
            decimal? getCell(string key) => TryGetCellDecimal(row, key);

            // ---- Spread
            decimal? spread = getCell("ATM_spread");
            if (!spread.HasValue)
            {
                // Försök härleda från Bid/Ask i celler (både Name "ATM_ask"/"ATM_bid" och Header "ATM Ask"/"ATM Bid")
                var bid = getCell("ATM_bid") ?? getCell("ATM Bid");
                var ask = getCell("ATM_ask") ?? getCell("ATM Ask");
                if (bid.HasValue && ask.HasValue && ask.Value >= bid.Value)
                    spread = ask.Value - bid.Value;
            }
            if (!spread.HasValue && row.Tag is Dictionary<string, decimal?> baseMap)
            {
                // Fallback till baseline
                if (baseMap.TryGetValue("ATM_ask", out var a0) && a0.HasValue &&
                    baseMap.TryGetValue("ATM_bid", out var b0) && b0.HasValue &&
                    a0.Value >= b0.Value)
                {
                    spread = a0.Value - b0.Value;
                }
            }

            // ---- Mid
            decimal? mid = null;
            if (!anchored)
            {
                mid = getCell("ATM_mid");
                if (!mid.HasValue && row.Tag is Dictionary<string, decimal?> m1 && m1.TryGetValue("ATM_mid", out var m0) && m0.HasValue)
                    mid = m0.Value;
            }
            else
            {
                decimal? anchorMid = null;
                if (row.Tag is Dictionary<string, decimal?> bm)
                {
                    if (bm.TryGetValue("ATM_mid", out var dbMid) && dbMid.HasValue &&
                        bm.TryGetValue("ATM_adj", out var dbOff) && dbOff.HasValue)
                    {
                        anchorMid = dbMid.Value - dbOff.Value;
                    }
                }
                var offset = getCell("ATM_adj");
                if (!offset.HasValue && row.Tag is Dictionary<string, decimal?> om && om.TryGetValue("ATM_adj", out var off0) && off0.HasValue)
                    offset = off0.Value;

                if (anchorMid.HasValue && offset.HasValue)
                    mid = anchorMid.Value + offset.Value;
            }

            // ---- Skriv tillbaka
            // Mid kan behöva uppdateras (t.ex. anchored när Offset ändras)
            if (mid.HasValue && grid.Columns.Contains("ATM_mid"))
                row.Cells["ATM_mid"].Value = mid.Value;

            if (mid.HasValue && spread.HasValue && spread.Value >= 0m)
            {
                var bidOut = mid.Value - spread.Value / 2m;
                var askOut = mid.Value + spread.Value / 2m;

                // Sätt via kolumnnamn om de finns
                if (grid.Columns.Contains("ATM_bid")) row.Cells["ATM_bid"].Value = bidOut;
                if (grid.Columns.Contains("ATM_ask")) row.Cells["ATM_ask"].Value = askOut;
            }
        }


        /// <summary>
        /// Visar en enkel inmatningsdialog för att sätta ett nytt ATM Mid-värde (decimal med punkt).
        /// Returnerar det användaren matat in, eller null om avbrutet.
        /// </summary>
        private decimal? ShowSetAtmMidDialog(string pair, string tenor, decimal currentMid)
        {
            var dlg = new Form
            {
                Text = $"Set ATM Mid – {pair} / {tenor}",
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowIcon = false,
                ShowInTaskbar = false,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                Width = 360,
                Height = 160
            };

            var lbl = new Label { Left = 12, Top = 12, AutoSize = true, Text = "ATM Mid (use '.' as decimal):" };
            var tb = new TextBox { Left = 12, Top = lbl.Bottom + 6, Width = dlg.ClientSize.Width - 24, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
            tb.Text = currentMid.ToString("0.####", CultureInfo.InvariantCulture);

            var btnOk = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 90, Left = dlg.ClientSize.Width - 12 - 90 - 8 - 90, Top = tb.Bottom + 12, Anchor = AnchorStyles.Right | AnchorStyles.Bottom };
            var btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90, Left = dlg.ClientSize.Width - 12 - 90, Top = tb.Bottom + 12, Anchor = AnchorStyles.Right | AnchorStyles.Bottom };

            dlg.AcceptButton = btnOk;
            dlg.CancelButton = btnCancel;

            dlg.Controls.Add(lbl);
            dlg.Controls.Add(tb);
            dlg.Controls.Add(btnOk);
            dlg.Controls.Add(btnCancel);

            decimal? parsedOrNull(string s)
            {
                if (string.IsNullOrWhiteSpace(s)) return null;
                s = s.Trim();
                // Tillåt även komma – normalisera till punkt
                if (s.IndexOf(',') >= 0) s = s.Replace(',', '.');
                if (decimal.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d))
                    return d;
                return null;
            }

            return dlg.ShowDialog(this) == DialogResult.OK ? parsedOrNull(tb.Text) : (decimal?)null;
        }

        /// <summary>
        /// Försöker härleda AnchorMid för en rad i ett ankrat par ur baseline (row.Tag):
        /// AnchorMid ≈ (baseline ATM_mid) − (baseline ATM_adj). Returnerar null om ej möjligt.
        /// </summary>
        private decimal? TryGetAnchorMidFromRow(DataGridViewRow row)
        {
            if (row?.Tag is Dictionary<string, decimal?> map)
            {
                var hasMid = map.TryGetValue("ATM_mid", out var mid);
                var hasAdj = map.TryGetValue("ATM_adj", out var adj); // baseline offset i anchored
                if (hasMid && hasAdj && mid.HasValue && adj.HasValue)
                    return mid.Value - adj.Value;
            }
            return null;
        }

        /// <summary>
        /// Bygger ATM Bid/Mid/Ask-preview för en rad utifrån nuvarande cellvärden (inkl. ev. draft)
        /// med robusta fallbacks för spread.
        /// - Non-anchored: Mid = ATM_mid (cell → baseline).
        /// - Anchored: Mid = AnchorMid + Offset, där AnchorMid ≈ (baseline ATM_mid − baseline ATM_adj).
        /// - Spread: ATM_spread (cell) → (ATM_ask − ATM_bid) (cell: både "ATM_ask"/"ATM ask") → baseline "ATM_ask"/"ATM_bid".
        /// Returnerar tooltip-text (punkt som decimal) eller null om ej beräkningsbart.
        /// </summary>
        private string BuildAtmPreviewTooltip(DataGridViewRow row, bool anchored)
        {
            if (row == null) return null;

            var inv = System.Globalization.CultureInfo.InvariantCulture;

            // ---- Spread: primärt från cell "ATM_spread"
            decimal? spread = TryGetCellDecimal(row, "ATM_spread");

            // Fallback 1: härled från Bid/Ask i celler (stöder både "ATM_ask"/"ATM ask" och "ATM_bid"/"ATM bid")
            if (!spread.HasValue)
            {
                decimal? bid = TryGetCellDecimal(row, "ATM_bid");
                if (!bid.HasValue) bid = TryGetCellDecimal(row, "ATM Bid");

                decimal? ask = TryGetCellDecimal(row, "ATM_ask");
                if (!ask.HasValue) ask = TryGetCellDecimal(row, "ATM Ask");

                if (bid.HasValue && ask.HasValue && ask.Value >= bid.Value)
                    spread = ask.Value - bid.Value;
            }

            // Fallback 2: baseline från row.Tag ("ATM_ask" / "ATM_bid")
            if (!spread.HasValue && row.Tag is Dictionary<string, decimal?> baseMap)
            {
                if (baseMap.TryGetValue("ATM_ask", out var a0) && a0.HasValue &&
                    baseMap.TryGetValue("ATM_bid", out var b0) && b0.HasValue &&
                    a0.Value >= b0.Value)
                {
                    spread = a0.Value - b0.Value;
                }
            }

            // ---- Mid
            decimal? mid = null;

            if (!anchored)
            {
                mid = TryGetCellDecimal(row, "ATM_mid");
                if (!mid.HasValue && row.Tag is Dictionary<string, decimal?> map1 && map1.TryGetValue("ATM_mid", out var m0) && m0.HasValue)
                    mid = m0.Value;
            }
            else
            {
                // AnchorMid ≈ baseline ATM_mid − baseline ATM_adj
                decimal? anchorMid = null;
                if (row.Tag is Dictionary<string, decimal?> map2)
                {
                    if (map2.TryGetValue("ATM_mid", out var bm) && bm.HasValue &&
                        map2.TryGetValue("ATM_adj", out var bo) && bo.HasValue)
                    {
                        anchorMid = bm.Value - bo.Value;
                    }
                }

                var offset = TryGetCellDecimal(row, "ATM_adj");
                if (!offset.HasValue && row.Tag is Dictionary<string, decimal?> map3 && map3.TryGetValue("ATM_adj", out var off0) && off0.HasValue)
                    offset = off0.Value;

                if (anchorMid.HasValue && offset.HasValue)
                    mid = anchorMid.Value + offset.Value;
            }

            // ---- Resultat
            if (!mid.HasValue || !spread.HasValue || spread.Value < 0m) return null;

            var bidOut = mid.Value - spread.Value / 2m;
            var askOut = mid.Value + spread.Value / 2m;
            return $"ATM Preview  Bid={bidOut.ToString("0.####", inv)}  Mid={mid.Value.ToString("0.####", inv)}  Ask={askOut.ToString("0.####", inv)}";
        }

        /// <summary>
        /// Säkerställer att griden har en kontextmeny för ATM Mid när paret är anchored.
        /// Högerklick på ATM_mid visar "Set ATM Mid…" som översätter till Offset=dMid.
        /// </summary>
        private void EnsureAtmContextMenuForGrid(DataGridView grid)
        {
            if (grid == null) return;

            // Koppla mus-hanterare en gång
            if (!string.Equals(grid.Tag as string, "edit-wired", StringComparison.OrdinalIgnoreCase))
                return; // Wire görs i EnableEditingForPairGrid

            grid.CellMouseDown -= OnPairGridCellMouseDown;
            grid.CellMouseDown += OnPairGridCellMouseDown;
        }

        /// <summary>
        /// Hanterar högerklick på ATM_mid för anchored par och visar kontextmeny "Set ATM Mid…".
        /// Utfallet blir att AtmOffset skrivs till draft (NewOffset = NewMid − AnchorMid).
        /// </summary>
        private void OnPairGridCellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
        {
            var grid = sender as DataGridView;
            if (grid == null || e.Button != MouseButtons.Right || e.RowIndex < 0 || e.ColumnIndex < 0) return;

            var col = grid.Columns[e.ColumnIndex];
            if (col == null || !string.Equals(col.Name, "ATM_mid", StringComparison.OrdinalIgnoreCase)) return;

            var pair = GetActivePairFor(grid);
            if (string.IsNullOrWhiteSpace(pair) || !_presenter.IsAnchoredPair(pair)) return;

            var row = grid.Rows[e.RowIndex];
            var tenor = GetTenorFromRow(row);
            if (string.IsNullOrEmpty(tenor)) return;

            // Läs aktuellt Mid (display) – används som default i dialogen
            var curMid = TryGetCellDecimal(row, "ATM_mid") ?? 0m;

            // Skapa enkel CM på plats
            var cms = new ContextMenuStrip
            {
                ShowImageMargin = false,
                ShowCheckMargin = false
            };
            var mi = new ToolStripMenuItem("Set ATM Mid"); 
            cms.Items.Add(mi);
            mi.Click += (s, _e) =>
            {
                var newMid = ShowSetAtmMidDialog(pair, tenor, curMid);
                if (!newMid.HasValue) return;

                var anchorMid = TryGetAnchorMidFromRow(row);
                if (!anchorMid.HasValue)
                {
                    MessageBox.Show(this, "AnchorMid saknas i baseline (row.Tag). Kan inte räkna Offset.", "Volatility Manager",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var newOffset = newMid.Value - anchorMid.Value;

                // Skriv till draft (AtmOffset) och uppdatera Offset-cellen
                UpsertDraftValue(pair, tenor, "AtmOffset", newOffset);
                if (grid.Columns.Contains("ATM_adj"))
                    row.Cells["ATM_adj"].Value = newOffset;

                // Kör radregler (hårda/mjuka)
                var hard = new List<string>();
                var soft = new List<string>();
                EvaluateRowRules_Scoped(row, true, hard, soft);
                if (hard.Count > 0) row.ErrorText = string.Join("  ", hard);

                // Draft-räknare + repaint
                UpdateDraftEditsCounter(pair);
                grid.InvalidateRow(e.RowIndex);
            };

            // Visa vid klickposition
            cms.Show(Cursor.Position);
        }


        /// <summary>
        /// Säkerställer att grid har separata kolumner för "ATM Offset" (ATM_adj) och "ATM Spread" (ATM_spread),
        /// samt sätter synlighet och ReadOnly enligt ankarstatus.
        /// </summary>
        private void EnsureAtmColumnsAndVisibility(DataGridView grid, bool anchored)
        {
            if (grid == null) return;

            // 1) ATM Offset-kolumn (ATM_adj) – ska alltid finnas, permanent header "ATM Offset"
            var colOffset = grid.Columns.Cast<DataGridViewColumn>()
                .FirstOrDefault(c => string.Equals(c.Name, "ATM_adj", StringComparison.OrdinalIgnoreCase));
            if (colOffset != null)
            {
                colOffset.HeaderText = "ATM Offset";
                colOffset.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
                colOffset.Width = Math.Max(colOffset.Width, 90);
            }

            // 2) ATM Spread-kolumn (ATM_spread) – skapa om den saknas
            var colSpread = grid.Columns.Cast<DataGridViewColumn>()
                .FirstOrDefault(c => string.Equals(c.Name, "ATM_spread", StringComparison.OrdinalIgnoreCase));
            if (colSpread == null)
            {
                colSpread = new DataGridViewTextBoxColumn
                {
                    Name = "ATM_spread",
                    HeaderText = "ATM Spread",
                    Width = 90,
                    SortMode = DataGridViewColumnSortMode.NotSortable,
                    DefaultCellStyle = new DataGridViewCellStyle
                    {
                        Alignment = DataGridViewContentAlignment.MiddleCenter
                    },
                    ReadOnly = false
                };
                // Placera bredvid ATM_mid om möjligt
                var idxMid = grid.Columns.Contains("ATM_mid") ? grid.Columns["ATM_mid"].Index : -1;
                var insertAt = idxMid >= 0 ? idxMid + 1 : grid.Columns.Count;
                grid.Columns.Insert(insertAt, colSpread);
            }
            else
            {
                colSpread.HeaderText = "ATM Spread";
                colSpread.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
                colSpread.Width = Math.Max(colSpread.Width, 90);
            }

            // 3) Synlighet/ReadOnly per ankarstatus
            // Anchored: visa båda (Offset + Spread); Mid = RO
            // Non-anchored: visa endast Spread; Offset göms; Mid = editerbar
            if (anchored)
            {
                if (colOffset != null)
                {
                    colOffset.Visible = true;
                    colOffset.ReadOnly = false;   // Offset editerbar vid anchored
                }
                if (colSpread != null)
                {
                    colSpread.Visible = true;
                    colSpread.ReadOnly = false;   // Spread editerbar vid anchored
                }
                if (grid.Columns.Contains("ATM_mid"))
                {
                    grid.Columns["ATM_mid"].ReadOnly = true; // Mid härledd vid anchored
                }
            }
            else
            {
                if (colOffset != null)
                {
                    colOffset.Visible = false;    // Offset används ej i non-anchored UI
                }
                if (colSpread != null)
                {
                    colSpread.Visible = true;
                    colSpread.ReadOnly = false;   // Spread editerbar
                }
                if (grid.Columns.Contains("ATM_mid"))
                {
                    grid.Columns["ATM_mid"].ReadOnly = false; // Mid editerbar vid non-anchored
                }
            }
        }


        /// <summary>
        /// Bygger diff-listan (Tenor, Field, Old, New) från draft-lagret för ett par
        /// genom att jämföra mot baseline som lades i row.Tag i BindPairSurface.
        /// Inkluderar "ATM Mid" (baseline-nyckel: "ATM_mid").
        /// </summary>
        private List<VolDraftChange> BuildDraftDiffForPair(string pair, DataGridView grid) // ERSÄTT
        {
            var list = new List<VolDraftChange>();
            if (string.IsNullOrWhiteSpace(pair) || grid == null) return list;

            if (!_draftStore.TryGetValue(pair, out var perPair) || perPair == null || perPair.Count == 0)
                return list;

            foreach (var kv in perPair)
            {
                var tenor = kv.Key;
                var d = kv.Value;
                if (d == null) continue;

                // Hitta grid-rad för detta tenor (för att läsa baseline från row.Tag)
                DataGridViewRow row = null;
                foreach (DataGridViewRow r in grid.Rows)
                {
                    var t = r.Cells["Tenor"]?.Value?.ToString();
                    if (string.Equals(t, tenor, StringComparison.OrdinalIgnoreCase)) { row = r; break; }
                }
                if (row == null) continue;

                // Baseline (“Before”) satt i BindPairSurface via row.Tag
                var baseMap = row.Tag as Dictionary<string, decimal?> ?? new Dictionary<string, decimal?>();

                decimal? GetOld(string fieldKey)
                {
                    switch (fieldKey)
                    {
                        case "ATM Mid": return baseMap.TryGetValue("ATM_mid", out var mid) ? mid : (decimal?)null;

                        case "RR25 Mid": return baseMap.TryGetValue("RR25", out var rr25) ? rr25 : (decimal?)null;
                        case "RR10 Mid": return baseMap.TryGetValue("RR10", out var rr10) ? rr10 : (decimal?)null;
                        case "BF25 Mid": return baseMap.TryGetValue("BF25", out var bf25) ? bf25 : (decimal?)null;
                        case "BF10 Mid": return baseMap.TryGetValue("BF10", out var bf10) ? bf10 : (decimal?)null;

                        // ATM Spread/Offset hämtas ur baseline-nyckeln "ATM_adj".
                        // Icke-ankrat: "Before" = ask−bid; ankrat: kan vara null (visas blankt).
                        case "ATM Spread":
                        case "ATM Offset":
                            return baseMap.TryGetValue("ATM_adj", out var adj) ? adj : (decimal?)null;

                        default:
                            return null;
                    }
                }

                void addIfChanged(string fieldKey, decimal? newVal)
                {
                    if (!newVal.HasValue) return;
                    var oldVal = GetOld(fieldKey);
                    if (!oldVal.HasValue || oldVal.Value != newVal.Value)
                    {
                        list.Add(new VolDraftChange
                        {
                            Tenor = tenor,
                            Field = fieldKey,
                            Old = oldVal,
                            New = newVal
                        });
                    }
                }

                // Viktigt: ATM Mid ska vara med i diffen
                addIfChanged("ATM Mid", d.AtmMid);

                // Övriga fält
                addIfChanged("ATM Spread", d.AtmSpread);
                addIfChanged("ATM Offset", d.AtmOffset);
                addIfChanged("RR25 Mid", d.Rr25Mid);
                addIfChanged("RR10 Mid", d.Rr10Mid);
                addIfChanged("BF25 Mid", d.Bf25Mid);
                addIfChanged("BF10 Mid", d.Bf10Mid);
            }

            list.Sort((a, b) => string.CompareOrdinal(a.Tenor, b.Tenor));
            return list;
        }


        /// <summary>
        /// Summerar antal rader i grid som har hårda fel (Row.ErrorText != tom)
        /// och antal som har någon mjuk varning (tooltip på berörda celler).
        /// </summary>
        private (int hard, int soft) CountRuleFlags(DataGridView grid)
        {
            int hard = 0, soft = 0;
            foreach (DataGridViewRow r in grid.Rows)
            {
                if (!string.IsNullOrEmpty(r.ErrorText)) hard++;

                // mjuka varningar finns som ToolTipText på berörda RR/BF/ATM_adj-celler
                bool anySoft = false;
                foreach (var name in new[] { "RR25_mid", "RR10_mid", "BF25_mid", "BF10_mid", "ATM_adj" })
                {
                    var c = r.Cells[name];
                    if (c != null && !string.IsNullOrEmpty(c.ToolTipText)) { anySoft = true; break; }
                }
                if (anySoft) soft++;
            }
            return (hard, soft);
        }

        /// <summary>
        /// Hämtar UI-behållaren (PairTabUi) för ett givet valutapar
        /// från TabControl-layouten. Returnerar null om fliken inte finns.
        /// </summary>
        private PairTabUi GetPairTabUi(string pair)
        {
            if (_pairTabs == null) return null;

            foreach (TabPage page in _pairTabs.TabPages)
            {
                if (page.Tag is PairTabUi ui &&
                    string.Equals(ui.PairSymbol, pair, StringComparison.OrdinalIgnoreCase))
                    return ui;
            }

            return null;
        }

        /// <summary>
        /// Försöker läsa snapshotens UTC-tidsstämpel för ett par från den aktiva pair-gridens baseline
        /// (sparad i DataGridViewRow.Tag vid bindningen). Returnerar null om ingen baseline hittas.
        /// </summary>
        private DateTime? ResolveSnapshotTsUtcForPair(string pair)
        {
            if (string.IsNullOrWhiteSpace(pair) || _pairTabs == null) return null;

            var grid = EnsurePairTabGrid(pair.Trim().ToUpperInvariant());
            if (grid == null) return null;

            foreach (DataGridViewRow r in grid.Rows)
            {
                var tag = r?.Tag as System.Collections.Generic.Dictionary<string, object>;
                if (tag != null && tag.TryGetValue("SnapshotTsUtc", out var o) && o is DateTime dt)
                    return dt;
            }
            return null;
        }

        /// <summary>
        /// Bygger Review-dialogen (draft-rader + QC). Förutom cross-tenor-QC lägger vi även in
        /// intra-tenor (radvisa) hårda fel som "QC: Intra-tenor (hard)" baserat på gridens Row.ErrorText.
        /// </summary>
        private Form BuildReviewDialog(string pair, List<VolDraftChange> changes, int hardCount, int softCount)
        {
            string F4(decimal? x) => x.HasValue
                ? x.Value.ToString("0.0000", CultureInfo.InvariantCulture)
                : string.Empty;

            var dlg = new Form
            {
                Text = $"Review – {pair}",
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowIcon = false,
                ShowInTaskbar = false,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                Width = 880,
                Height = 520
            };

            var lblHeader = new Label
            {
                AutoSize = true,
                Text = $"Draft changes: {changes?.Count ?? 0}  |  Hard errors: {hardCount}  |  Soft warnings: {softCount}",
                Left = 8,
                Top = 8
            };
            dlg.Controls.Add(lblHeader);

            var grid = new DataGridView
            {
                Left = 8,
                Top = lblHeader.Bottom + 8,
                Width = dlg.ClientSize.Width - 16,
                Height = dlg.ClientSize.Height - 16 - 44 - (lblHeader.Bottom + 8),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToOrderColumns = false,
                MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                RowHeadersVisible = false,
                BackgroundColor = Color.White,
                GridColor = Color.FromArgb(220, 220, 220),
                EnableHeadersVisualStyles = false
            };

            grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                BackColor = Color.FromArgb(245, 246, 248),
                Font = new Font("Segoe UI", 9F, FontStyle.Bold)
            };
            grid.DefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular)
            };
            grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(248, 250, 252)
            };

            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Tenor", Width = 90, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Field", Width = 180, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Before", Width = 120, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "After", Width = 120, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Warnings (soft) / Preview", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.NotSortable });

            var anchored = _presenter != null && _presenter.IsAnchoredPair(pair);
            var activeGrid = GetActivePairGrid();

            // 1) Draft-rader + ATM-preview
            foreach (var c in (changes ?? new List<VolDraftChange>()))
            {
                var note = TryGetWarning(c);
                if (activeGrid != null &&
                    (c.Field == "ATM Mid" || c.Field == "ATM Spread" || c.Field == "ATM Offset"))
                {
                    // lägg ATM-preview i noten
                    DataGridViewRow row = null;
                    foreach (DataGridViewRow r in activeGrid.Rows)
                    {
                        var t = r.Cells["Tenor"]?.Value?.ToString();
                        if (string.Equals(t, c.Tenor, StringComparison.OrdinalIgnoreCase)) { row = r; break; }
                    }
                    if (row != null)
                    {
                        var preview = BuildAtmPreviewTooltip(row, anchored);
                        if (!string.IsNullOrEmpty(preview))
                            note = string.IsNullOrEmpty(note) ? preview : (note + "  |  " + preview);
                    }
                }
                grid.Rows.Add(c.Tenor, c.Field, F4(c.Old), F4(c.New), note);
            }

            // 2) QC: intra-tenor (hårda) – ta radfel från den aktiva griden
            var intraHardAdd = 0;
            if (activeGrid != null)
            {
                foreach (DataGridViewRow r in activeGrid.Rows)
                {
                    if (!string.IsNullOrWhiteSpace(r.ErrorText))
                    {
                        var t = GetTenorFromRow(r) ?? "?";
                        grid.Rows.Add(t, "QC: Intra-tenor (hard)", "", "", r.ErrorText);
                        intraHardAdd++;
                    }
                }
            }

            // 3) QC: cross-tenor (som tidigare)
            var crossHardAdd = 0;
            var crossSoftAdd = 0;
            if (activeGrid != null)
            {
                var qc = BuildCrossTenorQcRows(pair, activeGrid);
                crossHardAdd = qc.Item1;
                crossSoftAdd = qc.Item2;
                foreach (var t in qc.Item3)
                    grid.Rows.Add(t.Item1, t.Item2, t.Item3, t.Item4, t.Item5);
            }

            // Uppdatera headern med totala counts
            var totalHard = hardCount + intraHardAdd + crossHardAdd;
            var totalSoft = softCount + crossSoftAdd;
            lblHeader.Text = $"Draft changes: {changes?.Count ?? 0}  |  Hard errors: {totalHard}  |  Soft warnings: {totalSoft}";

            dlg.Controls.Add(grid);

            // Knappar
            var pnlButtons = new Panel { Dock = DockStyle.Bottom, Height = 44 };
            dlg.Controls.Add(pnlButtons);

            var btnClose = new Button { Text = "Close", Size = new Size(90, 28), Anchor = AnchorStyles.Right | AnchorStyles.Bottom };
            btnClose.Click += (s, e) => dlg.Close();
            pnlButtons.Controls.Add(btnClose);

            var btnPublish = new Button
            {
                Name = "BtnDialogPublish",
                Text = "Publish",
                Enabled = (changes?.Count ?? 0) > 0,
                Size = new Size(90, 28),
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom
            };
            btnPublish.Click += async (s, e) =>
            {
                btnPublish.Enabled = false;
                try
                {
                    var tsOpt = ResolveSnapshotTsUtcForPair(pair);
                    var tsUtc = tsOpt ?? DateTime.UtcNow;
                    using (var cts = new CancellationTokenSource())
                        await HandlePublishAsync(pair, tsUtc, changes ?? new List<VolDraftChange>(), dlg, cts.Token);
                }
                finally { btnPublish.Enabled = true; }
            };
            pnlButtons.Controls.Add(btnPublish);

            // Layout
            dlg.Load += (s, e) =>
            {
                btnClose.Left = dlg.ClientSize.Width - 8 - btnClose.Width;
                btnClose.Top = 8;
                btnPublish.Left = btnClose.Left - 8 - btnPublish.Width;
                btnPublish.Top = 8;
            };
            dlg.Resize += (s, e) =>
            {
                btnClose.Left = dlg.ClientSize.Width - 8 - btnClose.Width;
                btnPublish.Left = btnClose.Left - 8 - btnPublish.Width;
                grid.Width = dlg.ClientSize.Width - 16;
                grid.Height = dlg.ClientSize.Height - 16 - pnlButtons.Height - (lblHeader.Bottom + 8);
            };

            return dlg;
        }



        /// <summary>
        /// Review-grid: säkerställer att numeriska kolumner "Before" och "After" alltid renderas
        /// med decimalpunkt (.) och 4 decimaler, även om källdata råkar vara text med kommatecken.
        /// Påverkar endast display; underliggande värden lämnas orörda.
        /// </summary>
        private void OnReviewGridCellFormattingNormalizeDecimals(object sender, DataGridViewCellFormattingEventArgs e)
        {
            var grid = sender as DataGridView;
            if (grid == null || e.RowIndex < 0 || e.ColumnIndex < 0) return;

            var col = grid.Columns[e.ColumnIndex];
            if (col == null) return;

            var isNumericDisplayCol =
                string.Equals(col.HeaderText, "Before", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(col.HeaderText, "After", StringComparison.OrdinalIgnoreCase);

            if (!isNumericDisplayCol || e.Value == null) return;

            try
            {
                // Om värdet redan är decimal/double → formatera invariant.
                if (e.Value is decimal dec)
                {
                    e.Value = dec.ToString("0.0000", CultureInfo.InvariantCulture);
                    e.FormattingApplied = true;
                    return;
                }
                if (e.Value is double dbl)
                {
                    e.Value = ((decimal)dbl).ToString("0.0000", CultureInfo.InvariantCulture);
                    e.FormattingApplied = true;
                    return;
                }

                // Om det är text: normalisera , → . och försök tolka.
                var s = e.Value.ToString();
                if (string.IsNullOrWhiteSpace(s)) return;

                // Försök först med nuvarande kultur (tillåter både , och .)
                if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.CurrentCulture, out var p1))
                {
                    e.Value = p1.ToString("0.0000", CultureInfo.InvariantCulture);
                    e.FormattingApplied = true;
                    return;
                }
                // Fallback: byt , → . och kör invariant
                s = s.Replace(',', '.');
                if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var p2))
                {
                    e.Value = p2.ToString("0.0000", CultureInfo.InvariantCulture);
                    e.FormattingApplied = true;
                }
            }
            catch
            {
                // Display-only: vid fel låter vi standardformatet gälla.
            }
        }

        /// <summary>
        /// Försöker plocka ev. “soft warning”-text från en VolDraftChange utan att anta exakt property-namn.
        /// Stöder t.ex. "Warning", "SoftWarning", "Warn", "Message". Finns ingen → tom sträng.
        /// </summary>
        private static string TryGetWarning(VolDraftChange change)
        {
            if (change == null) return string.Empty;
            var t = change.GetType();
            var pi = t.GetProperty("Warning") ??
                     t.GetProperty("SoftWarning") ??
                     t.GetProperty("Warn") ??
                     t.GetProperty("Message");
            return (pi?.GetValue(change) as string) ?? string.Empty;
        }

        /// <summary>
        /// Validerar editerbara celler. Skiljer på ATM Offset (ATM_adj) och ATM Spread (ATM_spread).
        /// - Inmatning med komma/punkt tillåts.
        /// - Anchored: ATM Offset |offset| ≤ 10.000.
        /// - Non-anchored + Anchored: ATM Spread ≥ 0 och ≤ 2×ATM Mid (om Mid finns).
        /// - BF25 Mid ≥ 0. Övriga endast numerisk kontroll här; relationer i EndEdit.
        /// </summary>
        private void PairGrid_CellValidating(object sender, DataGridViewCellValidatingEventArgs e)
        {
            var grid = sender as DataGridView;
            if (grid == null || e.RowIndex < 0 || e.ColumnIndex < 0) return;

            var row = grid.Rows[e.RowIndex];
            var col = grid.Columns[e.ColumnIndex];
            var name = col?.Name ?? string.Empty;

            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ATM_mid", "ATM_adj", "ATM_spread", "RR25_mid", "RR10_mid", "BF25_mid", "BF10_mid"
    };
            if (!targets.Contains(name)) return;

            var cell = row.Cells[e.ColumnIndex];
            var original = cell.Value?.ToString() ?? string.Empty;

            var txt = (e.FormattedValue ?? string.Empty).ToString().Trim();
            if (string.IsNullOrEmpty(txt))
            {
                cell.Tag = new Tuple<string, bool>(original, false);
                row.ErrorText = null;
                return;
            }

            if (!TryParseFlexibleDecimal(txt, out var parsed))
            {
                cell.Tag = new Tuple<string, bool>(original, true);
                return;
            }

            var pair = GetActivePairFor(grid);
            var anchored = _presenter != null && _presenter.IsAnchoredPair(pair);

            if (string.Equals(name, "ATM_adj", StringComparison.OrdinalIgnoreCase))
            {
                // ATM Offset – endast relevant vid anchored; men vi validerar alltid enligt offset-regel
                if (Math.Abs(parsed) > 10.000m)
                {
                    cell.Tag = new Tuple<string, bool>(original, true);
                    row.ErrorText = "ATM Offset orimlig (|offset| > 10.000).";
                    return;
                }
            }
            else if (string.Equals(name, "ATM_spread", StringComparison.OrdinalIgnoreCase))
            {
                // ATM Spread
                if (parsed < 0m)
                {
                    cell.Tag = new Tuple<string, bool>(original, true);
                    row.ErrorText = "ATM Spread kan inte vara negativ.";
                    return;
                }
                var mid = TryGetCellDecimal(row, "ATM_mid");
                if (mid.HasValue && parsed > 2.0m * mid.Value)
                {
                    cell.Tag = new Tuple<string, bool>(original, true);
                    row.ErrorText = "ATM Spread för stor i förhållande till Mid (> 2×Mid).";
                    return;
                }
            }
            else if (string.Equals(name, "BF25_mid", StringComparison.OrdinalIgnoreCase))
            {
                if (parsed < 0m)
                {
                    cell.Tag = new Tuple<string, bool>(original, true);
                    row.ErrorText = "BF25 kan inte vara negativ.";
                    return;
                }
            }

            cell.Tag = new Tuple<string, bool>(original, false);
            row.ErrorText = null;
        }


        /// <summary>
        /// Tillåter både punkt och komma som decimaltecken. Invariant parse.
        /// </summary>
        private static bool TryParseFlexibleDecimal(string s, out decimal value)
        {
            value = 0m;
            if (string.IsNullOrWhiteSpace(s)) return false;
            // ersätt komma med punkt för robusthet
            var t = s.Replace(',', '.');
            return decimal.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
        }

        // Håller senaste soft-targets för tooltips per Evaluate-körning
        private static readonly string[] _rrTargets = new[] { "RR25_mid", "RR10_mid" };
        private static readonly string[] _bfTargets = new[] { "BF25_mid", "BF10_mid" };
        private List<string> _lastSoftTargets = new List<string>();

        /// <summary>
        /// Utvärderar per-rad (intra-tenor) regler och fyller hårda/mjuka fel.
        /// - RR: |RR10| ≥ |RR25| (hård) + samma tecken (hård). Vid tecken-mismatch sätts även tooltip på RR10/RR25.
        /// - BF: BF10 ≥ BF25 ≥ 0 (hård).
        /// - ATM: negativ spread eller negativ mid → hårt fel.
        /// - Soft: RR-ratio |RR10|/|RR25| ∈ [1.0,2.5], BF-ratio BF10/BF25 ∈ [1.0,3.0] (tooltips).
        /// Sätter row.ErrorText för hårda fel och ToolTipText på relevanta celler för mjuka (och tecken-mismatch).
        /// </summary>
        private void EvaluateRowRules_Scoped(DataGridViewRow row, bool anchored, List<string> hard, List<string> soft)
        {
            if (row == null) return;
            hard?.Clear();
            soft?.Clear();

            var rr25 = TryGetCellDecimal(row, "RR25_mid");
            var rr10 = TryGetCellDecimal(row, "RR10_mid");
            var bf25 = TryGetCellDecimal(row, "BF25_mid");
            var bf10 = TryGetCellDecimal(row, "BF10_mid");
            var spr = TryGetCellDecimal(row, "ATM_spread");
            var mid = TryGetCellDecimal(row, "ATM_mid");

            // Nollställ mjuka tooltips på målkolumner (lämna ATM-preview orörd – den sitter på ATM-kolumnerna)
            foreach (var name in new[] { "RR25_mid", "RR10_mid", "BF25_mid", "BF10_mid" })
                if (row.DataGridView != null && row.DataGridView.Columns.Contains(name))
                    row.Cells[name].ToolTipText = null;

            const decimal eps = 1e-8m;

            // ----- Hårda regler -----

            // RR: samma tecken (båda ≠ 0)
            if (rr25.HasValue && rr10.HasValue && rr25.Value != 0m && rr10.Value != 0m)
            {
                var sign25 = Math.Sign(rr25.Value);
                var sign10 = Math.Sign(rr10.Value);
                if (sign25 != sign10)
                {
                    var msg = "RR: 10D och 25D måste ha samma tecken.";
                    hard?.Add(msg);

                    // Lägg också tooltip på båda RR-cellerna så det syns direkt i griden
                    if (row.DataGridView != null && row.DataGridView.Columns.Contains("RR25_mid"))
                        row.Cells["RR25_mid"].ToolTipText = msg;
                    if (row.DataGridView != null && row.DataGridView.Columns.Contains("RR10_mid"))
                        row.Cells["RR10_mid"].ToolTipText = msg;
                }
            }

            // RR: |RR10| ≥ |RR25|
            if (rr25.HasValue && rr10.HasValue)
            {
                if (Math.Abs(rr10.Value) + eps < Math.Abs(rr25.Value))
                    hard?.Add($"RR: |RR10| ≥ |RR25| bryts (|{rr10:0.####}| < |{rr25:0.####}|).");
            }

            // BF: BF10 ≥ BF25 ≥ 0
            if (bf25.HasValue && bf25.Value < 0m - eps)
                hard?.Add($"BF: BF25 < 0 ({bf25:0.####}).");

            if (bf10.HasValue && bf25.HasValue && bf10.Value + eps < bf25.Value)
                hard?.Add($"BF: BF10 < BF25 ({bf10:0.####} < {bf25:0.####}).");

            // ATM: spread < 0 eller mid < 0
            if (spr.HasValue && spr.Value < 0m - eps) hard?.Add($"ATM: Spread negativ ({spr:0.####}).");
            if (mid.HasValue && mid.Value < 0m - eps) hard?.Add($"ATM: Mid negativ ({mid:0.####}).");

            // ----- Mjuka regler (tooltips) -----

            // RR-ratio
            if (rr25.HasValue && Math.Abs(rr25.Value) > eps && rr10.HasValue)
            {
                var ratio = Math.Abs(rr10.Value) / Math.Abs(rr25.Value);
                if (ratio < 1.0m - 1e-6m || ratio > 2.5m + 1e-6m)
                {
                    var tip = $"RR-ratio hint: |RR10|/|RR25| = {ratio:0.###} (mål 1.0–2.5)";
                    soft?.Add($"RR-ratio: |RR10|/|RR25| = {ratio:0.###} utanför [1.0, 2.5].");
                    if (row.DataGridView != null && row.DataGridView.Columns.Contains("RR10_mid"))
                        row.Cells["RR10_mid"].ToolTipText = tip;
                    if (row.DataGridView != null && row.DataGridView.Columns.Contains("RR25_mid"))
                        row.Cells["RR25_mid"].ToolTipText = tip;
                }
            }

            // BF-ratio
            if (bf25.HasValue && bf25.Value > eps && bf10.HasValue && bf10.Value > eps)
            {
                var ratio = bf10.Value / bf25.Value;
                if (ratio < 1.0m - 1e-6m || ratio > 3.0m + 1e-6m)
                {
                    var tip = $"BF-ratio hint: BF10/BF25 = {ratio:0.###} (mål 1.0–3.0)";
                    soft?.Add($"BF-ratio: BF10/BF25 = {ratio:0.###} utanför [1.0, 3.0].");
                    if (row.DataGridView != null && row.DataGridView.Columns.Contains("BF10_mid"))
                        row.Cells["BF10_mid"].ToolTipText = tip;
                    if (row.DataGridView != null && row.DataGridView.Columns.Contains("BF25_mid"))
                        row.Cells["BF25_mid"].ToolTipText = tip;
                }
            }

            row.ErrorText = (hard != null && hard.Count > 0) ? string.Join("  ", hard) : null;
        }



        /// <summary>
        /// Hårda och mjuka regler på radnivå.
        /// Hårda:
        ///   • |RR10| ≥ |RR25| och samma tecken (om båda finns)
        ///   • BF10 ≥ BF25 ≥ 0 (om båda finns)
        ///   • ATM Spread ≥ 0 (icke-ankrat), |Offset| ≤ 5 (ankrat)
        /// Mjuka (varningar):
        ///   • |RR10| ≈ k×|RR25|, k i [1.0, 2.5]
        ///   • BF10 ≈ m×BF25,      m i [1.0, 3.0]
        /// </summary>
        private void EvaluateRowRules(DataGridViewRow row, bool anchored, List<string> hard, List<string> soft) 
        {
            decimal? rr25 = TryGetCellDecimal(row, "RR25_mid");
            decimal? rr10 = TryGetCellDecimal(row, "RR10_mid");
            decimal? bf25 = TryGetCellDecimal(row, "BF25_mid");
            decimal? bf10 = TryGetCellDecimal(row, "BF10_mid");
            decimal? adj = TryGetCellDecimal(row, "ATM_adj");

            // ATM justering (kompletterande hård kontroll – CellValidating tar det mesta)
            if (adj.HasValue)
            {
                if (!anchored && adj.Value < 0m)
                    hard.Add("ATM Spread kan inte vara negativ.");
                if (anchored && Math.Abs(adj.Value) > 5.000m)
                    hard.Add("ATM Offset orimlig (|offset| > 5.000).");
            }

            // RR: |RR10| >= |RR25| och samma tecken
            if (rr25.HasValue && rr10.HasValue)
            {
                if (Math.Sign(rr25.Value) != Math.Sign(rr10.Value))
                    hard.Add("RR10 och RR25 har olika tecken.");
                if (Math.Abs(rr10.Value) + 1e-12m < Math.Abs(rr25.Value))
                    hard.Add("|RR10| måste vara ≥ |RR25|.");

                // Mjuk: förhållande
                var k = (Math.Abs(rr25.Value) > 1e-12m) ? Math.Abs(rr10.Value) / Math.Abs(rr25.Value) : (decimal?)null;
                if (k.HasValue && (k.Value < 1.0m || k.Value > 2.5m))
                    soft.Add($"RR-ratio (|RR10|/|RR25| = {k.Value:0.00}) utanför [1.0, 2.5].");
            }

            // BF: BF10 ≥ BF25 ≥ 0
            if (bf25.HasValue)
            {
                if (bf25.Value < 0m) hard.Add("BF25 kan inte vara negativ.");
            }
            if (bf10.HasValue && bf25.HasValue)
            {
                if (bf10.Value + 1e-12m < bf25.Value)
                    hard.Add("BF10 måste vara ≥ BF25.");

                // Mjuk: förhållande
                if (bf25.Value > 1e-12m)
                {
                    var m = bf10.Value / bf25.Value;
                    if (m < 1.0m || m > 3.0m)
                        soft.Add($"BF-ratio (BF10/BF25 = {m:0.00}) utanför [1.0, 3.0].");
                }
            }
        }

        /// <summary>
        /// Försöker läsa ett decimalvärde från en rad för en given kolumnidentifierare.
        /// Robust mot att kolumnnamnet saknas: matchar först på Column.Name, därefter på HeaderText (case-insensitivt).
        /// Returnerar null om kolumn eller värde inte finns, istället för att kasta undantag.
        /// Tillåter både punkt och komma i strängvärden.
        /// </summary>
        private decimal? TryGetCellDecimal(DataGridViewRow row, string name)
        {
            if (row == null || string.IsNullOrWhiteSpace(name)) return null;
            var grid = row.DataGridView;
            if (grid == null) return null;

            // 1) Hitta kolumn via Name eller HeaderText (case-insensitivt)
            DataGridViewColumn col = null;
            foreach (DataGridViewColumn c in grid.Columns)
            {
                if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    col = c; break;
                }
            }
            if (col == null)
            {
                foreach (DataGridViewColumn c in grid.Columns)
                {
                    if (string.Equals(c.HeaderText, name, StringComparison.OrdinalIgnoreCase))
                    {
                        col = c; break;
                    }
                }
            }
            if (col == null) return null; // kolumnen finns inte → inget värde

            var cell = row.Cells[col.Index];
            var val = cell?.Value;
            if (val == null) return null;

            // 2) Tolka värdet
            if (val is decimal d) return d;
            if (val is double dd) return (decimal)dd;

            var s = val.ToString();
            if (string.IsNullOrWhiteSpace(s)) return null;

            // Tillåt både , och .
            if (decimal.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.CurrentCulture, out var p1))
                return p1;

            s = s.Replace(',', '.');
            if (decimal.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var p2))
                return p2;

            return null;
        }


        /// <summary>
        /// Hämtar aktivt parnamn för ett givet grid (läser TabPage.Text).
        /// </summary>
        private static string GetActivePairFor(DataGridView grid) // NY
        {
            var page = grid.FindForm() == null ? null : grid.Parent;
            while (page != null && !(page is TabPage)) page = page.Parent;
            var tp = page as TabPage;
            return tp != null ? tp.Text : string.Empty;
        }

        /// <summary>
        /// Tysta standard-DataError (vi visar egna cellfel).
        /// </summary>
        private void PairGrid_DataError(object sender, DataGridViewDataErrorEventArgs e)
        {
            e.Cancel = true;
        }

        #endregion

        #region Pair Tabs – vänsterpanel (Pin, Pinned, Recent)

        /// <summary>
        /// Klick på "Unpin" => ta bort från listan + stäng underflik + spara state.
        /// </summary>
        private void OnClickUnpin(object sender, EventArgs e)
        {
            var lb = FindChild<ListBox>(this, "LstPinned");
            var sel = lb?.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(sel)) return;

            RemovePinnedAndCloseTab(sel);
            SaveUiStateFromControls();
        }

        /// <summary>
        /// Växlar vyn till flikläge ("Pair Tabs") och sätter upp vänsterpanelen
        /// med Pin, Pinned, Recent, Refresh, vy-toggle (Tabs/Tiles) och kolumn-toggle för Tiles.
        /// Initierar även Tiles-containern (dold) så vi kan växla vy utan att förstöra layouten.
        ///
        /// Viktigt: Denna metod applicerar INTE längre något presenter-lagrat UI-state automatiskt.
        /// Workspace (VolWorkspaceControl) äger per-session-state och anropar ApplyUiState(...) själv
        /// vid uppstart/öppning. Nya sessioner startar därför tomma tills workspace sätter state.
        /// </summary>
        public void InitializeTabbedLayout()
        {
            // --- Vänster: panel med Pin, Pinned, Recent, Actions, View, Tile Columns ---
            var pnlLeft = new Panel
            {
                Name = "LeftPanel",
                Dock = DockStyle.Left,
                Width = 180,
                BackColor = SystemColors.Control
            };

            // Pin-sektion
            var lblPair = new Label { Text = "Pair", Dock = DockStyle.Top, Height = 18 };
            var txtPair = new TextBox { Name = "TxtPair", Dock = DockStyle.Top, Text = "EUR/USD" };
            var btnPin = new Button { Name = "BtnPin", Dock = DockStyle.Top, Text = "Pin" };
            btnPin.Click += (s, e) =>
            {
                var p = (txtPair.Text ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(p)) return;
                PinPair(p);
                AddToPinned(p);
                AddToRecent(p, 10);
                if (_tilesPanel != null && _tilesPanel.Visible) RebuildTilesFromPinned(force: false);
                SaveUiStateFromControls(); // persist (till presenter om man vill behålla global fallback)
            };
            txtPair.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    btnPin.PerformClick();
                    e.Handled = true;
                }
            };

            // Pinned
            var lblPinned = new Label { Text = "— Pinned —", Dock = DockStyle.Top, Height = 16 };
            var lstPinned = new ListBox { Name = "LstPinned", Dock = DockStyle.Top, Height = 140, IntegralHeight = false };
            lstPinned.DoubleClick += (s, e) =>
            {
                OnPinnedDoubleClick(s, e);
                SaveUiStateFromControls();
            };
            // NY: Delete = Unpin & stäng tab om öppen
            lstPinned.KeyDown += OnPinnedKeyDown;

            // Unpin-knapp under Pinned
            var btnUnpin = new Button { Name = "BtnUnpin", Dock = DockStyle.Top, Text = "Unpin" };
            btnUnpin.Click += OnClickUnpin;
            pnlLeft.Controls.Add(btnUnpin);



            // Recent
            var lblRecent = new Label { Text = "— Recent —", Dock = DockStyle.Top, Height = 16 };
            var lstRecent = new ListBox { Name = "LstRecent", Dock = DockStyle.Top, Height = 140, IntegralHeight = false };
            lstRecent.DoubleClick += (s, e) =>
            {
                OnRecentDoubleClick(s, e);
                SaveUiStateFromControls();
            };

            // Actions
            var lblActions = new Label { Text = "— Actions —", Dock = DockStyle.Top, Height = 16 };
            var btnRefresh = new Button { Name = "BtnRefresh", Dock = DockStyle.Top, Text = "Refresh" };
            btnRefresh.Click += OnClickRefresh;

            // View (Tabs / Tiles)
            var lblView = new Label { Text = "— View —", Dock = DockStyle.Top, Height = 16 };
            var btnTabs = new Button { Name = "BtnTabsView", Dock = DockStyle.Top, Text = "Tabs View" };
            btnTabs.Click += (s, e) =>
            {
                ShowTabsView();
                SaveUiStateFromControls();
            };
            var btnTiles = new Button { Name = "BtnTilesView", Dock = DockStyle.Top, Text = "Tiles View" };
            btnTiles.Click += (s, e) =>
            {
                ShowTilesView(forceInitialLoad: false);
                SaveUiStateFromControls();
            };

            // Tile Columns (ATM-only / Compact)
            var lblTileCols = new Label { Text = "— Tile Columns —", Dock = DockStyle.Top, Height = 16 };
            var rbAtmOnly = new RadioButton
            {
                Name = "RbTileAtmOnly",
                Dock = DockStyle.Top,
                Text = "ATM-only",
                AutoSize = true,
                Checked = (_tileColumnsMode == TileColumnsMode.AtmOnly)
            };
            rbAtmOnly.CheckedChanged += (s, e) =>
            {
                if (!rbAtmOnly.Checked) return;
                _tileColumnsMode = TileColumnsMode.AtmOnly;
                if (_tilesPanel != null && _tilesPanel.Visible) RebuildTilesFromPinned(force: false);
                SaveUiStateFromControls();
            };

            var rbCompact = new RadioButton
            {
                Name = "RbTileCompact",
                Dock = DockStyle.Top,
                Text = "Compact",
                AutoSize = true,
                Checked = (_tileColumnsMode == TileColumnsMode.Compact)
            };
            rbCompact.CheckedChanged += (s, e) =>
            {
                if (!rbCompact.Checked) return;
                _tileColumnsMode = TileColumnsMode.Compact;
                if (_tilesPanel != null && _tilesPanel.Visible) RebuildTilesFromPinned(force: false);
                SaveUiStateFromControls();
            };

            // Lägg upp i omvänd ordning (Dock=Top staplar uppåt)
            pnlLeft.Controls.Add(rbCompact);
            pnlLeft.Controls.Add(rbAtmOnly);
            pnlLeft.Controls.Add(lblTileCols);
            pnlLeft.Controls.Add(btnTiles);
            pnlLeft.Controls.Add(btnTabs);
            pnlLeft.Controls.Add(lblView);
            pnlLeft.Controls.Add(btnRefresh);
            pnlLeft.Controls.Add(lblActions);
            pnlLeft.Controls.Add(lstRecent);
            pnlLeft.Controls.Add(lblRecent);
            pnlLeft.Controls.Add(lstPinned);
            pnlLeft.Controls.Add(lblPinned);
            pnlLeft.Controls.Add(btnPin);
            pnlLeft.Controls.Add(txtPair);
            pnlLeft.Controls.Add(lblPair);

            // --- Tabs ---
            if (_pairTabs == null)
            {
                _pairTabs = new TabControl
                {
                    Dock = DockStyle.Fill,
                    Alignment = TabAlignment.Top,
                    SizeMode = TabSizeMode.Fixed,
                    ItemSize = new Size(100, 24),
                    Padding = new Point(12, 4),
                    Visible = true
                };
                // KOPPLING: låt presentern sköta laddning/overlay/debounce
                _pairTabs.SelectedIndexChanged += OnPairTabsSelectedIndexChanged;
            }

            // --- Tiles (dold initialt) ---
            if (_tilesPanel == null)
            {
                _tilesPanel = new FlowLayoutPanel
                {
                    Name = "TilesPanel",
                    Dock = DockStyle.Fill,
                    AutoScroll = true,
                    WrapContents = true,
                    FlowDirection = FlowDirection.LeftToRight,
                    Visible = false,
                    AllowDrop = true
                };
                _tilesPanel.DragOver += OnTilesPanelDragOver;
                _tilesPanel.DragDrop += OnTilesPanelDragDrop;
                _tilesPanel.DragEnter += OnTilesPanelDragEnter;
            }

            Controls.Clear();
            Controls.Add(_tilesPanel);
            Controls.Add(_pairTabs);
            Controls.Add(pnlLeft);

        }

        /// <summary>
        /// Delete på Pinned => Unpin + stäng underflik (om finns) + spara state.
        /// </summary>
        private void OnPinnedKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Delete) return;

            var lb = sender as ListBox;
            var sel = lb?.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(sel)) return;

            RemovePinnedAndCloseTab(sel);
            SaveUiStateFromControls();
            e.Handled = true;
        }

        /// <summary>
        /// Tar bort ett par från Pinned och stänger motsvarande underflik (om existerar).
        /// </summary>
        private void RemovePinnedAndCloseTab(string pair)
        {
            var target = (pair ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(target)) return;

            // 1) Ta bort från Pinned
            var lb = FindChild<ListBox>(this, "LstPinned");
            if (lb != null)
            {
                int idx = -1;
                for (int i = 0; i < lb.Items.Count; i++)
                {
                    var s = lb.Items[i] as string;
                    if (string.Equals(s, target, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
                }
                if (idx >= 0) lb.Items.RemoveAt(idx);
            }

            // 2) Stäng flik om den finns
            if (_pairTabs != null)
            {
                TabPage toRemove = null;
                foreach (TabPage p in _pairTabs.TabPages)
                {
                    var ui = p.Tag as PairTabUi;
                    var existing = ui?.PairSymbol ?? p.Text;
                    if (string.Equals(existing, target, StringComparison.OrdinalIgnoreCase))
                    {
                        toRemove = p; break;
                    }
                }
                if (toRemove != null)
                    _pairTabs.TabPages.Remove(toRemove);
            }

            // 3) Om Tiles-vy är aktiv: bygg om tiles (så rutan försvinner)
            if (_tilesPanel != null && _tilesPanel.Visible)
                RebuildTilesFromPinned(force: false);
        }

        /// <summary>
        /// Klick på "Refresh" – i Tabs-läge uppdateras aktiv flik.
        /// I Tiles-läge uppdateras alla tiles.
        /// Håll nere Ctrl för bypass av cache.
        /// </summary>
        private void OnClickRefresh(object sender, EventArgs e)
        {
            var force = (ModifierKeys & Keys.Control) == Keys.Control;

            if (_tilesPanel != null && _tilesPanel.Visible)
            {
                RefreshAllTiles(force);
            }
            else
            {
                RefreshActivePair(force);
            }
        }

        /// <summary>
        /// Dubbelklick på en Pinned-rad: aktivera/flika upp paret och bumpa det i Recent.
        /// </summary>
        private void OnPinnedDoubleClick(object sender, EventArgs e)
        {
            var lb = sender as ListBox;
            var sel = lb?.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(sel)) return;

            PinPair(sel);
            AddToRecent(sel, 10);
        }

        /// <summary>
        /// Dubbelklick på Recent: lägg till i Pinned (om saknas), öppna fliken och ladda via presentern.
        /// </summary>
        private void OnRecentDoubleClick(object sender, EventArgs e)
        {
            var lb = sender as ListBox;
            var sel = lb?.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(sel)) return;

            AddToPinned(sel);
            PinPair(sel); // aktiverar fliken

            if (_presenter != null)
                _presenter.RefreshPairAndBindAsync(sel.Trim(), force: false).ConfigureAwait(false);

            AddToRecent(sel, 10);
            SaveUiStateFromControls();
        }

        /// <summary>
        /// Lägger till ett par i Pinned-listan om det saknas (case-insensitivt).
        /// </summary>
        private void AddToPinned(string pair)
        {
            var lb = FindChild<ListBox>(this, "LstPinned");
            if (lb == null) return;

            // Finns redan?
            for (int i = 0; i < lb.Items.Count; i++)
            {
                var s = lb.Items[i] as string;
                if (string.Equals(s, pair, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            lb.Items.Add(pair.ToUpperInvariant());
        }

        /// <summary>
        /// Lägger överst i Recent (flyttar upp om det redan finns). Cap: maxCount.
        /// </summary>
        private void AddToRecent(string pair, int maxCount)
        {
            var lb = FindChild<ListBox>(this, "LstRecent");
            if (lb == null) return;

            // Ta bort om redan finns (case-insensitivt)
            int existing = -1;
            for (int i = 0; i < lb.Items.Count; i++)
            {
                var s = lb.Items[i] as string;
                if (string.Equals(s, pair, StringComparison.OrdinalIgnoreCase))
                {
                    existing = i; break;
                }
            }
            if (existing >= 0) lb.Items.RemoveAt(existing);

            // Lägg överst
            lb.Items.Insert(0, pair.ToUpperInvariant());

            // Cap
            while (lb.Items.Count > maxCount)
                lb.Items.RemoveAt(lb.Items.Count - 1);
        }

        /// <summary>
        /// Hittar första barnkontroll (rekursivt) med angivet Name och typ.
        /// </summary>
        private T FindChild<T>(Control root, string name) where T : Control
        {
            if (root == null) return null;
            var arr = root.Controls.Find(name, true);
            if (arr != null && arr.Length > 0) return arr[0] as T;
            return null;
        }

        /// <summary>
        /// Returnerar första barnkontrollen av typen <typeparamref name="T"/> under angiven <paramref name="parent"/>.
        /// Använd när du inte har (eller bryr dig om) kontrollens Name.
        /// </summary>
        private T FindChild<T>(Control parent) where T : Control
        {
            if (parent == null) return null;
            // Sök direkt bland barn
            var direct = parent.Controls.OfType<T>().FirstOrDefault();
            if (direct != null) return direct;

            // Rekursiv sökning
            foreach (Control c in parent.Controls)
            {
                var hit = FindChild<T>(c);
                if (hit != null) return hit;
            }
            return null;
        }

        #endregion

        #region Pair Tabs – flikar & laddning

        /// <summary>
        /// Händelse: användaren byter aktiv valutapar-flik.
        /// Delegar till presentern som hanterar all laddning/bindning (debounce, busy, overlays).
        /// </summary>
        private void OnPairTabsSelectedIndexChanged(object sender, EventArgs e)
        {
            if (_presenter == null || _pairTabs == null || _pairTabs.SelectedTab == null) return;

            var pair = _pairTabs.SelectedTab.Text?.Trim();
            if (string.IsNullOrWhiteSpace(pair)) return;

            try
            {
                // Presenter = enda vägen in (ingen direkt databindning i vyn).
                _presenter.RefreshPairAndBindAsync(pair, force: false).ConfigureAwait(false);
            }
            catch
            {
                // best effort – presenter visar ev. fel via ShowPairError()
            }
        }


        /// <summary>
        /// Visar Tabs-vyn (döljer Tiles-panelen) utan att riva kontrollerna.
        /// </summary>
        private void ShowTabsView()
        {
            if (_pairTabs != null) _pairTabs.Visible = true;
            if (_tilesPanel != null) _tilesPanel.Visible = false;
        }

        /// <summary>
        /// Visar Tiles-vyn (döljer Tabs). Bygger tiles från Pinned-listan.
        /// </summary>
        /// <param name="forceInitialLoad">True = hämta alla tiles med bypass av cache vid första visningen.</param>
        private void ShowTilesView(bool forceInitialLoad)
        {
            if (_tilesPanel == null) return;

            // Bygg tiles från Pinned innan vi visar
            RebuildTilesFromPinned(force: forceInitialLoad);

            _tilesPanel.Visible = true;
            if (_pairTabs != null) _pairTabs.Visible = false;
        }

        /// <summary>
        /// Synkar tiles-panelen mot Pinned-listan: skapar tiles för saknade par,
        /// tar bort tiles som inte längre är pinnade, uppdaterar kolumnläge och laddar data.
        /// </summary>
        /// <param name="force">True = bypass presenter-cache vid laddning.</param>
        private void RebuildTilesFromPinned(bool force)
        {
            if (_tilesPanel == null) return;

            var pinned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lstPinned = FindChild<ListBox>(this, "LstPinned");
            if (lstPinned != null)
            {
                foreach (var it in lstPinned.Items)
                {
                    var s = it as string;
                    if (!string.IsNullOrWhiteSpace(s)) pinned.Add(s.ToUpperInvariant());
                }
            }

            // 1) Ta bort tiles som inte längre är pinnade
            var toRemove = new List<Control>();
            foreach (Control c in _tilesPanel.Controls)
            {
                var ui = c.Tag as TileUi;
                if (ui == null || string.IsNullOrWhiteSpace(ui.PairSymbol) || !pinned.Contains(ui.PairSymbol))
                    toRemove.Add(c);
            }
            foreach (var c in toRemove) _tilesPanel.Controls.Remove(c);

            // 2) Skapa tiles för nya par
            foreach (var p in pinned)
            {
                var exists = _tilesPanel.Controls
                    .OfType<Panel>()
                    .Select(x => x.Tag as TileUi)
                    .Any(ui => ui != null && string.Equals(ui.PairSymbol, p, StringComparison.OrdinalIgnoreCase));

                if (!exists)
                {
                    var tile = CreateTileForPair(p);
                    _tilesPanel.Controls.Add(tile);
                }
            }

            // 3) Uppdatera kolumnläge för alla tiles (viktigt vid ATM↔Compact-växling)
            foreach (Control c in _tilesPanel.Controls)
                ApplyTileColumnsMode(c as Panel);

            // 4) Ladda/uppdatera alla tiles
            foreach (Control c in _tilesPanel.Controls)
            {
                var ui = c.Tag as TileUi;
                if (ui == null || string.IsNullOrWhiteSpace(ui.PairSymbol)) continue;
                LoadLatestForTile((Panel)c, ui.PairSymbol, force);
            }
        }


        /// <summary>
        /// Skapar ett tile-panel (ruta) för ett valutapar: header + grid.
        /// Gridets kolumner följer aktivt TileColumnsMode (ATM-only/Compact).
        /// </summary>
        /// <param name="pair">Valutapar i "AAA/BBB".</param>
        private Panel CreateTileForPair(string pair)
        {
            var root = new Panel
            {
                Width = 420,
                Height = 260,
                Margin = new Padding(8),
                BorderStyle = BorderStyle.FixedSingle
            };

            var header = new Panel { Dock = DockStyle.Top, Height = 26 };
            var lblTitle = new Label { AutoSize = true, Left = 6, Top = 5, Text = pair.ToUpperInvariant(), Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold) };
            var lblSnap = new Label { AutoSize = true, Left = 140, Top = 5, Text = "Snap: –" };
            var lblTs = new Label { AutoSize = true, Left = 240, Top = 5, Text = "TS: –" };
            var lblStat = new Label { AutoSize = true, Left = 340, Top = 5, Text = "…" };
            header.Controls.Add(lblTitle);
            header.Controls.Add(lblSnap);
            header.Controls.Add(lblTs);
            header.Controls.Add(lblStat);

            var grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AutoGenerateColumns = false,
                RowHeadersVisible = false,
                BackgroundColor = SystemColors.Window,
                BorderStyle = BorderStyle.None
            };
            var bs = new BindingSource();
            grid.DataSource = bs;

            EnsureTileColumnsByMode(grid, _tileColumnsMode);

            root.Controls.Add(grid);
            root.Controls.Add(header);

            root.Tag = new TileUi
            {
                PairSymbol = pair.ToUpperInvariant(),
                Root = root,
                Binding = bs,
                Grid = grid,                 // NY: spara griden
                LabelTitle = lblTitle,
                LabelSnapshot = lblSnap,
                LabelTs = lblTs,
                LabelStatus = lblStat
            };

            root.MouseDown += OnTileMouseDown;   // gör panelen draggbar

            // MouseDown på header + rubrik + grid startar också drag (events bubblar inte i WinForms)
            header.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) root.DoDragDrop(root, DragDropEffects.Move); };
            lblTitle.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) root.DoDragDrop(root, DragDropEffects.Move); };
            grid.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) root.DoDragDrop(root, DragDropEffects.Move); };


            return root;
        }


        /// <summary>
        /// Applicerar nuvarande _tileColumnsMode på angiven tile utan att tappa data.
        /// </summary>
        /// <param name="tile">Tile-panel vars Tag ska vara TileUi.</param>
        private void ApplyTileColumnsMode(Panel tile)
        {
            if (tile == null) return;
            var ui = tile.Tag as TileUi;
            if (ui == null) return;

            var grid = ui.Grid ?? tile.Controls.OfType<DataGridView>().FirstOrDefault();
            if (grid == null) return;

            var bs = ui.Binding ?? grid.DataSource as BindingSource;
            var data = bs?.DataSource;  // spara referensen till nuvarande lista

            EnsureTileColumnsByMode(grid, _tileColumnsMode);

            if (bs != null)
                bs.DataSource = data ?? new List<VolSurfaceRow>(); // rebind → kolumnerna ritas om
        }


        /// <summary>
        /// Triggar laddning för en tile via samma presenter-väg som Tabs.
        /// Tile-bindning sker i UpdateTile() som kallas av presentern.
        /// </summary>
        private void LoadLatestForTile(Panel tile, string pairSymbol, bool force)
        {
            if (_presenter == null) return;
            if (string.IsNullOrWhiteSpace(pairSymbol)) return;

            try
            {
                _presenter.RefreshPairAndBindAsync(pairSymbol.Trim(), force).ConfigureAwait(false);
            }
            catch
            {
                // best effort – presenter visar fel via ShowPairError()
            }
        }




        /// <summary>
        /// Lägger till (eller aktiverar) en pair-flik och triggar laddning via presentern.
        /// </summary>
        public void PinPair(string pair)
        {
            if (string.IsNullOrWhiteSpace(pair)) return;
            if (_pairTabs == null) InitializeTabbedLayout();

            var key = pair.ToUpperInvariant();

            // Finns fliken?
            TabPage page = null;
            foreach (TabPage p in _pairTabs.TabPages)
            {
                if (string.Equals(p.Text, key, StringComparison.OrdinalIgnoreCase))
                { page = p; break; }
            }

            // Skapa enkel tom tab om den saknas – själva gridet/huvudet skapas när vi binder
            if (page == null)
            {
                page = new TabPage { Text = key, UseVisualStyleBackColor = true, Padding = new Padding(0) };
                var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
                page.Controls.Add(host);
                _pairTabs.TabPages.Add(page);
            }

            _pairTabs.SelectedTab = page;

            // All data/overlay via presentern
            if (_presenter != null)
                _presenter.RefreshPairAndBindAsync(key, force: false).ConfigureAwait(false);
        }

        #endregion

        #region Privata hjälpare – Pair Tabs

        /// <summary>
        /// Hämtar aktivt valutapar från vald pair-flik.
        /// Ordning: SelectedTab.Tag (string) → SelectedTab.Text → headerlabel "PairHeaderLabel".
        /// Returnerar t.ex. "EUR/USD", annars null om inget kan hittas.
        /// </summary>
        private string GetActivePairSymbol()
        {
            var tab = _pairTabs?.SelectedTab;
            if (tab == null) return null;

            // 1) Rekommenderad: Tag sätts när pair-fliken skapas (t.ex. tab.Tag = pairSymbol).
            if (tab.Tag is string s && !string.IsNullOrWhiteSpace(s))
                return s.Trim().ToUpperInvariant();

            // 2) Fallback: fliktexten
            if (!string.IsNullOrWhiteSpace(tab.Text))
                return tab.Text.Trim().ToUpperInvariant();

            // 3) Fallback: headeretiketten "PairHeaderLabel" (ta allt före '|')
            Control host = tab;
            if (tab.Controls.Count > 0) host = tab.Controls[0];

            var lbl = FindChild<Label>(host, "PairHeaderLabel");
            if (lbl != null && !string.IsNullOrWhiteSpace(lbl.Text))
            {
                var t = lbl.Text;
                var pipe = t.IndexOf('|');
                var left = pipe >= 0 ? t.Substring(0, pipe) : t;
                return left.Trim().ToUpperInvariant();
            }

            return null;
        }

        /// <summary>
        /// Returnerar DataGridView för aktiv pair-tab.
        /// Försöker först hitta kontrollen med namn "PairGrid" via FindChild, 
        /// annars första förekomsten av DataGridView i tabben.
        /// </summary>
        private DataGridView GetActivePairGrid()
        {
            var tab = _pairTabs?.SelectedTab;
            if (tab == null) return null;

            Control host = tab;
            if (tab.Controls.Count > 0)
                host = tab.Controls[0];

            // Om du namnger gridden "PairGrid" i EnsurePairTabGrid hittar vi den deterministiskt.
            var grid = FindChild<DataGridView>(host, "PairGrid");
            if (grid != null) return grid;

            // Fallback: första DataGridView i hierarkin
            DataGridView first = null;
            void Walk(Control c)
            {
                if (first != null) return;
                foreach (Control child in c.Controls)
                {
                    if (child is DataGridView dgv)
                    {
                        first = dgv;
                        return;
                    }
                    if (child.HasChildren) Walk(child);
                    if (first != null) return;
                }
            }
            Walk(host);
            return first;
        }

        /// <summary>
        /// Lägger ut högersidan i headern: LblEdits, BtnDiscardDraft och BtnReviewDraft
        /// med jämn horisontell spacing och gemensam vertikal centrering.
        /// </summary>
        private void LayoutHeaderRightControls(Panel header)
        {
            if (header == null) return;

            var lblEdits = header.Controls.OfType<Label>().FirstOrDefault(x => x.Name == "LblEdits");
            var btnDiscard = header.Controls.OfType<Button>().FirstOrDefault(x => x.Name == "BtnDiscardDraft");
            var btnReview = header.Controls.OfType<Button>().FirstOrDefault(x => x.Name == "BtnReviewDraft");

            if (lblEdits == null || btnDiscard == null || btnReview == null)
                return;

            const int padRight = 10;
            const int gap = 8;

            // Förutsägbar storlek på knappar
            if (btnDiscard.Width == 0) btnDiscard.Size = new Size(78, 23);
            if (btnReview.Width == 0) btnReview.Size = new Size(78, 23);

            // Höger → vänster: Review, Discard, Edits
            btnReview.Left = header.Width - padRight - btnReview.Width;
            btnDiscard.Left = btnReview.Left - gap - btnDiscard.Width;
            lblEdits.Left = btnDiscard.Left - gap - lblEdits.PreferredWidth;

            // Vertikal centrering (alla tre i samma linje)
            int centerY = (header.Height - btnReview.Height) / 2;
            btnReview.Top = centerY;
            btnDiscard.Top = centerY;
            lblEdits.Top = centerY + (btnReview.Height - lblEdits.Height) / 2;

            // Anchors för att följa resize
            btnReview.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnDiscard.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            lblEdits.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        }

        /// <summary>
        /// Uppdaterar "Edits: n" i aktiva parets header samt enable/disable för Discard/Review.
        /// Räknar alla fält i draft-lagret, inklusive ATM Mid.
        /// </summary>
        private void UpdateDraftEditsCounter(string pair) // ERSÄTT
        {
            if (string.IsNullOrWhiteSpace(pair)) return;
            pair = pair.ToUpperInvariant();

            var page = FindPairTabPage(pair);
            if (page == null) return;

            var host = page.Controls[0] as Panel ?? page;
            var headerHost = FindChild<Panel>(host, "PairHeaderHost");
            var lbl = FindChild<Label>(host, "LblEdits");
            var btnDiscard = FindChild<Button>(host, "BtnDiscardDraft");
            var btnReview = FindChild<Button>(host, "BtnReviewDraft");
            if (lbl == null) return;

            int n = 0;
            if (_draftStore.TryGetValue(pair, out var perPair) && perPair != null)
            {
                foreach (var kv in perPair.Values)
                {
                    var r = kv;
                    if (r == null) continue;

                    // Viktigt: ta med ATM Mid
                    if (r.AtmMid.HasValue) n++;

                    if (r.Rr25Mid.HasValue) n++;
                    if (r.Bf25Mid.HasValue) n++;
                    if (r.Rr10Mid.HasValue) n++;
                    if (r.Bf10Mid.HasValue) n++;
                    if (r.AtmSpread.HasValue) n++;
                    if (r.AtmOffset.HasValue) n++;
                }
            }

            lbl.Text = $"Edits: {n}";
            if (btnDiscard != null) btnDiscard.Enabled = n > 0;
            if (btnReview != null) btnReview.Enabled = n > 0;

            LayoutHeaderRightControls(headerHost);
        }


        /// <summary>
        /// Discard all för aktiv par-tab: rensar draft-store, uppdaterar counter/knapp
        /// och laddar om via presentern så att både värden och gulmarkering försvinner.
        /// </summary>
        private void OnDiscardAllDraftClick(object sender, EventArgs e)
        {
            var page = _pairTabs?.SelectedTab;
            var pair = page?.Text?.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(pair)) return;

            if (_draftStore.ContainsKey(pair))
                _draftStore.Remove(pair);

            UpdateDraftEditsCounter(pair);

            try
            {
                _presenter?.RefreshPairAndBindAsync(pair, force: false).ConfigureAwait(false);
            }
            catch
            {
                // Fallback: åtminstone trigga omritning
                var grid = FindChild<DataGridView>(page);
                grid?.Invalidate();
            }
        }

        private TabPage FindPairTabPage(string pair)
        {
            if (_pairTabs == null || string.IsNullOrWhiteSpace(pair)) return null;
            pair = pair.Trim();
            foreach (TabPage p in _pairTabs.TabPages)
                if (string.Equals(p.Text, pair, StringComparison.OrdinalIgnoreCase))
                    return p;
            return null;
        }

        /// <summary>
        /// Säkerställer en editerbar ATM-justeringskolumn. Header blir "ATM Spread" (icke-ankrat)
        /// eller "ATM Offset" (ankrat). Kolumnen ligger alltid direkt efter "ATM Mid".
        /// Värden centreras och visas med 4 dp.
        /// </summary>
        private void EnsureAtmAdjustColumn(DataGridView grid, bool isAnchoredPair)
        {
            if (grid == null) return;

            // Sök "ATM Mid" för att veta var vi ska placera kolumnen
            var atmMid = grid.Columns.Cast<DataGridViewColumn>()
                .FirstOrDefault(c => string.Equals(c.HeaderText, "ATM Mid", StringComparison.OrdinalIgnoreCase));
            var insertAfterIdx = atmMid != null ? atmMid.DisplayIndex : 4; // default precis efter Mid

            var col = grid.Columns.Cast<DataGridViewColumn>()
                .FirstOrDefault(c => string.Equals(c.Name, "ATM_adj", StringComparison.OrdinalIgnoreCase));

            if (col == null)
            {
                col = new DataGridViewTextBoxColumn
                {
                    Name = "ATM_adj",
                    HeaderText = isAnchoredPair ? "ATM Offset" : "ATM Spread",
                    Width = 90,
                    SortMode = DataGridViewColumnSortMode.NotSortable,
                    ReadOnly = false
                };
                grid.Columns.Add(col);
            }
            else
            {
                col.HeaderText = isAnchoredPair ? "ATM Offset" : "ATM Spread";
                col.ReadOnly = false;
            }

            // Centrera och 4 dp (matchar ditt tema-krav)
            col.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            col.DefaultCellStyle.Format = "0.0000";

            // Placera direkt efter ATM Mid
            try { col.DisplayIndex = insertAfterIdx + 1; } catch { /* best effort */ }
        }

        /// <summary>
        /// Sätt/ta bort revert-flaggan i cell.Tag (Tuple&lt;orig, revert&gt;).
        /// </summary>
        private static void SetCellRevertFlag(DataGridViewCell cell, bool revert)
        {
            var t = cell.Tag as Tuple<string, bool>;
            var orig = t != null ? t.Item1 : (cell.Value != null ? cell.Value.ToString() : string.Empty);
            cell.Tag = new Tuple<string, bool>(orig, revert);
        }

        /// <summary>
        /// Applicerar draft-värden från _draftStore till grid och räknar därefter ATM Mid samt Bid/Ask per rad.
        /// Kör även per-rad QC-regler så att hårda/mjuka fel och tooltips syns direkt efter overlay.
        /// </summary>
        private void ApplyDraftToGrid(string pair, DataGridView grid) // ERSÄTT
        {
            if (string.IsNullOrWhiteSpace(pair) || grid == null) return;
            pair = pair.ToUpperInvariant();

            var anchored = _presenter != null && _presenter.IsAnchoredPair(pair);

            if (!_draftStore.TryGetValue(pair, out var perPair) || perPair == null || perPair.Count == 0)
            {
                // Ingen draft – men se till att Bid/Ask och QC ändå är koherenta
                foreach (DataGridViewRow row in grid.Rows)
                {
                    RecomputeAtmForRow(grid, row, anchored);

                    var hard = new List<string>();
                    var soft = new List<string>();
                    EvaluateRowRules_Scoped(row, anchored, hard, soft);
                }
                return;
            }

            int ColIndexByNameOrHeader(string key)
            {
                foreach (DataGridViewColumn c in grid.Columns)
                    if (string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase)) return c.Index;
                foreach (DataGridViewColumn c in grid.Columns)
                    if (string.Equals(c.HeaderText, key, StringComparison.OrdinalIgnoreCase)) return c.Index;
                return -1;
            }
            void SetCell(DataGridViewRow row, string key, object value)
            {
                var idx = ColIndexByNameOrHeader(key);
                if (idx >= 0) row.Cells[idx].Value = value ?? "";
            }

            foreach (DataGridViewRow row in grid.Rows)
            {
                var tenor = GetTenorFromRow(row);
                if (string.IsNullOrEmpty(tenor))
                {
                    // QC även om tenor saknas är inte meningsfullt; hoppa över.
                    continue;
                }

                if (perPair.TryGetValue(tenor, out var d) && d != null)
                {
                    // Lägg ut draftade fält på cellerna
                    if (d.AtmMid.HasValue && !anchored) SetCell(row, "ATM_mid", d.AtmMid.Value);  // endast non-anchored
                    if (d.AtmSpread.HasValue) SetCell(row, "ATM_spread", d.AtmSpread.Value);
                    if (d.AtmOffset.HasValue) SetCell(row, "ATM_adj", d.AtmOffset.Value);

                    if (d.Rr25Mid.HasValue) SetCell(row, "RR25_mid", d.Rr25Mid.Value);
                    if (d.Rr10Mid.HasValue) SetCell(row, "RR10_mid", d.Rr10Mid.Value);
                    if (d.Bf25Mid.HasValue) SetCell(row, "BF25_mid", d.Bf25Mid.Value);
                    if (d.Bf10Mid.HasValue) SetCell(row, "BF10_mid", d.Bf10Mid.Value);
                }

                // Efter overlay – räkna Mid + Bid/Ask och kör QC-regler
                RecomputeAtmForRow(grid, row, anchored);
                var hard = new List<string>();
                var soft = new List<string>();
                EvaluateRowRules_Scoped(row, anchored, hard, soft);
            }
        }



        /// <summary>
        /// Sparar originaltexten innan edit för att kunna återställa vid ogiltigt tal.
        /// Nollställer även tidigare cell- och raderror när man börjar editera, så att "rött" försvinner.
        /// </summary>
        private void OnPairGridCellBeginEdit(object sender, DataGridViewCellCancelEventArgs e)
        {
            var grid = sender as DataGridView;
            if (grid == null) return;

            var cell = grid.Rows[e.RowIndex].Cells[e.ColumnIndex];

            // Packa original i cell.Tag: (origText, revert=false)
            cell.Tag = new Tuple<string, bool>(
                cell.Value != null ? cell.Value.ToString() : string.Empty,
                false
            );

            // Rensa gamla felindikatorer direkt när användaren börjar editera
            cell.ErrorText = null;
            grid.Rows[e.RowIndex].ErrorText = null;
        }

        /// <summary>
        /// Öppnar Review-dialogen för aktivt par:
        /// - Bygger diff-lista från aktivt grids draft.
        /// - Räknar hårda/mjuka flaggor (radfel + tooltips) för visning i header.
        /// - Visar dialogen med fungerande Publish (kopplad till HandlePublishAsync).
        /// </summary>
        private void OnReviewDraftClick(object sender, EventArgs e)
        {
            var pair = GetActivePairSymbol();
            if (string.IsNullOrWhiteSpace(pair)) return;

            var grid = GetActivePairGrid();
            if (grid == null)
            {
                MessageBox.Show(this, "Kunde inte hitta grid för aktivt par.", "Volatility Manager",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Bygg diff
            var changes = BuildDraftDiffForPair(pair, grid);
            if (changes == null || changes.Count == 0)
            {
                MessageBox.Show(this, "Inga draftade ändringar att reviewa.", "Volatility Manager",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Räkna hårda/mjuka för dialogheadern
            var (hard, soft) = CountRuleFlags(grid);

            // Bygg & visa (dialogen har redan Publish-knapp kopplad till HandlePublishAsync)
            var dlg = BuildReviewDialog(pair, changes, hard, soft);
            dlg?.ShowDialog(this);
        }


        /// <summary>
        /// Klick på "Publish" i Review-dialogen. I 6c är detta endast en informations-stub
        /// (publicering implementeras i 6d).
        /// </summary>
        private void OnDialogPublishClick(object sender, EventArgs e) // NY
        {
            MessageBox.Show(this,
                "Publish är inte aktiverat ännu (steg 6d). Detta är en dry-run för att granska ändringarna.",
                "Volatility Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>
        /// Lätt validering: endast RR/BF-kolumner, accepterar både sv/eng decimaler.
        /// Röd radtext om parse misslyckas (blockerar commit).
        /// </summary>
        private void OnPairGridCellValidating(object sender, DataGridViewCellValidatingEventArgs e)
        {
            var grid = sender as DataGridView;
            if (grid == null || e.RowIndex < 0 || e.ColumnIndex < 0) return;

            var colHeader = grid.Columns[e.ColumnIndex].HeaderText?.Trim();
            if (colHeader != "RR25 Mid" && colHeader != "BF25 Mid" && colHeader != "RR10 Mid" && colHeader != "BF10 Mid")
                return;

            var txt = (e.FormattedValue ?? "").ToString().Trim();
            if (string.IsNullOrEmpty(txt))
            {
                grid.Rows[e.RowIndex].ErrorText = "";
                return; // tomt tillåtet i 6a
            }

            decimal parsed;
            bool ok =
                decimal.TryParse(txt, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.CurrentCulture, out parsed)
                || decimal.TryParse(txt, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out parsed);

            if (!ok)
            {
                e.Cancel = true;
                grid.Rows[e.RowIndex].ErrorText = "Ogiltigt tal";
            }
            else
            {
                grid.Rows[e.RowIndex].ErrorText = "";
            }
        }

        /// <summary>
        /// Commit i grid (alternativ handler i Pair Tabs)/>.
        /// Bevarar tooltips från <see cref="EvaluateRowRules_Scoped"/> och rensar dem inte här,
        /// så RR/BF-ratio/tecken och ATM-preview fortsätter att visas efter commit.
        /// </summary>
        private void OnPairGridCellEndEdit(object sender, DataGridViewCellEventArgs e)
        {
            var grid = sender as DataGridView;
            if (grid == null || e.RowIndex < 0 || e.ColumnIndex < 0) return;

            var row = grid.Rows[e.RowIndex];
            var col = grid.Columns[e.ColumnIndex];
            var cell = row.Cells[e.ColumnIndex];
            var name = col?.Name ?? string.Empty;

            // Revert-guard (ogiltigt tal)
            var tag = cell.Tag as Tuple<string, bool>;
            var revert = tag != null && tag.Item2;
            if (revert)
            {
                cell.Value = tag.Item1;
                cell.ErrorText = "Ogiltigt eller otillåtet värde. Återställdes.";
                UpdateDraftEditsCounter(GetActivePairFor(grid));
                grid.InvalidateCell(cell);
                return;
            }

            cell.ErrorText = null;
            row.ErrorText = null;

            var pair = GetActivePairFor(grid);
            var tenor = GetTenorFromRow(row);
            if (!string.IsNullOrEmpty(pair) && !string.IsNullOrEmpty(tenor))
            {
                var anchored = _presenter != null && _presenter.IsAnchoredPair(pair);
                var newVal = TryGetCellDecimal(row, name);

                if (string.Equals(name, "ATM_adj", StringComparison.OrdinalIgnoreCase))
                {
                    UpsertDraftValue(pair, tenor, "AtmOffset", newVal);
                }
                else if (string.Equals(name, "ATM_spread", StringComparison.OrdinalIgnoreCase))
                {
                    UpsertDraftValue(pair, tenor, "AtmSpread", newVal);
                }
                else if (string.Equals(name, "ATM_mid", StringComparison.OrdinalIgnoreCase))
                {
                    UpsertDraftValue(pair, tenor, "AtmMid", newVal);
                }
                else
                {
                    if (name == "RR25_mid") UpsertDraftValue(pair, tenor, "Rr25Mid", newVal);
                    if (name == "RR10_mid") UpsertDraftValue(pair, tenor, "Rr10Mid", newVal);
                    if (name == "BF25_mid") UpsertDraftValue(pair, tenor, "Bf25Mid", newVal);
                    if (name == "BF10_mid") UpsertDraftValue(pair, tenor, "Bf10Mid", newVal);
                }

                // Kör regler (sätter hårda fel + mjuka tooltips på rätt celler)
                var hard = new List<string>();
                var soft = new List<string>();
                EvaluateRowRules_Scoped(row, anchored, hard, soft);
                if (hard.Count > 0) row.ErrorText = string.Join("  ", hard);

                // Räkna om ATM Mid/Bid/Ask för raden (t.ex. hvis Spread/Offset/Mid ändrats)
                RecomputeAtmForRow(grid, row, anchored);
            }

            UpdateDraftEditsCounter(pair);
            grid.InvalidateRow(e.RowIndex);
        }





        /// <summary>
        /// Gulmarkering (draft) + visning av numeriska celler med decimalpunkt.
        /// Lägger dessutom på ATM Preview (Bid/Mid/Ask) i tooltip för ATM_mid/ATM_spread/ATM_adj.
        /// </summary>
        private void OnPairGridCellFormatting(object sender, DataGridViewCellFormattingEventArgs e) // ERSÄTT
        {
            var grid = sender as DataGridView;
            if (grid == null || e.RowIndex < 0 || e.ColumnIndex < 0) return;

            var row = grid.Rows[e.RowIndex];
            var col = grid.Columns[e.ColumnIndex];
            var name = col?.Name ?? string.Empty;

            var pair = GetActivePairFor(grid);
            var tenor = GetTenorFromRow(row);
            if (string.IsNullOrEmpty(pair) || string.IsNullOrEmpty(tenor))
                return;

            // 1) Gulmarkering vid draft (inkl. ATM_spread/ATM_adj)
            bool hasDraft = false;
            if (_draftStore.TryGetValue(pair, out var perPair) && perPair != null &&
                perPair.TryGetValue(tenor, out var d) && d != null)
            {
                if (name == "ATM_mid") hasDraft = d.AtmMid.HasValue;
                else if (name == "ATM_spread") hasDraft = d.AtmSpread.HasValue;
                else if (name == "RR25_mid") hasDraft = d.Rr25Mid.HasValue;
                else if (name == "RR10_mid") hasDraft = d.Rr10Mid.HasValue;
                else if (name == "BF25_mid") hasDraft = d.Bf25Mid.HasValue;
                else if (name == "BF10_mid") hasDraft = d.Bf10Mid.HasValue;
                else if (string.Equals(name, "ATM_adj", StringComparison.OrdinalIgnoreCase))
                    hasDraft = d.AtmOffset.HasValue;
            }
            if (hasDraft)
                row.Cells[e.ColumnIndex].Style.BackColor = Color.FromArgb(255, 249, 196);

            // 2) Numeriskt punkt-format
            var numericCols = name == "ATM_mid"
                              || name == "ATM_spread"
                              || name == "ATM_adj"
                              || name == "RR25_mid"
                              || name == "RR10_mid"
                              || name == "BF25_mid"
                              || name == "BF10_mid";
            if (numericCols && e.Value != null)
            {
                try
                {
                    if (e.Value is decimal dec)
                    {
                        e.Value = dec.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
                        e.FormattingApplied = true;
                    }
                    else if (e.Value is double dbl)
                    {
                        e.Value = ((decimal)dbl).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
                        e.FormattingApplied = true;
                    }
                    else
                    {
                        var s = e.Value.ToString();
                        if (!string.IsNullOrEmpty(s))
                        {
                            if (decimal.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.CurrentCulture, out var p1))
                            {
                                e.Value = p1.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
                                e.FormattingApplied = true;
                            }
                            else if (decimal.TryParse(s.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var p2))
                            {
                                e.Value = p2.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
                                e.FormattingApplied = true;
                            }
                            else
                            {
                                var normalized = s.Replace(',', '.');
                                if (!ReferenceEquals(normalized, s))
                                {
                                    e.Value = normalized;
                                    e.FormattingApplied = true;
                                }
                            }
                        }
                    }
                }
                catch { /* display only */ }
            }

            // 3) ATM Preview-tooltip på relevanta celler
            if (name == "ATM_mid" || name == "ATM_spread" || name == "ATM_adj")
            {
                var anchored = _presenter != null && _presenter.IsAnchoredPair(pair);
                var preview = BuildAtmPreviewTooltip(row, anchored);
                if (!string.IsNullOrEmpty(preview))
                {
                    var cell = row.Cells[e.ColumnIndex];
                    if (cell != null)
                    {
                        // Blanda in ev. befintliga mjuka varningar (från EndEdit) – lägg preview längst ned
                        var existing = cell.ToolTipText;
                        if (string.IsNullOrEmpty(existing))
                            cell.ToolTipText = preview;
                        else if (!existing.Contains("ATM Preview"))
                            cell.ToolTipText = existing + Environment.NewLine + preview;
                    }
                }
            }
        }




        private string GetPairKeyFromGrid(DataGridView grid)
        {
            var page = grid?.Parent?.Parent as TabPage ?? grid?.Parent as TabPage;
            return page?.Text?.Trim().ToUpperInvariant();
        }

        private string GetTenorFromRow(DataGridViewRow row)
        {
            // antar kol 0 = Tenor (som i din bind)
            return row?.Cells[0]?.Value?.ToString()?.Trim();
        }

        /// <summary>
        /// Aktiverar editering för RR/BF, ATM Mid samt separata ATM Offset/ATM Spread enligt modell B.
        /// - Wire: CellBeginEdit/Validating/EndEdit/Formatting/DataError (kopplas endast en gång).
        /// - Synlighet/ReadOnly för ATM-kolumner styrs av ankarstatus.
        /// - Kontextmeny för anchored: högerklick på ATM Mid → "Set ATM Mid…".
        /// </summary>
        private void EnableEditingForPairGrid(DataGridView grid) // ERSÄTT
        {
            if (grid == null) return;

            var pair = GetActivePairFor(grid);
            var anchored = _presenter != null && _presenter.IsAnchoredPair(pair);

            // Säkerställ ATM-kolumner + synlighet/ReadOnly enligt ankarpolicy
            EnsureAtmColumnsAndVisibility(grid, anchored);

            // RR/BF – alltid editerbara
            SetReadOnly(grid, "RR25 Mid", false);
            SetReadOnly(grid, "BF25 Mid", false);
            SetReadOnly(grid, "RR10 Mid", false);
            SetReadOnly(grid, "BF10 Mid", false);

            // Event wiring: koppla endast en gång
            if (!string.Equals(grid.Tag as string, "edit-wired", StringComparison.OrdinalIgnoreCase))
            {
                grid.CellBeginEdit -= OnPairGridCellBeginEdit;
                grid.CellValidating -= PairGrid_CellValidating;
                grid.CellEndEdit -= OnPairGridCellEndEdit;
                grid.CellFormatting -= OnPairGridCellFormatting;
                grid.DataError -= PairGrid_DataError;

                grid.CellBeginEdit += OnPairGridCellBeginEdit;
                grid.CellValidating += PairGrid_CellValidating;
                grid.CellEndEdit += OnPairGridCellEndEdit;
                grid.CellFormatting += OnPairGridCellFormatting;
                grid.DataError += PairGrid_DataError;

                grid.Tag = "edit-wired";
            }

            // Kontextmeny för anchored (ATM_mid)
            EnsureAtmContextMenuForGrid(grid);

            grid.Invalidate();
        }



        /// <summary>
        /// Sätter ReadOnly på en kolumn genom att matcha på HeaderText (t.ex. "RR25 Mid").
        /// </summary>
        private void SetReadOnly(DataGridView grid, string headerText, bool ro)
        {
            if (grid == null || string.IsNullOrWhiteSpace(headerText)) return;

            var col = grid.Columns
                         .Cast<DataGridViewColumn>()
                         .FirstOrDefault(c => string.Equals(c.HeaderText, headerText, StringComparison.OrdinalIgnoreCase));

            if (col != null)
                col.ReadOnly = ro;
        }

        /// <summary>
        /// Uppdaterar header-texten i par-fliken: "PAIR • yyyy-MM-dd HH:mm UTC".
        /// </summary>
        private void UpdatePairHeader(TabPage page, string pair, DateTime tsUtc)
        {
            if (page == null) return;
            var hostPanel = page.Controls[0] as Panel ?? page;
            var headerHost = FindChild<Panel>(hostPanel, "PairHeaderHost");
            var lbl = headerHost != null ? FindChild<Label>(headerHost, "PairHeaderLabel") : null;
            if (lbl != null) lbl.Text = $"{pair}  •  {FormatTimeUtc(tsUtc)}";
        }

        /// <summary>
        /// Uppdaterar status-texten i tab-headern (höger): "Cached" eller "Fresh @ hh:mm:ss".
        /// </summary>
        private void UpdatePairHeaderStatus(TabPage page, bool fromCache)
        {
            if (page == null) return;
            var hostPanel = page.Controls[0] as Panel ?? page;
            var headerHost = FindChild<Panel>(hostPanel, "PairHeaderHost");
            var lbl = headerHost != null ? FindChild<Label>(headerHost, "PairHeaderStatus") : null;
            if (lbl != null)
                lbl.Text = fromCache ? "Cached" : $"Fresh @ {DateTime.Now:HH:mm:ss}";
        }

        /// <summary>
        /// Skapar/hämtar TabPage + DataGridView för ett par i Tabs-läget och
        /// sätter kolumnordning samt applicerar Pricer-temat (typsnitt/färger).
        /// Ordning: Tenor, Days, ATM Bid, ATM Ask, ATM Mid, (ATM Spread/Offset infogas efter bind),
        /// därefter RR25 Mid, RR10 Mid, BF25 Mid, BF10 Mid.
        /// </summary>
        private DataGridView EnsurePairTabGrid(string pair)
        {
            if (_pairTabs == null)
                throw new InvalidOperationException("PairTabs saknas i vyn.");

            // Hitta/skap TabPage
            TabPage page = null;
            foreach (TabPage p in _pairTabs.TabPages)
                if (string.Equals(p.Text, pair, StringComparison.OrdinalIgnoreCase)) { page = p; break; }

            if (page == null)
            {
                page = new TabPage { Text = pair, UseVisualStyleBackColor = true, Padding = new Padding(0) };
                var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
                page.Controls.Add(host);
                _pairTabs.TabPages.Add(page);
            }

            var hostPanel = page.Controls[0] as Panel ?? page;

            // Header (vänster titel + tidsstämpel)
            EnsurePairTabHeader(hostPanel, "PairHeaderHost", "PairHeaderLabel");

            // Grid?
            var grid = FindChild<DataGridView>(hostPanel, "PairGrid");
            if (grid != null) return grid;

            grid = new DataGridView
            {
                Name = "PairGrid",
                Dock = DockStyle.Fill,
                ReadOnly = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                SelectionMode = DataGridViewSelectionMode.CellSelect,
                MultiSelect = false,
                EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                BorderStyle = BorderStyle.None,
                BackgroundColor = Color.White,
                CellBorderStyle = DataGridViewCellBorderStyle.Single,
                GridColor = Color.Gainsboro,                // samma gridline-färg som Pricer
                EnableHeadersVisualStyles = false           // krävs för att header-back ska slå igenom
            };

            grid.Columns.Clear();

            // Tenor + Days
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Tenor", HeaderText = "Tenor", Width = 70, SortMode = DataGridViewColumnSortMode.NotSortable, ReadOnly = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "DaysNom", HeaderText = "Days (nom.)", Width = 90, SortMode = DataGridViewColumnSortMode.NotSortable, ReadOnly = true });

            // ATM (Bid, Ask, Mid)
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ATM_bid", HeaderText = "ATM Bid", Width = 90, SortMode = DataGridViewColumnSortMode.NotSortable, ReadOnly = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ATM_ask", HeaderText = "ATM Ask", Width = 90, SortMode = DataGridViewColumnSortMode.NotSortable, ReadOnly = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ATM_mid", HeaderText = "ATM Mid", Width = 90, SortMode = DataGridViewColumnSortMode.NotSortable, ReadOnly = true });

            // RR/BF
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "RR25_mid", HeaderText = "RR25 Mid", Width = 90, SortMode = DataGridViewColumnSortMode.NotSortable, ReadOnly = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "RR10_mid", HeaderText = "RR10 Mid", Width = 90, SortMode = DataGridViewColumnSortMode.NotSortable, ReadOnly = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "BF25_mid", HeaderText = "BF25 Mid", Width = 90, SortMode = DataGridViewColumnSortMode.NotSortable, ReadOnly = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "BF10_mid", HeaderText = "BF10 Mid", Width = 90, SortMode = DataGridViewColumnSortMode.NotSortable, ReadOnly = true });

            // Applicera Pricer-tema (typsnitt/färger) + 4dp på volkolumner
            ApplyPricerThemeToGrid(grid);

            // Hooka validering (6b)
            //HookValidationEvents(grid);

            hostPanel.Controls.Add(grid);
            grid.BringToFront();
            return grid;
        }


        /// <summary>
        /// Applicerar Pricer-temat på en DataGridView:
        /// (1) Samma typsnitt i grid och headers: Segoe UI 8.25 (header Bold)
        /// (2) Samma alternating row-färg: FromArgb(250,251,253)
        /// (3) Samma gridline-färg: Gainsboro
        /// (4) Samma header-bakgrund: FromArgb(242,246,251)
        /// Dessutom centreras alla cellvärden och volkolumner får 4 dp.
        /// </summary>
        private void ApplyPricerThemeToGrid(DataGridView grid)
        {
            if (grid == null) return;

            // (1) Typsnitt – identiskt med Pricer
            var baseFont = new Font("Segoe UI", 8.25f, FontStyle.Regular);
            var headerFont = new Font("Segoe UI", 8.25f, FontStyle.Bold);
            grid.Font = baseFont;

            // Header: Bold, centrerad text, definierad bakgrund
            grid.ColumnHeadersDefaultCellStyle.Font = headerFont;
            grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(242, 246, 251); // (4)
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.Black;

            // Alternating row-färg (väldigt ljus)
            grid.RowsDefaultCellStyle.BackColor = Color.White;
            grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(250, 251, 253); // (2)

            // (3) Gridline-färg sätts i EnsurePairTabGrid (Gainsboro)

            // Centrera alla cellvärden (din önskan)
            grid.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;

            // Tenor och Days – också centrerade (Tenor bold för läsbarhet)
            var tenorCol = grid.Columns["Tenor"];
            if (tenorCol != null)
                tenorCol.DefaultCellStyle = new DataGridViewCellStyle
                {
                    Alignment = DataGridViewContentAlignment.MiddleCenter,
                    Font = new Font(baseFont, FontStyle.Bold)
                };

            var daysCol = grid.Columns["DaysNom"];
            if (daysCol != null)
                daysCol.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;

            // 4 dp på vol-kolumner
            Action<string> fmt = name =>
            {
                var col = grid.Columns.Cast<DataGridViewColumn>()
                    .FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                if (col != null) col.DefaultCellStyle.Format = "0.0000";
            };

            fmt("ATM_bid"); fmt("ATM_ask"); fmt("ATM_mid"); fmt("ATM_adj");
            fmt("RR25_mid"); fmt("RR10_mid"); fmt("BF25_mid"); fmt("BF10_mid");
        }



        /// <summary>
        /// Centrerar och applicerar 4-decimalsformat på givna kolumner om de finns.
        /// </summary>
        private static void CenterNumericColumnsAnd4Dp(DataGridView grid, params string[] columnNames) // NY
        {
            if (grid == null || columnNames == null) return;
            foreach (var name in columnNames)
            {
                var col = grid.Columns.Cast<DataGridViewColumn>()
                    .FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                if (col == null) continue;

                col.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
                col.DefaultCellStyle.Format = "0.0000";
            }
        }



        /// <summary>
        /// Bygger/återanvänder headern för ett par i Tabs-vyn när man hostar i en given Panel.
        /// Skapar vänster titel-label (PairHeaderLabel), status-label (PairHeaderStatus)
        /// samt edits-label (LblEdits) och knapparna BtnDiscardDraft/BtnReviewDraft som
        /// UpdateDraftEditsCounter och layout-logiken förväntar sig.
        /// </summary>
        private Panel EnsurePairTabHeader(Panel hostPanel, string headerPanelName, string pairHeaderLabelName)
        {
            if (hostPanel == null) throw new ArgumentNullException(nameof(hostPanel));
            if (string.IsNullOrWhiteSpace(headerPanelName)) throw new ArgumentNullException(nameof(headerPanelName));
            if (string.IsNullOrWhiteSpace(pairHeaderLabelName)) throw new ArgumentNullException(nameof(pairHeaderLabelName));

            // 1) Header-panel
            var header = hostPanel.Controls.OfType<Panel>().FirstOrDefault(p => p.Name == headerPanelName);
            if (header == null)
            {
                header = new Panel
                {
                    Name = headerPanelName,
                    Dock = DockStyle.Top,
                    Height = 28,
                    Padding = new Padding(8, 4, 8, 4),
                    BackColor = Color.FromArgb(250, 251, 253)
                };
                hostPanel.Controls.Add(header);
                header.BringToFront();
            }

            // 2) Vänster titel (PAIR • TS)
            var lblPair = header.Controls.OfType<Label>().FirstOrDefault(l => l.Name == pairHeaderLabelName);
            if (lblPair == null)
            {
                lblPair = new Label
                {
                    Name = pairHeaderLabelName,        // "PairHeaderLabel"
                    AutoSize = true,
                    Text = "",                         // sätts via UpdatePairHeader(...)
                    TextAlign = ContentAlignment.MiddleLeft
                };
                header.Controls.Add(lblPair);
            }

            // 3) Status-label (t.ex. "Cached" / "Fresh @ hh:mm:ss")
            var lblStatus = header.Controls.OfType<Label>().FirstOrDefault(l => l.Name == "PairHeaderStatus");
            if (lblStatus == null)
            {
                lblStatus = new Label
                {
                    Name = "PairHeaderStatus",
                    AutoSize = true,
                    Text = "",
                    TextAlign = ContentAlignment.MiddleLeft
                };
                header.Controls.Add(lblStatus);
            }

            // 4) Edits-label (det här namnet förväntas av UpdateDraftEditsCounter)
            var lblEdits = header.Controls.OfType<Label>().FirstOrDefault(l => l.Name == "LblEdits");
            if (lblEdits == null)
            {
                lblEdits = new Label
                {
                    Name = "LblEdits",
                    AutoSize = true,
                    Text = "Edits: 0",
                    TextAlign = ContentAlignment.MiddleLeft
                };
                header.Controls.Add(lblEdits);
            }

            // 5) Discard-knapp (namn förväntas av UpdateDraftEditsCounter/LayoutHeaderRightControls)
            var btnDiscard = header.Controls.OfType<Button>().FirstOrDefault(b => b.Name == "BtnDiscardDraft");
            if (btnDiscard == null)
            {
                btnDiscard = new Button
                {
                    Name = "BtnDiscardDraft",
                    Text = "Discard",
                    Size = new Size(78, 23),
                    Enabled = false
                };
                btnDiscard.Click += OnDiscardAllDraftClick;
                header.Controls.Add(btnDiscard);
            }

            // 6) Review-knapp (namn förväntas av UpdateDraftEditsCounter/LayoutHeaderRightControls)
            var btnReview = header.Controls.OfType<Button>().FirstOrDefault(b => b.Name == "BtnReviewDraft");
            if (btnReview == null)
            {
                btnReview = new Button
                {
                    Name = "BtnReviewDraft",
                    Text = "Review",
                    Size = new Size(78, 23),
                    Enabled = false
                };
                btnReview.Click += OnReviewDraftClick; // öppnar dry-run-dialogen
                header.Controls.Add(btnReview);
            }

            // 7) Layout – vänster etiketter, höger knappar/edits
            void DoLayout()
            {
                // Vänster sida: PairHeaderLabel följt av PairHeaderStatus
                lblPair.Left = 8;
                lblPair.Top = (header.Height - lblPair.Height) / 2;

                lblStatus.Left = lblPair.Right + 12;
                lblStatus.Top = (header.Height - lblStatus.Height) / 2;

                // Höger sida läggs av befintlig helper (placerar LblEdits, BtnDiscardDraft, BtnReviewDraft)
                LayoutHeaderRightControls(header);
            }

            header.Resize -= HeaderOnResize;
            header.Resize += HeaderOnResize;
            DoLayout();

            void HeaderOnResize(object sender, EventArgs e) => DoLayout();

            return header;
        }

        #endregion

        #region Privata hjälpare – Busy overlays

        /// <summary>
        /// Hämtar eller skapar en enkel meddelande-overlay (Panel + Label) på en host.
        /// </summary>
        private Control GetOrCreateMessageOverlay(Control host, string name, string text, Color foreColor)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));

            var existing = FindChild<Panel>(host, name);
            if (existing != null)
            {
                var lblOld = FindChild<Label>(existing, "MsgLabel");
                if (lblOld != null) { lblOld.Text = text; lblOld.ForeColor = foreColor; }
                return existing;
            }

            var overlay = new Panel
            {
                Name = name,
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Visible = false
            };

            var lbl = new Label
            {
                Name = "MsgLabel",
                Dock = DockStyle.Fill,
                Text = text,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font(Font, FontStyle.Italic),
                ForeColor = foreColor
            };

            overlay.Controls.Add(lbl);
            host.Controls.Add(overlay);
            return overlay;
        }

        /// <summary>
        /// Döljer en overlay om den finns.
        /// </summary>
        private void HideOverlay(Control host, string name)
        {
            if (host == null) return;
            var pnl = FindChild<Panel>(host, name);
            if (pnl != null) pnl.Visible = false;
        }


        /// <summary>
        /// Hämtar eller skapar en enkel busy-overlay (Panel + Label) på en host.
        /// </summary>
        private Control GetOrCreateBusyOverlay(Control host, string name)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));

            var existing = FindChild<Panel>(host, name);
            if (existing != null) return existing;

            var overlay = new Panel
            {
                Name = name,
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Visible = false
            };

            var lbl = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Loading…",
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font(Font, FontStyle.Bold),
                ForeColor = Color.Gray
            };

            overlay.Controls.Add(lbl);
            host.Controls.Add(overlay);
            overlay.BringToFront();
            return overlay;
        }


        #endregion

        #region Privata hjälpare – Formatting

        /// <summary>Snapshot UTC-tid i standardformat.</summary>
        private string FormatTimeUtc(DateTime tsUtc) => tsUtc.ToString("yyyy-MM-dd HH:mm") + " UTC";

        #endregion

        #region Shared helpers / grid

        /// <summary>
        /// Försöker läsa ett heltal från en rad för en given kolumn (Name eller HeaderText).
        /// Returnerar null om kolumn saknas eller ej kan tolkas.
        /// </summary>
        private int? TryGetCellInt(DataGridViewRow row, string name)
        {
            if (row == null || string.IsNullOrWhiteSpace(name)) return null;
            var grid = row.DataGridView;
            if (grid == null) return null;

            DataGridViewColumn col = null;
            foreach (DataGridViewColumn c in grid.Columns)
                if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) { col = c; break; }
            if (col == null)
                foreach (DataGridViewColumn c in grid.Columns)
                    if (string.Equals(c.HeaderText, name, StringComparison.OrdinalIgnoreCase)) { col = c; break; }

            if (col == null) return null;

            var val = row.Cells[col.Index]?.Value;
            if (val == null) return null;

            if (val is int i) return i;

            var s = val.ToString();
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.CurrentCulture, out var p1)) return p1;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p2)) return p2;

            return null;
        }

        /// <summary>
        /// Tolkar en tenorsträng till nominella dagar (fallback när DaysNom saknas). Enkla regler: 1W=7, 2W=14, 1M=30, 3M=90, 6M=180, 1Y=365, etc.
        /// Returnerar null om strängen ej känns igen.
        /// </summary>
        private int? TenorToNominalDaysFallback(string tenor)
        {
            if (string.IsNullOrWhiteSpace(tenor)) return null;
            tenor = tenor.Trim().ToUpperInvariant();

            try
            {
                if (tenor.EndsWith("W"))
                {
                    var n = int.Parse(tenor.Substring(0, tenor.Length - 1));
                    return n * 7;
                }
                if (tenor.EndsWith("M"))
                {
                    var n = int.Parse(tenor.Substring(0, tenor.Length - 1));
                    return n * 30; // enkel approx
                }
                if (tenor.EndsWith("Y"))
                {
                    var n = int.Parse(tenor.Substring(0, tenor.Length - 1));
                    return n * 365;
                }
            }
            catch { /* ignore */ }

            return null;
        }

        #endregion


        #region Draft-lager (modell + store)

        /// <summary>
        /// Sätter eller rensar ett draftvärde för ett par/tenor och fält.
        /// fieldKey stöder: "AtmMid","Rr25Mid","Rr10Mid","Bf25Mid","Bf10Mid","AtmSpread","AtmOffset".
        /// Skapar rad vid behov. Tar bort rad/par-nycklar när samtliga fält blir null.
        /// Anropar UpdateDraftEditsCounter(pair) och gör ingen IO.
        /// </summary>
        private void UpsertDraftValue(string pair, string tenor, string fieldKey, decimal? value)
        {
            if (string.IsNullOrWhiteSpace(pair) || string.IsNullOrWhiteSpace(tenor) || string.IsNullOrWhiteSpace(fieldKey))
                return;

            fieldKey = NormalizeDraftFieldKey(fieldKey);

            if (!_draftStore.TryGetValue(pair, out var perPair) || perPair == null)
            {
                perPair = new Dictionary<string, VolDraftRow>(StringComparer.OrdinalIgnoreCase);
                _draftStore[pair] = perPair;
            }

            if (!perPair.TryGetValue(tenor, out var row) || row == null)
            {
                row = new VolDraftRow { TenorCode = tenor };
                perPair[tenor] = row;
            }

            // Sätt/ta bort värdet på rätt fält
            switch (fieldKey)
            {
                case "AtmMid": row.AtmMid = value; break; // <— nytt fält i draft-raden
                case "Rr25Mid": row.Rr25Mid = value; break;
                case "Rr10Mid": row.Rr10Mid = value; break;
                case "Bf25Mid": row.Bf25Mid = value; break;
                case "Bf10Mid": row.Bf10Mid = value; break;
                case "AtmSpread": row.AtmSpread = value; break;
                case "AtmOffset": row.AtmOffset = value; break;
                default: return; // okänt fält – ignorera tyst
            }

            // Om raden blivit helt tom → ta bort den
            if (!HasAnyDraftValues(row))
            {
                perPair.Remove(tenor);
                if (perPair.Count == 0)
                    _draftStore.Remove(pair);
            }

            // Uppdatera liten status (om metoden finns; annars no-op)
            try { UpdateDraftEditsCounter(pair); } catch { /* best effort */ }
        }

        /// <summary>
        /// Normaliserar fältnamn till förväntade draft-nycklar.
        /// </summary>
        private static string NormalizeDraftFieldKey(string fieldKey)
        {
            if (string.IsNullOrWhiteSpace(fieldKey)) return fieldKey;
            var k = fieldKey.Trim();

            // Tillåt både header-texter och interna namn
            if (k.Equals("ATM Mid", StringComparison.OrdinalIgnoreCase) || k.Equals("ATM_mid", StringComparison.OrdinalIgnoreCase))
                return "AtmMid";
            if (k.Equals("RR25 Mid", StringComparison.OrdinalIgnoreCase) || k.Equals("RR25_mid", StringComparison.OrdinalIgnoreCase))
                return "Rr25Mid";
            if (k.Equals("RR10 Mid", StringComparison.OrdinalIgnoreCase) || k.Equals("RR10_mid", StringComparison.OrdinalIgnoreCase))
                return "Rr10Mid";
            if (k.Equals("BF25 Mid", StringComparison.OrdinalIgnoreCase) || k.Equals("BF25_mid", StringComparison.OrdinalIgnoreCase))
                return "Bf25Mid";
            if (k.Equals("BF10 Mid", StringComparison.OrdinalIgnoreCase) || k.Equals("BF10_mid", StringComparison.OrdinalIgnoreCase))
                return "Bf10Mid";
            if (k.Equals("ATM Spread", StringComparison.OrdinalIgnoreCase) || k.Equals("AtmSpread", StringComparison.OrdinalIgnoreCase))
                return "AtmSpread";
            if (k.Equals("ATM Offset", StringComparison.OrdinalIgnoreCase) || k.Equals("AtmOffset", StringComparison.OrdinalIgnoreCase))
                return "AtmOffset";

            return k;
        }

        /// <summary>
        /// True om raden bär någon draft (inklusive AtmMid).
        /// </summary>
        private static bool HasAnyDraftValues(VolDraftRow r)
        {
            return r != null && (
                r.AtmMid.HasValue ||          // <— nytt
                r.Rr25Mid.HasValue ||
                r.Rr10Mid.HasValue ||
                r.Bf25Mid.HasValue ||
                r.Bf10Mid.HasValue ||
                r.AtmSpread.HasValue ||
                r.AtmOffset.HasValue
            );
        }

        #endregion

        #region Draft – review/publish dialog

        /// <summary>
        /// Beräknar cross-tenor QC för ett par utifrån den aktiva griden (draftoverlay redan applicerad i cellerna).
        /// Returnerar (hardCount, softCount, lista av rader som kan adderas till Review-grid).
        /// Varje "rad" är ett tuple: (TenorLabel, Field, Before, After, Note).
        /// </summary>
        private Tuple<int, int, List<Tuple<string, string, string, string, string>>> BuildCrossTenorQcRows(string pair, DataGridView grid)
        {
            var hard = 0;
            var soft = 0;
            var rowsOut = new List<Tuple<string, string, string, string, string>>();
            if (grid == null) return Tuple.Create(hard, soft, rowsOut);

            var anchored = _presenter != null && _presenter.IsAnchoredPair(pair);

            // Läs ut tenorer sorterade på T (år) – använd DaysNom i första hand, annars fallback från tenorsträng
            var items = new List<(DataGridViewRow row, string tenor, decimal? atm, decimal? rr25, decimal? rr10, decimal? bf25, decimal? bf10, decimal T)>();
            foreach (DataGridViewRow r in grid.Rows)
            {
                var tenor = r.Cells["Tenor"]?.Value?.ToString();
                if (string.IsNullOrWhiteSpace(tenor)) continue;

                // ATM (effektiv): cell "ATM_mid" har redan eff mid (anchored = AnchorMid+Offset) efter våra tidigare steg
                var atm = TryGetCellDecimal(r, "ATM_mid");
                var rr25 = TryGetCellDecimal(r, "RR25_mid");
                var rr10 = TryGetCellDecimal(r, "RR10_mid");
                var bf25 = TryGetCellDecimal(r, "BF25_mid");
                var bf10 = TryGetCellDecimal(r, "BF10_mid");

                // T i år
                var days = TryGetCellInt(r, "DaysNom") ?? TenorToNominalDaysFallback(tenor) ?? 0;
                if (days <= 0) continue; // utan T kan vi inte göra kalender-QC
                var T = (decimal)days / 365.0m;

                items.Add((r, tenor, atm, rr25, rr10, bf25, bf10, T));
            }

            // Sortera på T
            items.Sort((a, b) => a.T.CompareTo(b.T));
            if (items.Count < 2) return Tuple.Create(hard, soft, rowsOut);

            // Hjälpare för varians
            decimal Var(decimal sigma, decimal T) => sigma * sigma * T;

            // Trösklar (kan göras konfigurerbara senare)
            const decimal EPS = 1e-8m;            // numerisk tolerans
            const decimal ATM_JUMP = 2.0m;        // soft varning vid stort hopp i ATM (vol-points)
            const decimal RR_JUMP = 1.5m;        // soft varning vid stort hopp i RR
            const decimal BF_JUMP = 1.0m;        // soft varning vid stort hopp i BF
            const decimal OFF_JUMP = 1.0m;        // soft varning vid "hackig" offset (endast anchored)

            // Loop över grannar för kalender-QC + släthet
            for (int i = 0; i < items.Count - 1; i++)
            {
                var a = items[i];
                var b = items[i + 1];
                var label = $"{a.tenor}→{b.tenor}";

                // Härled 10P/25P/25C/10C från ATM/RR/BF (om alla komponenter finns)
                bool have25 = a.atm.HasValue && a.rr25.HasValue && a.bf25.HasValue &&
                              b.atm.HasValue && b.rr25.HasValue && b.bf25.HasValue;
                bool have10 = a.atm.HasValue && a.rr10.HasValue && a.bf10.HasValue &&
                              b.atm.HasValue && b.rr10.HasValue && b.bf10.HasValue;

                if (have25)
                {
                    var a25C = a.atm.Value + 0.5m * a.bf25.Value - 0.5m * a.rr25.Value;
                    var a25P = a.atm.Value + 0.5m * a.bf25.Value + 0.5m * a.rr25.Value;
                    var b25C = b.atm.Value + 0.5m * b.bf25.Value - 0.5m * b.rr25.Value;
                    var b25P = b.atm.Value + 0.5m * b.bf25.Value + 0.5m * b.rr25.Value;

                    if (Var(b25C, b.T) + EPS < Var(a25C, a.T))
                    {
                        hard++;
                        rowsOut.Add(Tuple.Create(label, "QC: Calendar variance 25C", "", "",
                            $"Total variance decreases ({a.tenor}→{b.tenor}).  {a25C:0.####}²·{a.T:0.####} → {b25C:0.####}²·{b.T:0.####}"));
                    }
                    if (Var(b25P, b.T) + EPS < Var(a25P, a.T))
                    {
                        hard++;
                        rowsOut.Add(Tuple.Create(label, "QC: Calendar variance 25P", "", "",
                            $"Total variance decreases ({a.tenor}→{b.tenor}).  {a25P:0.####}²·{a.T:0.####} → {b25P:0.####}²·{b.T:0.####}"));
                    }
                }

                if (have10)
                {
                    var a10C = a.atm.Value + 0.5m * a.bf10.Value - 0.5m * a.rr10.Value;
                    var a10P = a.atm.Value + 0.5m * a.bf10.Value + 0.5m * a.rr10.Value;
                    var b10C = b.atm.Value + 0.5m * b.bf10.Value - 0.5m * b.rr10.Value;
                    var b10P = b.atm.Value + 0.5m * b.bf10.Value + 0.5m * b.rr10.Value;

                    if (Var(b10C, b.T) + EPS < Var(a10C, a.T))
                    {
                        hard++;
                        rowsOut.Add(Tuple.Create(label, "QC: Calendar variance 10C", "", "",
                            $"Total variance decreases ({a.tenor}→{b.tenor}).  {a10C:0.####}²·{a.T:0.####} → {b10C:0.####}²·{b.T:0.####}"));
                    }
                    if (Var(b10P, b.T) + EPS < Var(a10P, a.T))
                    {
                        hard++;
                        rowsOut.Add(Tuple.Create(label, "QC: Calendar variance 10P", "", "",
                            $"Total variance decreases ({a.tenor}→{b.tenor}).  {a10P:0.####}²·{a.T:0.####} → {b10P:0.####}²·{b.T:0.####}"));
                    }
                }

                // Släthet (soft): stora hopp mellan grannar
                if (a.atm.HasValue && b.atm.HasValue && Math.Abs(b.atm.Value - a.atm.Value) > ATM_JUMP)
                {
                    soft++;
                    rowsOut.Add(Tuple.Create(label, "QC: ATM jump", "", "",
                        $"Large ATM change: {a.atm.Value:0.####} → {b.atm.Value:0.####}"));
                }
                if (a.rr25.HasValue && b.rr25.HasValue && Math.Abs(b.rr25.Value - a.rr25.Value) > RR_JUMP)
                {
                    soft++;
                    rowsOut.Add(Tuple.Create(label, "QC: RR25 jump", "", "",
                        $"Large RR25 change: {a.rr25.Value:0.####} → {b.rr25.Value:0.####}"));
                }
                if (a.rr10.HasValue && b.rr10.HasValue && Math.Abs(b.rr10.Value - a.rr10.Value) > RR_JUMP)
                {
                    soft++;
                    rowsOut.Add(Tuple.Create(label, "QC: RR10 jump", "", "",
                        $"Large RR10 change: {a.rr10.Value:0.####} → {b.rr10.Value:0.####}"));
                }
                if (a.bf25.HasValue && b.bf25.HasValue && Math.Abs(b.bf25.Value - a.bf25.Value) > BF_JUMP)
                {
                    soft++;
                    rowsOut.Add(Tuple.Create(label, "QC: BF25 jump", "", "",
                        $"Large BF25 change: {a.bf25.Value:0.####} → {b.bf25.Value:0.####}"));
                }
                if (a.bf10.HasValue && b.bf10.HasValue && Math.Abs(b.bf10.Value - a.bf10.Value) > BF_JUMP)
                {
                    soft++;
                    rowsOut.Add(Tuple.Create(label, "QC: BF10 jump", "", "",
                        $"Large BF10 change: {a.bf10.Value:0.####} → {b.bf10.Value:0.####}"));
                }

                // Anchored: Offset-släthet (soft)
                if (anchored)
                {
                    var offA = TryGetCellDecimal(a.row, "ATM_adj");
                    var offB = TryGetCellDecimal(b.row, "ATM_adj");
                    if (offA.HasValue && offB.HasValue && Math.Abs(offB.Value - offA.Value) > OFF_JUMP)
                    {
                        soft++;
                        rowsOut.Add(Tuple.Create(label, "QC: Offset jump", "", "",
                            $"Large Offset change: {offA.Value:0.####} → {offB.Value:0.####}"));
                    }
                }
            }

            return Tuple.Create(hard, soft, rowsOut);
        }


        /// <summary>
        /// Utför publicering av draftade voländringar för ett valutapar.
        /// Mappar draft till VolPublishRow, anropar presenter.PublishAsync,
        /// visar en "Published OK"-notis, triggar refresh och stänger dialogen.
        /// </summary>
        private async Task HandlePublishAsync(
            string pair,
            DateTime tsUtc,
            List<VolDraftChange> changes,
            Form dialog,
            CancellationToken ct)
        {
            // 1) Bygg lista per tenor
            var rows = new Dictionary<string, VolPublishRow>();

            foreach (var change in changes)
            {
                if (!rows.TryGetValue(change.Tenor, out var pubRow))
                {
                    pubRow = new VolPublishRow
                    {
                        TenorCode = change.Tenor,
                        AtmMid = null,
                        AtmSpread = null,
                        AtmOffset = null,
                        Rr25Mid = null,
                        Rr10Mid = null,
                        Bf25Mid = null,
                        Bf10Mid = null
                    };
                    rows[change.Tenor] = pubRow;
                }

                // 2) Mappa Field → rätt fält i VolPublishRow
                switch (change.Field)
                {
                    case "ATM Mid":
                        pubRow.AtmMid = change.New;
                        break;

                    case "ATM Spread":
                        pubRow.AtmSpread = change.New;
                        break;

                    case "ATM Offset":
                        pubRow.AtmOffset = change.New;
                        break;

                    case "RR25 Mid":
                        pubRow.Rr25Mid = change.New;
                        break;

                    case "RR10 Mid":
                        pubRow.Rr10Mid = change.New;
                        break;

                    case "BF25 Mid":
                        pubRow.Bf25Mid = change.New;
                        break;

                    case "BF10 Mid":
                        pubRow.Bf10Mid = change.New;
                        break;
                }
            }

            // 3) Publicera via presentern
            var ok = await _presenter.PublishAsync(
                Environment.UserName,
                pair,
                tsUtc,
                rows.Values.ToList(),
                ct);

            if (!ok)
            {
                MessageBox.Show(
                    this,
                    "Publish misslyckades.",
                    "Volatility Manager",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            // 4) Visa "Published OK"
            MessageBox.Show(
                this,
                "Published OK",
                "Volatility Manager",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);


            // Rensa alla draft för paret
            if (_draftStore.ContainsKey(pair))
                _draftStore[pair].Clear();

            // 5) Refresh paret — force = true
            await _presenter.RefreshPairAndBindAsync(pair, true);

            // 6) Stäng dialogen
            dialog.Close();
        }


        /// <summary>
        /// Bygger publish-rader från draft för ett par.
        /// Säkerställer att AtmMid i raden alltid är "effective mid":
        /// - Non-anchored:   AtmMid = (draft.AtmMid om satt) annars grid/baseline ATM_mid.
        /// - Anchored:       AtmMid = AnchorMid + (draft/aktuellt) Offset,
        ///   där AnchorMid ≈ (baseline ATM_mid − baseline ATM_adj) per tenor.
        /// AtmSpread/Offset tas från draft om satt, annars aktuella cellvärden om vi tillåter fallback.
        /// RR/BF tas från draft om satta.
        /// </summary>
        private List<VolPublishRow> BuildPublishRowsFromDraft(string pair) // ERSÄTT
        {
            var list = new List<VolPublishRow>();
            if (string.IsNullOrWhiteSpace(pair)) return list;
            pair = pair.ToUpperInvariant();

            if (!_draftStore.TryGetValue(pair, out var perPair) || perPair == null || perPair.Count == 0)
                return list;

            var grid = GetActivePairGrid();
            var anchored = _presenter != null && _presenter.IsAnchoredPair(pair);
            if (grid == null) return list;

            foreach (var kv in perPair)
            {
                var tenor = kv.Key;
                var d = kv.Value;
                if (d == null) continue;

                // Hitta grid-raden och baseline
                DataGridViewRow row = null;
                foreach (DataGridViewRow r in grid.Rows)
                {
                    var t = r.Cells["Tenor"]?.Value?.ToString();
                    if (string.Equals(t, tenor, StringComparison.OrdinalIgnoreCase)) { row = r; break; }
                }
                if (row == null) continue;
                var baseMap = row.Tag as Dictionary<string, decimal?> ?? new Dictionary<string, decimal?>();

                // Effective Mid
                decimal? midEff = null;
                if (!anchored)
                {
                    midEff = d.AtmMid
                          ?? TryGetCellDecimal(row, "ATM_mid")
                          ?? (baseMap.TryGetValue("ATM_mid", out var m0) ? m0 : null);
                }
                else
                {
                    decimal? anchorMid = null;
                    if (baseMap.TryGetValue("ATM_mid", out var dbMid) && dbMid.HasValue &&
                        baseMap.TryGetValue("ATM_adj", out var dbOff) && dbOff.HasValue)
                    {
                        anchorMid = dbMid.Value - dbOff.Value;
                    }
                    var offset = d.AtmOffset
                              ?? TryGetCellDecimal(row, "ATM_adj")
                              ?? (baseMap.TryGetValue("ATM_adj", out var off0) ? off0 : null);

                    if (anchorMid.HasValue && offset.HasValue)
                        midEff = anchorMid.Value + offset.Value;
                }

                var rowOut = new VolPublishRow
                {
                    TenorCode = tenor,
                    AtmMid = midEff,                      // viktiga ändringen
                    AtmSpread = d.AtmSpread,                 // endast draftade fält skickas
                    AtmOffset = d.AtmOffset,
                    Rr25Mid = d.Rr25Mid,
                    Rr10Mid = d.Rr10Mid,
                    Bf25Mid = d.Bf25Mid,
                    Bf10Mid = d.Bf10Mid
                };

                // Skicka endast rader som faktiskt har något att publicera
                if (rowOut.AtmMid.HasValue || rowOut.AtmSpread.HasValue || rowOut.AtmOffset.HasValue ||
                    rowOut.Rr25Mid.HasValue || rowOut.Rr10Mid.HasValue || rowOut.Bf25Mid.HasValue || rowOut.Bf10Mid.HasValue)
                {
                    list.Add(rowOut);
                }
            }

            return list;
        }







        #endregion


        #region Inre typer

        /// <summary>
        /// Enkel behållare för UI-kontroller som hör till en underflik (pair-tab).
        /// Används för att slippa leta i Controls-samlingar vid uppdatering.
        /// </summary>
        private sealed class PairTabUi
        {
            /// <summary>Valutaparet som denna flik representerar (t.ex. "EUR/USD").</summary>
            public string PairSymbol { get; set; }

            public DataGridView Grid { get; set; }
            public BindingSource Binding { get; set; }

            public Label LabelSnapshot { get; set; }
            public Label LabelDelta { get; set; }
            public Label LabelPremiumAdj { get; set; }
            public Label LabelTs { get; set; }

            /// <summary>Statuslabel: "Cached" eller "Fresh @ hh:mm:ss".</summary>
            public Label LabelStatus { get; set; }
        }

        /// <summary>
        /// UI-behållare för en tile (ruta) i Tiles-vyn.
        /// </summary>
        private sealed class TileUi
        {
            public string PairSymbol { get; set; }
            public Panel Root { get; set; }
            public BindingSource Binding { get; set; }

            public DataGridView Grid { get; set; }              // NY: direkt referens till tile-grid
            public Label LabelTitle { get; set; }
            public Label LabelSnapshot { get; set; }
            public Label LabelTs { get; set; }
            public Label LabelStatus { get; set; }
        }

        /// <summary>
        /// Global hotkey-lyssnare för F5/Ctrl+F5.
        /// Mer tolerant: triggar om viewen har fokus ELLER om viewens top-level Form är aktiv.
        /// </summary>
        private sealed class HotkeysMessageFilter : IMessageFilter
        {
            private readonly VolManagerView _owner;

            public HotkeysMessageFilter(VolManagerView owner) => _owner = owner;

            public bool PreFilterMessage(ref Message m)
            {
                const int WM_KEYDOWN = 0x0100;
                if (m.Msg != WM_KEYDOWN) return false;

                var keyCode = (Keys)(int)m.WParam;
                if (keyCode != Keys.F5) return false;

                // Tolerant fokus/ägarkoll: antingen har owner fokus,
                // eller så är ownerns top-level form aktiv.
                var ownerHasFocus = _owner != null && _owner.Visible && _owner.ContainsFocus;
                var activeForm = Form.ActiveForm;
                var ownerForm = _owner?.TopLevelControl as Form;
                var sameTopForm = (activeForm != null && ownerForm != null && ReferenceEquals(activeForm, ownerForm));

                if (!ownerHasFocus && !sameTopForm)
                    return false;

                var force = (Control.ModifierKeys & Keys.Control) == Keys.Control;
                _owner.BeginInvoke(new Action(() => _owner.TriggerRefreshFromHotkey(force)));
                return true;
            }
        }



        #endregion

        #region Layout – Tiles (cards)

        /// <summary>
        /// Startar drag för en tile (flytta i FlowLayoutPanel).
        /// </summary>
        private void OnTileMouseDown(object sender, MouseEventArgs e)
        {
            var tile = sender as Panel;
            if (tile == null || e.Button != MouseButtons.Left) return;
            tile.DoDragDrop(tile, DragDropEffects.Move);
        }

        /// <summary>
        /// Tillåter Move i TilesPanel när en tile dras.
        /// </summary>
        private void OnTilesPanelDragOver(object sender, DragEventArgs e)
        {
            e.Effect = e.Data.GetDataPresent(typeof(Panel)) ? DragDropEffects.Move : DragDropEffects.None;
        }

        /// <summary>
        /// Tillåter Move-drop när en tile dras in över Tiles-panelen.
        /// </summary>
        private void OnTilesPanelDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = e.Data.GetDataPresent(typeof(Panel))
                ? DragDropEffects.Move
                : DragDropEffects.None;
        }

        /// <summary>
        /// Släpper en tile och flyttar den till ny position. Uppdaterar även Pinned-ordningen och sparar state.
        /// </summary>
        private void OnTilesPanelDragDrop(object sender, DragEventArgs e)
        {
            var panel = sender as FlowLayoutPanel;
            var tile = e.Data.GetData(typeof(Panel)) as Panel;
            if (panel == null || tile == null) return;

            var client = panel.PointToClient(new Point(e.X, e.Y));
            var target = panel.GetChildAtPoint(client);
            var newIndex = (target != null)
                ? panel.Controls.GetChildIndex(target, false)
                : panel.Controls.Count - 1;

            panel.Controls.SetChildIndex(tile, newIndex);

            // Synka Pinned-listans ordning till tile-ordningen och persist
            var lstPinned = FindChild<ListBox>(this, "LstPinned");
            if (lstPinned != null)
            {
                var order = panel.Controls
                    .OfType<Panel>()
                    .Select(p => (p.Tag as TileUi)?.PairSymbol)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();

                lstPinned.Items.Clear();
                foreach (var s in order) lstPinned.Items.Add(s);
                SaveUiStateFromControls();
            }
        }


        /// <summary>
        /// Binder/uppdaterar ett tile-kort för paret. Tiles är data-bundna:
        /// vi sätter helt enkelt BindingSource.DataSource = rows (eller tom lista)
        /// och låter kolumnerna (ATM-only/Compact) vara förskapade.
        /// Visar även "Cached"/"Fresh @ hh:mm:ss".
        /// </summary>
        public void UpdateTile(string pair, DateTime tsUtc, IList<VolSurfaceRow> rows, bool fromCache)
        {
            if (_tilesPanel == null) throw new InvalidOperationException("TilesPanel saknas i vyn.");
            if (string.IsNullOrWhiteSpace(pair)) return;

            // Hitta/skap tile
            Panel tile = null;
            TileUi ui = null;
            foreach (Control c in _tilesPanel.Controls)
            {
                var t = c as Panel;
                var tui = t?.Tag as TileUi;
                if (tui != null && string.Equals(tui.PairSymbol, pair, StringComparison.OrdinalIgnoreCase))
                { tile = t; ui = tui; break; }
            }
            if (tile == null || ui == null)
            {
                tile = CreateTileForPair(pair);
                ui = tile.Tag as TileUi;
                _tilesPanel.Controls.Add(tile);
                ApplyTileColumnsMode(tile); // säkerställ aktuellt kolumnläge
            }

            // Header
            if (ui.LabelTitle != null) ui.LabelTitle.Text = pair.ToUpperInvariant();
            if (ui.LabelTs != null) ui.LabelTs.Text = $"TS: {FormatTimeUtc(tsUtc)}";
            if (ui.LabelStatus != null) ui.LabelStatus.Text = fromCache ? "Cached" : $"Fresh @ {DateTime.Now:HH:mm:ss}";

            // Overlays
            HideOverlay(tile, "ErrorOverlay");
            HideOverlay(tile, "NoDataOverlay");

            // Säkerställ binding-objekt
            var grid = ui.Grid ?? tile.Controls.OfType<DataGridView>().FirstOrDefault();
            var bs = ui.Binding ?? grid?.DataSource as BindingSource;
            if (grid == null)
                return;

            if (bs == null)
            {
                bs = new BindingSource();
                grid.DataSource = bs;
                ui.Binding = bs;
            }

            // Håll kolumnlayouten i synk med valt TileColumnsMode
            EnsureTileColumnsByMode(grid, _tileColumnsMode);

            // Data-bindning
            var safeRows = (rows ?? Array.Empty<VolSurfaceRow>()).ToList();
            bs.DataSource = safeRows;

            // "No data" overlay om listan är tom
            if (safeRows.Count == 0)
            {
                var overlay = GetOrCreateMessageOverlay(tile, "NoDataOverlay", "No data", Color.Gray);
                overlay.Visible = true;
                overlay.BringToFront();
            }
        }



        #endregion
    }
}
