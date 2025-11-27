using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using FX.UI.WinForms.Features.Blotter;
using System.Linq;
using FxTradeHub.Contracts.Dtos;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.ToolTip;


namespace FX.UI.WinForms
{
    /// <summary>
    /// Workspace-kontroll för FX Trade Blotter.
    /// Layout:
    /// - Menyrad överst (File/Edit/Tools) – gäller båda flikar.
    /// - Vänster sidomeny med vertikala knappar (dummy i första versionen).
    /// - Pill-stylade flikar i botten (Options / Linear, All).
    /// - Varje flik innehåller en host-panel där vi senare lägger grids/layout.
    /// </summary>
    public sealed class BlotterWorkspaceControl : UserControl
    {
        #region === Fält & konstanter ===

        private readonly Panel _menuTopSpacer;
        private readonly MenuStrip _menu;

        private readonly Panel _sideBarHost;
        private readonly ToolStrip _sideBar;

        private readonly PillTabControl _tabs;

        private readonly Panel _optionsLinearHost;
        private readonly Panel _allHost;

        private static readonly Color TabBgActive = ColorTranslator.FromHtml("#293955");
        private static readonly Color TabBgInactive = ColorTranslator.FromHtml("#2F4366");
        private static readonly Color TabText = Color.White;
        private static readonly Color TabOutline = Color.FromArgb(30, Color.Black);
        private const int TabCornerRadius = 8;

        // === Layout för Options / Linear-tabben ===

        /// <summary>
        /// Yttersta splittern för Options / Linear-vyn:
        /// vänster = grids, höger = detaljpanel.
        /// </summary>
        private SplitContainer _splitMain;

        /// <summary>
        /// Inre splitter på vänstra sidan:
        /// överst = Options-grid, nederst = Hedge/FX Linear-grid.
        /// </summary>
        private SplitContainer _splitLeft;

        /// <summary>
        /// Grid som visar options-trades (övre vänstra grid).
        /// </summary>
        private DataGridView _gridOptions;

        /// <summary>
        /// Grid som visar hedge / FX linear-trades (nedre vänstra grid).
        /// </summary>
        private DataGridView _gridHedge;

        /// <summary>
        /// Panel som hostar detaljvyn till höger (Details).
        /// </summary>
        private Panel _detailHost;

        /// <summary>
        /// Grid i fliken "All" som visar alla trades.
        /// </summary>
        private DataGridView _gridAll;

        /// <summary>
        /// Markerar om initiala splitter-lägen har satts upp redan.
        /// </summary>
        private bool _splittersInitialized;


        #endregion

        // === Konstruktor & init ===

        /// <summary>
        /// Skapar blotter-workspace med meny, sidomeny och två fasta flikar:
        /// 1) Options / Linear – innehåller två grids (options + hedge/linear) + detaljpanel.
        /// 2) All – innehåller ett samlat grid för alla trades.
        /// </summary>
        public BlotterWorkspaceControl()
        {
            Dock = DockStyle.Fill;
            BackColor = SystemColors.Control;

            // --- Meny överst ---
            _menuTopSpacer = new Panel
            {
                Dock = DockStyle.Top,
                Height = 0,
                BackColor = SystemColors.Control
            };
            Controls.Add(_menuTopSpacer);

            _menu = BuildMenu();
            _menu.Dock = DockStyle.Top;
            Controls.Add(_menu);

            // --- Vänster sidomeny i wrapper-panel ---
            _sideBar = BuildSideBar();

            _sideBarHost = new Panel
            {
                Dock = DockStyle.Left,
                Width = 48,
                Padding = new Padding(0, 0, 0, 0)
            };
            _sideBar.Dock = DockStyle.Fill;
            _sideBarHost.Controls.Add(_sideBar);
            Controls.Add(_sideBarHost);

            // --- Flikar i botten (pill-stil) ---
            _tabs = new PillTabControl
            {
                Dock = DockStyle.Fill,
                Alignment = TabAlignment.Bottom,
                SizeMode = TabSizeMode.Fixed,
                ItemSize = new Size(120, 26),
                Padding = new Point(16, 6)
            };

            // Flik 1: Options / Linear
            var pageOptionsLinear = new TabPage("Options / Linear")
            {
                Padding = new Padding(0),
                UseVisualStyleBackColor = true
            };

            _optionsLinearHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Margin = Padding.Empty,
                Padding = new Padding(0)
            };
            pageOptionsLinear.Controls.Add(_optionsLinearHost);

