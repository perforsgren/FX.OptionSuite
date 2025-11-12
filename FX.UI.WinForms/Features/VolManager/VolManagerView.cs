using System;
using System.Collections.Generic;
using System.Drawing;
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
        #region Fält – enkelvy (snabbtest)
        private TextBox _txtPair;
        private Button _btnFetch;
        private Label _lblSnapshot;
        private DataGridView _grid;
        private BindingSource _bs;
        #endregion

        #region Fält – flikvy (Pair Tabs)

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
        /// Laddar om data (senaste snapshot) för den aktiva pair-fliken.
        /// <paramref name="force"/> = true bypassar presenterns cache.
        /// </summary>
        /// <param name="force">True = bypass cache, False = använd mjuk TTL-cache.</param>
        public void RefreshActivePair(bool force)
        {
            if (_pairTabs == null || _pairTabs.TabPages.Count == 0) return;

            var tab = _pairTabs.SelectedTab;
            if (tab == null) return;

            var ui = tab.Tag as PairTabUi;
            var pair = ui?.PairSymbol;
            if (string.IsNullOrWhiteSpace(pair))
                pair = tab.Text;

            if (!string.IsNullOrWhiteSpace(pair))
            {
                LoadLatestForPairTab(tab, pair, force);
                AddToRecent(pair, 10);
            }
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
        /// Superkompakt kolumnuppsättning för Tiles: endast Tenor och ATM Mid.
        /// </summary>
        /// <param name="grid">Grid som ska få kolumner.</param>
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
                DefaultCellStyle = { Format = "N6", Alignment = DataGridViewContentAlignment.MiddleRight }
            });
        }


        /// <summary>
        /// Sätter upp en kompakt kolumnuppsättning för Tile-grids:
        /// Tenor, ATM Mid, RR25 Mid, BF25 Mid. Avsedd för översiktsrutor (Tiles).
        /// </summary>
        /// <param name="grid">Grid som ska få tile-kolumner.</param>
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

            // Tenor
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(VolSurfaceRow.TenorCode),
                HeaderText = "Tenor",
                Width = 64,
                ReadOnly = true,
                DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleLeft }
            });

            // ATM Mid
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(VolSurfaceRow.AtmMid),
                HeaderText = "ATM",
                Width = 74,
                ReadOnly = true,
                DefaultCellStyle = { Format = "N6", Alignment = DataGridViewContentAlignment.MiddleRight }
            });

            // RR25 Mid
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(VolSurfaceRow.Rr25Mid),
                HeaderText = "RR25",
                Width = 70,
                ReadOnly = true,
                DefaultCellStyle = { Format = "N6", Alignment = DataGridViewContentAlignment.MiddleRight }
            });

            // BF25 Mid
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(VolSurfaceRow.Bf25Mid),
                HeaderText = "BF25",
                Width = 70,
                ReadOnly = true,
                DefaultCellStyle = { Format = "N6", Alignment = DataGridViewContentAlignment.MiddleRight }
            });

            // Totalt ca 64 + 74 + 70 + 70 = 278 px + marginaler → passar väl i en tile ~420 px bred.
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
        #endregion

        #region Pair Tabs – vänsterpanel (Pin, Pinned, Recent)

        /// <summary>
        /// Växlar vyn till flikläge ("Pair Tabs") och sätter upp vänsterpanelen
        /// med Pin, Pinned, Recent, Refresh, vy-toggle (Tabs/Tiles) och kolumn-toggle för Tiles.
        /// Initierar även Tiles-containern (dold) så vi kan växla vy utan att förstöra layouten.
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
            lstPinned.DoubleClick += OnPinnedDoubleClick;

            // Recent
            var lblRecent = new Label { Text = "— Recent —", Dock = DockStyle.Top, Height = 16 };
            var lstRecent = new ListBox { Name = "LstRecent", Dock = DockStyle.Top, Height = 140, IntegralHeight = false };
            lstRecent.DoubleClick += OnRecentDoubleClick;

            // Actions
            var lblActions = new Label { Text = "— Actions —", Dock = DockStyle.Top, Height = 16 };
            var btnRefresh = new Button { Name = "BtnRefresh", Dock = DockStyle.Top, Text = "Refresh" };
            btnRefresh.Click += OnClickRefresh;

            // View (Tabs / Tiles)
            var lblView = new Label { Text = "— View —", Dock = DockStyle.Top, Height = 16 };
            var btnTabs = new Button { Name = "BtnTabsView", Dock = DockStyle.Top, Text = "Tabs View" };
            btnTabs.Click += (s, e) => ShowTabsView();
            var btnTiles = new Button { Name = "BtnTilesView", Dock = DockStyle.Top, Text = "Tiles View" };
            btnTiles.Click += (s, e) => ShowTilesView(forceInitialLoad: false);

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
                _pairTabs.SelectedIndexChanged += PairTabs_SelectedIndexChanged;
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
                    Visible = false
                };
            }

            Controls.Clear();
            Controls.Add(_tilesPanel);
            Controls.Add(_pairTabs);
            Controls.Add(pnlLeft);
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
        /// Dubbelklick på en Recent-rad: aktivera/flika upp paret och bumpa upp det i Recent.
        /// </summary>
        private void OnRecentDoubleClick(object sender, EventArgs e)
        {
            var lb = sender as ListBox;
            var sel = lb?.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(sel)) return;

            PinPair(sel);
            AddToRecent(sel, 10);
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
        #endregion

        #region Pair Tabs – flikar & laddning

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
        /// Laddar senaste snapshot för en tile och uppdaterar dess grid + header-status.
        /// </summary>
        /// <param name="tile">Tile-panel vars Tag ska vara TileUi.</param>
        /// <param name="pair">Valutapar (t.ex. "EUR/USD").</param>
        /// <param name="force">True = bypass presenter-cache.</param>
        private void LoadLatestForTile(Panel tile, string pair, bool force)
        {
            if (_presenter == null) return;
            if (tile == null) return;

            var ui = tile.Tag as TileUi;
            if (ui == null) return;

            var result = _presenter.LoadLatestWithHeaderTagged(pair, force);

            ui.Binding.DataSource = result.Rows ?? new List<VolSurfaceRow>();

            ui.LabelSnapshot.Text = result.SnapshotId.HasValue ? $"Snap: {result.SnapshotId.Value}" : "Snap: –";
            ui.LabelTs.Text = (result.Header != null) ? $"TS: {result.Header.TsUtc:HH:mm:ss}Z" : "TS: –";
            ui.LabelStatus.Text = result.FromCache ? "Cached" : $"Fresh {DateTime.Now:HH:mm:ss}";
        }



        /// <summary>
        /// Lägger till (eller aktiverar) en pair-flik i sessionen och laddar dess data (utan force).
        /// Rättar även Tag-modellen: TabPage.Tag är alltid PairTabUi.
        /// </summary>
        /// <param name="pair">Valutapar i "AAA/BBB".</param>
        public void PinPair(string pair)
        {
            if (string.IsNullOrWhiteSpace(pair)) return;

            if (_pairTabs == null)
                InitializeTabbedLayout();

            var pairUpper = pair.ToUpperInvariant();

            // Finns fliken redan? Jämför mot PairTabUi.PairSymbol (om satt), annars tab.Text
            TabPage page = null;
            foreach (TabPage p in _pairTabs.TabPages)
            {
                var uiExisting = p.Tag as PairTabUi;
                var existingPair = uiExisting?.PairSymbol ?? p.Text;
                if (string.Equals(existingPair, pairUpper, StringComparison.OrdinalIgnoreCase))
                {
                    page = p; break;
                }
            }

            // Skapa ny flik om den saknas
            if (page == null)
            {
                page = CreatePairTab(pairUpper);
                var ui = page.Tag as PairTabUi;
                if (ui != null) ui.PairSymbol = pairUpper;
                _pairTabs.TabPages.Add(page);
            }
            else
            {
                var ui = page.Tag as PairTabUi;
                if (ui != null && string.IsNullOrWhiteSpace(ui.PairSymbol))
                    ui.PairSymbol = pairUpper;
            }

            // Aktivera fliken och ladda (utan force)
            _pairTabs.SelectedTab = page;
            LoadLatestForPairTab(page, pairUpper, force: false);
        }


        /// <summary>
        /// När användaren byter parflik laddas senaste snapshot i den fliken (utan force).
        /// </summary>
        private void PairTabs_SelectedIndexChanged(object sender, EventArgs e)
        {
            var page = _pairTabs?.SelectedTab;
            if (page == null) return;

            var ui = page.Tag as PairTabUi;
            var pair = ui?.PairSymbol ?? page.Text;

            if (!string.IsNullOrWhiteSpace(pair))
                LoadLatestForPairTab(page, pair, force: false);
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
            var lblStatus = new Label { AutoSize = true, Left = 520, Top = 6, Text = "Status: –" };

            header.Controls.Add(lblSnap);
            header.Controls.Add(lblDelta);
            header.Controls.Add(lblPA);
            header.Controls.Add(lblTs);
            header.Controls.Add(lblStatus);

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
                PairSymbol = pairSymbol,
                Grid = grid,
                Binding = bs,
                LabelSnapshot = lblSnap,
                LabelDelta = lblDelta,
                LabelPremiumAdj = lblPA,
                LabelTs = lblTs,
                LabelStatus = lblStatus
            };

            return tab;
        }



        /// <summary>
        /// Laddar "senaste + header + rows" för en given TabPage och uppdaterar grid, header-chipsen och status.
        /// </summary>
        /// <param name="tab">TabPage vars Tag ska innehålla PairTabUi.</param>
        /// <param name="pairSymbol">Valutapar som fliken representerar (t.ex. "EUR/USD").</param>
        /// <param name="force">True = bypass presenter-cache; False = tillåt mjuk TTL.</param>
        private void LoadLatestForPairTab(TabPage tab, string pairSymbol, bool force)
        {
            if (_presenter == null)
                throw new InvalidOperationException("SetPresenter måste anropas innan laddning.");

            if (tab == null || !(tab.Tag is PairTabUi ui))
                return;

            var result = _presenter.LoadLatestWithHeaderTagged(pairSymbol, force);

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

            // Status (cache/färskt)
            if (result.FromCache)
                ui.LabelStatus.Text = "Status: Cached";
            else
                ui.LabelStatus.Text = $"Status: Fresh @ {DateTime.Now:HH:mm:ss}";
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
    }
}
