using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using FX.Core.Domain;

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
        /// Läser state från presenter och applicerar på UI (Pinned/Recent, vy-läge, Tiles-kolumnläge).
        /// Kallas efter att layout och kontroller är skapade.
        /// </summary>
        private void TryApplyLoadedState()
        {
            if (_presenter == null) return;

            var state = _presenter.LoadUiState();

            // Fyll Pinned/Recent
            var lstPinned = FindChild<ListBox>(this, "LstPinned");
            var lstRecent = FindChild<ListBox>(this, "LstRecent");

            if (lstPinned != null)
            {
                lstPinned.Items.Clear();
                foreach (var p in state.Pinned) lstPinned.Items.Add(p);
            }
            if (lstRecent != null)
            {
                lstRecent.Items.Clear();
                foreach (var p in state.Recent) lstRecent.Items.Add(p);
            }

            // Tiles-kolumnläge
            _tileColumnsMode = string.Equals(state.TileColumns, "AtmOnly", StringComparison.OrdinalIgnoreCase)
                ? TileColumnsMode.AtmOnly
                : TileColumnsMode.Compact;

            var rbAtmOnly = FindChild<RadioButton>(this, "RbTileAtmOnly");
            var rbCompact = FindChild<RadioButton>(this, "RbTileCompact");
            if (rbAtmOnly != null) rbAtmOnly.Checked = (_tileColumnsMode == TileColumnsMode.AtmOnly);
            if (rbCompact != null) rbCompact.Checked = (_tileColumnsMode == TileColumnsMode.Compact);

            // Skapa flikar för alla pinnade (utan force)
            if (lstPinned != null)
            {
                foreach (var it in lstPinned.Items.Cast<object>())
                {
                    var pair = it as string;
                    if (!string.IsNullOrWhiteSpace(pair))
                        PinPair(pair);
                }
            }

            // Växla vy
            if (string.Equals(state.View, "Tiles", StringComparison.OrdinalIgnoreCase))
                ShowTilesView(forceInitialLoad: false);
            else
                ShowTabsView();

            // Spara normaliserat läge direkt (valfritt)
            SaveUiStateFromControls();
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
        /// Binder ytrader i Tabs för ett visst par. Lägger även en baseline per rad (Before),
        /// applicerar draft-värden, och uppdaterar header + edits-räknare.
        /// För icke-ankrade par beräknas ATM_adj(Before) = ATM_ask − ATM_bid.
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

            foreach (var r in rows)
            {
                var ri = grid.Rows.Add();
                var row = grid.Rows[ri];

                // 0–1: Tenor + nominal days
                row.Cells[0].Value = r.TenorCode ?? "";
                row.Cells[1].Value = r.TenorDaysNominal.HasValue ? (object)r.TenorDaysNominal.Value : "";

                // 2–4: ATM Bid / Ask / Mid
                row.Cells[2].Value = r.AtmBid;
                row.Cells[3].Value = r.AtmAsk;
                row.Cells[4].Value = r.AtmMid;

                // 5–8: RR25, RR10, BF25, BF10 (mid)
                row.Cells[5].Value = r.Rr25Mid;
                row.Cells[6].Value = r.Rr10Mid;
                row.Cells[7].Value = r.Bf25Mid;
                row.Cells[8].Value = r.Bf10Mid;

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

        #region Public API – Kommandon (vy)

        /// <summary>
        /// Tar bort alla draft-ändringar för aktivt par.
        /// </summary>
        public void DiscardAllDraftForActivePair()
        {
            var page = _pairTabs?.SelectedTab;
            var pair = page?.Text?.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(pair)) return;

            if (_draftStore.ContainsKey(pair))
                _draftStore.Remove(pair);

            var grid = FindChild<DataGridView>(page);
            grid?.Invalidate(); // ta bort gul markering
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
        /// Öppnar Review-dialogen (dry-run) för aktiv par-flik. Visar diff med Before→After.
        /// </summary>
        private void ShowReviewDialogForActivePair()
        {
            var pair = GetActivePairFromTabs();
            if (string.IsNullOrWhiteSpace(pair))
            {
                MessageBox.Show(this, "Ingen aktiv par-flik.", "Review", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var grid = EnsurePairTabGrid(pair);
            if (grid == null)
            {
                MessageBox.Show(this, $"Ingen grid för {pair}.", "Review", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var changes = BuildDraftDiffForPair(pair, grid);
            var (hardCount, softCount) = CountRuleFlags(grid);

            if (changes == null || changes.Count == 0)
            {
                MessageBox.Show(this, "Inga draftade ändringar att reviewa.", "Volatility Manager",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dlg = BuildReviewDialog(pair, changes, hardCount, softCount))
            {
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.ShowDialog(this);
            }
        }



        /// <summary>
        /// Skapar en diff-lista (Tenor, Field, Old, New) genom att jämföra draftvärden
        /// mot DB-baseline som snapshot:ats i BindPairSurface (radens row.Tag).
        /// </summary>
        private List<VolDraftChange> BuildDraftDiffForPair(string pair, DataGridView grid)
        {
            var list = new List<VolDraftChange>();
            if (string.IsNullOrWhiteSpace(pair) || grid == null) return list;

            if (!_draftStore.TryGetValue(pair, out var perPair) || perPair == null || perPair.Count == 0)
                return list;

            foreach (var kv in perPair) // kv.Key = Tenor
            {
                var tenor = kv.Key;
                var d = kv.Value;
                if (d == null) continue;

                // Hitta rad för aktuellt tenor
                DataGridViewRow row = null;
                foreach (DataGridViewRow r in grid.Rows)
                {
                    var t = r.Cells["Tenor"]?.Value?.ToString();
                    if (string.Equals(t, tenor, StringComparison.OrdinalIgnoreCase)) { row = r; break; }
                }
                if (row == null) continue;

                // Baseline (“Before”) som sattes i BindPairSurface via row.Tag
                var baseMap = row.Tag as Dictionary<string, decimal?> ?? new Dictionary<string, decimal?>();

                decimal? GetOld(string fieldKey)
                {
                    switch (fieldKey)
                    {
                        case "RR25 Mid": return baseMap.TryGetValue("RR25", out var rr25) ? rr25 : (decimal?)null;
                        case "RR10 Mid": return baseMap.TryGetValue("RR10", out var rr10) ? rr10 : (decimal?)null;
                        case "BF25 Mid": return baseMap.TryGetValue("BF25", out var bf25) ? bf25 : (decimal?)null;
                        case "BF10 Mid": return baseMap.TryGetValue("BF10", out var bf10) ? bf10 : (decimal?)null;

                        // NYTT: hämta ATM Spread/Offset “Before” från baseline-nyckeln "ATM_adj".
                        // För icke-ankrat par är detta Ask−Bid; för ankrat kan den vara null (visas som blankt).
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

                addIfChanged("ATM Spread", d.AtmSpread);
                addIfChanged("ATM Offset", d.AtmOffset);
                addIfChanged("RR25 Mid", d.Rr25Mid);
                addIfChanged("RR10 Mid", d.Rr10Mid);
                addIfChanged("BF25 Mid", d.Bf25Mid);
                addIfChanged("BF10 Mid", d.Bf10Mid);
            }

            // Sortera i samma ordning som tenor-etiketterna (alfabetiskt räcker här)
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
        /// Bygger en modal Review-dialog (dry-run) som visar Tenor/Field och Before→After,
        /// samt mjuka varningar. Knappen Publish är avsiktligt disabled i 6c.
        /// Metoden är tolerant mot olika egenskapsnamn i VolDraftChange (Before/After/Warning
        /// eller Old/New/SoftWarn etc).
        /// </summary>
        private Form BuildReviewDialog(string pair, List<VolDraftChange> changes, int hardCount, int softCount)
        {
            var dlg = new Form
            {
                Text = $"Review – {pair} (Dry-run)",
                Width = 880,
                Height = 520,
                MinimizeBox = false,
                MaximizeBox = false,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                ShowIcon = false,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.CenterParent
            };

            var info = new Label
            {
                Dock = DockStyle.Top,
                Height = 32,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 0, 0),
                Text = $"Draft changes: {changes?.Count ?? 0}   |   Hard errors: {hardCount}   |   Soft warnings: {softCount}"
            };

            var grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = Color.White,
                GridColor = Color.Gainsboro,
                EnableHeadersVisualStyles = false
            };

            // Pricer-lik look
            grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(242, 246, 251);
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.Black;
            grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 8.25f, FontStyle.Bold);
            grid.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            grid.Font = new Font("Segoe UI", 8.25f, FontStyle.Regular);
            grid.RowsDefaultCellStyle.BackColor = Color.White;
            grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(250, 251, 253);

            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Tenor", HeaderText = "Tenor", FillWeight = 90 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Field", HeaderText = "Field", FillWeight = 130 });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Old",
                HeaderText = "Before",
                FillWeight = 90,
                DefaultCellStyle = new DataGridViewCellStyle { Format = "0.0000" }
            });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "New",
                HeaderText = "After",
                FillWeight = 90,
                DefaultCellStyle = new DataGridViewCellStyle { Format = "0.0000" }
            });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Warn", HeaderText = "Warnings (soft)", FillWeight = 230 });

            // Lokal, enkel mappare som tål olika property-namn utan att kräva ändringar i övrig kod.
            object ReadProp(object o, params string[] names)
            {
                if (o == null) return null;
                var t = o.GetType();
                foreach (var n in names)
                {
                    var p = t.GetProperty(n, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.IgnoreCase);
                    if (p != null) return p.GetValue(o);
                }
                return null;
            }
            string ReadString(object o, params string[] names) => ReadProp(o, names) as string;
            decimal? ReadDec(object o, params string[] names)
            {
                var v = ReadProp(o, names);
                if (v == null) return null;
                if (v is decimal d) return d;
                if (v is double dbl) return (decimal)dbl;
                if (v is float fl) return (decimal)fl;
                if (v is string s && decimal.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dp)) return dp;
                return null;
            }

            if (changes != null)
            {
                foreach (var c in changes)
                {
                    var tenor = ReadString(c, "TenorCode", "Tenor") ?? "";
                    var field = ReadString(c, "Field") ?? "";
                    var before = ReadDec(c, "Before", "Old", "OldValue");
                    var after = ReadDec(c, "After", "New", "NewValue");
                    var warn = ReadString(c, "Warning", "Warn", "SoftWarning") ?? "";

                    grid.Rows.Add(tenor, field, before, after, warn);
                }
            }

            // Bottenpanel med Close + (disabled) Publish i 6c
            var pnlButtons = new Panel { Dock = DockStyle.Bottom, Height = 44 };

            var btnClose = new Button
            {
                Name = "BtnDialogClose",
                Text = "Close",
                Size = new Size(90, 28),
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom
            };
            btnClose.Click += (s, e) => dlg.Close();

            var btnPublish = new Button
            {
                Name = "BtnDialogPublish",
                Text = "Publish",
                Enabled = false, // 6c: dry-run – aktiveras i 6d
                Size = new Size(90, 28),
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom
            };
            btnPublish.Click += OnDialogPublishClick;

            pnlButtons.Controls.Add(btnClose);
            pnlButtons.Controls.Add(btnPublish);
            pnlButtons.Resize += (s, e) =>
            {
                btnClose.Left = pnlButtons.ClientSize.Width - btnClose.Width - 12;
                btnClose.Top = (pnlButtons.ClientSize.Height - btnClose.Height) / 2;
                btnPublish.Left = btnClose.Left - btnPublish.Width - 8;
                btnPublish.Top = btnClose.Top;
            };

            dlg.Controls.Add(grid);
            dlg.Controls.Add(pnlButtons);
            dlg.Controls.Add(info);

            return dlg;
        }






        /// <summary>
        /// Ansluter validerings- och edit-händelser för ett par-grid.
        /// </summary>
        private void HookValidationEvents(DataGridView grid) // NY
        {
            if (grid == null) return;
            grid.CellValidating -= PairGrid_CellValidating;
            grid.CellEndEdit -= PairGrid_CellEndEdit;
            grid.DataError -= PairGrid_DataError;

            grid.CellValidating += PairGrid_CellValidating;
            grid.CellEndEdit += PairGrid_CellEndEdit;
            grid.DataError += PairGrid_DataError;
        }

        /// <summary>
        /// Cellvalidering: parsar tal & kör hårda kolumnregler.
        /// Vi CANCEL:ar inte, så ENTER fungerar. Vid ogiltigt tal markerar vi revert.
        /// Vid giltigt värde rensas ev. tidigare felindikatorer omedelbart.
        /// Visar röd error-badge direkt genom explicit Invalidate av cellen.
        /// </summary>
        private void PairGrid_CellValidating(object sender, DataGridViewCellValidatingEventArgs e)
        {
            var grid = sender as DataGridView;
            if (grid == null) return;

            var col = grid.Columns[e.ColumnIndex];
            var name = col?.Name ?? "";
            if (name == "Tenor" || name == "DaysNom") return; // ej numeriska

            var row = grid.Rows[e.RowIndex];
            var cell = row.Cells[e.ColumnIndex];

            string text = (e.FormattedValue ?? "").ToString().Trim();
            if (text.Length == 0)
            {
                // Tomt => rensa cellfel. Draft rensas i EndEdit.
                cell.ErrorText = null;
                SetCellRevertFlag(cell, false);
                // Visa direkt
                grid.InvalidateCell(e.ColumnIndex, e.RowIndex);
                grid.BeginInvoke(new Action(() => grid.InvalidateCell(e.ColumnIndex, e.RowIndex)));
                return;
            }

            decimal value;
            if (!TryParseFlexibleDecimal(text, out value))
            {
                // Ogiltigt tal: markera revert (återställ i EndEdit), visa cellfel men blockera inte ENTER.
                cell.ErrorText = "Ogiltigt tal.";
                SetCellRevertFlag(cell, true);
                // Visa direkt
                grid.InvalidateCell(e.ColumnIndex, e.RowIndex);
                grid.BeginInvoke(new Action(() => grid.InvalidateCell(e.ColumnIndex, e.RowIndex)));
                return;
            }

            // Numeric OK => rensa ev. gamla fel
            cell.ErrorText = null;
            SetCellRevertFlag(cell, false);

            // ATM Spread/Offset regler
            if (string.Equals(name, "ATM_adj", StringComparison.OrdinalIgnoreCase))
            {
                var pair = GetActivePairFor(grid);
                var anchored = _presenter != null && _presenter.IsAnchoredPair(pair);

                if (!anchored)
                {
                    // Spread hårt: >= 0 och (om ATM_mid finns) Spread <= 2*ATM_mid (så att Bid >= 0)
                    if (value < 0m)
                        cell.ErrorText = "ATM Spread kan inte vara negativ.";
                    else
                    {
                        var mid = TryGetCellDecimal(row, "ATM_mid");
                        if (mid.HasValue && value > 2m * mid.Value)
                            cell.ErrorText = $"ATM Spread för stor: Bid blir negativ (kräver ≤ {2m * mid.Value:0.0000}).";
                    }
                }
                // Anchored: inga hårda absoluta limits i 6b (preview/ankare kommer senare).
            }
            else if (name == "RR25_mid" || name == "RR10_mid")
            {
                if (Math.Abs(value) > 20m)
                    cell.ErrorText = "|RR| för stort (> 20).";
            }
            else if (name == "BF25_mid" || name == "BF10_mid")
            {
                if (value < 0m) cell.ErrorText = "Butterfly kan inte vara negativ.";
                else if (value > 25m) cell.ErrorText = "Butterfly för stor (> 25).";
            }

            // Se till att den röda badgen uppdateras direkt
            grid.InvalidateCell(e.ColumnIndex, e.RowIndex);
            grid.BeginInvoke(new Action(() => grid.InvalidateCell(e.ColumnIndex, e.RowIndex)));
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


        /// <summary>
        /// Efter commit: hantera ogiltigt tal (revert), skriv draft (endast vid giltigt tal),
        /// kör radregler och sätt soft/hard meddelanden på relevanta celler.
        /// Säkerställer också att cellens ErrorText nollställs vid giltig commit.
        /// </summary>
        private void PairGrid_CellEndEdit(object sender, DataGridViewCellEventArgs e)
        {
            var grid = sender as DataGridView;
            if (grid == null) return;

            var row = grid.Rows[e.RowIndex];
            var cell = row.Cells[e.ColumnIndex];
            var col = grid.Columns[e.ColumnIndex];
            var name = col?.Name ?? "";

            // 1) Ogiltigt tal? Revert till original utan att skapa draft.
            var tag = cell.Tag as Tuple<string, bool>;
            var revert = tag != null && tag.Item2;
            if (revert)
            {
                cell.Value = tag.Item1;          // återställ
                cell.ErrorText = "Ogiltigt tal. Värdet återställdes.";
                // Ingen draft vid ogiltigt tal
                UpdateDraftEditsCounter(GetActivePairFor(grid));
                grid.InvalidateCell(cell);
                return;
            }

            // 2) Giltig commit → rensa cell/rad-fel
            cell.ErrorText = null;
            row.ErrorText = null;

            // 3) Draft-skrivning endast för numeriskt värde
            decimal? newVal = TryGetCellDecimal(row, name); // efter commit ligger nya texten i Value
            var pair = GetActivePairFor(grid);
            var tenor = GetTenorFromRow(row);
            if (!string.IsNullOrEmpty(pair) && !string.IsNullOrEmpty(tenor))
            {
                var anchored = _presenter != null && _presenter.IsAnchoredPair(pair);
                if (string.Equals(name, "ATM_adj", StringComparison.OrdinalIgnoreCase))
                {
                    if (anchored) UpsertDraftValue(pair, tenor, "AtmOffset", newVal);
                    else UpsertDraftValue(pair, tenor, "AtmSpread", newVal);
                }
                else
                {
                    if (name == "RR25_mid") UpsertDraftValue(pair, tenor, "Rr25Mid", newVal);
                    if (name == "RR10_mid") UpsertDraftValue(pair, tenor, "Rr10Mid", newVal);
                    if (name == "BF25_mid") UpsertDraftValue(pair, tenor, "Bf25Mid", newVal);
                    if (name == "BF10_mid") UpsertDraftValue(pair, tenor, "Bf10Mid", newVal);
                }
            }

            // 4) Radregler – endast relevanta celler får soft-warn tooltip
            row.ErrorText = null;
            var hard = new List<string>();
            var soft = new List<string>();
            EvaluateRowRules_Scoped(row, _presenter != null && _presenter.IsAnchoredPair(pair), hard, soft);

            if (hard.Count > 0)
                row.ErrorText = string.Join("  ", hard);

            // rensa tooltips
            foreach (var n in new[] { "RR25_mid", "RR10_mid", "BF25_mid", "BF10_mid", "ATM_adj" })
            {
                var c = row.Cells[n];
                if (c != null) c.ToolTipText = null;
            }
            foreach (var kv in _lastSoftTargets)
            {
                var c = row.Cells[kv];
                if (c != null) c.ToolTipText = string.Join("  ", soft);
            }

            UpdateDraftEditsCounter(pair);
            grid.InvalidateRow(e.RowIndex);
        }


        // Håller senaste soft-targets för tooltips per Evaluate-körning
        private static readonly string[] _rrTargets = new[] { "RR25_mid", "RR10_mid" };
        private static readonly string[] _bfTargets = new[] { "BF25_mid", "BF10_mid" };
        private List<string> _lastSoftTargets = new List<string>();

        /// <summary>
        /// Hårda & mjuka regler på radnivå, men returnerar även vilka kolumner som är "berörda"
        /// för att begränsa soft-warn tooltips till rätt fält.
        /// </summary>
        private void EvaluateRowRules_Scoped(DataGridViewRow row, bool anchored, List<string> hard, List<string> soft)
        {
            _lastSoftTargets.Clear();

            decimal? rr25 = TryGetCellDecimal(row, "RR25_mid");
            decimal? rr10 = TryGetCellDecimal(row, "RR10_mid");
            decimal? bf25 = TryGetCellDecimal(row, "BF25_mid");
            decimal? bf10 = TryGetCellDecimal(row, "BF10_mid");
            decimal? adj = TryGetCellDecimal(row, "ATM_adj");
            decimal? mid = TryGetCellDecimal(row, "ATM_mid");

            // ATM Spread (icke-ankrat): redan cellfel i Validating om > 2*mid; ingen extra rad-hard här.

            // RR: |RR10| ≥ |RR25| och samma tecken
            if (rr25.HasValue && rr10.HasValue)
            {
                if (Math.Sign(rr25.Value) != Math.Sign(rr10.Value))
                    hard.Add("RR10 och RR25 har olika tecken.");
                if (Math.Abs(rr10.Value) + 1e-12m < Math.Abs(rr25.Value))
                    hard.Add("|RR10| måste vara ≥ |RR25|.");

                // Soft: ratio inom [1.0, 2.5]
                var denom = Math.Abs(rr25.Value);
                if (denom > 1e-12m)
                {
                    var k = Math.Abs(rr10.Value) / denom;
                    if (k < 1.0m || k > 2.5m)
                    {
                        soft.Add($"RR-ratio (|RR10|/|RR25| = {k:0.00}) utanför [1.0, 2.5].");
                        _lastSoftTargets.AddRange(_rrTargets);
                    }
                }
            }

            // BF: BF10 ≥ BF25 ≥ 0 (BF25>=0 checkades i Validating; vi säkrar relationen här)
            if (bf10.HasValue && bf25.HasValue)
            {
                if (bf10.Value + 1e-12m < bf25.Value)
                    hard.Add("BF10 måste vara ≥ BF25.");

                if (bf25.Value > 1e-12m)
                {
                    var m = bf10.Value / bf25.Value;
                    if (m < 1.0m || m > 3.0m)
                    {
                        soft.Add($"BF-ratio (BF10/BF25 = {m:0.00}) utanför [1.0, 3.0].");
                        _lastSoftTargets.AddRange(_bfTargets);
                    }
                }
            }

            // ATM Offset – soft varning (frivillig): om Mid finns och adj är väldigt negativ
            if (anchored && adj.HasValue && mid.HasValue && (mid.Value + adj.Value) < 0m)
            {
                // Vi kan inte räkna korrekt anchored-mid här utan ankar-ATM, så vi nöjer oss med en hint:
                soft.Add("Offset verkar göra Mid negativ (kontrollera ankare i preview).");
                _lastSoftTargets.Add("ATM_adj");
            }
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
        private void EvaluateRowRules(DataGridViewRow row, bool anchored, List<string> hard, List<string> soft) // NY
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

        /// <summary> Försöker läsa en decimal från en cells Value (invariant kultur). </summary>
        private static decimal? TryGetCellDecimal(DataGridViewRow row, string name) // NY
        {
            var cell = row.Cells[name];
            if (cell == null) return null;
            var obj = cell.Value;
            if (obj == null) return null;
            if (obj is decimal d) return d;
            if (obj is double db) return (decimal)db;
            var s = obj.ToString();
            decimal val;
            if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out val))
                return val;
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

            // OBS: Ingen TryApplyLoadedState här längre – workspace applicerar per-session-state.
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
        private string GetActivePairSymbol() // NY
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
        /// Positionerar LblEdits och knapparna Review/Discard mot högerkanten:
        /// [Review][mellanrum][Discard] och LblEdits strax till vänster.
        /// </summary>
        private void LayoutHeaderRightControls(Control headerHost)
        {
            if (headerHost == null) return;

            var lblEdits = FindChild<Label>(headerHost, "LblEdits");
            var btnDiscard = FindChild<Button>(headerHost, "BtnDiscardDraft");
            var btnReview = FindChild<Button>(headerHost, "BtnReviewDraft");
            if (lblEdits == null || btnDiscard == null || btnReview == null) return;

            int w = headerHost.ClientSize.Width;

            // Höger till vänster: Review, Discard, LblEdits
            btnReview.Left = w - btnReview.Width - 4;
            btnDiscard.Left = btnReview.Left - btnDiscard.Width - 6;
            lblEdits.Left = btnDiscard.Left - lblEdits.PreferredWidth - 12;
        }



        /// <summary>
        /// Hämtar aktuellt par från PairTabs (flikens Text).
        /// </summary>
        private string GetActivePairFromTabs()
        {
            if (_pairTabs == null || _pairTabs.SelectedTab == null) return null;
            return _pairTabs.SelectedTab.Text;
        }


        /// <summary>
        /// Räknar antal draft-fält för ett par och uppdaterar "Edits: n" i headern.
        /// Enable/Disable på Discard/Review och om-layout efter textbyte.
        /// </summary>
        private void UpdateDraftEditsCounter(string pair)
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
                    if (kv == null) continue;
                    if (kv.Rr25Mid.HasValue) n++;
                    if (kv.Bf25Mid.HasValue) n++;
                    if (kv.Rr10Mid.HasValue) n++;
                    if (kv.Bf10Mid.HasValue) n++;
                    if (kv.AtmSpread.HasValue) n++;
                    if (kv.AtmOffset.HasValue) n++;
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
        /// Applicerar utkast/draft-värden (RR/BF + ATM Spread/Offset) på en redan bunden grid för ett visst par.
        /// Anropas direkt efter att raderna lagts in i BindPairSurface(...).
        /// </summary>
        private void ApplyDraftToGrid(string pair, DataGridView grid)
        {
            if (grid == null || string.IsNullOrWhiteSpace(pair)) return;

            if (!_draftStore.TryGetValue(pair, out var perPair) || perPair == null || perPair.Count == 0)
                return;

            DataGridViewColumn ColByHeader(string header) =>
                grid.Columns.Cast<DataGridViewColumn>()
                    .FirstOrDefault(c => string.Equals(c.HeaderText, header, StringComparison.OrdinalIgnoreCase));

            var cRR25 = ColByHeader("RR25 Mid");
            var cRR10 = ColByHeader("RR10 Mid");
            var cBF25 = ColByHeader("BF25 Mid");
            var cBF10 = ColByHeader("BF10 Mid");

            var cATMAdj = grid.Columns.Cast<DataGridViewColumn>()
                .FirstOrDefault(c => string.Equals(c.Name, "ATM_adj", StringComparison.OrdinalIgnoreCase));

            // Vilken header har ATM_adj just nu?
            var atmAdjHeader = cATMAdj?.HeaderText?.Trim();

            foreach (DataGridViewRow row in grid.Rows)
            {
                var tenor = GetTenorFromRow(row);
                if (string.IsNullOrEmpty(tenor)) continue;
                if (!perPair.TryGetValue(tenor, out var d) || d == null) continue;

                void SetIf(DataGridViewColumn col, decimal? val)
                {
                    if (col == null || !val.HasValue) return;
                    row.Cells[col.Index].Value = val.Value; // 4dp-formaten sköts redan av kolumnen
                }

                SetIf(cRR25, d.Rr25Mid);
                SetIf(cRR10, d.Rr10Mid);
                SetIf(cBF25, d.Bf25Mid);
                SetIf(cBF10, d.Bf10Mid);

                // ATM Spread / Offset
                if (cATMAdj != null)
                {
                    if (string.Equals(atmAdjHeader, "ATM Spread", StringComparison.OrdinalIgnoreCase))
                        SetIf(cATMAdj, d.AtmSpread);
                    else if (string.Equals(atmAdjHeader, "ATM Offset", StringComparison.OrdinalIgnoreCase))
                        SetIf(cATMAdj, d.AtmOffset);
                }
            }

            grid.Invalidate(); // trigga omritning så gul markering syns ihop med värdena
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
        /// Öppnar Review-dialogen för aktivt par (6c). 
        /// Bygger diff från det aktiva parets grid, och lägger till en inaktiv "Publish"-knapp (dry-run).
        /// </summary>
        private void OnReviewDraftClick(object sender, EventArgs e)
        {
            // 1) Hämta aktivt par + dess grid
            var pair = GetActivePairSymbol();
            if (string.IsNullOrWhiteSpace(pair))
                return;

            var grid = GetActivePairGrid();
            if (grid == null)
            {
                MessageBox.Show(this, "Kunde inte hitta grid för aktivt par.", "Volatility Manager",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 2) Bygg diff (din befintliga helper kräver grid)
            var rows = BuildDraftDiffForPair(pair, grid);
            if (rows == null || rows.Count == 0)
            {
                MessageBox.Show(this, "Inga draftade ändringar att reviewa.", "Volatility Manager",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 3) Hard/soft-counts – minimal variant (0/0). 
            //    Vill du visa faktiska counts kan vi knyta mot dina valideringsflaggor i 6d.
            int hardCount = 0;
            int warnCount = 0;

            // 4) Bygg och visa dialogen (din dialog tar pair, rows, hardCount, warnCount)
            var dlg = BuildReviewDialog(pair, rows, hardCount, warnCount);
            if (dlg == null) return;

            // 5) Lägg till en "Publish"-knapp (disabled i 6c – aktiveras i 6d)
            var btnPublish = new Button
            {
                Name = "BtnDialogPublish",
                Text = "Publish",
                Enabled = false,                      // 6c: dry-run, ingen DB-skriv ännu
                Size = new System.Drawing.Size(90, 28),
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom
            };

            // Försök placera till vänster om ev. Close/OK-knapp om den finns, annars nere till höger.
            Control closeBtn = null;
            foreach (Control c in dlg.Controls)
            {
                if (c is Button && (string.Equals(c.Text, "Stäng", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(c.Text, "Close", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(c.Name, "BtnDialogClose", StringComparison.OrdinalIgnoreCase)))
                {
                    closeBtn = c;
                    break;
                }
            }

            if (closeBtn != null)
            {
                btnPublish.Left = Math.Max(8, closeBtn.Left - btnPublish.Width - 8);
                btnPublish.Top = closeBtn.Top;
            }
            else
            {
                btnPublish.Left = dlg.ClientSize.Width - btnPublish.Width - 16;
                btnPublish.Top = dlg.ClientSize.Height - btnPublish.Height - 16;
                dlg.Resize += (s, ev) =>
                {
                    btnPublish.Left = dlg.ClientSize.Width - btnPublish.Width - 16;
                    btnPublish.Top = dlg.ClientSize.Height - btnPublish.Height - 16;
                };
            }

            btnPublish.Click += OnDialogPublishClick;
            dlg.Controls.Add(btnPublish);

            dlg.ShowDialog(this);
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
        /// Slutför cellredigering: parse sv/eng decimal, skriv till draft (RR/BF + ATM Spread/Offset),
        /// och se till att visuellt värdet är numeriskt (formateras 4 dp).
        /// </summary>
        private void OnPairGridCellEndEdit(object sender, DataGridViewCellEventArgs e)
        {
            var grid = sender as DataGridView;
            if (grid == null || e.RowIndex < 0 || e.ColumnIndex < 0) return;

            var pair = GetPairKeyFromGrid(grid);
            var row = grid.Rows[e.RowIndex];
            var tenor = GetTenorFromRow(row);
            if (string.IsNullOrEmpty(pair) || string.IsNullOrEmpty(tenor)) return;

            if (!_draftStore.TryGetValue(pair, out var perPair))
            {
                perPair = new Dictionary<string, VolDraftRow>(StringComparer.OrdinalIgnoreCase);
                _draftStore[pair] = perPair;
            }
            if (!perPair.TryGetValue(tenor, out var d))
            {
                d = new VolDraftRow { TenorCode = tenor };
                perPair[tenor] = d;
            }

            var header = grid.Columns[e.ColumnIndex].HeaderText?.Trim();
            var cell = row.Cells[e.ColumnIndex];
            var txt = (cell.Value ?? "").ToString().Trim();

            // Tomt → null
            if (string.IsNullOrEmpty(txt))
            {
                switch (header)
                {
                    case "RR25 Mid": d.Rr25Mid = null; break;
                    case "BF25 Mid": d.Bf25Mid = null; break;
                    case "RR10 Mid": d.Rr10Mid = null; break;
                    case "BF10 Mid": d.Bf10Mid = null; break;
                    case "ATM Spread": d.AtmSpread = null; break;
                    case "ATM Offset": d.AtmOffset = null; break;
                }
                grid.InvalidateRow(e.RowIndex);
                UpdateDraftEditsCounter(pair);
                return;
            }

            decimal parsed;
            bool ok =
                decimal.TryParse(txt, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.CurrentCulture, out parsed)
                || decimal.TryParse(txt, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out parsed);

            if (!ok)
            {
                grid.CancelEdit();
                return;
            }

            switch (header)
            {
                case "RR25 Mid": d.Rr25Mid = parsed; break;
                case "BF25 Mid": d.Bf25Mid = parsed; break;
                case "RR10 Mid": d.Rr10Mid = parsed; break;
                case "BF10 Mid": d.Bf10Mid = parsed; break;
                case "ATM Spread": d.AtmSpread = parsed; break;
                case "ATM Offset": d.AtmOffset = parsed; break;
            }

            cell.Value = parsed;         // visa som nummer
            grid.InvalidateRow(e.RowIndex);
            UpdateDraftEditsCounter(pair);
        }



        /// <summary>
        /// Gulmarkerar endast celler där draft finns (inte hela raden).
        /// </summary>
        private void OnPairGridCellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            var grid = sender as DataGridView;
            if (grid == null) return;

            var row = grid.Rows[e.RowIndex];
            var col = grid.Columns[e.ColumnIndex];
            var name = col?.Name ?? "";
            var pair = GetActivePairFor(grid);
            var tenor = GetTenorFromRow(row);
            if (string.IsNullOrEmpty(pair) || string.IsNullOrEmpty(tenor)) return;

            bool hasDraft = false;
            if (_draftStore.TryGetValue(pair, out var perPair) && perPair != null && perPair.TryGetValue(tenor, out var d) && d != null)
            {
                if (name == "RR25_mid") hasDraft = d.Rr25Mid.HasValue;
                else if (name == "RR10_mid") hasDraft = d.Rr10Mid.HasValue;
                else if (name == "BF25_mid") hasDraft = d.Bf25Mid.HasValue;
                else if (name == "BF10_mid") hasDraft = d.Bf10Mid.HasValue;
                else if (string.Equals(name, "ATM_adj", StringComparison.OrdinalIgnoreCase))
                {
                    var anchored = _presenter != null && _presenter.IsAnchoredPair(pair);
                    hasDraft = anchored ? d.AtmOffset.HasValue : d.AtmSpread.HasValue;
                }
            }

            if (hasDraft)
                row.Cells[e.ColumnIndex].Style.BackColor = Color.FromArgb(255, 249, 196); // ljusgul
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
        /// Aktiverar editering för RR/BF och ATM-justering, kopplar strikt validering
        /// + draft-uppdatering och gul markering. Visar cellfel (inte radheader).
        /// </summary>
        private void EnableEditingForPairGrid(DataGridView grid)
        {
            if (grid == null) return;
            if (grid.Tag as string == "edit-wired") return;

            // Skrivbara kolumner
            SetReadOnly(grid, "RR25 Mid", false);
            SetReadOnly(grid, "BF25 Mid", false);
            SetReadOnly(grid, "RR10 Mid", false);
            SetReadOnly(grid, "BF10 Mid", false);
            var atmAdj = grid.Columns.Cast<DataGridViewColumn>()
                .FirstOrDefault(c => string.Equals(c.Name, "ATM_adj", StringComparison.OrdinalIgnoreCase));
            if (atmAdj != null) atmAdj.ReadOnly = false;

            // Koppla bort ev. legacy-handlers
            grid.CellBeginEdit -= OnPairGridCellBeginEdit;
            grid.CellValidating -= PairGrid_CellValidating;
            grid.CellEndEdit -= PairGrid_CellEndEdit;
            grid.CellEndEdit -= OnPairGridCellEndEdit;
            grid.CellFormatting -= OnPairGridCellFormatting;
            grid.DataError -= PairGrid_DataError;

            // Nya handlers
            grid.CellBeginEdit += OnPairGridCellBeginEdit;   // spara original för Esc/ogiltigt tal
            grid.CellValidating += PairGrid_CellValidating;  // strikt parse + hårda kolumnregler (utan cancel)
            grid.CellEndEdit += PairGrid_CellEndEdit;     // radregler + soft warns + draft-skriv
            grid.CellEndEdit += OnPairGridCellEndEdit;    // befintlig: uppdaterar draftstore
            grid.CellFormatting += OnPairGridCellFormatting; // gul markering endast när draft finns
            grid.DataError += PairGrid_DataError;       // tysta grid-formatfel

            grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
            grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
            grid.ReadOnly = false;
            grid.ShowCellErrors = true;
            grid.ShowRowErrors = false;

            grid.Tag = "edit-wired";
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
        /// Bygger/återanvänder headern för ett par i Tabs-vyn när man hostar i en given Panel,
        /// i stället för direkt i en TabPage. Används när koden anropar
        /// EnsurePairTabHeader(hostPanel, "PairHeaderHost", "PairHeaderLabel").
        /// Skapar etikett för antal edits samt knapparna Discard och Review.
        /// </summary>
        /// <param name="hostPanel">Panel som äger headern (ligger normalt över gridet i tab-sidan).</param>
        /// <param name="headerPanelName">Name att ge header-panelen (för att kunna hitta/återanvända).</param>
        /// <param name="editsLabelName">Name att ge edits-labeln (för att kunna hitta/uppdatera).</param>
        /// <returns>Header-panelen som innehåller label + knappar.</returns>
        private Panel EnsurePairTabHeader(Panel hostPanel, string headerPanelName, string editsLabelName)
        {
            if (hostPanel == null) throw new ArgumentNullException(nameof(hostPanel));
            if (string.IsNullOrWhiteSpace(headerPanelName)) throw new ArgumentNullException(nameof(headerPanelName));
            if (string.IsNullOrWhiteSpace(editsLabelName)) throw new ArgumentNullException(nameof(editsLabelName));

            // 1) Header-panel (dockad överst i hostPanel)
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

            // 2) Edits-label
            var lblEdits = header.Controls.OfType<Label>().FirstOrDefault(l => l.Name == editsLabelName);
            if (lblEdits == null)
            {
                lblEdits = new Label
                {
                    Name = editsLabelName,
                    AutoSize = true,
                    Text = "Edits: 0",
                    TextAlign = ContentAlignment.MiddleLeft
                };
                header.Controls.Add(lblEdits);
            }

            // 3) Discard-knapp (återanvänd om den redan finns)
            var btnDiscard = header.Controls.OfType<Button>().FirstOrDefault(b => b.Name == "BtnDiscard");
            if (btnDiscard == null)
            {
                btnDiscard = new Button
                {
                    Name = "BtnDiscard",
                    Text = "Discard",
                    Size = new Size(78, 23),
                    Enabled = false // aktiveras när det finns draft
                };
                btnDiscard.Click += (s, e) => DiscardAllDraftForActivePair();
                header.Controls.Add(btnDiscard);
            }

            // 4) Review-knapp (öppnar review-dialogen)
            var btnReview = header.Controls.OfType<Button>().FirstOrDefault(b => b.Name == "BtnReview");
            if (btnReview == null)
            {
                btnReview = new Button
                {
                    Name = "BtnReview",
                    Text = "Review",
                    Size = new Size(78, 23),
                    Enabled = false // aktiveras när det finns draft
                };
                // Koppla klick → review-dialogen för aktivt par
                btnReview.Click += (s, e) => ShowReviewDialogForActivePair();
                header.Controls.Add(btnReview);
            }

            // 5) Enkel högerställd layout: [lblEdits] ... [Review][Discard]
            void LayoutHeader()
            {
                var right = header.ClientSize.Width - 8;

                btnDiscard.Top = (header.Height - btnDiscard.Height) / 2;
                btnDiscard.Left = right - btnDiscard.Width;
                right = btnDiscard.Left - 6;

                btnReview.Top = (header.Height - btnReview.Height) / 2;
                btnReview.Left = right - btnReview.Width;
                right = btnReview.Left - 12;

                lblEdits.Top = (header.Height - lblEdits.Height) / 2;
                lblEdits.Left = 8;
            }

            header.Resize -= HeaderOnResize;
            header.Resize += HeaderOnResize;
            LayoutHeader(); // fixar initialt att label inte hamnar fel

            void HeaderOnResize(object sender, EventArgs e) => LayoutHeader();

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

        /// <summary>Vol-tal (ATM/RR/BF) med 4 decimaler eller tomt.</summary>
        private string FormatVol4(decimal? v) => v.HasValue ? v.Value.ToString("0.0000") : "";

        /// <summary>Snapshot UTC-tid i standardformat.</summary>
        private string FormatTimeUtc(DateTime tsUtc) => tsUtc.ToString("yyyy-MM-dd HH:mm") + " UTC";

        /// <summary>
        /// Sätter 4 decimalers format på angivna kolumner i en DGV.
        /// </summary>
        private void ApplyVol4Dp(DataGridView grid, params string[] columnNames)
        {
            if (grid == null || columnNames == null) return;
            var style = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleRight,
                Format = "0.0000"
            };
            foreach (var name in columnNames)
            {
                var col = grid.Columns[name] as DataGridViewTextBoxColumn;
                if (col != null) col.DefaultCellStyle = style;
            }
        }


        #endregion


        #region Draft-lager (modell + store)

        /// <summary>
        /// Sätter eller rensar ett draftvärde för ett par/tenor och fält.
        /// fieldKey stöder: "Rr25Mid","Rr10Mid","Bf25Mid","Bf10Mid","AtmSpread","AtmOffset".
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

            // Uppdatera liten status (om du har denna metod; annars no-op)
            try { UpdateDraftEditsCounter(pair); } catch { /* best effort */ }
        }

        /// <summary>
        /// Normaliserar fältnamn till förväntade draft-nycklar.
        /// </summary>
        private static string NormalizeDraftFieldKey(string fieldKey)
        {
            var k = fieldKey.Trim();
            // Tillåt även grid-kolumnnamn (case-insensitivt)
            if (k.Equals("RR25_mid", StringComparison.OrdinalIgnoreCase)) return "Rr25Mid";
            if (k.Equals("RR10_mid", StringComparison.OrdinalIgnoreCase)) return "Rr10Mid";
            if (k.Equals("BF25_mid", StringComparison.OrdinalIgnoreCase)) return "Bf25Mid";
            if (k.Equals("BF10_mid", StringComparison.OrdinalIgnoreCase)) return "Bf10Mid";
            if (k.Equals("ATM_adj", StringComparison.OrdinalIgnoreCase)) return "AtmSpread"; // default – växlas till Offset i call-site vid anchored
                                                                                             // Stöd redan “rätta” nycklar
            if (k.Equals("Rr25Mid", StringComparison.OrdinalIgnoreCase)) return "Rr25Mid";
            if (k.Equals("Rr10Mid", StringComparison.OrdinalIgnoreCase)) return "Rr10Mid";
            if (k.Equals("Bf25Mid", StringComparison.OrdinalIgnoreCase)) return "Bf25Mid";
            if (k.Equals("Bf10Mid", StringComparison.OrdinalIgnoreCase)) return "Bf10Mid";
            if (k.Equals("AtmSpread", StringComparison.OrdinalIgnoreCase)) return "AtmSpread";
            if (k.Equals("AtmOffset", StringComparison.OrdinalIgnoreCase)) return "AtmOffset";
            return k;
        }

        /// <summary>
        /// True om någon draft-kolumn i raden har värde.
        /// </summary>
        private static bool HasAnyDraftValues(VolDraftRow r)
        {
            return r != null && (
                r.Rr25Mid.HasValue ||
                r.Rr10Mid.HasValue ||
                r.Bf25Mid.HasValue ||
                r.Bf10Mid.HasValue ||
                r.AtmSpread.HasValue ||
                r.AtmOffset.HasValue
            );
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