            // Flik 2: All
            var pageAll = new TabPage("All")
            {
                Padding = new Padding(0),
                UseVisualStyleBackColor = true
            };

            _allHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Margin = Padding.Empty,
                Padding = new Padding(0)
            };
            pageAll.Controls.Add(_allHost);

            _tabs.TabPages.Add(pageOptionsLinear);
            _tabs.TabPages.Add(pageAll);

            _tabs.SelectedIndexChanged += (s, e) => UpdateSidebarOffset();

            Controls.Add(_tabs);
            Controls.SetChildIndex(_tabs, 0);

            Padding = new Padding(0, 0, 0, 6);

            Layout += (s, e) =>
            {
                UpdateSidebarOffset();
                InitializeSplittersIfNeeded();
            };

            // Bygg layouten inne i respektive host-panel.
            BuildOptionsLinearTabLayout();
            BuildAllTabLayout();

            // Sätt initial splitter-position när kontrollen är laddad.
            Load += BlotterWorkspaceControl_Load;
        }

        /// <summary>
        /// Sätter initiala splitter-positioner första gången workspacet laddas.
        /// </summary>
        private void BlotterWorkspaceControl_Load(object sender, EventArgs e)
        {
            InitializeSplittersIfNeeded();
        }

        /// <summary>
        /// Sätter upp initiala SplitterDistance-värden på ett säkert sätt.
        /// Anropas från Load (och kan anropas igen från Layout/Resize om vi vill),
        /// men gör inget om det redan är gjort.
        /// </summary>
        private void InitializeSplittersIfNeeded()
        {
            if (_splittersInitialized)
            {
                return;
            }

            if (_splitMain == null || _splitLeft == null)
            {
                return;
            }

            // --- Yttre splitter (vertikal): ca 70 % vänster / 30 % höger ---
            if (_splitMain.Width > 0)
            {
                int min = _splitMain.Panel1MinSize;
                int max = _splitMain.Width - _splitMain.Panel2MinSize - _splitMain.SplitterWidth;

                if (max > min)
                {
                    int target = (int)(_splitMain.Width * 0.7);
                    int clamped = Math.Min(Math.Max(target, min), max);
                    _splitMain.SplitterDistance = clamped;
                }
            }

            // --- Inre splitter (horisontell): ca 40 % options / 60 % hedge ---
            if (_splitLeft.Height > 0)
            {
                int min = _splitLeft.Panel1MinSize;
                int max = _splitLeft.Height - _splitLeft.Panel2MinSize - _splitLeft.SplitterWidth;

                if (max > min)
                {
                    int target = (int)(_splitLeft.Height * 0.4);
                    int clamped = Math.Min(Math.Max(target, min), max);
                    _splitLeft.SplitterDistance = clamped;
                }
            }

            _splittersInitialized = true;
        }


        /// <summary>
        /// Bygger layouten i fliken "Options / Linear":
        /// - Yttersta vertikal SplitContainer: vänster grids / höger detaljpanel.
        /// - Inre horisontell SplitContainer på vänster sida: Options-grid överst,
        ///   Hedge/FX Linear-grid nederst.
        /// </summary>
        private void BuildOptionsLinearTabLayout()
        {
            _splitMain = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                BorderStyle = BorderStyle.None,
                SplitterWidth = 4
            };

            _splitLeft = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                BorderStyle = BorderStyle.None,
                SplitterWidth = 4
            };

            // --- Options-panel (övre vänster) ---
            var optionsPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(0),
                Margin = Padding.Empty
            };

            var lblOptions = new Label
            {
                Text = "Options",
                Dock = DockStyle.Top,
                Height = 18,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(4, 0, 0, 0),
                Font = new Font(Font, FontStyle.Bold),
                BackColor = ColorTranslator.FromHtml("#F2F2F2")
            };

            _gridOptions = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true
            };
            ConfigureGrid(_gridOptions);
            CreateColumnsOptions(_gridOptions); // <-- här skapar vi kolumnerna

            optionsPanel.Controls.Add(_gridOptions);
            optionsPanel.Controls.Add(lblOptions);

            // --- Hedge/FX Linear-panel (nedre vänster) ---
            var hedgePanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(0),
                Margin = Padding.Empty
            };

            var lblHedge = new Label
            {
                Text = "Hedge / FX Linear",
                Dock = DockStyle.Top,
                Height = 18,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(4, 0, 0, 0),
                Font = new Font(Font, FontStyle.Bold),
                BackColor = ColorTranslator.FromHtml("#F2F2F2")
            };

            _gridHedge = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true
            };
            ConfigureGrid(_gridHedge);
            // kolumner för hedge-grid kommer i nästa steg

            hedgePanel.Controls.Add(_gridHedge);
            hedgePanel.Controls.Add(lblHedge);

            _splitLeft.Panel1.Controls.Add(optionsPanel);
            _splitLeft.Panel2.Controls.Add(hedgePanel);

            // --- Detaljpanel (höger) ---
            _detailHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = SystemColors.Control,
                Padding = new Padding(4)
            };

            var lblDetails = new Label
            {
                Text = "Details",
                Dock = DockStyle.Top,
                Height = 18,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(4, 0, 0, 0),
                Font = new Font(Font, FontStyle.Bold),
                BackColor = ColorTranslator.FromHtml("#F2F2F2")
            };

            var detailsPlaceholder = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = SystemColors.Window
            };

            _detailHost.Controls.Add(detailsPlaceholder);
            _detailHost.Controls.Add(lblDetails);

            _splitMain.Panel1.Controls.Add(_splitLeft);
            _splitMain.Panel2.Controls.Add(_detailHost);

            _optionsLinearHost.Controls.Add(_splitMain);
        }

        /// <summary>
        /// Skapar kolumnerna i Options-griden baserat på blotter-metadata.
        /// Använder BlotterColumnMetadata.All och filtrerar på VisibleIn = Options.
        /// Kolumner med EditorType = Combo + IsEditable = true blir ComboBox-kolumner.
        /// </summary>
        /// <param name="grid">Grid som ska få options-kolumner.</param>
        private void CreateColumnsOptions(DataGridView grid)
        {
            if (grid == null)
            {
                return;
            }

            grid.AutoGenerateColumns = false;
            grid.Columns.Clear();

            var defs = BlotterColumnMetadata.All
                .Where(d => (d.VisibleIn & BlotterGridVisibility.Options) != 0)
                .OrderBy(d => d.DisplayOrder)
                .ToList();

            grid.SuspendLayout();

            foreach (var def in defs)
            {
                DataGridViewColumn col;

                // Välj kolumntyp utifrån editor-metadata
                if (def.EditorType == BlotterEditorType.Combo && def.IsEditable)
                {
                    var combo = new DataGridViewComboBoxColumn
                    {
                        DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
                        FlatStyle = FlatStyle.Flat
                    };

                    // Tillfälliga standardval – ersätts senare med riktiga lookups.
                    if (string.Equals(def.LookupKey, "BuySell", StringComparison.OrdinalIgnoreCase))
                    {
                        combo.Items.AddRange("Buy", "Sell");
                    }
                    else if (string.Equals(def.LookupKey, "CallPut", StringComparison.OrdinalIgnoreCase))
                    {
                        combo.Items.AddRange("Call", "Put");
                    }
                    // Trader och PortfolioMx3 får sina värden via DataSource senare.

                    col = combo;
                }
                else
                {
                    col = new DataGridViewTextBoxColumn();
                }

                col.Name = def.Key;
                col.HeaderText = string.IsNullOrEmpty(def.HeaderText)
                    ? def.Key
                    : def.HeaderText;

                col.DataPropertyName = string.IsNullOrEmpty(def.BindingPath)
                    ? def.Key
                    : def.BindingPath;

                col.SortMode = DataGridViewColumnSortMode.Programmatic;

                // ReadOnly styrs av metadata (radens CanEdit hanterar vi i nästa steg).
                col.ReadOnly = !def.IsEditable;

                if (def.Alignment != 0)
                {
                    col.DefaultCellStyle.Alignment = def.Alignment;
                }

                if (!string.IsNullOrEmpty(def.Format))
                {
                    col.DefaultCellStyle.Format = def.Format;
                }

                if (!string.IsNullOrEmpty(def.HeaderToolTip))
                {
                    col.ToolTipText = def.HeaderToolTip;
                }

                // Default-synlighet enligt metadata (ingen persistering ännu).
                bool defaultVisible =
                    (def.DefaultVisibleIn & BlotterGridVisibility.Options) != 0;

                col.Visible = defaultVisible;

                grid.Columns.Add(col);
            }

            grid.ResumeLayout();
        }




        /// <summary>
        /// Bygger layouten i fliken "All":
        /// - En rubrikrad.
        /// - Ett DataGridView som visar alla trades (tom i första versionen).
        /// </summary>
        private void BuildAllTabLayout()
        {
            var container = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(0),
                Margin = Padding.Empty
            };

            var lblAll = new Label
            {
                Text = "All trades",
                Dock = DockStyle.Top,
                Height = 18,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(4, 0, 0, 0),
                Font = new Font(Font, FontStyle.Bold),
                BackColor = ColorTranslator.FromHtml("#F2F2F2")
            };

            _gridAll = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true
            };
            ConfigureGrid(_gridAll);

            container.Controls.Add(_gridAll);
            container.Controls.Add(lblAll);

            _allHost.Controls.Add(container);
        }

        /// <summary>
        /// Sätter upp grundstil för blotter-grids så de beter sig konsekvent.
        /// </summary>
        /// <param name="grid">Grid som ska konfigureras.</param>
        private void ConfigureGrid(DataGridView grid)
        {
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false;

            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = false;

            grid.AllowUserToOrderColumns = true;
            grid.AllowUserToResizeColumns = true;

            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;

            grid.BackgroundColor = SystemColors.Window;
            grid.BorderStyle = BorderStyle.None;
            grid.EnableHeadersVisualStyles = false;

            // Header: ljusgrå + bold text
            grid.ColumnHeadersDefaultCellStyle.BackColor = ColorTranslator.FromHtml("#E0E0E0");
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.Black;
            grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
            grid.ColumnHeadersDefaultCellStyle.Font = new Font(grid.Font, FontStyle.Bold);

            grid.DefaultCellStyle.BackColor = Color.White;
            grid.DefaultCellStyle.ForeColor = Color.Black;
            grid.DefaultCellStyle.SelectionBackColor = ColorTranslator.FromHtml("#CDE5FF");
            grid.DefaultCellStyle.SelectionForeColor = Color.Black;

            grid.AlternatingRowsDefaultCellStyle.BackColor = ColorTranslator.FromHtml("#F7F7F7");
        }





        /// <summary>
        /// Bygger menyraden (File/Edit/Tools). Menyalternativen är placeholders
        /// i första versionen men strukturen är klar för framtida kommandon.
        /// </summary>
        private MenuStrip BuildMenu()
        {
            var ms = new MenuStrip();

            // File
            var mFile = new ToolStripMenuItem("File");
            //var miRefresh = new ToolStripMenuItem("Refresh", null, (s, e) => { /* TODO: implementera refresh */ })
            //{
            //    ShortcutKeys = Keys.F5,
            //    ShowShortcutKeys = true
            //};
            //var miExport = new ToolStripMenuItem("Export...", null, (s, e) => { /* TODO: export */ });
            //var miClose = new ToolStripMenuItem("Close", null, (s, e) => { /* TODO: ev. stäng-kommandon */ })
            //{
            //    ShortcutKeys = Keys.Control | Keys.W,
            //    ShowShortcutKeys = true
            //};
            //mFile.DropDownItems.AddRange(new ToolStripItem[] { miRefresh, miExport, new ToolStripSeparator(), miClose });

            // Manual Input
            var mManualInput = new ToolStripMenuItem("Manual Input");
            var miOption = new ToolStripMenuItem("Option", null, (s, e) => { /* TODO */ })
            {
                //ShortcutKeys = Keys.Control | Keys.C,
                //ShowShortcutKeys = true
            };
            var miHedge = new ToolStripMenuItem("Hedge", null, (s, e) => { /* TODO */ })
            {
                //ShortcutKeys = Keys.Control | Keys.C,
                //ShowShortcutKeys = true
            };
            mManualInput.DropDownItems.AddRange(new ToolStripItem[] { miOption, miHedge });




            // === Settings ===
            var mSettings = new ToolStripMenuItem("Settings");

            // Övre delen
            var miNewTradeNotification = new ToolStripMenuItem("New trade notification", null, (s, e) =>
            {
                // TODO: toggla popup / notifiering
            })
            {
                CheckOnClick = true,
                //Checked = true
            };

            var miFlashTaskbar = new ToolStripMenuItem("Flash taskbar icon on new trade", null, (s, e) =>
            {
                // TODO: toggla flash i taskbar
            })
            {
                CheckOnClick = true,
                //Checked = false
            };

            var miCurrencyPairMapping = new ToolStripMenuItem("Currency pair MX3 mapping", null, (s, e) =>
            {
                // TODO: öppna dialog för MX3-mappning
            });

            var miAddMx3Counterpart = new ToolStripMenuItem("Add MX3 counterpart", null, (s, e) =>
            {
                // TODO: öppna dialog
            });

            var miAddSwedCounterpart = new ToolStripMenuItem("Add SWED counterpart", null, (s, e) =>
            {
                // TODO: öppna dialog
            });

            var miAddRemoveUser = new ToolStripMenuItem("Add/remove Swedbank user", null, (s, e) =>
            {
                // TODO: öppna dialog
            });

            // Show columns-submeny
            var miShowColumns = new ToolStripMenuItem("Show columns");

            var miCustomView = new ToolStripMenuItem("Custom view", null, (s, e) =>
            {
                // TODO: öppna kolumn-layout-dialog
            });

            var miShowMifid = new ToolStripMenuItem("Show MiFID details", null, (s, e) =>
            {
                // TODO: toggla MiFID-kolumner
            })
            {
                CheckOnClick = true,
                //Checked = true,
                ShortcutKeys = Keys.Control | Keys.M,
                ShowShortcutKeys = true
            };

            var miShowMarginField = new ToolStripMenuItem("Show margin field", null, (s, e) =>
            {
                // TODO: toggla margin-kolumner
            })
            {
                CheckOnClick = true
            };

            var miShowMx3CalypsoIds = new ToolStripMenuItem("Show MX3/Calypso IDs", null, (s, e) =>
            {
                // TODO: toggla ID-kolumner
            })
            {
                CheckOnClick = true
            };

            miShowColumns.DropDownItems.Add(miCustomView);
            miShowColumns.DropDownItems.Add(new ToolStripSeparator());
            miShowColumns.DropDownItems.Add(miShowMifid);
            miShowColumns.DropDownItems.Add(miShowMarginField);
            miShowColumns.DropDownItems.Add(miShowMx3CalypsoIds);

            // Filtreringsval
            var miShowMyTrades = new ToolStripMenuItem("Show my trades", null, (s, e) =>
            {
                // TODO: filtrera på Trader
            })
            {
                CheckOnClick = true,
                //Checked = true
            };

            var miShowTodaysTrades = new ToolStripMenuItem("Show today's trades", null, (s, e) =>
            {
                // TODO: filtrera på dagens datum
            })
            {
                CheckOnClick = true,
                //Checked = true
            };

            var miShowLast20 = new ToolStripMenuItem("Show last 20 trades", null, (s, e) =>
            {
                // TODO: begränsa antal rader
            })
            {
                CheckOnClick = true
            };

            // Mail / rules
            var miMoveConfToFolder = new ToolStripMenuItem("Move confirmation email to folder", null, (s, e) =>
            {
                // TODO: toggla automatisk flytt till mapp
            })
            {
                CheckOnClick = true
            };

            var miMoveOnlySpotTrades = new ToolStripMenuItem("Move only spot trades", null, (s, e) =>
            {
                // TODO: toggla begränsning till spot
            })
            {
                CheckOnClick = true
            };

            var miEmailFolderRules = new ToolStripMenuItem("Email folder rules", null, (s, e) =>
            {
                // TODO: öppna dialog för regler
            });

            var miSuspendAfterEod = new ToolStripMenuItem("Suspend automatic import after EOD", null, (s, e) =>
            {
                // TODO: toggla scheduler efter EOD
            })
            {
                CheckOnClick = true
            };

            // Lägg in allt i Settings-menyn i samma ordning som i din gamla blotter
            mSettings.DropDownItems.Add(miNewTradeNotification);
            mSettings.DropDownItems.Add(miFlashTaskbar);
            mSettings.DropDownItems.Add(new ToolStripSeparator());
            mSettings.DropDownItems.Add(miCurrencyPairMapping);
            mSettings.DropDownItems.Add(miAddMx3Counterpart);
            mSettings.DropDownItems.Add(miAddSwedCounterpart);
            mSettings.DropDownItems.Add(miAddRemoveUser);
            mSettings.DropDownItems.Add(new ToolStripSeparator());
            mSettings.DropDownItems.Add(miShowColumns);
            mSettings.DropDownItems.Add(new ToolStripSeparator());
            mSettings.DropDownItems.Add(miShowMyTrades);
            mSettings.DropDownItems.Add(miShowTodaysTrades);
            mSettings.DropDownItems.Add(miShowLast20);
            mSettings.DropDownItems.Add(new ToolStripSeparator());
            mSettings.DropDownItems.Add(miMoveConfToFolder);
            mSettings.DropDownItems.Add(miMoveOnlySpotTrades);
            mSettings.DropDownItems.Add(miEmailFolderRules);
            mSettings.DropDownItems.Add(miSuspendAfterEod);


            // === Admin (tom placeholder tills vidare) ===
            var mAdmin = new ToolStripMenuItem("Admin");
            var miAdminPlaceholder = new ToolStripMenuItem("Admin tools...", null, (s, e) =>
            {
                // TODO: fyll på med blotter-adminfunktioner
            });
            mAdmin.DropDownItems.Add(miAdminPlaceholder);


            ms.Items.Add(mFile);
            ms.Items.Add(mManualInput);
            ms.Items.Add(mSettings);
            ms.Items.Add(mAdmin);

            return ms;
        }

        /// <summary>
        /// Bygger vänster sidomeny med vertikala knappar.
        /// Just nu är det bara dummy-knappar för layout (New/Refresh),
        /// men vi återanvänder mönstret från VolWorkspace (smal blå vertikal list).
        /// </summary>
        private ToolStrip BuildSideBar()
        {
            const int barWidth = 28;
            const int buttonWidth = 28;
            const int buttonHeight = 24;

            var ts = new ToolStrip
            {
                GripStyle = ToolStripGripStyle.Hidden,
                LayoutStyle = ToolStripLayoutStyle.VerticalStackWithOverflow,
                AutoSize = false,
                Width = barWidth,
                ImageScalingSize = new Size(16, 16),
                Padding = new Padding(0),
                Margin = Padding.Empty,
                RenderMode = ToolStripRenderMode.System
            };

            ToolStripButton MakeButton(string text, string tooltip, EventHandler onClick)
            {
                var b = new ToolStripButton
                {
                    Text = text,
                    DisplayStyle = ToolStripItemDisplayStyle.Text,
                    AutoToolTip = true,
                    ToolTipText = tooltip,
                    AutoSize = false,
                    Width = buttonWidth,
                    Height = buttonHeight,
                    Margin = new Padding(0, 2, 0, 2),
                    Padding = Padding.Empty
                };
                if (onClick != null)
                {
                    b.Click += onClick;
                }
                return b;
            }

            var bNew = MakeButton("+", "New blotter filter/layout (placeholder)", (s, e) => { /* TODO */ });
            var bRefresh = MakeButton("⟳", "Refresh trades (placeholder)", (s, e) => { /* TODO */ });

            ts.Items.Add(bNew);
            ts.Items.Add(bRefresh);

            return ts;
        }

        // === Publika host-ytor ===

        /// <summary>
        /// Panel där vi i nästa steg lägger layouten
        /// med Options-grid och Linear-grid (vänster/höger split, detaljer till höger).
        /// </summary>
        public Control OptionsLinearHost
        {
            get { return _optionsLinearHost; }
        }

        /// <summary>
        /// Panel för All-vyn där vi senare placerar ett gemensamt grid
        /// (alla trades oavsett typ).
        /// </summary>
        public Control AllHost
        {
            get { return _allHost; }
        }

        // === Publikt API (activation) ===

        /// <summary>
        /// Anropas när blotter-fönstret aktiveras i shellen.
        /// Här ser vi bara till att sidomenyn hamnar i rätt höjd mot innehållet.
        /// </summary>
        public void OnActivated()
        {
            UpdateSidebarOffset();
        }

        /// <summary>
        /// Anropas när blotter-fönstret tappar fokus i shellen.
        /// Första versionen gör inget särskilt, men hooken finns för framtiden.
        /// </summary>
        public void OnDeactivated()
        {
            // no-op i första versionen
        }

        // === Layout-hjälpare ===

        /// <summary>
        /// Justerar topp-paddning på sidomenyn så att dess överkant matchar
        /// den synliga ytan i tab-innehållet (samma känsla som VolWorkspace).
        /// </summary>
        private void UpdateSidebarOffset()
        {
            try
            {
                int menuBottom = _menu != null ? _menu.Height : 0;
                int topFromMenu = menuBottom + 6;

                int tabsContentTop = 0;
                if (_tabs != null && _tabs.IsHandleCreated)
                {
                    tabsContentTop = _tabs.DisplayRectangle.Top;
                }

                int targetTop = Math.Max(topFromMenu, tabsContentTop + 2);

                if (_sideBarHost != null)
                {
                    _sideBarHost.Padding = new Padding(0, targetTop, 0, 0);
                }
            }
            catch
            {
                // best effort – inga UI-fel får bubbla upp
            }
        }

        // === GDI-hjälpare (pill-ritning) ===

        /// <summary>
        /// Bygger en path med rundade nederhörn (rak topp) för pill-flikar.
        /// </summary>
        private static GraphicsPath RoundedBottom(Rectangle r, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();

            // Rak topp
            path.AddLine(r.Left, r.Top, r.Right, r.Top);

            // Höger sida
            path.AddLine(r.Right, r.Top, r.Right, r.Bottom - radius);

            // Rundning nederkant höger
            path.AddArc(new Rectangle(r.Right - d, r.Bottom - d, d, d), 0, 90);

            // Rundning nederkant vänster
            path.AddArc(new Rectangle(r.Left, r.Bottom - d, d, d), 90, 90);

            // Vänster sida upp
            path.AddLine(r.Left, r.Bottom - radius, r.Left, r.Top);

            path.CloseFigure();
            return path;
        }

        // === Nested: PillTabControl (custom-drawn tabs utan stäng-kryss) ===

        /// <summary>
        /// Egen TabControl som ritar flikar som blå “pills”, identiskt med Vol/Priser
        /// men utan stäng-kryss och utan rename – här är flikarna fasta.
        /// </summary>
        private sealed class PillTabControl : TabControl
        {
            public PillTabControl()
            {
                SetStyle(ControlStyles.UserPaint |
                         ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.ResizeRedraw, true);
                DoubleBuffered = true;
            }

            protected override void OnParentChanged(EventArgs e)
            {
                base.OnParentChanged(e);
                if (Parent != null)
                {
                    BackColor = Parent.BackColor;
                }
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                var g = e.Graphics;
                var bg = Parent != null ? Parent.BackColor : BackColor;

                g.Clear(bg);

                int bandTop = ClientRectangle.Bottom;
                for (int i = 0; i < TabPages.Count; i++)
                {
                    bandTop = Math.Min(bandTop, GetTabRect(i).Top);
                }

                using (var b = new SolidBrush(bg))
                {
                    var r = new Rectangle(
                        ClientRectangle.X,
                        Math.Max(0, bandTop - 1),
                        ClientRectangle.Width,
                        ClientRectangle.Bottom - Math.Max(0, bandTop - 1));
                    g.FillRectangle(b, r);
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                int bandTop = ClientRectangle.Bottom;
                for (int i = 0; i < TabPages.Count; i++)
                {
                    bandTop = Math.Min(bandTop, GetTabRect(i).Top);
                }

                for (int i = 0; i < TabPages.Count; i++)
                {
                    var rect = GetTabRect(i);

                    using (var wipe = new SolidBrush(BackColor))
                    {
                        g.FillRectangle(wipe, rect);
                    }

                    bool isSel = (i == SelectedIndex);

                    var pill = Rectangle.Inflate(rect, -2, -3);
                    pill.X -= 1;
                    pill.Width += 2;

                    int upOverlap = isSel ? 2 : 1;
                    int downOverlap = isSel ? 3 : 2;

                    int pillTop = Math.Max(0, bandTop - upOverlap);
                    int pillBottom = rect.Bottom + downOverlap;
                    pill.Y = pillTop;
                    pill.Height = pillBottom - pillTop;

                    using (var path = RoundedBottom(pill, TabCornerRadius))
                    {
                        if (isSel)
                        {
                            var top = ColorTranslator.FromHtml("#6FA3DF");
                            var bottom = TabBgActive;
                            using (var lg = new LinearGradientBrush(pill, top, bottom, 90f))
                            {
                                g.FillPath(lg, path);
                            }

                            using (var region = new Region(path))
                            {
                                g.SetClip(region, CombineMode.Replace);
                                var hiRect = new Rectangle(
                                    pill.X + 1,
                                    pill.Y + 1,
                                    pill.Width - 2,
                                    Math.Max(6, pill.Height / 3));
                                using (var hi = new LinearGradientBrush(
                                           hiRect,
                                           Color.FromArgb(120, Color.White),
                                           Color.FromArgb(0, Color.White),
                                           90f))
                                {
                                    g.FillRectangle(hi, hiRect);
                                }
                                g.ResetClip();
                            }

                            using (var pen = new Pen(TabOutline))
                            {
                                g.DrawPath(pen, path);
                            }
                        }
                        else
                        {
                            using (var bg = new SolidBrush(TabBgInactive))
                            {
                                g.FillPath(bg, path);
                            }
                            using (var pen = new Pen(TabOutline))
                            {
                                g.DrawPath(pen, path);
                            }
                        }
                    }

                    // Text – bold för vald flik
                    var drawFont = isSel ? new Font(Font, FontStyle.Bold) : Font;
                    bool created = isSel;

                    try
                    {
                        const int paddingX = 12;
                        const int paddingY = 4;

                        var textRect = new Rectangle(
                            pill.X + paddingX,
                            pill.Y + paddingY,
                            pill.Width - (paddingX * 2),
                            pill.Height - (paddingY * 2));

                        TextRenderer.DrawText(
                            g,
                            TabPages[i].Text ?? string.Empty,
                            drawFont,
                            textRect,
                            TabText,
                            TextFormatFlags.EndEllipsis |
                            TextFormatFlags.VerticalCenter |
                            TextFormatFlags.NoPrefix);
                    }
                    finally
                    {
                        if (created && drawFont != null)
                        {
                            drawFont.Dispose();
                        }
                    }
                }
            }
        }
    }
}
