using System;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using FX.UI.WinForms.Features.Blotter;



namespace FX.UI.WinForms
{
    public partial class BlotterWorkspaceControl : UserControl, IBlotterView
    {


        /// <summary>
        /// Presenter/view-model som äger blotter-datat för Options/Hedge.
        /// </summary>
        private BlotterPresenter _presenter;

        /// <summary>
        /// BindingSource för Options-griden (binder mot presenterns OptionsTrades-lista).
        /// </summary>
        //private BindingSource _optionsBindingSource;

        /// <summary>
        /// BindingSource för Hedge-griden (binder mot presenterns HedgeTrades-lista).
        /// </summary>
        //private BindingSource _hedgeBindingSource;

        /// <summary>
        /// Flagga som markerar om initial dataladdning redan är gjord.
        /// Förhindrar att vi laddar om varje gång fönstret aktiveras.
        /// </summary>
        private bool _initialLoadDone;


        public BlotterWorkspaceControl()
        {
            InitializeComponent();

            // Justera meny-padding och sidomeny-layout så de linjerar med innehållet.
            ConfigureMenuAndSidebarLayout();

            ConfigureGrid(dgvOptions);
            CreateColumnsOptions(dgvOptions);

            ConfigureGrid(dgvHedge);
            CreateColumnsHedge(dgvHedge);

            ConfigureGrid(dgvAll);
            CreateColumnsAll(dgvAll);

        }

        /// <summary>
        /// Initierar blotter-workspacet med en given presenter.
        /// Kopplar ihop presenter och view samt sätter upp databindning
        /// mot Options-, Hedge- och All-grids.
        /// </summary>
        /// <param name="presenter">Presenter som äger blotter-logiken.</param>
        public void Initialize(BlotterPresenter presenter)
        {
            if (presenter == null) throw new ArgumentNullException(nameof(presenter));

            _presenter = presenter;
            _presenter.AttachView(this);

            // Bind grids till presenterns BindingList:ar (read-only v1).
            var optionsSource = new BindingSource { DataSource = _presenter.OptionsTrades };
            var hedgeSource = new BindingSource { DataSource = _presenter.HedgeTrades };
            var allSource = new BindingSource { DataSource = _presenter.AllTrades };

            dgvOptions.DataSource = optionsSource;
            dgvHedge.DataSource = hedgeSource;
            dgvAll.DataSource = allSource;

            // Initiera row-count-labels till 0 tills första laddningen är gjord.
            UpdateRowCounts();
        }


        /// <summary>
        /// Anropas när blotter-fönstret aktiveras i shellen.
        /// Delegerar till presentern och uppdaterar sedan rad-räknarna.
        /// </summary>
        public void OnActivated()
        {
            if (_presenter == null)
            {
                return;
            }

            _presenter.OnActivated();

            // Efter att presentern ev. laddat initial data uppdaterar vi counts.
            UpdateRowCounts();
        }


        public void OnDeactivated() => _presenter?.OnDeactivated();

        /// <summary>
        /// Grid som används för att visa options-trades i blottern.
        /// Presentern binder sina options-rader mot denna grid.
        /// </summary>
        public DataGridView OptionsGrid => dgvOptions;

        /// <summary>
        /// Grid som används för att visa hedge/linjära trades i blottern.
        /// Presentern binder sina hedge-rader mot denna grid.
        /// </summary>
        public DataGridView HedgeGrid => dgvHedge;

        /// <summary>
        /// Grid som används för att visa alla trades (alla produkt-typer).
        /// Presentern binder sina "All"-rader mot denna grid.
        /// </summary>
        public DataGridView AllGrid => dgvAll;



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
                    var combo = new CustomComboBoxColumn
                    {

                        DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
                        FlatStyle = FlatStyle.Flat,
                        //DisplayStyleForCurrentCellOnly = true
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
                    var combo = new CustomComboBoxColumn
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
                    var combo = new CustomComboBoxColumn
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
                lblOptionsCount.Text = "0 trades";
                lblHedgeCount.Text = "0 trades";
                return;
            }

            lblOptionsCount.Text = _presenter.OptionsTrades.Count.ToString() + " trades";
            lblHedgeCount.Text = _presenter.HedgeTrades.Count.ToString() + " trades";
        }


        //Event handlers

        /// <summary>
        /// Hanterar manuellt refresh-kommando (meny eller toolbar).
        /// Delegerar till presentern och uppdaterar rad-räknarna i UI:t.
        /// </summary>
        private void HandleRefreshRequested(object sender, EventArgs e)
        {
            if (_presenter == null)
            {
                return;
            }

            _presenter.Refresh();
            UpdateRowCounts();
        }
    }

    public class CustomComboBoxCell : DataGridViewComboBoxCell
    {
        protected override void Paint(Graphics graphics,
            Rectangle clipBounds,
            Rectangle cellBounds,
            int rowIndex,
            DataGridViewElementStates cellState,
            object value,
            object formattedValue,
            string errorText,
            DataGridViewCellStyle cellStyle,
            DataGridViewAdvancedBorderStyle advancedBorderStyle,
            DataGridViewPaintParts paintParts)
        {
            // Låt basen rita allt som vanligt (text, borders, osv)
            base.Paint(graphics, clipBounds, cellBounds, rowIndex,
                       cellState, value, formattedValue, errorText,
                       cellStyle, advancedBorderStyle, paintParts);

            // Vilken bakgrund ska knappen ha? (selected eller ej)
            bool isSelected = (cellState & DataGridViewElementStates.Selected) != 0;
            Color backColor = isSelected
                ? cellStyle.SelectionBackColor
                : cellStyle.BackColor;

            // Rektangel för knapp/pil-ytan
            int buttonWidth = 18; // justera efter smak
            var buttonRect = new Rectangle(
                cellBounds.Right - buttonWidth - 1,
                cellBounds.Top + 1,
                buttonWidth,
                cellBounds.Height - 2);

            // Måla om knappytan i samma färg som cellen
            using (var backBrush = new SolidBrush(backColor))
            {
                graphics.FillRectangle(backBrush, buttonRect);
            }

            // (valfritt) tunn ram runt knappytan
            //using (var borderPen = new Pen(cellStyle.ForeColor))
            //{
            //    graphics.DrawRectangle(borderPen,
            //        buttonRect.X, buttonRect.Y,
            //        buttonRect.Width - 1, buttonRect.Height - 1);
            //}

            // Rita vit pil i mitten av knappytan
            int cx = buttonRect.Left + buttonRect.Width / 2;
            int cy = buttonRect.Top + buttonRect.Height / 2;

            Point[] arrow =
            {
            new Point(cx - 4, cy - 2),
            new Point(cx + 4, cy - 2),
            new Point(cx,     cy + 3)
        };

            using (var arrowBrush = new SolidBrush(Color.White))
            {
                graphics.FillPolygon(arrowBrush, arrow);
            }
        }
    }

    public class CustomComboBoxColumn : DataGridViewComboBoxColumn
    {
        public CustomComboBoxColumn()
        {
            this.CellTemplate = new CustomComboBoxCell();
            this.FlatStyle = FlatStyle.Flat; // ser oftast bäst ut ihop med custom-paint
        }
    }


}
