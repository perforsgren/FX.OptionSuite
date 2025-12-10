using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using FX.UI.WinForms.Features.Blotter;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;
using FxTradeHub.Contracts.Dtos;
using FxTradeHub.Services;


namespace FX.UI.WinForms
{
    public partial class BlotterWorkspaceControl : UserControl
    {


        /// <summary>
        /// Presenter/view-model som äger blotter-datat för Options/Hedge.
        /// </summary>
        private BlotterPresenter _presenter;

        /// <summary>
        /// BindingSource för Options-griden (binder mot presenter.OptionsTrades).
        /// </summary>
        private BindingSource _optionsBindingSource;

        /// <summary>
        /// BindingSource för Hedge/FX Linear-griden (binder mot presenter.HedgeTrades).
        /// </summary>
        private BindingSource _hedgeBindingSource;


        public BlotterWorkspaceControl()
        {
            InitializeComponent();

            // Justera meny-padding och sidomeny-layout så de linjerar med innehållet.
            ConfigureMenuAndSidebarLayout();

            ConfigureGrid(dgvOptions);
            CreateColumnsOptions(dgvOptions);

            ConfigureGrid(dgvHedge);
            CreateColumnsHedge(dgvHedge);

            //ConfigureGrid(dgvAll);
            //CreateColumnsAll(dgvAll);

        }

        /// <summary>
        /// Initialiserar blotter-workspacet med en presenter och kopplar
        /// presenter-listorna till Options- respektive Hedge-griden.
        /// 
        /// v1:
        /// - Skapar BindingSource:or mot presenter.OptionsTrades/HedgeTrades.
        /// - Gör en första laddning mot STP-hubben med ett enkelt datumfilter.
        /// </summary>
        /// <param name="presenter">
        /// BlotterPresenter som ansvarar för att läsa data från STP-hubben.
        /// Får inte vara null och får bara initieras en gång per kontroll-instans.
        /// </param>
        public void Initialize(BlotterPresenter presenter)
        {
            if (presenter == null) throw new ArgumentNullException(nameof(presenter));

            if (_presenter != null)
            {
                // Skydd mot dubbel-initiering om någon försöker kalla Initialize två gånger.
                throw new InvalidOperationException("BlotterWorkspaceControl är redan initialiserad.");
            }

            _presenter = presenter;

            // --- Skapa BindingSource:or och koppla till grids ---

            _optionsBindingSource = new BindingSource
            {
                DataSource = _presenter.OptionsTrades
            };
            dgvOptions.AutoGenerateColumns = false;
            dgvOptions.DataSource = _optionsBindingSource;

            _hedgeBindingSource = new BindingSource
            {
                DataSource = _presenter.HedgeTrades
            };
            dgvHedge.AutoGenerateColumns = false;
            dgvHedge.DataSource = _hedgeBindingSource;

            // --- Enkel default-filtrering v1 ---
            // v1: "senaste dygnet" runt idag, max t.ex. 2000 rader.
            var filter = new BlotterFilter
            {
                FromTradeDate = DateTime.Today.AddDays(-1),
                ToTradeDate = DateTime.Today.AddDays(1),
                MaxRows = 2000,
                // Kan användas server-side för "mina trades"-logik om du vill.
                CurrentUserId = Environment.UserName
            };

            _presenter.LoadInitial(filter);

            // Uppdatera rubrikerna med antal trades.
            UpdateRowCounts();
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

            //grid.BackgroundColor = SystemColors.Window;
            //grid.BorderStyle = BorderStyle.None;
            grid.EnableHeadersVisualStyles = false;

            // Header: ljusgrå + bold text
            //grid.ColumnHeadersDefaultCellStyle.BackColor = ColorTranslator.FromHtml("#E0E0E0");
            //grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.Black;
            //grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
            //grid.ColumnHeadersDefaultCellStyle.Font = new Font(grid.Font, FontStyle.Bold);

            //grid.DefaultCellStyle.BackColor = Color.White;
            //grid.DefaultCellStyle.ForeColor = Color.Black;
            //grid.DefaultCellStyle.SelectionBackColor = ColorTranslator.FromHtml("#CDE5FF");
            //grid.DefaultCellStyle.SelectionForeColor = Color.Black;

            //grid.AlternatingRowsDefaultCellStyle.BackColor = ColorTranslator.FromHtml("#F7F7F7");
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
        /// Skapar kolumnerna i Hedge-/FX Linear-griden baserat på blotter-metadata.
        /// Använder BlotterColumnMetadata.All och filtrerar på VisibleIn = Hedge.
        /// Kolumner med EditorType = Combo + IsEditable = true blir ComboBox-kolumner.
        /// </summary>
        /// <param name="grid">Grid som ska få hedge/linear-kolumner.</param>
        private void CreateColumnsHedge(DataGridView grid)
        {
            if (grid == null)
            {
                return;
            }

            grid.AutoGenerateColumns = false;
            grid.Columns.Clear();

            var defs = BlotterColumnMetadata.All
                .Where(d => (d.VisibleIn & BlotterGridVisibility.Hedge) != 0)
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
                    // Trader, PortfolioMx3 etc får sina värden via DataSource senare.

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

                // ReadOnly styrs av metadata (radens CanEdit hanterar vi i ett senare steg).
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
                    (def.DefaultVisibleIn & BlotterGridVisibility.Hedge) != 0;

                col.Visible = defaultVisible;

                grid.Columns.Add(col);
            }

            grid.ResumeLayout();
        }

        /// <summary>
        /// Skapar kolumnerna i "All trades"-griden baserat på blotter-metadata.
        /// Använder BlotterColumnMetadata.All och filtrerar på VisibleIn = All.
        /// Kolumner med EditorType = Combo + IsEditable = true blir ComboBox-kolumner.
        /// </summary>
        /// <param name="grid">Grid som ska få "All"-kolumner.</param>
        private void CreateColumnsAll(DataGridView grid)
        {
            if (grid == null)
            {
                return;
            }

            grid.AutoGenerateColumns = false;
            grid.Columns.Clear();

            var defs = BlotterColumnMetadata.All
                .Where(d => (d.VisibleIn & BlotterGridVisibility.All) != 0)
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
                    // Trader, PortfolioMx3 etc får sina värden via DataSource senare.

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

                // ReadOnly styrs av metadata (radens CanEdit hanterar vi i ett senare steg).
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
                    (def.DefaultVisibleIn & BlotterGridVisibility.All) != 0;

                col.Visible = defaultVisible;

                grid.Columns.Add(col);
            }

            grid.ResumeLayout();
        }



        // === Publikt API (activation) ===

        /// <summary>
        /// Anropas när blotter-fönstret aktiveras i shellen.
        /// Här ser vi bara till att sidomenyn hamnar i rätt höjd mot innehållet.
        /// </summary>
        public void OnActivated()
        {

        }

        /// <summary>
        /// Anropas när blotter-fönstret tappar fokus i shellen.
        /// Första versionen gör inget särskilt, men hooken finns för framtiden.
        /// </summary>
        public void OnDeactivated()
        {

        }

        // === Hjälpare ===

        /// <summary>
        /// Sätter padding/marginaler för top-menyn och sidomenyn så att
        /// första menyposten hamnar längre in från vänsterkanten och
        /// första sidomeny-knappen ligger närmare överkanten.
        /// </summary>
        private void ConfigureMenuAndSidebarLayout()
        {
            try
            {
                // Flytta hela menyraden åt höger så att "File" hamnar i linje
                // med innehållet (ca samma offset som vänster-sidomenyn).
                if (_menu != null)
                {
                    // Utgå från sidomenyns bredd som bas för offset.
                    int leftOffset = _sideBarHost != null ? _sideBarHost.Width + 6 : 16;

                    // Behåll standardvertikala padding (2 px) men lägg till vänsteroffset.
                    _menu.Padding = new Padding(leftOffset, 2, 4, 2);
                }

                if (_sideBar != null)
                {
                    // Minska intern top-padding i själva toolstripen
                    // så första knappen kommer högre upp.
                    _sideBar.Padding = new Padding(0, 6, 0, 0);

                    if (_sideBar.Items.Count > 0)
                    {
                        var firstItem = _sideBar.Items[0];
                        if (firstItem != null)
                        {
                            // Ta bort extra top-margin på första knappen
                            // och lämna bara en liten bottenmarginal.
                            firstItem.Margin = new Padding(0, 0, 0, 4);
                        }
                    }
                }
            }
            catch
            {
                // Layout-justeringar är "best effort" – får aldrig krascha UI:t.
            }
        }

        /// <summary>
        /// Uppdaterar rad-räknarna i Options- och Hedge-rubrikerna
        /// baserat på presenter-listornas aktuella antal rader.
        /// </summary>
        private void UpdateRowCounts()
        {
            if (_presenter == null)
            {
                lblOptionsCount.Text = "0";
                lblHedgeCount.Text = "0";
                return;
            }

            lblOptionsCount.Text = _presenter.OptionsTrades.Count.ToString();
            lblHedgeCount.Text = _presenter.HedgeTrades.Count.ToString();
        }

    }
}
