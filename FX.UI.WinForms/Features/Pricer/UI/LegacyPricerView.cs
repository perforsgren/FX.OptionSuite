using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using FX.Core.Domain.MarketData;
using static FX.UI.WinForms.LegacyPricerView;

namespace FX.UI.WinForms
{
    /// <summary>
    /// LegacyPricerView – WinForms-UI med per-leg inputs, procentformat för rd/rf/vol,
    /// Deal-push (Notional/Rd/Rf/Vol/Expiry/Spot) och korrekt reprice-routing per ben.
    /// </summary>
    public sealed class LegacyPricerView : UserControl
    {
        #region === Types & Labels ===

        public enum Section { DealDetails, MktData, Pricing, Risk }

        private sealed class RowSpec
        {
            public string Label;
            public Section Sec;
            public string Key;
            public string Summary;
            public bool IsDate;
            public RowSpec(string label, Section sec, string key, string summary = null, bool isDate = false)
            { Label = label; Sec = sec; Key = key; Summary = summary; IsDate = isDate; }
        }

        public static class L
        {
            public const string DealDetails = "Deal Details";
            public const string MktData = "Mkt Data";
            public const string Pricing = "Pricing";
            public const string Risk = "Risk";

            public const string Pair = "CcyPair";
            public const string Notional = "Notional (Base)";
            public const string Side = "Buy/Sell";
            public const string Expiry = "Expiry";
            public const string Delivery = "Delivery";
            public const string Strike = "Strike";
            public const string CallPut = "Call/Put";

            public const string Spot = "Spot Rate";
            public const string FwdPts = "Forward Points";
            public const string FwdRate = "Forward Rate";
            public const string Rd = "rd QUOTE %";
            public const string Rf = "rf BASE %";
            public const string Vol = "Vol %";
            public const string VolSprd = "Vol Spread";

            public const string PremUnit = "Premium per unit";
            public const string PremTot = "Premium total (Quote)";
            public const string PremPips = "Premium (pips)";
            public const string PremPct = "% of Notional";

            public const string Delta = "Delta (position)";
            public const string DeltaPct = "Delta (%)";
            public const string Vega = "Vega (position)";
            public const string Gamma = "Gamma (position)";
            public const string Theta = "Theta (position)";
        }

        #endregion

        #region === Constants & Colors ===

        private const int ColumnDealWidth = 180;
        private const int ColumnFieldWidth = 220;
        private const int ColumnLegWidth = 190;

        private const int GlyphWidth = 9;
        private const int GlyphHeight = 6;
        private const int GlyphRightPadding = 10;

        private static readonly Color HeaderBack = Color.FromArgb(242, 246, 251);
        private static readonly Color SectionBack = Color.FromArgb(225, 230, 235);
        private static readonly Color FieldBack = Color.FromArgb(235, 238, 242);
        private static readonly Color DealBack = Color.FromArgb(245, 247, 250);
        private static readonly Color RowAltBack = Color.FromArgb(250, 251, 253);
        private static readonly Color FieldFore = Color.FromArgb(30, 40, 50);
        private static readonly Color SectionFore = Color.FromArgb(35, 45, 55);

        #endregion

        #region === Fields & State ===

        private DataGridView _dgv;
        private DataGridViewCell _pendingBeginEditCell;

        private string[] _legs = new[] { "Vanilla 1" };
        private readonly List<RowSpec> _rows = new List<RowSpec>
        {
            new RowSpec(L.DealDetails, Section.DealDetails, "SECTION"),
            new RowSpec(L.Pair,         Section.DealDetails, "pair"),
            new RowSpec(L.Notional,     Section.DealDetails, "notional"),
            new RowSpec(L.Side,         Section.DealDetails, "side"),
            new RowSpec(L.Expiry,       Section.DealDetails, "expiry"),
            new RowSpec(L.Delivery,     Section.DealDetails, "delivery"),
            new RowSpec(L.Strike,       Section.DealDetails, "strike"),
            new RowSpec(L.CallPut,      Section.DealDetails, "payoff"),

            new RowSpec(L.MktData, Section.MktData, "SECTION"),
            new RowSpec(L.Spot,        Section.MktData, "spot"),
            new RowSpec(L.FwdPts,      Section.MktData, "fwdpts"),
            new RowSpec(L.FwdRate,     Section.MktData, "fwdrate"),
            new RowSpec(L.Rd,          Section.MktData, "rd"),
            new RowSpec(L.Rf,          Section.MktData, "rf"),
            new RowSpec(L.Vol,         Section.MktData, "vol"),
            new RowSpec(L.VolSprd,     Section.MktData, "volsprd"),

            new RowSpec(L.Pricing, Section.Pricing, "SECTION"),
            new RowSpec(L.PremUnit, Section.Pricing, "prem_unit"),
            new RowSpec(L.PremTot,  Section.Pricing, "prem_total", "SUM"),
            new RowSpec(L.PremPips, Section.Pricing, "prem_pips"),
            new RowSpec(L.PremPct,  Section.Pricing, "prem_pct", "AVG"),

            new RowSpec(L.Risk, Section.Risk, "SECTION"),
            new RowSpec(L.Delta,    Section.Risk, "delta",    "SUM"),
            new RowSpec(L.DeltaPct, Section.Risk, "deltapct", "AVG"),
            new RowSpec(L.Vega,     Section.Risk, "vega",     "SUM"),
            new RowSpec(L.Gamma,    Section.Risk, "gamma",    "SUM"),
            new RowSpec(L.Theta,    Section.Risk, "theta",    "SUM"),
        };

        // Expiry-hint per ben (t.ex. "[1M]") och backup av tidigare text för rollback
        private readonly Dictionary<string, string> _expiryRawHintByCol = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _expiryPrevDisplayByCol = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private Font _hintFont;

        private static readonly Color OverrideFore = Color.Purple; // ändra om du vill
        private readonly HashSet<string> _overrides = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Color> _origFore = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);

        // Snapshot av logiskt startvärde under edit (per cell)
        private readonly Dictionary<string, object> _editStart = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        // Nyckelåteranvändning
        private string Key(string label, string col) => label + "|" + col;

        // Feed-baslinje per cell (logiskt värde: double eller VolCellData)
        private readonly Dictionary<string, object> _feedValue = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);


        private enum PricingCurrency { Quote, Base } // default: Quote
        private PricingCurrency _pricingCurrency = PricingCurrency.Quote;
        private bool _suppressNextSectionToggle; // hindra infällning när vi klickar knappen


        private Rectangle _pricingBtnRect = Rectangle.Empty;
        private bool _pricingBtnHover = false;
        private bool _pricingBtnPressed = false;
        private int _pricingHeaderRow = -1; // sätts när du hittar Pricing-raden

        private bool _mktBtnPressed;
        private bool _mktBtnInside;

        // Fält i klassen (utöver de du redan har)
        private int _mktHeaderRow = -1;
        private Rectangle _mktBtnRect = Rectangle.Empty;

        private enum DisplayCcy { Quote, Base } // intern spegling
        private DisplayCcy _displayCcy = DisplayCcy.Quote; // default

        /// <summary>
        /// Visnings-/editeringsläge för SPOT i UI.
        /// Endast för Spot (andra fält kan få egna modes senare).
        /// </summary>
        public enum SpotMode { Mid = 0, Full = 1, Live = 3 }
        private SpotMode _spotMode = SpotMode.Mid;


        #endregion

        #region === Events ===

        public event EventHandler<PriceRequestUiArgs> PriceRequested;
        public event EventHandler NotionalChanged;
        public event EventHandler<ExpiryEditRequestedEventArgs> ExpiryEditRequested;
        public event EventHandler AddLegRequested; //Fired när användaren vill lägga till ett nytt ben(via F6 eller annat UI).

        /// <summary>
        /// Signal upp till presentern att Spot ändrats av användaren (parsningsklar, normaliserad two-way).
        /// Presentern kan då uppdatera MarketStore och trigga omprisning centralt.
        /// </summary>
        public event EventHandler<SpotEditedEventArgs> SpotEdited;

        /// <summary>
        /// Fired när användaren växlar SPOT-läge (Mid ⇆ Full / Live) i UI.
        /// Presentern använder detta för att uppdatera MarketStore (ViewMode/Override) och trigga prisning centralt.
        /// </summary>
        public event EventHandler SpotModeChanged;

        public event EventHandler PairChanged;

        /// <summary>
        /// Begär att nya räntor (RD/RF) ska hämtas från källa (force refresh).
        /// Presentern lyssnar och initierar reprice med ForceRefreshRates=true.
        /// </summary>
        public event EventHandler RatesRefreshRequested;

        #endregion

        #region === EventArgs

        /// <summary>Event-data när användaren editerat Spot i gridet.</summary>
        public sealed class SpotEditedEventArgs : EventArgs
        {
            /// <summary>”EURSEK”, ”USDJPY”, … (utan ”/”).</summary>
            public string Pair6 { get; set; }

            /// <summary>Tvåvägsvärdet som användaren angav. Om mid skrevs in är Bid==Ask.</summary>
            public double Bid { get; set; }
            public double Ask { get; set; }

            /// <summary>True om inmatningen var ett ensamt tal (tolkas som mid).</summary>
            public bool WasMid { get; set; }

            /// <summary>True om editen gjordes i Deal-kolumnen (push mot alla ben i UI).</summary>
            public bool IsDealLevel { get; set; }

        }

        #endregion


        private bool _suspendDealPricingSummary = true;
        public void SuspendDealPricingSummary(bool on) => _suspendDealPricingSummary = on;

        public event EventHandler SpotRefreshRequested;

        #region === Constructor & init ===

        /// <summary>
        /// Initierar grid, kopplar events, seedar värden och snapshot:ar UI som feed-baseline.
        /// </summary>
        public LegacyPricerView()
        {
            Dock = DockStyle.Fill;
            BackColor = Color.White;

            BuildGrid();
            ApplyLegacyTheme();
            WireEvents();
            SeedDemoValues();
            SnapshotFeedFromGrid(); // baseline från nuvarande visning

            // Säkerställ Tag-snapshots på startup (så Presentern alltid har bid/ask att läsa)
            EnsureSpotSnapshotsForAll();

            // Rita om spotraden direkt så visningen matchar Tag
            InvalidateSpotRow();

            _dgv.EditingControlShowing += (s, e) =>
            {
                var cell = _dgv.CurrentCell; if (cell == null) return;
                string rowLabel = Convert.ToString(_dgv.Rows[cell.RowIndex].Cells["FIELD"].Value ?? "");
                if (!string.Equals(rowLabel, L.Expiry, StringComparison.OrdinalIgnoreCase)) return;
                if (_dgv.Columns[cell.ColumnIndex].Name != FirstLegColumn()) return;

                var tb = e.Control as TextBox; if (tb == null) return;
                var data = cell.Tag as ExpiryCellData;
                tb.Text = data != null && !string.IsNullOrWhiteSpace(data.Raw) ? data.Raw : "";
                tb.SelectionStart = tb.TextLength;
            };
        }

        private void ApplyLegacyTheme()
        {
            // 1) Lås font för hela pricer-vyn (välj den storlek du vill ha som “baseline”)
            //    8.25f var vanligt i WinForms, eller 9f om du vill ha lite större.
            var baseFont = new Font("Segoe UI", 8.25f, FontStyle.Regular, GraphicsUnit.Point);
            this.Font = baseFont;
            _dgv.Font = baseFont;

            // 2) Radhöjd som matchar fonten + lite luft för vertikal centrering
            int rowH = TextRenderer.MeasureText("Ag", baseFont).Height + 8;  // +8 ger lite padding
            _dgv.RowTemplate.Height = rowH;
            _dgv.RowHeadersWidth = 28; // valfritt, men brukar se bättre ut

            // 3) Headerfont kan gärna vara fet för tydlighet
            _dgv.ColumnHeadersDefaultCellStyle.Font = new Font(baseFont, FontStyle.Bold);

            // 4) Säkerställ att celler (som inte specialmålas) centreras vertikalt snyggt
            _dgv.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight; // din default
            _dgv.DefaultCellStyle.Padding = new Padding(0, 0, 0, 0);

            // 5) Om du har kolumner med text som ska centreras (inte Spot som du målar själv):
            //_dgv.Columns["FIELD"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
        }

        /// <summary>Använder mindre font för “hint” i expiry-fältet när handtaget är skapat.</summary>
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (_hintFont == null)
                _hintFont = new Font(this.Font.FontFamily, Math.Max(6f, this.Font.Size - 1f), FontStyle.Regular);
        }

        /// <summary>Disponerar ev. hint-font.</summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing && _hintFont != null) _hintFont.Dispose();
            base.Dispose(disposing);
        }

        /// <summary>Skapar DataGridView, kolumner, rader och basstilar.</summary>
        private void BuildGrid()
        {
            _dgv = new DataGridView
            {
                Dock = DockStyle.Fill,
                AutoGenerateColumns = false,
                SelectionMode = DataGridViewSelectionMode.CellSelect,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                EnableHeadersVisualStyles = false,
                BackgroundColor = Color.White,
                GridColor = Color.Gainsboro,
                ColumnHeadersHeight = 34,
                EditMode = DataGridViewEditMode.EditProgrammatically
            };

            typeof(DataGridView).InvokeMember("DoubleBuffered",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.SetProperty,
                null, _dgv, new object[] { true });

            _dgv.ColumnHeadersDefaultCellStyle.BackColor = HeaderBack;
            _dgv.ColumnHeadersDefaultCellStyle.ForeColor = Color.Black;
            _dgv.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;

            Controls.Add(_dgv);

            // Columns
            var colDeal = new DataGridViewTextBoxColumn
            {
                Name = "Deal",
                HeaderText = "Deal",
                Width = ColumnDealWidth,
                Frozen = true,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Alignment = DataGridViewContentAlignment.MiddleRight,
                    BackColor = DealBack,
                    Padding = new Padding(10, 0, 10, 0)
                }
            };
            _dgv.Columns.Add(colDeal);

            var colField = new DataGridViewTextBoxColumn
            {
                Name = "FIELD",
                HeaderText = "",
                Width = ColumnFieldWidth,
                Frozen = true,
                ReadOnly = true,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Alignment = DataGridViewContentAlignment.MiddleLeft,
                    BackColor = FieldBack,
                    ForeColor = FieldFore,
                    Font = new Font(Font, FontStyle.Bold),
                    Padding = new Padding(12, 0, 8, 0)
                }
            };
            _dgv.Columns.Add(colField);

            for (int i = 0; i < _legs.Length; i++)
            {
                var col = new DataGridViewTextBoxColumn
                {
                    Name = _legs[i],
                    HeaderText = _legs[i],
                    Width = ColumnLegWidth,
                    ValueType = typeof(string),
                    DefaultCellStyle = new DataGridViewCellStyle
                    {
                        Alignment = DataGridViewContentAlignment.MiddleRight,
                        Padding = new Padding(0, 0, 10, 0)
                    }
                };
                _dgv.Columns.Add(col);
            }

            // Rows
            foreach (var r in _rows)
            {
                int idx = _dgv.Rows.Add();
                var row = _dgv.Rows[idx];

                if (r.Key == "SECTION")
                {
                    bool isDetails = (r.Sec == Section.DealDetails);
                    row.Tag = r.Sec;
                    row.ReadOnly = true;

                    row.Cells["FIELD"].Value = isDetails ? r.Label : "▾  " + r.Label;
                    row.DefaultCellStyle.BackColor = SectionBack;
                    row.DefaultCellStyle.ForeColor = SectionFore;
                    row.DefaultCellStyle.Font = new Font(Font, FontStyle.Bold);
                    row.DefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
                    row.Cells["Deal"].Value = "";
                }
                else
                {
                    row.Cells["FIELD"].Value = r.Label;
                    row.DefaultCellStyle.BackColor = (idx % 2 == 0) ? RowAltBack : Color.White;
                }
            }

            foreach (DataGridViewColumn c in _dgv.Columns)
                c.SortMode = DataGridViewColumnSortMode.NotSortable;
            _dgv.ColumnAdded += (s, e) => e.Column.SortMode = DataGridViewColumnSortMode.NotSortable;

            ApplySpecialReadOnlyRules();
            SnapshotOriginalForeColors();
        }

        /// <summary>ReadOnly-regler och gråtext för Pair/Delivery/Spot i ben, Deal-editbar mm.</summary>
        /// <summary>ReadOnly-regler och gråtext för Pair/Delivery/Spot i ben, samt låsning av Pricing/Risk.</summary>
        private void ApplySpecialReadOnlyRules()
        {
            // Pair: Deal editbar; legs readonly + grå
            int rPair = FindRow(L.Pair);
            if (rPair >= 0)
            {
                _dgv.Rows[rPair].Cells["Deal"].ReadOnly = false;
                foreach (var leg in _legs)
                {
                    var c = _dgv.Rows[rPair].Cells[leg];
                    c.ReadOnly = true;
                    c.Style.ForeColor = Color.Gray;
                    c.Style.Format = "";
                }
            }

            // Expiry/Delivery i Deal: lås + clear; Delivery även readonly i legs och GRÅ
            SetDealCellReadOnlyAndClear(L.Expiry, false);
            SetDealCellReadOnlyAndClear(L.Delivery, true);
            SetRowReadOnly(L.Delivery, true);
            int rDel = FindRow(L.Delivery);
            if (rDel >= 0)
            {
                foreach (var leg in _legs)
                {
                    var c = _dgv.Rows[rDel].Cells[leg];
                    c.ReadOnly = true;
                    c.Style.ForeColor = Color.Gray;
                    c.Style.Format = "";
                }
            }

            // Spot: Deal editbar; legs readonly
            SetRowReadOnly(L.Spot, true);
            int rSpot = FindRow(L.Spot);
            if (rSpot >= 0)
            {
                foreach (var leg in _legs)
                {
                    var cell = _dgv.Rows[rSpot].Cells[leg];
                    cell.ReadOnly = true;
                    cell.Style.ForeColor = Color.Gray;
                    cell.Style.Format = "";
                }
                _dgv.Rows[rSpot].Cells["Deal"].ReadOnly = false;
            }

            // === NYTT: Lås alla celler i sektionerna Pricing och Risk (Deal + legs) ===
            foreach (var spec in _rows)
            {
                if ((spec.Sec == Section.Pricing || spec.Sec == Section.Risk) && spec.Key != "SECTION")
                {
                    SetRowReadOnly(spec.Label, true);
                }
            }
        }

        /// <summary>Kopplar alla grid-händelser (click, paint, edit, key, dbl-click).</summary>
        private void WireEvents()
        {
            _dgv.CellClick += Dgv_CellClick;
            _dgv.CellMouseClick += Dgv_CellMouseClick;
            _dgv.CellMouseDown += Dgv_CellMouseDown;
            _dgv.CellPainting += Dgv_CellPainting;
            _dgv.CellBeginEdit += Dgv_CellBeginEdit;
            _dgv.CellEndEdit += Dgv_CellEndEdit;
            _dgv.KeyPress += Dgv_KeyPressStartEdit;
            _dgv.KeyDown += Dgv_KeyDownStartEdit;

            _dgv.CellMouseDown += Dgv_CellMouseDown_PricingButton;
            _dgv.CellMouseUp += Dgv_CellMouseUp_PricingButton;
            _dgv.CellMouseMove += Dgv_CellMouseMove_PricingButton;
            _dgv.CellMouseLeave += Dgv_CellMouseLeave_PricingButton;

            _dgv.CellMouseDown += Dgv_CellMouseDown_MktDataButton;
            _dgv.CellMouseUp += Dgv_CellMouseUp_MktDataButton;
            _dgv.CellMouseMove += Dgv_CellMouseMove_MktDataButton;

            _dgv.CellDoubleClick += (s, e) =>
            {
                if (e.RowIndex >= 0 && e.ColumnIndex >= 0 && !_dgv.Rows[e.RowIndex].Cells[e.ColumnIndex].ReadOnly)
                    _dgv.BeginEdit(true);
            };

            // Vid scroll/resize räknar vi om knapprutan i nästa målning
            _dgv.Scroll += (s, e) => _pricingBtnRect = Rectangle.Empty;
            _dgv.Resize += (s, e) => _pricingBtnRect = Rectangle.Empty;
        }

        void SeedDemoValues()
        {
            Set("Deal", L.Pair, "EURSEK");
            Set("Deal", L.Notional, "10 000 000");
            Set("Deal", L.Side, "Buy");
            Set("Deal", L.CallPut, "Call");
            Set("Deal", L.Strike, "11.0000");

            // Sprid Deal → legs, sedan RD/RF
            CopyDealToLegs(Section.DealDetails, keepDeal: false);
            CopyDealToLegs(Section.MktData, keepDeal: false);

            int rRd = FindRow(L.Rd);
            int rRf = FindRow(L.Rf);
            foreach (var leg in _legs)
            {
                var cRd = _dgv.Rows[rRd].Cells[leg];
                cRd.Tag = 0.020;
                cRd.Value = FormatPercent(0.020, 3); // 3 dp

                var cRf = _dgv.Rows[rRf].Cells[leg];
                cRf.Tag = 0.010;
                cRf.Value = FormatPercent(0.010, 3); // 3 dp
            }

            // SEED: tvåvägs vol 5.0/6.0 = 0.050/0.060 (3 dp i display)
            int rVol = FindRow(L.Vol);
            if (rVol >= 0)
            {
                foreach (var leg in _legs)
                {
                    var cVol = _dgv.Rows[rVol].Cells[leg];
                    var v = new VolCellData { Bid = 0.050, Ask = 0.060 };
                    cVol.Tag = v;
                    cVol.Value = FormatPercentPair(v.Bid, v.Ask, 3); // "5.000% / 6.000%"
                }
            }

            ApplyPremiumCurrencyLabel();

            // räkna fram Vol spread (3 dp) från seedad vol
            RecalcVolSpreadForAllLegs();
        }

        #endregion

        #region === Event handlers ===

        private void Dgv_CellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;

            string rowLabel = Convert.ToString(_dgv.Rows[e.RowIndex].Cells["FIELD"].Value ?? "");
            string colName = _dgv.Columns[e.ColumnIndex].Name;

            // Snabb-edit på Spot/Deal (din befintliga logik)
            if (rowLabel.Equals(L.Spot, StringComparison.OrdinalIgnoreCase) && colName == "Deal")
            {
                if (!_dgv.Rows[e.RowIndex].Cells[e.ColumnIndex].ReadOnly)
                {
                    _dgv.CurrentCell = _dgv.Rows[e.RowIndex].Cells[e.ColumnIndex];
                    _dgv.BeginEdit(true);
                }
                return;
            }

            // Sektion-toggle via FIELD-kolumnen
            if (e.ColumnIndex == _dgv.Columns["FIELD"].Index)
            {
                var row = _dgv.Rows[e.RowIndex];

                if (row.Tag is Section s && s != Section.DealDetails)
                {
                    string norm = (rowLabel ?? "").TrimStart(' ', '▾', '▸');
                    bool isPricing = (s == Section.Pricing) || string.Equals(norm, L.Pricing, StringComparison.OrdinalIgnoreCase);
                    bool isMktData = (s == Section.MktData) || string.Equals(norm, L.MktData, StringComparison.OrdinalIgnoreCase);

                    // VIKTIGT: om någon av våra header-knappar (Pricing eller Mkt Data) var nedtryckt,
                    // undertryck fäll/expand EN gång.
                    //if ((isPricing || isMktData) && _suppressNextSectionToggle)
                    if (isPricing && _suppressNextSectionToggle)
                        {
                        _suppressNextSectionToggle = false; // reset för nästa klick
                        return; // hoppa över ToggleSection
                    }

                    // Din ordinarie fäll/expand-logik
                    string title = Convert.ToString(row.Cells["FIELD"].Value) ?? "";
                    string trimmed = title.TrimStart();
                    if (trimmed.StartsWith("▾") || trimmed.StartsWith("▸"))
                        ToggleSection(e.RowIndex);
                }
            }
        }

        /// <summary>Startar edit på Spot/Deal via mousedown om cellen är redigerbar.</summary>
        private void Dgv_CellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;

            string rowLabel = Convert.ToString(_dgv.Rows[e.RowIndex].Cells["FIELD"].Value ?? "");
            string colName = _dgv.Columns[e.ColumnIndex].Name;

            if (rowLabel.Equals(L.Spot, StringComparison.OrdinalIgnoreCase) && colName == "Deal")
            {
                var cell = _dgv.Rows[e.RowIndex].Cells[e.ColumnIndex];
                if (!cell.ReadOnly)
                {
                    _pendingBeginEditCell = cell;
                    _dgv.CurrentCell = cell;
                    _dgv.BeginEdit(true);
                    _dgv.InvalidateCell(cell);
                }
            }
        }

        private void Dgv_CellMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;

            string rowLabel = Convert.ToString(_dgv.Rows[e.RowIndex].Cells["FIELD"].Value ?? "");
            string colName = _dgv.Columns[e.ColumnIndex].Name;

            Rectangle cellRect = _dgv.GetCellDisplayRectangle(e.ColumnIndex, e.RowIndex, false);
            Rectangle glyphRectLocal = GetGlyphRectLocal(cellRect);
            bool hit = glyphRectLocal.Contains(e.Location);
            if (!hit) return;

            // ▼ i ben → toggle side/payoff (OFÖRÄNDRAT + invalidates för bold)
            if (IsLegColumn(colName))
            {
                if (string.Equals(rowLabel, L.Side, StringComparison.OrdinalIgnoreCase))
                {
                    ToggleSideInCell(e.RowIndex, colName);
                    RecalcDerivedForColumn(colName);

                    // ===== [ADDED] Bold-overlay ska uppdateras direkt =====
                    _dgv.InvalidateRow(R(L.PremUnit));
                    _dgv.InvalidateRow(R(L.PremTot));
                    return;
                }
                else if (string.Equals(rowLabel, L.CallPut, StringComparison.OrdinalIgnoreCase))
                {
                    ToggleCallPutInCell(e.RowIndex, colName);
                    RaisePriceRequestedForLeg(colName);
                    return;
                }
            }

            // ▼ i FIELD på “Buy/Sell” → mass-toggle (OFÖRÄNDRAT + invalidates för bold)
            if (colName == "FIELD" && string.Equals(rowLabel, L.Side, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var leg in _legs) ToggleSideInCell(e.RowIndex, leg);
                RecalcDerivedForAllLegs();

                // ===== [ADDED] Uppdatera bold direkt även vid mass-toggle =====
                _dgv.InvalidateRow(R(L.PremUnit));
                _dgv.InvalidateRow(R(L.PremTot));

                _dgv.InvalidateRow(e.RowIndex);
                return;
            }
        }

        /// <summary>Sätter rollback-state och edit-start snapshot för ändringsdetektering.</summary>
        private void Dgv_CellBeginEdit(object sender, DataGridViewCellCancelEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;

            string lbl = Convert.ToString(_dgv.Rows[e.RowIndex].Cells["FIELD"].Value ?? "");
            var cell = _dgv.Rows[e.RowIndex].Cells[e.ColumnIndex];
            string col = _dgv.Columns[e.ColumnIndex].Name;

            // Spara föregående visningsvärde i Tag för rollback om användaren lämnar cellen tom
            if (string.Equals(lbl, L.Notional, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(lbl, L.Side, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(lbl, L.CallPut, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(lbl, L.Rd, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(lbl, L.Rf, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(lbl, L.Vol, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(lbl, L.Spot, StringComparison.OrdinalIgnoreCase))        // <-- Spot
            {
                cell.Tag = cell.Tag ?? cell.Value; // Tag = "senast visade"
            }

            // Expiry: spara tidigare visning för rollback (separat cache)
            if (string.Equals(lbl, L.Expiry, StringComparison.OrdinalIgnoreCase))
            {
                _expiryPrevDisplayByCol[col] = Convert.ToString(cell.Value ?? "");
            }

            string k = Key(lbl, col);

            if (lbl.Equals(L.Spot, StringComparison.OrdinalIgnoreCase))
            {
                _editStart[k] = ReadSpot(col); // double
            }
            else if (lbl.Equals(L.Notional, StringComparison.OrdinalIgnoreCase))
            {
                _editStart[k] = ReadNotional(col); // double
            }
            else if (lbl.Equals(L.Rd, StringComparison.OrdinalIgnoreCase))
            {
                _editStart[k] = ReadRd(col); // double decimal
            }
            else if (lbl.Equals(L.Rf, StringComparison.OrdinalIgnoreCase))
            {
                _editStart[k] = ReadRf(col); // double decimal
            }
            else if (lbl.Equals(L.Vol, StringComparison.OrdinalIgnoreCase))
            {
                // Föredra Tag om VolCellData finns; annars tolka display till (bid,ask)
                if (cell.Tag is VolCellData vcd) _editStart[k] = new VolCellData { Bid = vcd.Bid, Ask = vcd.Ask };
                else if (TryParseVolInput(Convert.ToString(cell.Value ?? ""), out var b, out var a, out _))
                    _editStart[k] = new VolCellData { Bid = b, Ask = a };
            }
            else if (lbl.Equals(L.Strike, StringComparison.OrdinalIgnoreCase))
            {
                // Spara startvärde som double för ändringsdetektering i EndEdit
                double s0;
                if (TryParseCellNumber(ReadStrike(col), out s0))
                    _editStart[k] = s0; // double
            }
        }

        /// <summary>
        /// Validerar, normaliserar, rollback vid fel, samt sätter override-färg endast om ändrat
        /// (feed-baseline jämförelse) och triggar repricing för berörda ben.
        /// </summary>
        private void Dgv_CellEndEdit(object sender, DataGridViewCellEventArgs e)
        {
            _pendingBeginEditCell = null;
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;

            var row = _dgv.Rows[e.RowIndex];
            string lbl = Convert.ToString(row.Cells["FIELD"].Value ?? "");
            string col = _dgv.Columns[e.ColumnIndex].Name;
            var cell = row.Cells[e.ColumnIndex];
            string rowLabel = Convert.ToString(_dgv.Rows[e.RowIndex].Cells["FIELD"].Value ?? "");

            // Rollback som bevarar rätt format (%, vol-par)
            Action rollback = () =>
            {
                if (string.Equals(lbl, L.Rd, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(lbl, L.Rf, StringComparison.OrdinalIgnoreCase))
                {
                    if (cell.Tag is double decPct)
                        cell.Value = FormatPercent(decPct, 3);
                    else
                        cell.Value = Convert.ToString(cell.Tag ?? cell.Value ?? "");
                }
                else if (string.Equals(lbl, L.Vol, StringComparison.OrdinalIgnoreCase))
                {
                    if (cell.Tag is VolCellData vtag)
                    {
                        if (Math.Abs(vtag.Bid - vtag.Ask) < 1e-12)
                            cell.Value = FormatPercent(vtag.Bid, 2);
                        else
                            cell.Value = FormatPercentPair(vtag.Bid, vtag.Ask, 2);
                    }
                    else if (cell.Tag is double vdec)
                    {
                        cell.Value = FormatPercent(vdec, 2);
                    }
                    else
                    {
                        cell.Value = Convert.ToString(cell.Tag ?? cell.Value ?? "");
                    }
                }
                else
                {
                    cell.Value = Convert.ToString(cell.Tag ?? cell.Value ?? "");
                }
                _dgv.InvalidateCell(e.ColumnIndex, e.RowIndex);
            };

            // ===== SPOT =====
            if (string.Equals(rowLabel, L.Spot, StringComparison.OrdinalIgnoreCase))
            {
                string raw = Convert.ToString(cell.Value ?? "").Trim();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    rollback();
                    return;
                }

                // 1) Parse UI-input → two-way (mid = ett tal; two-way = "b/a" el. "b a" el. "b,a")
                bool wasMid;
                var tw = MarketParser.ParseToTwoWay(raw, out wasMid);
                var bid = tw.Bid;
                var ask = tw.Ask;
                if (ask < bid) { var t = bid; bid = ask; ask = t; } // extra monotoni-säkerhet
                double mid = 0.5 * (bid + ask);

                // 2) Ändringsdetektering mot edit-start (undvik onödig reprissning)
                string kStart = Key(L.Spot, col);
                bool changed = true; object startObj;
                if (_editStart.TryGetValue(kStart, out startObj) && startObj is double s0)
                    changed = Math.Abs(s0 - mid) > 1e-9;
                _editStart.Remove(kStart);

                // 3) Enligt regel: Spot editeras endast i Deal (inte i ben)
                if (!string.Equals(col, "Deal", StringComparison.OrdinalIgnoreCase))
                {
                    rollback();
                    _dgv.InvalidateCell(e.ColumnIndex, e.RowIndex);
                    return;
                }

                // 4) Normaliserad visning i Deal-cellen (minst 4 d.p., behåll fler om user skrev fler)
                string display = FormatSpotWithMinDecimals(raw, mid, 4);
                cell.Value = display;

                // 5) Tag på Deal (snapshot med two-way)
                cell.Tag = new SpotCellData
                {
                    Bid = bid,
                    Mid = mid,
                    Ask = ask,
                    TimeUtc = DateTime.UtcNow,
                    Source = "User"
                };

                // 6) Push till alla ben (UI-state + visning)
                int rSpot = FindRow(L.Spot);
                for (int i = 0; i < _legs.Length; i++)
                {
                    var lg = _legs[i];
                    var cLeg = _dgv.Rows[rSpot].Cells[lg];

                    cLeg.Value = display;
                    cLeg.Tag = new SpotCellData
                    {
                        Bid = bid,
                        Mid = mid,
                        Ask = ask,
                        TimeUtc = DateTime.UtcNow,
                        Source = "User"
                    };

                    // Override-färg relativt feed-baseline (per ben)
                    double fLeg;
                    bool atFeedLeg = TryGetFeedDouble(L.Spot, lg, out fLeg) && Math.Abs(fLeg - mid) <= 1e-9;
                    MarkOverride(L.Spot, lg, !atFeedLeg);
                }

                // 7) Override-färg för Deal relativt ev. Deal-feed-baseline
                double fDeal;
                bool atFeedDeal = TryGetFeedDouble(L.Spot, "Deal", out fDeal) && Math.Abs(fDeal - mid) <= 1e-9;
                MarkOverride(L.Spot, "Deal", !atFeedDeal);

                // 8) Växla läge baserat på inmatning:
                //    - Two-way → Full
                //    - Mid (bid==ask) → Mid
                var desired = (Math.Abs(bid - ask) > 1e-12) ? SpotMode.Full : SpotMode.Mid;
                var oldMode = _spotMode;
                if (oldMode != desired)
                {
                    _spotMode = desired;

                    // FULL/LIVE: se till att tvåvägssnapshots finns
                    if (_spotMode == SpotMode.Full || _spotMode == SpotMode.Live)
                        EnsureSpotSnapshotsForAll();

                    // Nollställ knapp-state och tvinga repaint (fix för att chipet ibland inte målades om)
                    _mktBtnPressed = false;
                    _mktBtnInside = false;
                    _dgv.Cursor = Cursors.Default;
                    _mktBtnRect = Rectangle.Empty;   // räkna om rect i nästa målning

                    // Invalidera och tvinga grid att rita om efter att edit-loopen är klar
                    this.BeginInvoke((Action)(() =>
                    {
                        _dgv.Invalidate();   // invalidatera allt (enkelt och robust)
                        _dgv.Update();       // rendera direkt
                    }));

                    // Meddela Presentern om nytt läge FÖRE SpotEdited så Store.ViewMode hinner sättas
                    var ev = SpotModeChanged;
                    if (ev != null) ev(this, EventArgs.Empty);
                }
                else
                {
                    // Läget oförändrat → säkerställ ändå ommålning (chip + rad)
                    _mktBtnPressed = false;
                    _mktBtnInside = false;
                    _dgv.Cursor = Cursors.Default;
                    _mktBtnRect = Rectangle.Empty;

                    this.BeginInvoke((Action)(() =>
                    {
                        _dgv.Invalidate();
                        _dgv.Update();
                    }));
                }

                // 9) Signalera upp att Spot ändrats – Presentern uppdaterar Store och triggar prissättning
                SpotEdited?.Invoke(this, new SpotEditedEventArgs
                {
                    Pair6 = ReadPair6(),
                    Bid = bid,
                    Ask = ask,
                    WasMid = wasMid,
                    IsDealLevel = true
                });

                return;
            }

            // ===== NOTIONAL =====
            if (string.Equals(lbl, L.Notional, StringComparison.OrdinalIgnoreCase))
            {
                string raw = Convert.ToString(cell.Value ?? "").Trim();
                if (string.IsNullOrWhiteSpace(raw)) { rollback(); return; }

                double val; int decs; bool allZero;
                if (!TryParseNotional(raw, out val, out decs, out allZero)) { rollback(); return; }

                string kStart = Key(L.Notional, col);
                bool changed = true; object startObj;
                if (_editStart.TryGetValue(kStart, out startObj) && startObj is double n0)
                    changed = Math.Abs(n0 - val) > 1e-3;

                string display = FormatNotional(val, decs, allZero);

                if (string.Equals(col, "Deal", StringComparison.OrdinalIgnoreCase))
                {
                    for (int i = 0; i < _legs.Length; i++)
                        row.Cells[_legs[i]].Value = display;
                    cell.Value = "";
                    _editStart.Remove(kStart);

                    if (changed)
                    {
                        RecalcDerivedForAllLegs();
                        PriceRequested?.Invoke(this, new PriceRequestUiArgs { TargetLeg = null });
                    }
                }
                else
                {
                    cell.Value = display;
                    _editStart.Remove(kStart);
                    if (changed)
                    {
                        RecalcDerivedForColumn(col);
                        RaisePriceRequestedForLeg(col);
                    }
                }
                return;
            }

            // ===== EXPIRY =====
            if (string.Equals(lbl, L.Expiry, StringComparison.OrdinalIgnoreCase))
            {
                string raw = Convert.ToString(cell.Value ?? "").Trim();
                if (raw.Length > 0 && ExpiryEditRequested != null)
                {
                    ExpiryEditRequested(this, new ExpiryEditRequestedEventArgs
                    {
                        Pair6 = ReadPair6(),
                        Raw = raw,
                        LegColumn = col
                    });

                    if (string.Equals(col, "Deal", StringComparison.OrdinalIgnoreCase))
                    {
                        cell.Value = "";
                        _dgv.InvalidateCell(e.ColumnIndex, e.RowIndex);
                    }
                }
                else if (string.IsNullOrWhiteSpace(raw))
                {
                    RevertExpiryEdit(col);
                }
                _editStart.Remove(Key(L.Expiry, col));
                return;
            }

            // ===== STRIKE (Deal: push till legs, minst 4 d.p., CLEARS Deal) =====
            if (string.Equals(lbl, L.Strike, StringComparison.OrdinalIgnoreCase))
            {
                string raw = Convert.ToString(cell.Value ?? "").Trim();
                if (string.IsNullOrWhiteSpace(raw)) { rollback(); return; }

                double strikeVal;
                if (!TryParseCellNumber(raw, out strikeVal)) { rollback(); return; }

                string display = FormatSpotWithMinDecimals(raw, strikeVal, 4);

                if (string.Equals(col, "Deal", StringComparison.OrdinalIgnoreCase))
                {
                    string kStart = Key(L.Strike, "Deal");
                    bool changed = true; object startObj;
                    if (_editStart.TryGetValue(kStart, out startObj) && startObj is double s0)
                        changed = Math.Abs(s0 - strikeVal) > 1e-9;

                    // Push strike till alla ben
                    int rStrike = R(L.Strike);
                    for (int i = 0; i < _legs.Length; i++)
                        _dgv.Rows[rStrike].Cells[_legs[i]].Value = display;

                    // Clear Deal
                    cell.Value = "";
                    _editStart.Remove(kStart);

                    if (changed) RaisePriceRequestedForLeg(null);
                    _dgv.InvalidateRow(e.RowIndex);
                    return;
                }
                else
                {
                    string kStart = Key(L.Strike, col);
                    bool changed = true; object startObj;
                    if (_editStart.TryGetValue(kStart, out startObj) && startObj is double s0)
                        changed = Math.Abs(s0 - strikeVal) > 1e-9;

                    // Sätt normaliserad visning i leg-cellen
                    cell.Value = display;
                    _editStart.Remove(kStart);

                    if (changed) RaisePriceRequestedForLeg(col);
                    _dgv.InvalidateCell(e.ColumnIndex, e.RowIndex);
                    return;
                }
            }

            // ===== RD / RF (3 d.p., all input = procent) =====
            if (string.Equals(lbl, L.Rd, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(lbl, L.Rf, StringComparison.OrdinalIgnoreCase))
            {
                bool isDeal = string.Equals(col, "Deal", StringComparison.OrdinalIgnoreCase);
                string raw = Convert.ToString(cell.Value ?? "").Trim();

                if (string.IsNullOrWhiteSpace(raw)) { rollback(); _editStart.Remove(Key(lbl, col)); return; }

                double dec;
                if (!TryParsePercentToDecimal(raw, out dec)) { rollback(); _editStart.Remove(Key(lbl, col)); return; }

                string display = FormatPercent(dec, 3);

                if (isDeal)
                {
                    // Endast ändrade ben påverkas; färga/reprice benvis
                    for (int i = 0; i < _legs.Length; i++)
                    {
                        var lg = _legs[i];
                        var cLeg = row.Cells[lg];

                        // nuvarande logiska värde
                        bool has = cLeg.Tag is double;
                        double curr = has ? (double)cLeg.Tag : 0.0;
                        bool legChanged = !has || Math.Abs(curr - dec) > 1e-9;
                        if (!legChanged) continue;

                        cLeg.Tag = dec;
                        cLeg.Value = display;

                        bool atFeed = false; double f;
                        if (TryGetFeedDouble(lbl, lg, out f))
                            atFeed = Math.Abs(f - dec) <= 1e-9;
                        MarkOverride(lbl, lg, !atFeed);

                        RaisePriceRequestedForLeg(lg);
                    }
                    cell.Value = "";
                    _editStart.Remove(Key(lbl, col));
                }
                else
                {
                    string kStart = Key(lbl, col);
                    bool changed = true; object startObj;
                    if (_editStart.TryGetValue(kStart, out startObj) && startObj is double d0)
                        changed = Math.Abs(d0 - dec) > 1e-9;

                    // skriv normaliserat värde
                    cell.Tag = dec;
                    cell.Value = display;
                    _editStart.Remove(kStart);

                    if (!changed) { _dgv.InvalidateCell(e.ColumnIndex, e.RowIndex); return; }

                    bool atFeed = false; double f;
                    if (TryGetFeedDouble(lbl, col, out f))
                        atFeed = Math.Abs(f - dec) <= 1e-9;
                    MarkOverride(lbl, col, !atFeed);

                    RaisePriceRequestedForLeg(col);
                    _dgv.InvalidateCell(e.ColumnIndex, e.RowIndex);
                }
                return;
            }

            // ===== VOL (Deal: push/normalisera; legs: normalisera) – 3 d.p. =====
            if (string.Equals(lbl, L.Vol, StringComparison.OrdinalIgnoreCase))
            {
                bool isDeal = string.Equals(col, "Deal", StringComparison.OrdinalIgnoreCase);
                string raw = Convert.ToString(cell.Value ?? "").Trim();

                if (string.IsNullOrWhiteSpace(raw)) { rollback(); _editStart.Remove(Key(L.Vol, col)); return; }

                double bidDec, askDec; bool isPairInput;
                if (!TryParseVolInput(raw, out bidDec, out askDec, out isPairInput))
                {
                    rollback(); _editStart.Remove(Key(L.Vol, col)); return;
                }

                // normaliserad display (3 dp)
                string display = isPairInput ? FormatPercentPair(bidDec, askDec, 3) : FormatPercent(bidDec, 3);

                if (isDeal)
                {
                    int rVol = FindRow(L.Vol);
                    for (int i = 0; i < _legs.Length; i++)
                    {
                        var lg = _legs[i];
                        var cLeg = _dgv.Rows[rVol].Cells[lg];

                        var curr = cLeg.Tag as VolCellData;
                        bool legChanged = curr == null ||
                                          Math.Abs(curr.Bid - bidDec) > 1e-9 ||
                                          Math.Abs(curr.Ask - askDec) > 1e-9;

                        if (!legChanged) continue;

                        cLeg.Tag = new VolCellData { Bid = bidDec, Ask = askDec };
                        cLeg.Value = display;
                        MarkOverride(L.Vol, lg, true);
                    }

                    // Uppdatera legs Vol spread efter Vol-ändring (3 dp)
                    RecalcVolSpreadForAllLegs();

                    // Clear Deal-input, trigger omräkning
                    cell.Value = "";
                    _editStart.Remove(Key(L.Vol, col));
                    RaisePriceRequestedForLeg(null);
                    _dgv.InvalidateRow(e.RowIndex);
                    return;
                }
                else
                {
                    string kStart = Key(L.Vol, col);
                    bool changed = true; object startObj;
                    if (_editStart.TryGetValue(kStart, out startObj) && startObj is VolCellData v0)
                        changed = !(Math.Abs(v0.Bid - bidDec) <= 1e-9 && Math.Abs(v0.Ask - askDec) <= 1e-9);

                    // skriv normaliserat (3 dp)
                    cell.Tag = new VolCellData { Bid = bidDec, Ask = askDec };
                    cell.Value = display;
                    _editStart.Remove(kStart);

                    if (!changed) { _dgv.InvalidateCell(e.ColumnIndex, e.RowIndex); return; }

                    MarkOverride(L.Vol, col, true);

                    // Uppdatera Vol spread för aktuell kolumn (3 dp)
                    RecalcVolSpreadForAllLegs();

                    RaisePriceRequestedForLeg(col);
                    _dgv.InvalidateCell(e.ColumnIndex, e.RowIndex);
                    return;
                }
            }

            // ===== VOL SPREAD (3 dp; Deal → push till alla legs via mid; Leg → uppdatera eget Vol) =====
            if (string.Equals(lbl, L.VolSprd, StringComparison.OrdinalIgnoreCase))
            {
                string raw = Convert.ToString(cell.Value ?? "").Trim();
                if (string.IsNullOrWhiteSpace(raw)) { rollback(); _editStart.Remove(Key(L.VolSprd, col)); return; }

                // parse spread (procent → decimal)
                if (!TryParsePercentToDecimal(raw, out double spr)) { rollback(); _editStart.Remove(Key(L.VolSprd, col)); return; }
                if (spr < 0) spr = 0.0;

                if (string.Equals(col, "Deal", StringComparison.OrdinalIgnoreCase))
                {
                    // Deal: sprid till alla legs baserat på respektive legs mid
                    ApplyVolSpreadFromDealToLegs(spr);

                    // Clear Deal-cellen (precis som Spot, Strike m.m.)
                    cell.Value = "";
                    cell.Tag = null;

                    _editStart.Remove(Key(L.VolSprd, col));
                    RaisePriceRequestedForLeg(null);
                    _dgv.InvalidateRow(e.RowIndex);
                    return;
                }
                else
                {
                    // Leg: läs leg-mid och sätt vol = mid ± spr/2 för just detta ben
                    var (b, a, mid) = ReadVolTriplet(col);
                    double half = 0.5 * spr;
                    double nbid = Math.Max(0.0, mid - half);
                    double nask = mid + half;

                    WriteVolFromBidAsk(col, nbid, nask);
                    MarkOverride(L.Vol, col, true);

                    // normalisera spread-cellen (3 dp) & Tag
                    cell.Tag = spr;
                    cell.Value = FormatPercent(spr, 3);

                    _editStart.Remove(Key(L.VolSprd, col));
                    RecalcVolSpreadForAllLegs(); // håll allt i sync
                    RaisePriceRequestedForLeg(col);
                    _dgv.InvalidateCell(e.ColumnIndex, e.RowIndex);
                    return;
                }
            }

            // ===== SIDE =====
            if (string.Equals(lbl, L.Side, StringComparison.OrdinalIgnoreCase))
            {
                string raw = Convert.ToString(cell.Value ?? "").Trim();
                string canon;
                if (string.Equals(col, "Deal", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryCanonicalizeSide(raw, out canon))
                    {
                        for (int i = 0; i < _legs.Length; i++) row.Cells[_legs[i]].Value = canon;
                        cell.Value = "";
                        RecalcDerivedForAllLegs();
                    }
                    else cell.Value = "";

                    _dgv.InvalidateRow(e.RowIndex);
                    _editStart.Remove(Key(L.Side, col));

                    // =======================
                    // [ADDED - STEP 3] Bold i premie ska uppdateras när Side ändras
                    // =======================
                    _dgv.InvalidateRow(R(L.PremUnit));
                    _dgv.InvalidateRow(R(L.PremTot));

                    return;
                }
                else
                {
                    if (TryCanonicalizeSide(raw, out canon))
                    {
                        string kStart = Key(L.Side, col);
                        bool changed = true; object startObj;
                        if (_editStart.TryGetValue(kStart, out startObj))
                            changed = !string.Equals(Convert.ToString(startObj ?? ""), canon, StringComparison.OrdinalIgnoreCase);

                        cell.Value = canon;
                        _editStart.Remove(kStart);
                        if (changed) RecalcDerivedForColumn(col);
                    }
                    else rollback();

                    _dgv.InvalidateCell(e.ColumnIndex, e.RowIndex);

                    // =======================
                    // [ADDED - STEP 3] Bold i premie ska uppdateras när Side ändras
                    // =======================
                    _dgv.InvalidateRow(R(L.PremUnit));
                    _dgv.InvalidateRow(R(L.PremTot));

                    return;
                }
            }

            // ===== CALL/PUT =====
            if (string.Equals(lbl, L.CallPut, StringComparison.OrdinalIgnoreCase))
            {
                string raw = Convert.ToString(cell.Value ?? "").Trim();
                string canon;
                if (string.Equals(col, "Deal", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryCanonicalizePayoff(raw, out canon))
                    {
                        for (int i = 0; i < _legs.Length; i++) row.Cells[_legs[i]].Value = canon;
                        cell.Value = "";
                        RaisePriceRequestedForLeg(null);
                    }
                    else cell.Value = "";
                    _dgv.InvalidateRow(e.RowIndex);
                    _editStart.Remove(Key(L.CallPut, col));
                    return;
                }
                else
                {
                    if (TryCanonicalizePayoff(raw, out canon))
                    {
                        string kStart = Key(L.CallPut, col);
                        bool changed = true; object startObj;
                        if (_editStart.TryGetValue(kStart, out startObj))
                            changed = !string.Equals(Convert.ToString(startObj ?? ""), canon, StringComparison.OrdinalIgnoreCase);

                        cell.Value = canon;
                        _editStart.Remove(kStart);
                        if (changed) RaisePriceRequestedForLeg(col);
                    }
                    else rollback();

                    _dgv.InvalidateCell(e.ColumnIndex, e.RowIndex);
                    return;
                }
            }

            // ===== CCY PAIR =====
            if (string.Equals(lbl, L.Pair, StringComparison.OrdinalIgnoreCase))
            {
                // ... din befintliga normalisering/sättning av cellvärdet om sådan finns ...
                PairChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
        }

        /// <summary>KeyPress → starta edit med teckeninsättning och caret på slutet.</summary>
        private void Dgv_KeyPressStartEdit(object sender, KeyPressEventArgs e)
        {
            if (char.IsControl(e.KeyChar)) return;
            var cell = _dgv.CurrentCell;
            if (cell == null || cell.ReadOnly) return;
            if (IsSectionRow(_dgv.Rows[cell.RowIndex])) return;

            _dgv.BeginEdit(true);
            if (_dgv.EditingControl is TextBox tb)
            {
                tb.Text = string.Empty;
                tb.AppendText(e.KeyChar.ToString());
                tb.SelectionStart = tb.TextLength;
            }

            _dgv.InvalidateCell(cell.ColumnIndex, cell.RowIndex);
            e.Handled = true;
        }

        /// <summary>
        /// Back/Delete startar edit men rensar inte celler; blockerar rensning på Spot/Deal.
        /// </summary>
        private void Dgv_KeyDownStartEdit(object sender, KeyEventArgs e)
        {
            var cell = _dgv.CurrentCell;
            if (cell == null) return;

            if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Delete)
            {
                string lbl = Convert.ToString(_dgv.Rows[cell.RowIndex].Cells["FIELD"].Value);
                string colName = _dgv.Columns[cell.ColumnIndex].Name;

                // BEHÅLLT: Blockera Delete/Backspace i Spot/Deal
                if (lbl.Equals(L.Spot, StringComparison.OrdinalIgnoreCase) && colName == "Deal")
                {
                    e.Handled = true;
                    return;
                }

                // Töm inte celler via Delete/Backspace (även detta uppfyller nya kravet)
                if (cell.ReadOnly || IsSectionRow(_dgv.Rows[cell.RowIndex]))
                {
                    e.Handled = true;
                    return;
                }

                // Starta edit men behåll texten (töm inte); caret sist
                _dgv.BeginEdit(true);
                if (_dgv.EditingControl is TextBox tb)
                {
                    // Behåll befintligt innehåll – rör bara caret
                    tb.SelectionStart = tb.TextLength;
                    tb.SelectionLength = 0;
                }
                _dgv.InvalidateCell(cell.ColumnIndex, cell.RowIndex);
                e.Handled = true;
                return;
            }

            // andra tangenter: inget specialfall här
        }

        // ========= MouseDown: markera "pressed" + blockera att FIELD fäller sektionen =========
        private void Dgv_CellMouseDown_PricingButton(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (e.ColumnIndex != _dgv.Columns["FIELD"].Index) return;
            if (e.RowIndex != _pricingHeaderRow) return;
            if (_pricingBtnRect.IsEmpty) return;

            if (_pricingBtnRect.Contains(e.Location))
            {
                _pricingBtnPressed = true;
                _suppressNextSectionToggle = true;   // så FIELD-klicket inte fäller sektionen
                _dgv.InvalidateCell(e.ColumnIndex, e.RowIndex);
            }
        }

        // ========= MouseUp: toggla valuta om släppet sker över knappen, rendera om =========
        private void Dgv_CellMouseUp_PricingButton(object sender, DataGridViewCellMouseEventArgs e)
        {
            // Var det inte vår knapp som var nedtryckt? Gör inget.
            if (!_pricingBtnPressed) return;

            // Släpp "pressed" direkt (så chipet ritas upp igen)
            _pricingBtnPressed = false;

            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;

            // Toggle endast om vi SLÄPPER inom chipet i Pricing-raden
            if (!IsInsidePricingHeaderButton(e))
            {
                // säkerställ att FIELD-klick inte fäller sektionen efter detta släpp
                _suppressNextSectionToggle = false;
                InvalidatePricingHeaderCell();
                return;
            }

            // 1) Byt valuta
            _pricingCurrency = (_pricingCurrency == PricingCurrency.Quote)
                ? PricingCurrency.Base
                : PricingCurrency.Quote;

            // >>> VIKTIGT: spegla display-valutan till formatteraren <<<
            _displayCcy = (_pricingCurrency == PricingCurrency.Quote)
                ? DisplayCcy.Quote
                : DisplayCcy.Base;

            // 2) Uppdatera rubriken "Premium total (XXX)"
            ApplyPremiumCurrencyLabel();

            // 3) Bygg om tvåvägs TOTAL-cache i aktuell visningsvaluta
            RebuildTwoWayTotalsCacheForCurrentCurrency();

            // 4) Rendera om alla ben från befintliga Tag/cache (INGA motoranrop)
            foreach (var leg in _legs)
                RenderLegPricing(leg);

            // 5) Deal-summeringar (byggs från lagrade Tag-tupler — också avrundat)
            RecalcDealPricingAndRiskTotals();

            // 6) Släpp spärren för fäll/expand och rita om headern (släck hover/pressed)
            _suppressNextSectionToggle = false;
            InvalidatePricingHeaderCell();
        }

        // ========= MouseMove: hover-state + cursor =========
        private void Dgv_CellMouseMove_PricingButton(object sender, DataGridViewCellMouseEventArgs e)
        {
            bool over = false;

            if (e.RowIndex == _pricingHeaderRow &&
                e.ColumnIndex == _dgv.Columns["FIELD"].Index &&
                !_pricingBtnRect.IsEmpty)
            {
                over = _pricingBtnRect.Contains(e.Location);
            }

            if (over != _pricingBtnHover)
            {
                _pricingBtnHover = over;
                _dgv.Cursor = over ? Cursors.Hand : Cursors.Default;
                _dgv.InvalidateCell(_dgv.Columns["FIELD"].Index, _pricingHeaderRow);
            }
        }

        // ========= MouseLeave: släck hover/pressed och återställ cursor =========
        private void Dgv_CellMouseLeave_PricingButton(object sender, DataGridViewCellEventArgs e)
        {
            if (_pricingBtnHover || _pricingBtnPressed)
            {
                _pricingBtnHover = false;
                _pricingBtnPressed = false;
                _dgv.Cursor = Cursors.Default;

                if (_pricingHeaderRow >= 0)
                    _dgv.InvalidateCell(_dgv.Columns["FIELD"].Index, _pricingHeaderRow);
            }
        }

        private void Dgv_CellMouseDown_MktDataButton(object sender, DataGridViewCellMouseEventArgs e)
        {
            // Reagera bara om trycket är inne i vår knapp (vänster eller höger)
            if (!IsInsideMktHeaderButton(e)) return;

            _mktBtnPressed = true;
            _mktBtnInside = true;
            InvalidateSpotModeButton(); // rita pressed-state
        }

        /// <summary>
        /// MouseUp på rubrikens Spot-knapp (MID ⇆ FULL, högerklick → LIVE).
        /// - Växlar internt _mktDataMode (endast för SPOT).
        /// - Fire:ar SpotModeChanged om läget faktiskt ändrats (Presenter uppdaterar MarketStore).
        /// - Ritar om knapp + spotrad och säkerställer tvåvägs-snapshots i FULL/LIVE.
        /// OBS: Vi kallar inte längre RaisePriceRequestedForLeg här.
        /// </summary>
        private void Dgv_CellMouseUp_MktDataButton(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (!_mktBtnPressed) return;

            bool inside = IsInsideMktHeaderButton(e);
            var oldMode = _spotMode;

            if (inside)
            {
                if (e.Button == MouseButtons.Right)
                {
                    // Högerklick: MID/FULL -> LIVE, LIVE -> ingen ändring
                    if (_spotMode != SpotMode.Live)
                        _spotMode = SpotMode.Live;
                }
                else if (e.Button == MouseButtons.Left)
                {
                    // Vänsterklick: MID <-> FULL; LIVE -> MID
                    if (_spotMode == SpotMode.Live)
                        _spotMode = SpotMode.Mid;
                    else
                        _spotMode = (_spotMode == SpotMode.Mid) ? SpotMode.Full : SpotMode.Mid;
                }
                // andra knappar ignoreras
            }

            _mktBtnPressed = false;
            _mktBtnInside = false;

            // SYNC SNAPSHOTS MOT NYTT LÄGE
            if (_spotMode == SpotMode.Full || _spotMode == SpotMode.Live)
            {
                // FULL/LIVE: se till att Tag finns, men skriv aldrig över befintliga tvåvägsvärden
                EnsureSpotSnapshotsForAll();
            }
            // MID: gör inget med Tag – vi byter bara policy för visning/prissättning

            // Direkt omritning (knapp + spotrad)
            InvalidateSpotModeButton();
            InvalidateSpotRow();

            // Fire:a SpotModeChanged endast om läget faktiskt ändrades
            if (oldMode != _spotMode)
            {
                var h = SpotModeChanged;
                if (h != null) h(this, EventArgs.Empty);
            }

            // Håll sektionen ofälld (släpp spärren efter denna klick-loop)
            //this.BeginInvoke((Action)(() => { _suppressNextSectionToggle = false; }));
        }

        private void Dgv_CellMouseMove_MktDataButton(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (!_mktBtnPressed) return;

            bool nowInside = IsInsideMktHeaderButton(e);
            if (_mktBtnInside != nowInside)
            {
                _mktBtnInside = nowInside;
                InvalidateSpotModeButton();
            }
        }

        #endregion

        #region === Painting ===

        private void Dgv_CellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;

            string colName = _dgv.Columns[e.ColumnIndex].Name;

            // Normalisera label så "Premium total (XXX)" blir L.PremTot
            string rowLabelRaw = Convert.ToString(_dgv.Rows[e.RowIndex].Cells["FIELD"].Value ?? "");
            string rowLabel = NormalizePremLabel(rowLabelRaw);
            var row = _dgv.Rows[e.RowIndex];

            // Neutralisera selektionsfärger (behåll look)
            Color oldSelBack = e.CellStyle.SelectionBackColor;
            Color oldSelFore = e.CellStyle.SelectionForeColor;
            if ((e.State & DataGridViewElementStates.Selected) != 0)
            {
                e.CellStyle.SelectionBackColor = e.CellStyle.BackColor;
                e.CellStyle.SelectionForeColor = e.CellStyle.ForeColor;
            }

            // ===== 1) Sektionstitelceller (Deal Details / Mkt Data / Pricing / Risk) =====
            bool isSectionTitleCell =
                (e.ColumnIndex == _dgv.Columns["FIELD"].Index) &&
                IsSectionRow(_dgv.Rows[e.RowIndex]);

            if (isSectionTitleCell)
            {
                e.PaintBackground(e.CellBounds, true);

                // Titeltext
                string txt = Convert.ToString(e.FormattedValue ?? rowLabelRaw);
                TextRenderer.DrawText(
                    e.Graphics, txt, new Font(this.Font, FontStyle.Bold),
                    e.CellBounds, e.CellStyle.ForeColor,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left);

                // ---- Pricing: valuta-chip (Quote/Base) till höger (OFÖRÄNDRAT) ----
                bool isPricingByTag = (row.Tag is Section sP) && (sP == Section.Pricing);
                string normTitle = (rowLabelRaw ?? "").TrimStart(' ', '▾', '▸');
                bool isPricingByText = string.Equals(normTitle, L.Pricing, StringComparison.OrdinalIgnoreCase);

                if (isPricingByTag || isPricingByText)
                {
                    var (b, q) = GetBaseQuoteFromPair6(ReadPair6());
                    string cur = (_pricingCurrency == PricingCurrency.Quote) ? q : b;

                    var f = _dgv.Font;
                    var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding;
                    int textW = Math.Max(
                        TextRenderer.MeasureText(b, f, Size.Empty, TextFormatFlags.NoPadding).Width,
                        TextRenderer.MeasureText(q, f, Size.Empty, TextFormatFlags.NoPadding).Width
                    );

                    int padX = 8, padY = 2, corner = 8;
                    int btnW = textW + padX * 2;
                    int btnH = Math.Min(e.CellBounds.Height - 10, Math.Max(16, f.Height + padY));

                    int btnAbsX = e.CellBounds.Right - btnW - 8;
                    int btnAbsY = e.CellBounds.Top + (e.CellBounds.Height - btnH) / 2 - 1;

                    var btnRectLocal = new Rectangle(btnAbsX - e.CellBounds.X, btnAbsY - e.CellBounds.Y, btnW, btnH);

                    // Spara state för pricing-knappen
                    _pricingHeaderRow = e.RowIndex;
                    _pricingBtnRect = btnRectLocal;

                    Color fill = _pricingBtnPressed
                        ? Color.FromArgb(220, 230, 238)
                        : _pricingBtnHover ? Color.FromArgb(240, 245, 250) : Color.FromArgb(235, 240, 245);
                    Color border = _pricingBtnPressed ? Color.FromArgb(150, 170, 190) : Color.FromArgb(196, 204, 212);

                    var btnRectAbs = new Rectangle(
                        e.CellBounds.X + btnRectLocal.X,
                        e.CellBounds.Y + btnRectLocal.Y,
                        btnRectLocal.Width, btnRectLocal.Height);

                    using (var path = RoundedRect(btnRectAbs, corner))
                    using (var pen = new Pen(border))
                    using (var fillBr = new SolidBrush(fill))
                    {
                        e.Graphics.FillPath(fillBr, path);
                        e.Graphics.DrawPath(pen, path);
                    }

                    var textRectAbs = _pricingBtnPressed
                        ? new Rectangle(btnRectAbs.X, btnRectAbs.Y + 1, btnRectAbs.Width, btnRectAbs.Height)
                        : btnRectAbs;

                    TextRenderer.DrawText(e.Graphics, cur, f, textRectAbs, Color.Black, flags);
                }
                else
                {
                    // Inte Pricing → nolla pricing-button state om vi lämnar tidigare rad
                    if (_pricingHeaderRow == e.RowIndex)
                    {
                        _pricingBtnRect = Rectangle.Empty;
                        _pricingBtnHover = false;
                        _pricingBtnPressed = false;
                    }
                }

                // ---- OBS: Gamla "Mkt Data"-headerknappen är BORTTAGEN här ----

                e.Handled = true;
                e.CellStyle.SelectionBackColor = oldSelBack;
                e.CellStyle.SelectionForeColor = oldSelFore;
                return;
            }

            // ===== 2) Expiry-hint overlay (rå [1M] etc.) =====
            if (string.Equals(rowLabel, L.Expiry, StringComparison.OrdinalIgnoreCase) &&
                (IsLegColumn(colName) || string.Equals(colName, "Deal", StringComparison.OrdinalIgnoreCase)))
            {
                e.Paint(e.CellBounds, DataGridViewPaintParts.All);

                if (_expiryRawHintByCol.TryGetValue(colName, out var hint) && !string.IsNullOrWhiteSpace(hint))
                {
                    var rc = e.CellBounds; rc.Inflate(-4, -2);
                    string text = "[" + hint + "]";
                    using (var f = _hintFont ?? this.Font)
                    {
                        TextRenderer.DrawText(
                            e.Graphics, text, f,
                            new Rectangle(rc.Left + 2, rc.Top + 1, rc.Width, rc.Height),
                            Color.FromArgb(150, 80, 80, 80),
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                        );
                    }
                }

                DrawFocusIfNeeded(e);
                e.Handled = true;
                e.CellStyle.SelectionBackColor = oldSelBack;
                e.CellStyle.SelectionForeColor = oldSelFore;
                return;
            }

            // ===== 3) SPOT-radens FIELD-cell: rita etikett + MID/FULL/LIVE-knapp (NY PLACERING) =====
            bool isSpotFieldCell =
                string.Equals(rowLabel, L.Spot, StringComparison.OrdinalIgnoreCase) &&
                e.ColumnIndex == _dgv.Columns["FIELD"].Index;

            if (isSpotFieldCell)
            {
                // Bakgrund
                e.PaintBackground(e.CellBounds, true);

                // Etikett till vänster
                TextRenderer.DrawText(
                    e.Graphics, L.Spot, new Font(this.Font, FontStyle.Bold),
                    new Rectangle(e.CellBounds.X + 12, e.CellBounds.Y, e.CellBounds.Width, e.CellBounds.Height),
                    e.CellStyle.ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);

                // Chip-text styrs av _spotMode
                string modeText = (_spotMode == SpotMode.Live) ? "LIVE" :
                                  (_spotMode == SpotMode.Full ? "FULL" : "MID");

                var f = _dgv.Font;
                var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding;

                // Håll konstant bredd (mät största av texterna)
                int wMid = TextRenderer.MeasureText("MID", f, Size.Empty, TextFormatFlags.NoPadding).Width;
                int wFull = TextRenderer.MeasureText("FULL", f, Size.Empty, TextFormatFlags.NoPadding).Width;
                int wLive = TextRenderer.MeasureText("LIVE", f, Size.Empty, TextFormatFlags.NoPadding).Width;
                int textW = Math.Max(wMid, Math.Max(wFull, wLive));

                int padX = 8, padY = 2, corner = 8;
                int btnW = textW + padX * 2;
                int btnH = Math.Min(e.CellBounds.Height - 10, Math.Max(16, f.Height + padY));

                int btnAbsX = e.CellBounds.Right - btnW - 8;
                int btnAbsY = e.CellBounds.Top + (e.CellBounds.Height - btnH) / 2 - 1;

                // Spara lokal-rect för hit-test
                var btnRectLocal = new Rectangle(btnAbsX - e.CellBounds.X, btnAbsY - e.CellBounds.Y, btnW, btnH);

                // Viktigt: exponera "header"-state för befintliga mouse-handlers
                _mktHeaderRow = e.RowIndex;
                _mktBtnRect = btnRectLocal;

                Color fill = _mktBtnPressed ? Color.FromArgb(220, 230, 238) : Color.FromArgb(235, 240, 245);
                Color border = _mktBtnPressed ? Color.FromArgb(150, 170, 190) : Color.FromArgb(196, 204, 212);

                var btnRectAbs = new Rectangle(
                    e.CellBounds.X + btnRectLocal.X,
                    e.CellBounds.Y + btnRectLocal.Y,
                    btnRectLocal.Width, btnRectLocal.Height);

                using (var path = RoundedRect(btnRectAbs, corner))
                using (var pen = new Pen(border))
                using (var fillBr = new SolidBrush(fill))
                {
                    e.Graphics.FillPath(fillBr, path);
                    e.Graphics.DrawPath(pen, path);
                }

                var textRectAbs = _mktBtnPressed
                    ? new Rectangle(btnRectAbs.X, btnRectAbs.Y + 1, btnRectAbs.Width, btnRectAbs.Height)
                    : btnRectAbs;

                TextRenderer.DrawText(e.Graphics, modeText, f, textRectAbs, Color.Black, flags);

                e.Handled = true;
                e.CellStyle.SelectionBackColor = oldSelBack;
                e.CellStyle.SelectionForeColor = oldSelFore;
                return;
            }

            // ===== 4) SPOT-värdeceller: specialmålning (MID vs BID/ASK) =====
            bool isSpotRow = string.Equals(rowLabel, L.Spot, StringComparison.OrdinalIgnoreCase);
            bool isSpotValueCell = isSpotRow && (string.Equals(colName, "Deal", StringComparison.OrdinalIgnoreCase) || IsLegColumn(colName));

            if (isSpotValueCell)
            {
                // Måla bakgrund etc. (inte texten)
                e.Paint(e.CellBounds, e.PaintParts & ~DataGridViewPaintParts.ContentForeground);

                var cellSpot = _dgv.Rows[e.RowIndex].Cells[e.ColumnIndex];
                var pad = e.CellStyle.Padding;
                int padR = Math.Max(0, pad.Right);
                var g = e.Graphics;
                var font = _dgv.Font;
                Color fore = e.CellStyle.ForeColor;

                double bid = 0.0, ask = 0.0, mid = 0.0;
                string source = null;

                if (cellSpot.Tag is SpotCellData snap && snap != null)
                {
                    bid = snap.Bid;
                    ask = snap.Ask;
                    mid = (snap.Mid != 0.0) ? snap.Mid : 0.5 * (bid + ask);
                    source = snap.Source;
                }
                else
                {
                    double.TryParse(Convert.ToString(cellSpot.Value ?? "0").Replace(" ", "").Replace(',', '.'),
                                    NumberStyles.Any, CultureInfo.InvariantCulture, out mid);
                    bid = ask = mid;
                    source = "Feed";
                }

                bool showTwoWay = (_spotMode == SpotMode.Full) || (_spotMode == SpotMode.Live);
                bool isFeed = string.Equals(source, "Feed", StringComparison.OrdinalIgnoreCase);

                if (showTwoWay)
                {
                    string bidTxt = isFeed
                        ? bid.ToString("0.0000", CultureInfo.InvariantCulture)
                        : FormatSpotWithMinDecimals(bid.ToString(CultureInfo.InvariantCulture), bid, 4);

                    string askTxt = isFeed
                        ? ask.ToString("0.0000", CultureInfo.InvariantCulture)
                        : FormatSpotWithMinDecimals(ask.ToString(CultureInfo.InvariantCulture), ask, 4);

                    string txt = bidTxt + "/" + askTxt;
                    Size sz = TextRenderer.MeasureText(txt, font, Size.Empty, TextFormatFlags.NoPadding);
                    int x = e.CellBounds.Right - padR - sz.Width;
                    int y = e.CellBounds.Y + (e.CellBounds.Height - font.Height) / 2;
                    TextRenderer.DrawText(g, txt, font, new Point(x, y), fore, TextFormatFlags.NoPadding);
                }
                else
                {
                    string midTxt = isFeed
                        ? mid.ToString("0.0000", CultureInfo.InvariantCulture)
                        : (Convert.ToString(cellSpot.Value ?? "") is string sMid && !string.IsNullOrWhiteSpace(sMid)
                            ? sMid
                            : FormatSpotWithMinDecimals(mid.ToString(CultureInfo.InvariantCulture), mid, 4));

                    Size szMid = TextRenderer.MeasureText(midTxt, font, Size.Empty, TextFormatFlags.NoPadding);
                    int x = e.CellBounds.Right - padR - szMid.Width;
                    int y = e.CellBounds.Y + (e.CellBounds.Height - font.Height) / 2;
                    TextRenderer.DrawText(g, midTxt, font, new Point(x, y), fore, TextFormatFlags.NoPadding);
                }

                DrawFocusIfNeeded(e);
                e.Handled = true;
                e.CellStyle.SelectionBackColor = oldSelBack;
                e.CellStyle.SelectionForeColor = oldSelFore;
                return;
            }

            // ===== 5) Overlay för Premium (per unit / total) i ben =====
            bool isPremUnit = string.Equals(rowLabel, L.PremUnit, StringComparison.OrdinalIgnoreCase);
            bool isPremTot = string.Equals(rowLabel, L.PremTot, StringComparison.OrdinalIgnoreCase);
            bool isOverlayCell =
                (isPremUnit || isPremTot) &&
                colName != "FIELD" && colName != "Deal" && IsLegColumn(colName);

            if (isOverlayCell)
                e.Paint(e.CellBounds, e.PaintParts & ~DataGridViewPaintParts.ContentForeground);
            else
                e.Paint(e.CellBounds, DataGridViewPaintParts.All);

            if (isOverlayCell)
            {
                var cell = _dgv.Rows[e.RowIndex].Cells[e.ColumnIndex];

                double bid, ask; int dec;
                if (_twoWayPremCache.TryGetValue(PremKey(rowLabel, colName), out var pd))
                {
                    bid = pd.Bid; ask = pd.Ask; dec = pd.Decimals;
                }
                else
                {
                    if (isPremUnit)
                    {
                        double mid = (cell.Tag is double dtag) ? dtag : 0.0;
                        bid = mid; ask = mid; dec = 6;
                    }
                    else
                    {
                        double mid = 0.0;
                        double.TryParse(Convert.ToString(cell.Value ?? "0").Replace(" ", ""),
                                        NumberStyles.Any, CultureInfo.InvariantCulture, out mid);
                        bid = mid; ask = mid; dec = 2;
                    }
                }

                string side = ReadSide(colName);
                if (string.IsNullOrWhiteSpace(side) || side == "-") side = ReadSide("Deal");
                bool boldAsk = side.Equals("Buy", StringComparison.OrdinalIgnoreCase);

                Color fore = e.CellStyle.ForeColor;

                string left = FmtSpaces(bid, dec);
                string right = FmtSpaces(ask, dec);
                const string sep = " / ";

                var g = e.Graphics;
                var baseFont = _dgv.Font;
                using (var boldFont = new Font(baseFont, FontStyle.Bold))
                {
                    var fontLeft = boldAsk ? baseFont : boldFont;  // Sell => Bid bold
                    var fontRight = boldAsk ? boldFont : baseFont;  // Buy  => Ask bold

                    var szLeft = TextRenderer.MeasureText(left, fontLeft, Size.Empty, TextFormatFlags.NoPadding);
                    var szSep = TextRenderer.MeasureText(sep, baseFont, Size.Empty, TextFormatFlags.NoPadding);
                    var szRight = TextRenderer.MeasureText(right, fontRight, Size.Empty, TextFormatFlags.NoPadding);

                    int totalW = szLeft.Width + szSep.Width + szRight.Width;

                    var pad = e.CellStyle.Padding;
                    int padR = Math.Max(0, pad.Right);

                    int x = e.CellBounds.Right - padR - totalW;
                    int y = e.CellBounds.Y + (e.CellBounds.Height - baseFont.Height) / 2;

                    TextRenderer.DrawText(g, left, fontLeft, new Point(x, y), fore, TextFormatFlags.NoPadding);
                    x += szLeft.Width;
                    TextRenderer.DrawText(g, sep, baseFont, new Point(x, y), fore, TextFormatFlags.NoPadding);
                    x += szSep.Width;
                    TextRenderer.DrawText(g, right, fontRight, new Point(x, y), fore, TextFormatFlags.NoPadding);
                }
            }

            // ===== 6) Glyphs (▼) för Side/CallPut i ben + mass-toggle i FIELD/Side =====
            bool showGlyphInLegs =
                IsLegColumn(colName) &&
                (string.Equals(rowLabel, L.Side, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(rowLabel, L.CallPut, StringComparison.OrdinalIgnoreCase));

            bool showGlyphInField =
                (colName == "FIELD") &&
                string.Equals(rowLabel, L.Side, StringComparison.OrdinalIgnoreCase);

            if (showGlyphInLegs || showGlyphInField)
                DrawDownArrow(e.Graphics, e.CellBounds);

            DrawFocusIfNeeded(e);
            e.Handled = true;

            e.CellStyle.SelectionBackColor = oldSelBack;
            e.CellStyle.SelectionForeColor = oldSelFore;
        }


        /// <summary>Ritar en blå fokusram för vald cell (när vi neutraliserar selectionfärg).</summary>
        private void DrawFocusIfNeeded(DataGridViewCellPaintingEventArgs e)
        {
            bool isEditingThisCell =
                _dgv.IsCurrentCellInEditMode &&
                _dgv.CurrentCell != null &&
                _dgv.CurrentCell.RowIndex == e.RowIndex &&
                _dgv.CurrentCell.ColumnIndex == e.ColumnIndex;

            bool isPendingThisCell = (_pendingBeginEditCell != null &&
                                      _pendingBeginEditCell.RowIndex == e.RowIndex &&
                                      _pendingBeginEditCell.ColumnIndex == e.ColumnIndex);

            if (!isEditingThisCell && !isPendingThisCell &&
                (e.State & DataGridViewElementStates.Selected) != 0)
            {
                using (var p = new Pen(Color.DodgerBlue, 2))
                {
                    var inner = new Rectangle(
                        e.CellBounds.X + 1,
                        e.CellBounds.Y + 1,
                        e.CellBounds.Width - 3,
                        e.CellBounds.Height - 3);
                    e.Graphics.DrawRectangle(p, inner);
                }
            }
        }

        /// <summary>Ritar en liten nedåtpil till höger i cellen.</summary>
        private static void DrawDownArrow(Graphics g, Rectangle cellAbsBounds)
        {
            int x = cellAbsBounds.Right - (GlyphRightPadding + GlyphWidth / 2);
            int y = cellAbsBounds.Top + (cellAbsBounds.Height - GlyphHeight) / 2 + 1;

            using (SmoothingScope.Smooth(g))
            using (Brush br = new SolidBrush(Color.Gray))
            {
                Point p1 = new Point(x, y);
                Point p2 = new Point(x + GlyphWidth, y);
                Point p3 = new Point(x + GlyphWidth / 2, y + GlyphHeight);
                g.FillPolygon(br, new[] { p1, p2, p3 });
            }
        }

        // Enkel rounded-rect helper för chipet
        private System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            int d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        #endregion

        #region === Collapse & Toggles ===

        /// <summary>Expand/collapse på sektionsrader och begränsa synlighet i MktData/Risk.</summary>
        private void ToggleSection(int headerRow)
        {
            string title = Convert.ToString(_dgv.Rows[headerRow].Cells["FIELD"].Value) ?? "";
            bool currentlyExpanded = title.TrimStart().StartsWith("▾");
            bool collapse = currentlyExpanded;

            var sec = (Section)_dgv.Rows[headerRow].Tag;

            if (sec == Section.DealDetails)
            {
                if (!currentlyExpanded)
                    _dgv.Rows[headerRow].Cells["FIELD"].Value = "▾" + title.Substring(1);

                for (int r = headerRow + 1; r < _dgv.Rows.Count; r++)
                {
                    if (IsSectionRow(_dgv.Rows[r])) break;
                    _dgv.Rows[r].Visible = true;
                }
                return;
            }

            _dgv.Rows[headerRow].Cells["FIELD"].Value = (collapse ? "▸" : "▾") + title.Substring(1);

            for (int r = headerRow + 1; r < _dgv.Rows.Count; r++)
            {
                if (IsSectionRow(_dgv.Rows[r])) break;

                if (!collapse) { _dgv.Rows[r].Visible = true; continue; }

                string rowLabel = Convert.ToString(_dgv.Rows[r].Cells["FIELD"].Value) ?? "";
                switch (sec)
                {
                    case Section.MktData:
                        bool keepMkt = rowLabel.Equals(L.Spot, StringComparison.OrdinalIgnoreCase)
                                    || rowLabel.Equals(L.Vol, StringComparison.OrdinalIgnoreCase)
                                    || rowLabel.Equals(L.VolSprd, StringComparison.OrdinalIgnoreCase);
                        _dgv.Rows[r].Visible = keepMkt;
                        break;

                    case Section.Risk:
                        bool keepRisk = rowLabel.StartsWith("Delta", StringComparison.OrdinalIgnoreCase);
                        _dgv.Rows[r].Visible = keepRisk;
                        break;

                    default:
                        _dgv.Rows[r].Visible = false;
                        break;
                }
            }
        }

        /// <summary>Byter Buy/Sell i en cell och kanoniserar strängen.</summary>
        private void ToggleSideInCell(int rowIndex, string colName)
        {
            string curr = Convert.ToString(_dgv.Rows[rowIndex].Cells[colName].Value ?? "");
            string next = (curr.IndexOf("BUY", StringComparison.OrdinalIgnoreCase) >= 0) ? "Sell" : "Buy";
            _dgv.Rows[rowIndex].Cells[colName].Value = CanonicalizeInput("side", next);
        }

        /// <summary>Byter Call/Put i en cell och kanoniserar strängen.</summary>
        private void ToggleCallPutInCell(int rowIndex, string colName)
        {
            string curr = Convert.ToString(_dgv.Rows[rowIndex].Cells[colName].Value ?? "");
            string next = (curr.IndexOf("CALL", StringComparison.OrdinalIgnoreCase) >= 0) ? "Put" : "Call";
            _dgv.Rows[rowIndex].Cells[colName].Value = CanonicalizeInput("payoff", next);
        }

        #endregion

        #region === Value normalization & percent helpers ===

        /// <summary>
        /// Normaliserar rå input för utvalda fält (expiry/delivery: passthrough, side/payoff: mapping).
        /// </summary>
        private object CanonicalizeInput(string key, object raw)
        {
            if (raw == null) return "";
            string t = Convert.ToString(raw ?? "").Trim();
            t = t.Replace(',', '.');

            if (key == "expiry" || key == "delivery") return t;

            if (key == "side")
            {
                string s = t.ToLowerInvariant();
                if (s == "b" || s == "buy" || s == "köp") return "Buy";
                if (s == "s" || s == "sell" || s == "sälj") return "Sell";
                return "";
            }
            if (key == "payoff")
            {
                string s = t.ToLowerInvariant();
                if (s == "c" || s == "call" || s.Contains("call")) return "Call";
                if (s == "p" || s == "put" || s.Contains("put")) return "Put";
                return "";
            }
            return t;
        }

        /// <summary>Tolkar ALL input som procenttal: ”1”→0.01, ”1%”→0.01, ”0.9”→0.009.</summary>
        private static bool TryParsePercentToDecimal(string raw, out double dec)
        {
            dec = 0.0;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            string s = raw.Trim().Replace(" ", "").Replace("\u00A0", "");
            s = s.Replace(',', '.');

            bool hadPct = s.EndsWith("%", StringComparison.Ordinal);
            if (hadPct) s = s.Substring(0, s.Length - 1);

            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                return false;

            dec = v / 100.0; // alltid procent
            return true;
        }

        /// <summary>Formatterar decimalränta till ”X.XXX%”.</summary>
        private static string FormatPercent(double dec, int decimals)
        {
            string num = (dec * 100.0).ToString("0." + new string('0', decimals), CultureInfo.InvariantCulture);
            return num + "%";
        }

        #endregion

        #region === Data helpers (find/set/copy) ===

        /// <summary>Returnerar radindex för ett givet radnamn (icke-sektionsrad), annars -1.</summary>
        private int FindRow(string label)
        {
            for (int i = 0; i < _rows.Count; i++)
                if (_rows[i].Key != "SECTION" && _rows[i].Label == label) return i;
            return -1;
        }

        /// <summary>Sätter text i angiven kolumn/rad om raden finns.</summary>
        private void Set(string column, string label, string value)
        {
            int r = FindRow(label);
            if (r >= 0) _dgv.Rows[r].Cells[column].Value = value;
        }

        /// <summary>Kopierar Deal-värden till legs i angiven sektion (kan rensa Deal).</summary>
        private void CopyDealToLegs(Section section, bool keepDeal)
        {
            bool inSec = false;
            for (int i = 0; i < _rows.Count; i++)
            {
                var spec = _rows[i];
                if (spec.Key == "SECTION") { inSec = (spec.Sec == section); continue; }
                if (!inSec) continue;

                var val = Convert.ToString(_dgv.Rows[i].Cells["Deal"].Value ?? "");
                for (int k = 0; k < _legs.Length; k++)
                    _dgv.Rows[i].Cells[_legs[k]].Value = val;

                if (!keepDeal && spec.Label != L.Pair && spec.Label != L.Spot)
                    _dgv.Rows[i].Cells["Deal"].Value = "";
            }
        }

        /// <summary>Sätter Deal-cell ReadOnly och rensar dess värde.</summary>
        private void SetDealCellReadOnlyAndClear(string label, bool ro)
        {
            int r = FindRow(label);
            if (r < 0) return;
            _dgv.Rows[r].Cells["Deal"].ReadOnly = ro;
            _dgv.Rows[r].Cells["Deal"].Value = "";
        }

        /// <summary>Sätter en hel rad ReadOnly (Deal + alla legs).</summary>
        private void SetRowReadOnly(string label, bool ro)
        {
            int r = FindRow(label);
            if (r < 0) return;
            _dgv.Rows[r].Cells["Deal"].ReadOnly = ro;
            for (int i = 0; i < _legs.Length; i++)
                _dgv.Rows[r].Cells[_legs[i]].ReadOnly = ro;
        }

        #endregion

        #region === Geometry (glyph hit test) ===

        /// <summary>Beräknar lokal rektangel för glyph-pilen i en cell.</summary>
        private static Rectangle GetGlyphRectLocal(Rectangle cellBounds)
        {
            int x = cellBounds.Width - (GlyphRightPadding + GlyphWidth / 2);
            int y = (cellBounds.Height - GlyphHeight) / 2 + 1;
            return new Rectangle(x - 2, y - 2, GlyphWidth + 4, GlyphHeight + 4);
        }

        /// <summary>True om kolumnnamnet motsvarar ett ben.</summary>
        private bool IsLegColumn(string colName) => Array.IndexOf(_legs, colName) >= 0;

        /// <summary>True om raden är en sektionsrad (ej Deal Details).</summary>
        private static bool IsSectionRow(DataGridViewRow row)
        {
            if (row == null) return false;
            if (row.Tag is Section s && s == Section.DealDetails) return false;
            var txt = Convert.ToString(row.Cells["FIELD"].Value);
            if (string.IsNullOrEmpty(txt)) return false;
            txt = txt.TrimStart();
            return txt.StartsWith("▾") || txt.StartsWith("▸");
        }

        #endregion

        #region === UI API used by Presenter ===

        /// <summary>Visar resultatrader för ett ben (premier/greker) och lagrar nyckeltal i Tag.</summary>
        public void ShowLegResult(string legCol, double pricePerUnitMid,
                                  double deltaUnit, double vegaUnit, double gammaUnit, double thetaUnit)
        {
            // === Läs inputs ===
            double N = ReadNotional(legCol);
            double S = ReadSpot("Deal");
            string side = ReadSide(legCol);
            int sg = BuySellSign(side);
            bool boldAsk = side.Equals("Buy", StringComparison.OrdinalIgnoreCase);

            // === Hämta per-unit tvåväg från cache (presentern ska ha fyllt den); fallback mid/mid ===
            double puBid = pricePerUnitMid, puAsk = pricePerUnitMid;
            if (_twoWayPremCache.TryGetValue(PremKey(L.PremUnit, legCol), out var cachedPu))
            {
                puBid = cachedPu.Bid;
                puAsk = cachedPu.Ask;
            }

            // === Kör din Formatter med BID/ASK (inte mid/mid) ===
            var fmt = CreateFormatter();
            var vm = fmt.Build(
                leg: legCol,
                perUnitBid: puBid,
                perUnitAsk: puAsk,
                notional: N,
                spot: S,
                sideSign: sg,
                deltaUnit: deltaUnit,
                vegaUnit: vegaUnit,
                gammaUnit: gammaUnit,
                thetaUnit: thetaUnit,
                boldAsk: boldAsk
            );

            // === Premium per unit: behåll mid i Tag; overlay visar tvåvägen ===
            _dgv.Rows[R(L.PremUnit)].Cells[legCol].Tag = pricePerUnitMid; // mid som råvärde
            _dgv.Rows[R(L.PremUnit)].Cells[legCol].Value = "";              // valfritt, overlay ritar texten

            // Uppdatera overlay-cache per unit från vm
            _twoWayPremCache[PremKey(L.PremUnit, legCol)] = new PremiumCellData
            {
                Bid = vm.PremUnitBid,
                Ask = vm.PremUnitAsk,
                Decimals = vm.PremUnitDecimals
            };

            // === Premium total (tvåväg i aktiv valuta) från vm ===
            _twoWayPremCache[PremKey(L.PremTot, legCol)] = new PremiumCellData
            {
                Bid = vm.PremTotalBid,
                Ask = vm.PremTotalAsk,
                Decimals = vm.PremTotalDecimals
            };

            // Visa mid i celltext (fallback/sum-look); overlay visar tvåväg
            double totMidDisp = 0.5 * (vm.PremTotalBid + vm.PremTotalAsk);
            _dgv.Rows[R(L.PremTot)].Cells[legCol].Value = FmtSpaces(totMidDisp, 2);

            // === Pips och % (avrundade) direkt från vm ===
            _dgv.Rows[R(L.PremPips)].Cells[legCol].Value = FmtSpaces(vm.PipsRounded1, 1);
            _dgv.Rows[R(L.PremPct)].Cells[legCol].Value = FmtSpaces(vm.PercentRounded4, 4) + "%";

            // === Tag-tuple för Deal-summering (valuta-invariant, bygger på avrundade pips/%) ===
            var (_, q) = GetBaseQuoteFromPair6(ReadPair6());
            double pipSize = (q == "JPY") ? 0.01 : 0.0001;
            double premTotQuoteRounded = (vm.PipsRounded1 * pipSize) * N * sg;
            double premTotBaseRounded = (vm.PercentRounded4 / 100.0) * Math.Abs(N) * sg;
            _dgv.Rows[R(L.PremTot)].Cells[legCol].Tag = Tuple.Create(premTotQuoteRounded, premTotBaseRounded);

            // === Risk (samma som tidigare – eller använd vm-fälten om din Formatter redan räknar dem) ===
            _dgv.Rows[R(L.Delta)].Cells[legCol].Tag = deltaUnit;
            _dgv.Rows[R(L.Vega)].Cells[legCol].Tag = vegaUnit;
            _dgv.Rows[R(L.Gamma)].Cells[legCol].Tag = gammaUnit;
            _dgv.Rows[R(L.Theta)].Cells[legCol].Tag = thetaUnit;

            _dgv.Rows[R(L.Delta)].Cells[legCol].Value = FmtSpaces(vm.DeltaPos, 0);
            _dgv.Rows[R(L.DeltaPct)].Cells[legCol].Value = FmtSpaces(vm.DeltaPctRounded1, 1) + "%";
            _dgv.Rows[R(L.Vega)].Cells[legCol].Value = FmtSpaces(vm.VegaPosRounded100, 0);
            _dgv.Rows[R(L.Gamma)].Cells[legCol].Value = FmtSpaces(vm.GammaPosRounded100, 0);
            _dgv.Rows[R(L.Theta)].Cells[legCol].Value = FmtSpaces(vm.ThetaPos, 2);

            // === Rita om overlay-celler + summera Deal ===
            _dgv.InvalidateCell(_dgv.Columns[legCol].Index, R(L.PremUnit));
            _dgv.InvalidateCell(_dgv.Columns[legCol].Index, R(L.PremTot));
            RecalcDealPricingAndRiskTotals();
        }


        // === Aggregates: Deal totals (Pricing & Risk) ===
        private void RecalcDealPricingAndRiskTotals()
        {

            if (_suspendDealPricingSummary) return;  // <-- lägg först

            // === 1) Summera Premium totals valuta-invariant från Tag ===
            double sumPremQuote = 0.0; // SEK
            double sumPremBase = 0.0; // EUR
            foreach (var lg in _legs)
            {
                var tag = _dgv.Rows[R(L.PremTot)].Cells[lg].Tag;
                if (tag is ValueTuple<double, double> tup)
                {
                    sumPremQuote += tup.Item1;
                    sumPremBase += tup.Item2;
                }
                else if (tag is Tuple<double, double> tupObj) // om nånstans sparats som klass-Tuple
                {
                    sumPremQuote += tupObj.Item1;
                    sumPremBase += tupObj.Item2;
                }
                else
                {
                    // Fallback om Tag saknas: ta visat värde till aktiv bank (behövs sällan)
                    if (TryParseCellNumber(_dgv.Rows[R(L.PremTot)].Cells[lg].Value, out var x))
                    {
                        if (_pricingCurrency == PricingCurrency.Quote) sumPremQuote += x;
                        else sumPremBase += x;
                    }
                }
            }

            // Visa Deal Premium total i aktiv valuta (UI oförändrat)
            double dealPremTotDisplay = (_pricingCurrency == PricingCurrency.Quote) ? sumPremQuote : sumPremBase;
            _dgv.Rows[R(L.PremTot)].Cells["Deal"].Value = FmtSpaces(dealPremTotDisplay, 2);

            // === 2) Referens: leg 1
            string leg1 = _legs.Length > 0 ? _legs[0] : null;
            if (string.IsNullOrEmpty(leg1)) return;

            double N1 = ReadNotional(leg1);
            double S1 = ReadSpot(leg1);

            // 3) Visa Deal Premium i pips – härled pipSize från kvotvalutan
            var (_, q) = GetBaseQuoteFromPair6(ReadPair6());
            double pipSize1 = (q == "JPY") ? 0.01 : 0.0001;

            // === 3) Deal Pips – härled ALLTID från Σquote (valutainvariant)
            double pricePerUnitDealQuote = (Math.Abs(N1) > 0 ? sumPremQuote / N1 : 0.0);
            double dealPips = (pipSize1 > 0 ? pricePerUnitDealQuote / pipSize1 : 0.0);
            _dgv.Rows[R(L.PremPips)].Cells["Deal"].Value =
                FmtSpaces(Math.Round(dealPips, 1, MidpointRounding.AwayFromZero), 1);

            // === 4) Deal % of Notional – härled ALLTID från Σbase (valutainvariant)
            double dealPct = (Math.Abs(N1) > 0 ? (sumPremBase / Math.Abs(N1)) * 100.0 : 0.0);
            _dgv.Rows[R(L.PremPct)].Cells["Deal"].Value =
                FmtSpaces(Math.Round(dealPct, 4, MidpointRounding.AwayFromZero), 4) + "%";

            // === 5) Summera greker – OFÖRÄNDRAT (rör inte gamma) ===
            double sumDelta = 0.0, sumVega = 0.0, sumGamma = 0.0, sumTheta = 0.0;

            foreach (var lg in _legs)
            {
                int sg = SideSign(lg);
                double N = ReadNotional(lg);

                // Hämta från Tag – default 0 om saknas
                double d = (_dgv.Rows[R(L.Delta)].Cells[lg].Tag is double td) ? td : 0.0;
                double v = (_dgv.Rows[R(L.Vega)].Cells[lg].Tag is double tv) ? tv : 0.0;
                double g = (_dgv.Rows[R(L.Gamma)].Cells[lg].Tag is double tg) ? tg : 0.0; // exakt som du hade
                double t = (_dgv.Rows[R(L.Theta)].Cells[lg].Tag is double ttv) ? ttv : 0.0;

                sumDelta += sg * d;
                sumVega += sg * v;
                sumGamma += sg * g;
                sumTheta += t * N * sg;   // <-- FIX fanns redan i din kod
            }

            // Delta position: avrunda till närmaste 1000
            double sumDeltaRoundedToK = Math.Round(sumDelta / 1000.0, 0, MidpointRounding.AwayFromZero) * 1000.0;
            _dgv.Rows[R(L.Delta)].Cells["Deal"].Value = FmtSpaces(sumDeltaRoundedToK, 0);

            // Delta %: mot leg1 (N1), 1 d.p. + '%'
            double dealDeltaPct = (Math.Abs(N1) > 0 ? (sumDelta / N1) * 100.0 : 0.0);
            _dgv.Rows[R(L.DeltaPct)].Cells["Deal"].Value =
                FmtSpaces(Math.Round(dealDeltaPct, 1, MidpointRounding.AwayFromZero), 1) + "%";

            // Avrunda Vega/Gamma till närmaste 100, 0 d.p. i Deal (oförändrat)
            double sumVegaRounded = Math.Round(sumVega / 100.0, 0, MidpointRounding.AwayFromZero) * 100.0;
            double sumGammaRounded = Math.Round(sumGamma / 100.0, 0, MidpointRounding.AwayFromZero) * 100.0;

            _dgv.Rows[R(L.Vega)].Cells["Deal"].Value = FmtSpaces(sumVegaRounded, 0);
            _dgv.Rows[R(L.Gamma)].Cells["Deal"].Value = FmtSpaces(sumGammaRounded, 0);
            _dgv.Rows[R(L.Theta)].Cells["Deal"].Value = FmtSpaces(sumTheta, 2);

            // 6) Deal-celler readonly (Pricing & Risk)
            MakeDealPricingRiskReadOnly();
        }

        /// <summary>Returnerar första bencolumn (fallback: "Deal").</summary>
        public string FirstLegColumn() => _legs != null && _legs.Length > 0 ? _legs[0] : "Deal";

        /// <summary>Returnerar en kopia av bencolumnsnamnen.</summary>
        public string[] GetLegColumns() => (string[])_legs.Clone();

        /// <summary>Hjälpare: shorthand för <see cref="FindRow"/>.</summary>
        private int R(string label) => FindRow(label);

        /// <summary>Parsar generellt tal (accepterar mellanslag/komma).</summary>
        private static bool TryParseCellNumber(object val, out double d)
        {
            d = 0;
            var s = Convert.ToString(val ?? "").Trim().Replace(" ", "").Replace(',', '.');
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
        }

        /// <summary>Läser valutaparet som 6 tecken (EURSEK, osv.).</summary>
        public string ReadPair6()
        {
            int r = FindRow(L.Pair);
            if (r < 0) return "EURSEK";
            string col = FirstLegColumn();
            object v = _dgv.Rows[r].Cells[col].Value ?? _dgv.Rows[r].Cells["Deal"].Value;
            string s = Convert.ToString(v ?? "").Trim().Replace("/", "").ToUpperInvariant();
            return s.Length >= 6 ? s.Substring(0, 6) : s;
        }

        /// <summary>Läser spot från angiven kolumn.</summary>
        public double ReadSpot(string col)
        {
            var cell = _dgv.Rows[R(L.Spot)].Cells[col];
            double v; return TryParseCellNumber(cell.Value, out v) ? v : 0.0;
        }

        // Returnerar (bid, ask, mid) för Spot från cell.Tag om SpotCellData finns;
        // annars faller vi tillbaka till ditt existerande ReadSpot(col) och kör one-way (mid=bid=ask).
        // Returnerar (bid, mid, ask, hasTwoWay) från Spot-cellen.
        // Läser Tag=SpotCellData om satt; annars tolkar visad mid (Value).
        private (double bid, double mid, double ask, bool hasTwoWay) ReadSpotTriplet(string col)
        {
            int r = R(L.Spot);
            if (r < 0) return (0, 0, 0, false);
            var cell = _dgv.Rows[r].Cells[col];

            if (cell?.Tag is SpotCellData sd)
            {
                double b = sd.Bid, m = sd.Mid, a = sd.Ask;
                if (m == 0.0) m = 0.5 * (b + a);
                bool two = (b > 0 && a > 0 && Math.Abs(a - b) > 1e-12);
                return (b, m, a, two);
            }

            // fallback: nuvarande visning (mid)
            double mid;
            if (!TryParseCellNumber(cell?.Value, out mid)) mid = 0.0;
            return (0.0, mid, 0.0, false);
        }

        /// <summary>Läser notional från angiven kolumn.</summary>
        public double ReadNotional(string col)
        {
            var cell = _dgv.Rows[R(L.Notional)].Cells[col];
            double v; return TryParseCellNumber(cell.Value, out v) ? v : 0.0;
        }

        /// <summary>Läser Buy/Sell (fallback Buy).</summary>
        public string ReadSide(string col)
        {
            var s = Convert.ToString(_dgv.Rows[R(L.Side)].Cells[col].Value ?? "").Trim().ToUpperInvariant();
            return (s == "SELL" || s == "BUY") ? s : "BUY";
        }

        /// <summary>Läser Call/Put (fallback Call).</summary>
        public string ReadCallPut(string col)
        {
            var s = Convert.ToString(_dgv.Rows[R(L.CallPut)].Cells[col].Value ?? "").Trim().ToUpperInvariant();
            return (s == "CALL" || s == "PUT") ? s : "CALL";
        }

        /// <summary>Läser strike som text.</summary>
        public string ReadStrike(string col)
        {
            return Convert.ToString(_dgv.Rows[R(L.Strike)].Cells[col].Value ?? "").Trim();
        }

        /// <summary>RD som decimal (föredrar Tag).</summary>
        public double ReadRd(string col)
        {
            int r = R(L.Rd);
            var c = _dgv.Rows[r].Cells[col];
            if (c != null && c.Tag is double) return (double)c.Tag;

            double dec;
            return TryParsePercentToDecimal(Convert.ToString(c.Value ?? ""), out dec) ? dec : 0.0;
        }

        /// <summary>RF som decimal (föredrar Tag).</summary>
        public double ReadRf(string col)
        {
            int r = R(L.Rf);
            var c = _dgv.Rows[r].Cells[col];
            if (c != null && c.Tag is double) return (double)c.Tag;

            double dec;
            return TryParsePercentToDecimal(Convert.ToString(c.Value ?? ""), out dec) ? dec : 0.0;
        }

        /// <summary>Rå expiry-text (under edit/efter edit).</summary>
        public string TryReadExpiryRaw(string col)
        {
            int r = R(L.Expiry);
            if (r < 0) return "";
            return Convert.ToString(_dgv.Rows[r].Cells[col].Value ?? "").Trim();
        }

        /// <summary>Resolved expiry i ISO om satt i Tag, annars null.</summary>
        public string TryGetResolvedExpiryIso(string col)
        {
            int r = R(L.Expiry);
            if (r < 0) return null;
            var data = _dgv.Rows[r].Cells[col].Tag as ExpiryCellData;
            return data != null ? data.Iso : null;
        }

        /// <summary>Visar resolved expiry + hint/weekday och cachar i Tag.</summary>
        public void ShowResolvedExpiry(string col, string iso, string weekday, string rawHint)
        {
            Set(col, L.Expiry, iso + "  (" + weekday + ")");
            if (!string.IsNullOrWhiteSpace(rawHint))
                _expiryRawHintByCol[col] = rawHint;
            else
                _expiryRawHintByCol.Remove(col);

            int r = R(L.Expiry);
            if (r >= 0) _dgv.InvalidateCell(_dgv.Columns[col].Index, r);

            var cell = _dgv.Rows[r].Cells[col];
            cell.Tag = new ExpiryCellData { Iso = iso, Raw = rawHint, Wd = weekday };
        }

        /// <summary>Visar resolved settlement i UI (och sätter Tag).</summary>
        public void ShowResolvedSettlement(string col, string iso)
        {
            int r = R(L.Delivery); if (r < 0) return;
            _dgv.Rows[r].Cells[col].Value = iso;
            _dgv.Rows[r].Cells[col].Tag = iso;
            _dgv.InvalidateRow(r);
        }

        private void MakeDealPricingRiskReadOnly()
        {
            string[] rows =
            {
                L.PremTot, L.PremPips, L.PremPct,
                L.Delta, L.DeltaPct, L.Vega, L.Gamma, L.Theta
            };
            foreach (var r in rows)
            {
                var c = _dgv.Rows[R(r)].Cells["Deal"];
                c.ReadOnly = true;
            }
        }

        /// <summary>Räknar om derived fält för ett ben utan ny prissättning.</summary>
        private void RecalcDerivedForColumn(string legCol)
        {
            int rPremUnit = R(L.PremUnit);
            var puCell = _dgv.Rows[rPremUnit].Cells[legCol];
            if (puCell == null || !(puCell.Tag is double)) return;

            double priceUnit = (double)puCell.Tag;

            double deltaUnit = 0.0, vegaUnit = 0.0, gammaUnit = 0.0;
            var t = _dgv.Rows[R(L.Delta)].Cells[legCol].Tag; if (t is double) deltaUnit = (double)t;
            t = _dgv.Rows[R(L.Vega)].Cells[legCol].Tag; if (t is double) vegaUnit = (double)t;
            t = _dgv.Rows[R(L.Gamma)].Cells[legCol].Tag; if (t is double) gammaUnit = (double)t;

            ShowLegResult(legCol, priceUnit, deltaUnit, vegaUnit, gammaUnit, 0.0);
        }

        /// <summary>Räknar om derived fält för alla ben.</summary>
        private void RecalcDerivedForAllLegs()
        {
            for (int i = 0; i < _legs.Length; i++)
                RecalcDerivedForColumn(_legs[i]);
        }

        /// <summary>Signalerar till presenter/motor att prissätta (null = alla ben).</summary>
        public void RaisePriceRequestedForLeg(string colOrNullAll)
        {
            PriceRequested?.Invoke(this, new PriceRequestUiArgs { TargetLeg = colOrNullAll });
        }

        /// <summary>Rollback av expiry-display på kolumn vid ogiltig input/parsing.</summary>
        public void RevertExpiryEdit(string col)
        {
            int r = R(L.Expiry);
            if (r < 0) return;
            if (_expiryPrevDisplayByCol.TryGetValue(col, out var prev))
                _dgv.Rows[r].Cells[col].Value = prev;
            _dgv.InvalidateCell(_dgv.Columns[col].Index, r);
        }

        // =======================
        // DTO för Presenter-snapshot, per ben
        // =======================
        public sealed class LegSnapshot
        {
            public string Leg { get; set; }

            // Core deal inputs
            public string Pair6 { get; set; }
            public double Spot { get; set; }
            public double Rd { get; set; }      // decimal
            public double Rf { get; set; }      // decimal
            public string Side { get; set; }    // "Buy"/"Sell"
            public string Type { get; set; }    // "Call"/"Put"
            public string Strike { get; set; }  // behålls som text enligt din formattering
            public string ExpiryRaw { get; set; }
            public double Notional { get; set; }

            public double SpotBid { get; set; }     // 0 om ej satt
            public double SpotMid { get; set; }     // = Spot (alltid satt)
            public double SpotAsk { get; set; }     // 0 om ej satt
            public bool SpotHasTwoWay { get; set; } // true om (bid>0 && ask>0 && ask!=bid)
            public bool SpotIsOverride { get; set; }

            // Vol (normaliserad)
            public double VolBid { get; set; }  // decimal, t.ex. 0.050
            public double VolAsk { get; set; }  // decimal, t.ex. 0.060
            public double VolMid { get; set; }  // (bid+ask)/2 eller singelvärde
            public bool VolHasTwoWay { get; set; }
            public bool VolIsOverride { get; set; }
        }

        /// <summary>
        /// Samlar ett immutable snapshot per ben för prissättning.
        /// Ingen UI-förändring och inga engine-calls triggas här.
        /// </summary>
        // =======================
        // Export till Presenter: immutable snapshot per ben
        // =======================
        public LegSnapshot[] GetLegSnapshotsForPresenter()
        {
            var legs = GetLegColumns();
            var list = new List<LegSnapshot>(legs.Length);

            string pair6 = ReadPair6();

            foreach (var col in legs)
            {
                // --- NYTT: spot som triplet + bakåtkomp Spot=mid ---
                double sb, sm, sa; bool sh;
                (sb, sm, sa, sh) = ReadSpotTriplet(col); // från punkt 2
                if (sm <= 0) sm = ReadSpot(col);         // safety fallback

                var (vBid, vAsk, vMid) = ReadVolTriplet(col);
                bool twoWayVol = Math.Abs(vAsk - vBid) > 1e-12;

                list.Add(new LegSnapshot
                {
                    Leg = col,
                    Pair6 = pair6,

                    // bakåtkompatibel singel-spot
                    Spot = sm,

                    // NYTT
                    SpotBid = sb,
                    SpotMid = sm,
                    SpotAsk = sa,
                    SpotHasTwoWay = sh,
                    SpotIsOverride = IsOverride(L.Spot, col),

                    Rd = ReadRd(col),
                    Rf = ReadRf(col),
                    Side = ReadSide(col),
                    Type = ReadCallPut(col),
                    Strike = ReadStrike(col),
                    ExpiryRaw = TryReadExpiryRaw(col),
                    Notional = ReadNotional(col),

                    VolBid = vBid,
                    VolAsk = vAsk,
                    VolMid = vMid,
                    VolHasTwoWay = twoWayVol,
                    VolIsOverride = IsOverride(L.Vol, col)
                });
            }

            return list.ToArray();
        }

        #endregion

        #region === Market hooks (feed snapshot & apply) ===

        // NOTE: MARKET HOOKS
        // Dessa metoder (ApplyMarketSpot/Rate/Vol + SnapshotFeedFromGrid + Set/TryGet feed)
        // är avsedda för riktig marknadsdata senare. När en feed uppdaterar UI:
        //  - Skriv normaliserat displayvärde (min 4 d.p. för Spot, 3 d.p. för rd/rf, 2 d.p. för Vol).
        //  - Uppdatera feed-baseline (_feedValue) så manuella overrides kan jämföras korrekt.
        //  - Släck override-färgen (MarkOverride(..., false)) – UI visar att värdet inte är manuellt.
        //  - Låt Presentern avgöra när pricing ska triggas (vi kallar inte RaisePriceRequested här).

        /// <summary>MARKET HOOK: feed-uppdatering med singel MID för Spot (Deal + ben).</summary>
        public void ApplyMarketSpot(double mid)
        {
            int rSpot = R(L.Spot);
            if (rSpot < 0) return;

            if (mid <= 0.0) return;

            // Skriv till Deal (bid=ask=mid) som "Feed"
            WriteSpotTwoWayToCell("Deal", mid, mid, mid, "Feed");

            // Skriv till alla ben (samma snapshot)
            for (int i = 0; i < _legs.Length; i++)
                WriteSpotTwoWayToCell(_legs[i], mid, mid, mid, "Feed");

            // Viktigt: feed-uppdatering ska inte ändra UI-läge (MID/FULL/LIVE)
            // men radens visning ska uppdateras direkt:
            _dgv.InvalidateRow(rSpot);
        }

        /// <summary>MARKETS: tvåvägs-spot till Deal (och ev. spegla till ben om du vill).</summary>
        public void ApplyMarketSpotTwoWayForDeal(double bid, double ask, bool pushToLegs = true)
        {
            int rSpot = R(L.Spot);
            if (rSpot < 0) return;

            if (bid <= 0.0 || ask <= 0.0) return;
            if (ask < bid) { var t = bid; bid = ask; ask = t; }
            double mid = 0.5 * (bid + ask);

            // Deal
            WriteSpotTwoWayToCell("Deal", bid, mid, ask, "Feed");

            // (valfritt) Skjut ut samma snapshot till alla ben
            if (pushToLegs)
                for (int i = 0; i < _legs.Length; i++)
                    WriteSpotTwoWayToCell(_legs[i], bid, mid, ask, "Feed");

            _dgv.InvalidateRow(rSpot);
        }

        // single writer för tvåvägs-spot till en kolumn (Deal eller ben)
        // - Tag får SpotCellData (bid/mid/ask + tidsstämpel + källa).
        // - Value visas med EXAKT 4 d.p. för feed (Manual fortsätter följa användarens inmatning).
        // - Feed/override-flagga sätts korrekt (override=false för feed).
        private void WriteSpotTwoWayToCell(string col, double bid, double mid, double ask, string source = "Feed")
        {
            // Sanity
            if (ask < bid) { var t = bid; bid = ask; ask = t; }
            if (mid == 0.0) mid = 0.5 * (bid + ask);

            int r = R(L.Spot);
            if (r < 0) return;

            var cell = _dgv.Rows[r].Cells[col];

            // 1) Sätt snapshot i Tag
            cell.Tag = new SpotCellData
            {
                Bid = bid,
                Mid = mid,
                Ask = ask,
                TimeUtc = DateTime.UtcNow,
                Source = source ?? "Feed"
            };

            // 2) Visa mid. För FEED vill vi EXAKT 4 d.p., inte “så många som råtexten råkar ha”.
            //    Tricket: ge FormatSpotWithMinDecimals en "raw" med 4 d.p.
            string rawForDisplay;
            if (string.Equals(source, "Manual", StringComparison.OrdinalIgnoreCase))
            {
                // Manuell input: behåll tidigare beteende (minst 4 d.p., men respekt för användarens fler d.p.)
                rawForDisplay = mid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                // Feed: lås till exakt 4 d.p.
                rawForDisplay = mid.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture);
            }

            cell.Value = FormatSpotWithMinDecimals(rawForDisplay, mid, 4);

            // 3) Marknadsvärde → släck override + uppdatera feed-baseline (behåller double)
            SetFeedValue(L.Spot, col, mid);
            MarkOverride(L.Spot, col, false);

            // 4) Invalidera just denna cell
            _dgv.InvalidateCell(_dgv.Columns[col].Index, r);
        }

        /// <summary>
        /// Sätter SpotCellData i Deal + alla ben till bid=ask=mid (behåller tid/källa).
        /// Om Tag saknas byggs snapshot från visad mid.
        /// </summary>
        private void CollapseSpotSnapshotsToMidForAll()
        {
            int rSpot = R(L.Spot);
            if (rSpot < 0) return;

            void collapse(string col)
            {
                var cell = _dgv.Rows[rSpot].Cells[col];
                double mid = 0.0;
                if (cell.Tag is SpotCellData sd)
                {
                    mid = (sd.Mid != 0.0) ? sd.Mid : 0.5 * (sd.Bid + sd.Ask);
                    cell.Tag = new SpotCellData
                    {
                        Bid = mid,
                        Mid = mid,
                        Ask = mid,
                        TimeUtc = sd.TimeUtc,
                        Source = sd.Source
                    };
                }
                else
                {
                    if (!TryParseCellNumber(cell.Value, out mid) || mid <= 0.0) return;
                    cell.Tag = new SpotCellData
                    {
                        Bid = mid,
                        Mid = mid,
                        Ask = mid,
                        TimeUtc = DateTime.UtcNow,
                        Source = "Feed"
                    };
                }

                string raw = mid.ToString(System.Globalization.CultureInfo.InvariantCulture);
                cell.Value = FormatSpotWithMinDecimals(raw, mid, 4);
                _dgv.InvalidateCell(_dgv.Columns[col].Index, rSpot);
            }

            collapse("Deal");
            for (int i = 0; i < _legs.Length; i++)
                collapse(_legs[i]);
        }






        /// <summary>MARKET HOOK: Sann feed-uppdatering för ränta (rd/rf) per kolumn.</summary>
        public void ApplyMarketRate(string labelRdOrRf, string col, double dec)
        {
            // label = L.Rd eller L.Rf, col = "Leg1"/"Leg2"/... eller "Deal" om du vill
            int r = R(labelRdOrRf); if (r < 0) return;
            var c = _dgv.Rows[r].Cells[col];
            c.Tag = dec;
            c.Value = FormatPercent(dec, 3);

            SetFeedValue(labelRdOrRf, col, dec);
            MarkOverride(labelRdOrRf, col, false);
            _dgv.InvalidateCell(_dgv.Columns[col].Index, r);
        }

        /// <summary>MARKET HOOK: Sann feed-uppdatering för vol (bid/ask) per kolumn.</summary>
        public void ApplyMarketVol(string col, double bidDec, double askDec)
        {
            int r = R(L.Vol); if (r < 0) return;
            var c = _dgv.Rows[r].Cells[col];
            var v = new VolCellData { Bid = bidDec, Ask = askDec };
            c.Tag = v;
            c.Value = (Math.Abs(bidDec - askDec) < 1e-12)
                        ? FormatPercent(bidDec, 2)
                        : (FormatPercent(bidDec, 2) + " / " + FormatPercent(askDec, 2));

            SetFeedValue(L.Vol, col, v);
            MarkOverride(L.Vol, col, false);
            _dgv.InvalidateCell(_dgv.Columns[col].Index, r);
        }

        /// <summary>Tar en snapshot av nuvarande gridvärden som feed-baseline (släcker overrides).</summary>
        private void SnapshotFeedFromGrid()
        {
            // SPOT: läs Deal + alla ben (treat current visning som "market mid")
            int rSpot = R(L.Spot);
            if (rSpot >= 0)
            {
                var deal = _dgv.Rows[rSpot].Cells["Deal"];
                double spot;
                if (TryParseCellNumber(Convert.ToString(deal.Value ?? ""), out spot) && spot > 0)
                {
                    SetFeedValue(L.Spot, "Deal", spot);
                    MarkOverride(L.Spot, "Deal", false);
                }
                for (int i = 0; i < _legs.Length; i++)
                {
                    string col = _legs[i];
                    var c = _dgv.Rows[rSpot].Cells[col];
                    if (TryParseCellNumber(Convert.ToString(c.Value ?? ""), out spot) && spot > 0)
                    {
                        SetFeedValue(L.Spot, col, spot);
                        MarkOverride(L.Spot, col, false);
                    }
                }
            }

            // RD/RF: decimalen ligger i Tag om du följt tidigare kod; annars tolka Value som %
            Action<string> snapRate = (label) =>
            {
                int r = R(label);
                if (r < 0) return;
                foreach (var col in _legs)
                {
                    var c = _dgv.Rows[r].Cells[col];
                    double dec;
                    if (c.Tag is double) dec = (double)c.Tag;
                    else if (!TryParsePercentToDecimal(Convert.ToString(c.Value ?? ""), out dec)) continue;

                    SetFeedValue(label, col, dec);
                    MarkOverride(label, col, false);
                }
                // (Deal-kolumnen för rd/rf använder du mest som “push”; sätt baseline om du vill)
                var deal = _dgv.Rows[r].Cells["Deal"];
                double dDeal;
                if (TryParsePercentToDecimal(Convert.ToString(deal.Value ?? ""), out dDeal))
                {
                    SetFeedValue(label, "Deal", dDeal);
                    MarkOverride(label, "Deal", false);
                }
            };
            snapRate(L.Rd);
            snapRate(L.Rf);

            // VOL: föredra Tag=VolCellData, annars tolka Value (singel eller "b/a")
            int rVol = R(L.Vol);
            if (rVol >= 0)
            {
                foreach (var col in _legs)
                {
                    var c = _dgv.Rows[rVol].Cells[col];
                    VolCellData vcd = c.Tag as VolCellData;
                    if (vcd == null)
                    {
                        double b, a; bool isPair;
                        if (TryParseVolInput(Convert.ToString(c.Value ?? ""), out b, out a, out isPair))
                            vcd = new VolCellData { Bid = b, Ask = a };
                    }
                    if (vcd != null)
                    {
                        SetFeedValue(L.Vol, col, vcd);
                        MarkOverride(L.Vol, col, false);
                    }
                }
                // (Deal kan du ignorera eller snapshot:a om du vill)
            }

            _dgv.Invalidate();
        }

        /// <summary>Sätter cellvärde + ev. Tag och släcker override-färg (används av Apply*).</summary>
        public void ApplyMarketValue(string label, string col, string display, object tag = null)
        {
            int r = FindRow(label);
            if (r < 0) return;

            var cell = _dgv.Rows[r].Cells[col];
            cell.Value = display;
            if (tag != null) cell.Tag = tag;

            MarkOverride(label, col, false); // återställ färg
            _dgv.InvalidateCell(_dgv.Columns[col].Index, r);
        }

        /// <summary>Sätter feed-baseline (kopierar VolCellData för säkerhets skull).</summary>
        private void SetFeedValue(string label, string col, object logical)
        {
            if (logical is VolCellData vv) logical = CloneVol(vv);
            _feedValue[Key(label, col)] = logical;
        }

        /// <summary>Försöker hämta feed-baseline som double.</summary>
        private bool TryGetFeedDouble(string label, string col, out double d)
        {
            object o;
            if (_feedValue.TryGetValue(Key(label, col), out o) && o is double)
            {
                d = (double)o; return true;
            }
            d = 0; return false;
        }

        /// <summary>Försöker hämta feed-baseline som VolCellData.</summary>
        private bool TryGetFeedVol(string label, string col, out VolCellData v)
        {
            object o;
            if (_feedValue.TryGetValue(Key(label, col), out o) && o is VolCellData)
            {
                v = CloneVol((VolCellData)o); return true;
            }
            v = null; return false;
        }

        /// <summary>Snapshot: sparar ursprunglig textfärg per cell för korrekt override on/off.</summary>
        private void SnapshotOriginalForeColors()
        {
            for (int r = 0; r < _dgv.Rows.Count; r++)
            {
                string label = Convert.ToString(_dgv.Rows[r].Cells["FIELD"].Value ?? "");
                if (string.IsNullOrWhiteSpace(label)) continue;

                foreach (DataGridViewColumn col in _dgv.Columns)
                {
                    string colName = col.Name;
                    if (colName == "FIELD") continue;

                    var cell = _dgv.Rows[r].Cells[colName];
                    // Använd faktiskt nedärvd färg (kan vara grå på vissa rader/kolumner)
                    var c = cell.InheritedStyle.ForeColor;
                    if (c.IsEmpty) c = Color.Black;

                    _origFore[Key(label, colName)] = c;
                }
            }
        }

        /// <summary>Slår på/av override-färg (och selection-färg) för en cell.</summary>
        private void MarkOverride(string label, string col, bool on)
        {
            int r = FindRow(label);
            if (r < 0) return;

            var cell = _dgv.Rows[r].Cells[col];
            string k = Key(label, col);

            if (on)
            {
                _overrides.Add(k);
                cell.Style.ForeColor = OverrideFore;
                cell.Style.SelectionForeColor = OverrideFore; // följ med i selection
            }
            else
            {
                _overrides.Remove(k);
                Color orig;
                if (_origFore.TryGetValue(k, out orig))
                {
                    cell.Style.ForeColor = orig;                // explicit original
                    cell.Style.SelectionForeColor = orig;
                }
                else
                {
                    cell.Style.ForeColor = Color.Empty;         // återgå till inherited
                    cell.Style.SelectionForeColor = Color.Empty;
                }
            }

            _dgv.InvalidateCell(_dgv.Columns[col].Index, r);
        }

        /// <summary>True om cellen är markerad som override.</summary>
        private bool IsOverride(string label, string col) => _overrides.Contains(Key(label, col));

        #endregion

        #region === Notional + formatting helpers ===

        /// <summary>Parsar notional med suffix (k/m/bn) och returnerar värde + precision.</summary>
        private static bool TryParseNotional(string raw, out double value, out int decimals, out bool allZero)
        {
            value = 0.0; decimals = 0; allZero = false;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            string s = raw.Trim().Replace(" ", "").Replace("\u00A0", "");
            s = s.Replace(',', '.');

            double factor = 1.0;
            if (s.Length > 0)
            {
                char last = s[s.Length - 1];
                char c = char.ToLowerInvariant(last);
                if (char.IsLetter(c))
                {
                    s = s.Substring(0, s.Length - 1);
                    if (c == 'k') factor = 1_000d;
                    else if (c == 'm') factor = 1_000_000d;
                    else if (c == 'b' || c == 'y') factor = 1_000_000_000d;
                    else return false;
                }
            }

            int dot = s.IndexOf('.');
            if (dot >= 0 && dot < s.Length - 1)
            {
                string dec = s.Substring(dot + 1);
                decimals = dec.Length;
                allZero = IsAllZero(dec);
            }

            double core;
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out core))
                return false;

            value = core * factor;
            return true;
        }

        private static string FormatFixed(double v, int decimals)
        {
            return v.ToString("F" + decimals, System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>True om alla tecken i strängen är '0' och strängen ej tom.</summary>
        private static bool IsAllZero(string t)
        {
            for (int i = 0; i < t.Length; i++)
                if (t[i] != '0') return false;
            return t.Length > 0;
        }

        /// <summary>
        /// Formaterar notional: om det skalade värdet är heltal → 0 d.p.,
        /// annars behåll minsta nödvändiga d.p. baserat på input.
        /// </summary>
        private static string FormatNotional(double value, int decimalsFromInput, bool allZeroFromInput)
        {
            int d = IsEffectivelyInteger(value) ? 0
                    : (allZeroFromInput ? 0 : Math.Max(0, decimalsFromInput));

            string text = value.ToString("N" + d, CultureInfo.InvariantCulture);
            return text.Replace(",", " ");
        }

        /// <summary>Formattering med mellanslag som tusentalsavskiljare.</summary>
        private static string FmtSpaces(double v, int decimals = 6)
        {
            var s = v.ToString("N" + decimals, CultureInfo.InvariantCulture);
            return s.Replace(",", " ");
        }

        /// <summary>Side→"Buy"/"Sell".</summary>
        private static bool TryCanonicalizeSide(string raw, out string canon)
        {
            canon = null;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var s = raw.Trim().ToLowerInvariant();
            if (s == "b" || s == "buy" || s == "köp") { canon = "Buy"; return true; }
            if (s == "s" || s == "sell" || s == "sälj") { canon = "Sell"; return true; }
            return false;
        }

        /// <summary>Payoff→"Call"/"Put".</summary>
        private static bool TryCanonicalizePayoff(string raw, out string canon)
        {
            canon = null;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var s = raw.Trim().ToLowerInvariant();
            if (s == "c" || s == "call") { canon = "Call"; return true; }
            if (s == "p" || s == "put") { canon = "Put"; return true; }
            return false;
        }

        /// <summary>+1 för Buy, −1 för Sell, default +1.</summary>
        private int SideSign(string col)
        {
            var s = Convert.ToString(_dgv.Rows[R(L.Side)].Cells[col].Value ?? "");
            if (s.IndexOf("SELL", StringComparison.OrdinalIgnoreCase) >= 0) return -1;
            if (s.IndexOf("BUY", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
            return 1;
        }

        /// <summary>Spot: minst N decimaler, men behåll fler om användaren skrev fler.</summary>
        private static string FormatSpotWithMinDecimals(string raw, double numeric, int minDecimals)
        {
            int userDecs = CountDecimalsInRaw(raw);
            int decs = Math.Max(minDecimals, userDecs);
            string fmt = "0." + new string('0', decs);
            return numeric.ToString(fmt, CultureInfo.InvariantCulture);
        }

        /// <summary>Räknar antal decimaler i råtext (hanterar ',' '.' och mellanslag).</summary>
        private static int CountDecimalsInRaw(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return 0;
            string s = raw.Trim().Replace(" ", "").Replace("\u00A0", "").Replace(',', '.');
            int i = s.IndexOf('.');
            if (i < 0 || i == s.Length - 1) return 0;
            return Math.Max(0, s.Length - 1 - i);
        }

        /// <summary>Robust heltalskontroll för double.</summary>
        private static bool IsEffectivelyInteger(double v)
        {
            return Math.Abs(v - Math.Round(v)) < 1e-9;
        }

        /// <summary>Double-jämförelse med epsilon.</summary>
        private static bool Eq(double a, double b, double eps = 1e-9) => Math.Abs(a - b) <= eps;

        /// <summary>Jämförelse av vol-par med epsilon.</summary>
        private static bool Eq(VolCellData a, VolCellData b, double eps = 1e-9)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            return Eq(a.Bid, b.Bid, eps) && Eq(a.Ask, b.Ask, eps);
        }

        /// <summary>Vol-input: ”5” ⇒ mid; ”5/6” eller ”5 6” ⇒ bid/ask (procent).</summary>
        private static bool TryParseVolInput(string raw, out double bid, out double ask, out bool isPair)
        {
            bid = ask = 0; isPair = false;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            string s = raw.Trim().Replace("\u00A0", " ");
            s = s.Replace(',', '.').ToLowerInvariant();

            string[] tokens;
            if (s.Contains("/"))
            {
                tokens = s.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            }
            else
            {
                tokens = System.Text.RegularExpressions.Regex
                         .Split(s, "\\s+")
                         .Where(t => !string.IsNullOrWhiteSpace(t))
                         .ToArray();
            }

            if (tokens.Length == 1)
            {
                if (!TryParsePercentToDecimal(tokens[0], out double mid)) return false;
                bid = ask = mid; isPair = false; return true;
            }
            else if (tokens.Length == 2)
            {
                if (!TryParsePercentToDecimal(tokens[0], out double b)) return false;
                if (!TryParsePercentToDecimal(tokens[1], out double a)) return false;
                bid = b; ask = a; isPair = true; return true;
            }
            return false;
        }

        /// <summary>Formaterar tvåvägsvol som ”bid / ask”.</summary>
        private static string FormatPercentPair(double bidDec, double askDec, int decimals = 2)
        {
            return FormatPercent(bidDec, decimals) + " / " + FormatPercent(askDec, decimals);
        }

        /// <summary>Klonar vol-objekt.</summary>
        private static VolCellData CloneVol(VolCellData v)
        {
            return v == null ? null : new VolCellData { Bid = v.Bid, Ask = v.Ask };
        }

        #endregion

        #region === Event args & key handling ===

        /// <summary>Args till prissättningen.</summary>
        public sealed class PriceRequestUiArgs : EventArgs
        {
            public string Pair6;
            public double? Spot;
            public double? Rd;
            public double? Rf;
            public string Side;
            public string Type;
            public string Strike;
            public string ExpiryRaw;
            public double Notional;

            /// <summary>Om satt (icke tom), prisa endast detta ben; annars prisa alla.</summary>
            public Guid LegId { get; set; }            // NEW

            /// <summary>Bakåtkompatibel legacy-etikett (om din UI fortfarande skickar label).</summary>
            public string TargetLeg { get; set; }      // befintlig
        }

        /// <summary>Args för att låta presenter lösa/validera expiry.</summary>
        public sealed class ExpiryEditRequestedEventArgs : EventArgs
        {
            public string Pair6 { get; set; }
            public string Raw { get; set; }
            public string LegColumn { get; set; }
        }

        /// <summary>
        /// F9: prisa alla ben. F2: gå in i edit-läge och markera hela innehållet i cellen.
        /// </summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {

            // F2: in i edit mode + markera allt innehåll
            if (keyData == Keys.F2)
            {
                var cell = _dgv.CurrentCell;
                if (cell != null && !cell.ReadOnly && !IsSectionRow(_dgv.Rows[cell.RowIndex]))
                {
                    _dgv.BeginEdit(true);

                    if (_dgv.EditingControl is TextBox tb)
                    {
                        // markera hela cellens text
                        tb.SelectionStart = 0;
                        tb.SelectionLength = tb.TextLength;
                    }
                    return true;
                }
                return false; // ej editerbar → låt bas hantera
            }

            if (keyData == Keys.F5)
            {
                SpotRefreshRequested?.Invoke(this, EventArgs.Empty);
                return true; // markerat som hanterat
            }

            if (keyData == Keys.F6)
            {
                AddLegRequested?.Invoke(this, EventArgs.Empty);
                return true;
            }

            if (keyData == Keys.F7)
            {
                RatesRefreshRequested?.Invoke(this, EventArgs.Empty);
                return true;
            }

            // F9: reprice alla ben (oförändrat)
            if (keyData == Keys.F9)
            {
                PriceRequested?.Invoke(this, new PriceRequestUiArgs { TargetLeg = null });
                return true;
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        #endregion

        #region === GDI helper ===

        private static class SmoothingScope
        {
            public static IDisposable Smooth(Graphics g)
            {
                var old = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                return new Reset(g, old);
            }
            private sealed class Reset : IDisposable
            {
                private readonly Graphics _g;
                private readonly SmoothingMode _old;
                public Reset(Graphics g, SmoothingMode old) { _g = g; _old = old; }
                public void Dispose() { _g.SmoothingMode = _old; }
            }
        }

        #endregion

        #region === Helpers ===

        /// <summary>
        /// Säkerställer att en UI-kolumn med namn <paramref name="label"/> finns.
        /// Om den saknas skapas den och seedas från <paramref name="seedFromLabel"/> om angiven och giltig,
        /// annars från första ben-kolumnen. Kopierar både Value och Tag så format/hints följer med.
        /// </summary>
        private void EnsureLegColumnExists(string label, string seedFromLabel = null)
        {
            if (string.IsNullOrWhiteSpace(label)) return;

            // Finns kolumnen redan? Se till att _legs känner till den och returnera.
            if (_dgv.Columns.Contains(label))
            {
                bool known = false;
                for (int i = 0; i < _legs.Length; i++)
                {
                    if (string.Equals(_legs[i], label, StringComparison.OrdinalIgnoreCase)) { known = true; break; }
                }
                if (!known)
                {
                    Array.Resize(ref _legs, _legs.Length + 1);
                    _legs[_legs.Length - 1] = label;
                }
                return;
            }

            // Skapa ny benkolumn
            var col = new DataGridViewTextBoxColumn
            {
                Name = label,
                HeaderText = label,
                Width = ColumnLegWidth,
                ValueType = typeof(string),
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Alignment = DataGridViewContentAlignment.MiddleRight,
                    Padding = new Padding(0, 0, 10, 0)
                }
            };
            _dgv.Columns.Add(col);

            // Välj seed-källa: specificerad kolumn om den finns och är ett ben; annars första benkolumnen.
            string srcCol = null;
            if (!string.IsNullOrWhiteSpace(seedFromLabel) &&
                _dgv.Columns.Contains(seedFromLabel) &&
                IsLegColumn(seedFromLabel))
            {
                srcCol = seedFromLabel;
            }
            else
            {
                var first = FirstLegColumn();
                if (!string.IsNullOrEmpty(first) && IsLegColumn(first))
                    srcCol = first;
            }

            // Kopiera cellinnehåll (Value + Tag) rad för rad
            for (int r = 0; r < _dgv.Rows.Count; r++)
            {
                var dst = _dgv.Rows[r].Cells[label];

                if (!string.IsNullOrEmpty(srcCol) && _dgv.Columns.Contains(srcCol))
                {
                    var src = _dgv.Rows[r].Cells[srcCol];
                    dst.Value = src?.Value; // inkl. formaterad expiry "[1M] yyyy-mm-dd (Wed)"
                    dst.Tag = src?.Tag;   // om du sparar metadata här
                }
                else
                {
                    dst.Value = null;
                    dst.Tag = null;
                }

                // Sätt readonly/utseende för vissa rader enligt din initlogik
                var rowLabel = Convert.ToString(_dgv.Rows[r].Cells["FIELD"].Value ?? "");
                if (string.Equals(rowLabel, L.Pair, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(rowLabel, L.Delivery, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(rowLabel, L.Spot, StringComparison.OrdinalIgnoreCase) ||
                    rowLabel == L.PremUnit || rowLabel == L.PremTot || rowLabel == L.PremPips || rowLabel == L.PremPct ||
                    rowLabel == L.Delta || rowLabel == L.DeltaPct || rowLabel == L.Vega || rowLabel == L.Gamma || rowLabel == L.Theta)
                {
                    dst.ReadOnly = true;
                    if (string.Equals(rowLabel, L.Pair, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(rowLabel, L.Delivery, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(rowLabel, L.Spot, StringComparison.OrdinalIgnoreCase))
                    {
                        dst.Style.ForeColor = Color.Gray;
                    }
                }
            }

            // Registrera kolumnen i _legs så att övriga loopar tar med den
            Array.Resize(ref _legs, _legs.Length + 1);
            _legs[_legs.Length - 1] = label;

            _dgv.Invalidate();
        }



        private static (string Base, string Quote) GetBaseQuoteFromPair6(string pair6)
        {
            if (string.IsNullOrWhiteSpace(pair6) || pair6.Length < 6) return ("", "");
            return (pair6.Substring(0, 3).ToUpperInvariant(), pair6.Substring(3, 3).ToUpperInvariant());
        }

        // Uppdatera "Premium total (XXX)"-etiketten i FIELD-kolumnen
        private void ApplyPremiumCurrencyLabel()
        {
            var (b, q) = GetBaseQuoteFromPair6(ReadPair6());
            string cur = (_pricingCurrency == PricingCurrency.Quote) ? q : b;
            int r = R(L.PremTot);
            if (r >= 0) _dgv.Rows[r].Cells["FIELD"].Value = $"Premium total ({cur})";
        }

        /// <summary>
        /// True om SPOT-läget är Mid (UI visar/prisar som mid). False om Full (two-way).
        /// </summary>
        public bool IsSpotModeMid()
        {
            return _spotMode == SpotMode.Mid;
        }

        private static int BuySellSign(string side)
        {
            return (string.Equals(side, "SELL", StringComparison.OrdinalIgnoreCase)) ? -1 : 1;
        }

        #endregion

        #region === Vol & vol spread

        /// <summary>
        /// Recalc Vol Spread (Ask - Bid) för alla legs. Visar 3 dp och sparar decimal i Tag.
        /// </summary>
        private void RecalcVolSpreadForAllLegs()
        {
            int rVol = FindRow(L.Vol);
            int rSpr = FindRow(L.VolSprd);
            if (rVol < 0 || rSpr < 0) return;

            foreach (var leg in _legs)
            {
                var cVol = _dgv.Rows[rVol].Cells[leg];
                double bid = 0.0, ask = 0.0;

                if (cVol.Tag is VolCellData vd)
                {
                    bid = vd.Bid;
                    ask = vd.Ask > 0.0 ? vd.Ask : vd.Bid;
                }
                else
                {
                    // fallback: parse display (”5/6”, ”5 6” eller ”5”)
                    double b, a; bool isPair;
                    if (TryParseVolInput(Convert.ToString(cVol.Value ?? ""), out b, out a, out isPair))
                    { bid = b; ask = isPair ? a : b; }
                    else
                    { bid = ask = 0.0; }
                }

                double spr = Math.Max(0.0, ask - bid);
                var cSpr = _dgv.Rows[rSpr].Cells[leg];
                cSpr.Tag = spr;                     // decimal (t.ex. 0.010)
                cSpr.Value = FormatPercent(spr, 3); // ”1.000%”
            }
        }


        /// <summary>Skriver Vol (bid/ask) till cell + Tag och normaliserar visning till 3 dp.</summary>
        private void WriteVolFromBidAsk(string col, double bid, double ask)
        {
            int rVol = FindRow(L.Vol);
            if (rVol < 0) return;
            var cVol = _dgv.Rows[rVol].Cells[col];

            if (ask < bid) { var t = bid; bid = ask; ask = t; } // säkerställ ask ≥ bid
            cVol.Tag = new VolCellData { Bid = bid, Ask = ask };
            cVol.Value = (Math.Abs(ask - bid) < 1e-12)
                ? FormatPercent(bid, 3)                // singel (mid) visning "x.xxx%"
                : FormatPercentPair(bid, ask, 3);      // tvåvägs "x.xxx% / y.yyy%"
        }

        /// <summary>Returnerar (bid, ask, mid) för en kolumn (leg) från Vol-cellen.</summary>
        private (double bid, double ask, double mid) ReadVolTriplet(string col)
        {
            int rVol = FindRow(L.Vol);
            if (rVol < 0) return (0, 0, 0);
            var cVol = _dgv.Rows[rVol].Cells[col];

            double bid, ask; bool twoWay;
            if (cVol.Tag is VolCellData vd)
            {
                bid = vd.Bid; ask = (vd.Ask > 0.0 ? vd.Ask : vd.Bid);
                twoWay = (vd.Ask > 0.0 && vd.Ask != vd.Bid);
            }
            else
            {
                if (!TryParseVolInput(Convert.ToString(cVol.Value ?? ""), out bid, out ask, out twoWay))
                    bid = ask = 0.0;
                if (!twoWay) ask = bid;
            }
            double mid = 0.5 * (bid + ask);
            return (bid, ask, mid);
        }

        /// <summary>
        /// Applicera en Vol spread (decimal) från Deal till alla legs:
        /// bid_leg = mid_leg − spr/2, ask_leg = mid_leg + spr/2.
        /// Skriver Vol (3 dp) och uppdaterar legs Vol spread (3 dp).
        /// </summary>
        private void ApplyVolSpreadFromDealToLegs(double spr)
        {
            if (spr < 0) spr = 0.0;
            double half = 0.5 * spr;

            foreach (var leg in _legs)
            {
                var (b, a, mid) = ReadVolTriplet(leg);
                double nbid = Math.Max(0.0, mid - half);
                double nask = mid + half;

                WriteVolFromBidAsk(leg, nbid, nask);
                MarkOverride(L.Vol, leg, true); // användarstyrt från Deal spridning
            }

            // skriv samtidigt Vol spread (3 dp) per leg
            RecalcVolSpreadForAllLegs();
        }

        #endregion

        #region === LegId ↔ UI-kolumn ===

        // === NEW: LegId → label-karta i vyn ===
        private readonly Dictionary<Guid, string> _legIdToLabel = new Dictionary<Guid, string>();

        /// <summary>
        /// Registrerar kopplingen mellan ett stabilt LegId och en UI-etikett (t.ex. "Vanilla 2").
        /// Säkerställer att kolumnen för etiketten finns (skapas vid behov) innan bindningen.
        /// </summary>
        public void BindLegIdToLabel(Guid legId, string label)
        {
            if (legId == Guid.Empty || string.IsNullOrWhiteSpace(label)) return;

            // Se till att UI-kolumnen finns
            EnsureLegColumnExists(label);

            // Registrera mappningen
            _legIdToLabel[legId] = label;
        }

        /// <summary>
        /// Binder ett stabilt <paramref name="legId"/> till en UI-kolumn/etikett <paramref name="label"/> och
        /// säkerställer att kolumnen finns. Om <paramref name="seedFromLabel"/> anges och finns,
        /// seedas värden (Value/Tag) från den kolumnen i stället för default.
        /// </summary>
        public void BindLegIdToLabel(Guid legId, string label, string seedFromLabel)
        {
            if (legId == Guid.Empty || string.IsNullOrWhiteSpace(label)) return;
            EnsureLegColumnExists(label, seedFromLabel);
            _legIdToLabel[legId] = label;
        }



        /// <summary>Slår upp UI-etiketten för ett LegId. Returnerar null om okänt.</summary>
        private string TryGetLabel(Guid legId)
        {
            if (_legIdToLabel.TryGetValue(legId, out var label))
                return label;
            return null;
        }



        /// <summary>Uppdaterar tvåväg per-unit för ett specifikt ben via LegId.</summary>
        public void ShowTwoWayPremiumFromPerUnitById(Guid legId, double bid, double ask)
        {
            var label = TryGetLabel(legId);
            if (label == null) return;
            ShowTwoWayPremiumFromPerUnit(label, bid, ask); // befintlig label-metod
        }

        /// <summary>Uppdaterar mid + greker för ett specifikt ben via LegId.</summary>
        public void ShowLegResultById(Guid legId, double mid, double delta, double vega, double gamma, double theta)
        {
            var label = TryGetLabel(legId);
            if (label == null) return;
            ShowLegResult(label, mid, delta, vega, gamma, theta); // befintlig label-metod
        }

        /// <summary>Returnerar redan-resolved Expiry ISO för ett ben (om det finns), via LegId.</summary>
        public string TryGetResolvedExpiryIsoById(Guid legId)
        {
            var label = TryGetLabel(legId);
            return label == null ? null : TryGetResolvedExpiryIso(label); // befintlig label-metod
        }

        /// <summary>Visar resolved expiry (ISO + weekday + hint) för ett ben via LegId.</summary>
        public void ShowResolvedExpiryById(Guid legId, string expiryIso, string weekdayEn, string hint = null)
        {
            var label = TryGetLabel(legId);
            if (label == null) return;
            ShowResolvedExpiry(label, expiryIso, weekdayEn, hint); // befintlig label-metod
        }

        /// <summary>Visar resolved settlement-datum för ett ben via LegId.</summary>
        public void ShowResolvedSettlementById(Guid legId, string settlementIso)
        {
            var label = TryGetLabel(legId);
            if (label == null) return;
            ShowResolvedSettlement(label, settlementIso); // befintlig label-metod
        }

        /// <summary>
        /// Hämtar presenter-snapshot för ett specifikt ben via LegId
        /// genom att slå upp UI-etiketten och filtrera befintliga snapshots.
        /// </summary>
        public LegSnapshot GetLegSnapshotById(Guid legId)
        {
            var label = TryGetLabel(legId);
            if (label == null) return null;

            var all = GetLegSnapshotsForPresenter();           // befintlig metod som returnerar alla
            return Array.Find(all, s => string.Equals(s.Leg,   // filtrera på kolumn/label
                                  label, StringComparison.OrdinalIgnoreCase));
        }

        #endregion


        /// <summary>
        /// Programmatisk trigger för att lägga till ett nytt ben (motsvarar F6).
        /// </summary>
        public void TriggerAddLeg() => AddLegRequested?.Invoke(this, EventArgs.Empty);


        // Formatter skapas on-demand utifrån pair och vald display-ccy
        private PricingFormatter CreateFormatter()
        {
            var pair6 = ReadPair6();
            var quote = (pair6 != null && pair6.Length >= 6) ? pair6.Substring(3, 3).ToUpperInvariant() : "SEK";
            var ffCcy = (_displayCcy == DisplayCcy.Quote)
                ? PricingFormatter.DisplayCcy.Quote
                : PricingFormatter.DisplayCcy.Base;
            return new PricingFormatter(quote, ffCcy);
        }

        // Intern cache för tvåvägs-premie per cell (label|col)
        private readonly Dictionary<string, PremiumCellData> _twoWayPremCache = new Dictionary<string, PremiumCellData>(StringComparer.Ordinal);

        // Nyckel för cache
        private static string PremKey(string label, string col) => label + "|" + col;

        private static string NormalizePremLabel(string lbl)
        {
            if (string.IsNullOrWhiteSpace(lbl)) return "";
            lbl = lbl.Trim();
            // Hantera "Premium total (SEK/EUR/…)" som "Premium total (Quote)"
            if (lbl.StartsWith("Premium total", StringComparison.OrdinalIgnoreCase))
                return L.PremTot;
            return lbl;
        }

        private void RebuildTwoWayTotalsCacheForCurrentCurrency()
        {
            // Här vill vi använda samma avrundningsregler som initial render:
            //  - Quote: runda pips (1 d.p.) -> per-unit -> total
            //  - Base : runda %   (4 d.p.)  -> total
            // och lägga in resultatet i overlay-cachen för "Premium total".
            var pair6 = ReadPair6();
            var quote = (pair6 != null && pair6.Length >= 6)
                ? pair6.Substring(3, 3).ToUpperInvariant()
                : "SEK";
            var disp = (_pricingCurrency == PricingCurrency.Quote)
                ? PricingFormatter.DisplayCcy.Quote
                : PricingFormatter.DisplayCcy.Base;

            var fmt = new PricingFormatter(quote, disp); // befintlig formatter (ingen ny helper)  :contentReference[oaicite:2]{index=2}

            foreach (var leg in _legs)
            {
                // Hämta per-unit tvåväg ur befintlig cache (fallback mid/mid=0)
                if (!_twoWayPremCache.TryGetValue(PremKey(L.PremUnit, leg), out var perUnit) || perUnit == null)
                {
                    _twoWayPremCache.Remove(PremKey(L.PremTot, leg));
                    continue;
                }

                double puBid = perUnit.Bid;
                double puAsk = perUnit.Ask;

                // Inputs
                double N = ReadNotional(leg);
                double S = ReadSpot(leg);
                int sg = SideSign(leg);

                // Kör formattern med BID/ASK så vi får totaler enligt avrundningsreglerna
                var vm = fmt.Build(
                    leg: leg,
                    perUnitBid: puBid,
                    perUnitAsk: puAsk,
                    notional: N,
                    spot: S,
                    sideSign: sg,
                    deltaUnit: 0.0, vegaUnit: 0.0, gammaUnit: 0.0, thetaUnit: 0.0,
                    boldAsk: false
                ); // totals: vm.PremTotalBid/Ask avrundade korrekt  :contentReference[oaicite:3]{index=3}

                _twoWayPremCache[PremKey(L.PremTot, leg)] = new PremiumCellData
                {
                    Bid = vm.PremTotalBid,
                    Ask = vm.PremTotalAsk,
                    Decimals = vm.PremTotalDecimals
                };
            }
        }

        // === Helper: re-rendera ett ben med befintliga Tag-värden och aktiv valuta ===
        private void RenderLegPricing(string legCol)
        {
            // 1) Mid per unit från Tag (fallback 0.0)
            double pricePerUnitMid = 0.0;
            var cellPU = _dgv.Rows[R(L.PremUnit)].Cells[legCol];
            if (cellPU?.Tag is double dmid) pricePerUnitMid = dmid;

            // 2) Tvåvägs per-unit till overlay (fallback mid/mid)
            double puBid = pricePerUnitMid, puAsk = pricePerUnitMid;
            PremiumCellData puPd;
            if (_twoWayPremCache.TryGetValue(PremKey(L.PremUnit, legCol), out puPd) && puPd != null)
            {
                puBid = puPd.Bid;
                puAsk = puPd.Ask;
            }

            // 3) Inputs för totals
            double N = ReadNotional(legCol);
            double S = ReadSpot(legCol);
            int sg = SideSign(legCol);

            // 4) Skriv tillbaka tvåvägs per-unit i overlay-cachen (6 dp)
            _twoWayPremCache[PremKey(L.PremUnit, legCol)] = new PremiumCellData
            {
                Bid = puBid,
                Ask = puAsk,
                Decimals = 6
            };

            // 5) Hämta rundade totals från Tag (lagras av ShowLegResult)
            var cellTot = _dgv.Rows[R(L.PremTot)].Cells[legCol];
            double totQuoteRounded = 0.0, totBaseRounded = 0.0;

            var tag = cellTot?.Tag;
            if (tag is ValueTuple<double, double> vt)
            {
                totQuoteRounded = vt.Item1;
                totBaseRounded = vt.Item2;
            }
            else if (tag is Tuple<double, double> t)
            {
                totQuoteRounded = t.Item1;
                totBaseRounded = t.Item2;
            }
            else
            {
                // Fallback om tuple saknas: räkna enkel total från mid
                double midTotQuote = pricePerUnitMid * N * sg;
                totQuoteRounded = midTotQuote;
                totBaseRounded = (S != 0.0 ? midTotQuote / S : 0.0);
            }

            // 6) Visa total i aktiv valuta (text i cellen) – FmtSpaces med 2 d.p.
            bool showQuote = (_pricingCurrency == PricingCurrency.Quote);
            double displayTotal = showQuote ? totQuoteRounded : totBaseRounded;
            _dgv.Rows[R(L.PremTot)].Cells[legCol].Value = FmtSpaces(displayTotal, 2);

            // 7) TVÅVÄGS OVERLAY FÖR TOTAL: använd PricingFormatter för avrundning istället för rå konvertering
            var pair6 = ReadPair6();
            var quote = (pair6 != null && pair6.Length >= 6)
                ? pair6.Substring(3, 3).ToUpperInvariant()
                : "SEK";
            var disp = (_pricingCurrency == PricingCurrency.Quote)
                ? PricingFormatter.DisplayCcy.Quote
                : PricingFormatter.DisplayCcy.Base;

            var fmt = new PricingFormatter(quote, disp);  // samma som i rebuild  :contentReference[oaicite:7]{index=7}

            var vm = fmt.Build(
                leg: legCol,
                perUnitBid: puBid,
                perUnitAsk: puAsk,
                notional: N,
                spot: S,
                sideSign: sg,
                deltaUnit: 0.0, vegaUnit: 0.0, gammaUnit: 0.0, thetaUnit: 0.0,
                boldAsk: false
            ); // ger PremTotalBid/Ask med formatterns avrundning  :contentReference[oaicite:8]{index=8}

            _twoWayPremCache[PremKey(L.PremTot, legCol)] = new PremiumCellData
            {
                Bid = vm.PremTotalBid,
                Ask = vm.PremTotalAsk,
                Decimals = vm.PremTotalDecimals
            };

            // 8) Invalidera just de två premie-cellerna
            _dgv.InvalidateCell(_dgv.Columns[legCol].Index, R(L.PremUnit));
            _dgv.InvalidateCell(_dgv.Columns[legCol].Index, R(L.PremTot));
        }

        // === VYN: mata in tvåvägs per-unit från presentern ===
        public void ShowTwoWayPremiumFromPerUnit(string legCol, double pricePerUnitBid, double pricePerUnitAsk)
        {
            // Cacha per-unit tvåväg (overlay, 6 d.p.)
            _twoWayPremCache[PremKey(L.PremUnit, legCol)] = new PremiumCellData
            {
                Bid = pricePerUnitBid,
                Ask = pricePerUnitAsk,
                Decimals = 6
            };

            // Bygg även tvåvägs TOTAL i nuvarande visningsvaluta så overlayn är rätt direkt
            double N = ReadNotional(legCol);
            double S = ReadSpot(legCol);
            int sg = SideSign(legCol);

            double totBidQuote = pricePerUnitBid * N * sg;
            double totAskQuote = pricePerUnitAsk * N * sg;

            bool showQuote = (_pricingCurrency == PricingCurrency.Quote);
            double totBidDisp = showQuote ? totBidQuote : (S != 0.0 ? totBidQuote / S : 0.0);
            double totAskDisp = showQuote ? totAskQuote : (S != 0.0 ? totAskQuote / S : 0.0);

            _twoWayPremCache[PremKey(L.PremTot, legCol)] = new PremiumCellData
            {
                Bid = totBidDisp,
                Ask = totAskDisp,
                Decimals = 2
            };

            // Rita om premiumraderna för benet
            _dgv.InvalidateCell(_dgv.Columns[legCol].Index, R(L.PremUnit));
            _dgv.InvalidateCell(_dgv.Columns[legCol].Index, R(L.PremTot));
        }

        // True om (row,col) är Pricing-rubriken OCH e.Location ligger inuti chipet
        private bool IsInsidePricingHeaderButton(DataGridViewCellMouseEventArgs e)
        {
            if (e.RowIndex != _pricingHeaderRow) return false;
            if (e.ColumnIndex != _dgv.Columns["FIELD"].Index) return false;

            // _pricingBtnRect är "lokal" rect vi satte i CellPainting (relativt cellens 0,0)
            if (_pricingBtnRect.IsEmpty) return false;

            // e.Location är också lokal i cellen → direkt jämförelse
            return _pricingBtnRect.Contains(e.Location);
        }

        // Bara en bekväm invalidation för chipet
        private void InvalidatePricingHeaderCell()
        {
            if (_pricingHeaderRow >= 0)
                _dgv.InvalidateCell(_dgv.Columns["FIELD"].Index, _pricingHeaderRow);
        }

        // === HELPERS: använder din rad-lookup R(Spot) och FIELD-kolumnen ===
        private Rectangle GetMktDataFieldCellRect()
        {
            // Rad = "Spot Rate" via din befintliga metod R(...)
            int row = R(L.Spot);
            if (row < 0) return Rectangle.Empty;

            // Kolumn = "FIELD" (ändra namnet om din kolumn heter något annat)
            int col = _dgv.Columns.Contains("FIELD") ? _dgv.Columns["FIELD"].Index : -1;
            if (col < 0) return Rectangle.Empty;

            return _dgv.GetCellDisplayRectangle(col, row, true);
        }

        private void InvalidateSpotModeButton()
        {
            int r = R(L.Spot);
            if (r >= 0)
                _dgv.InvalidateCell(_dgv.Columns["FIELD"].Index, r);
        }

        private bool IsInsideMktDataFieldCell(Point clientPt)
        {
            var rc = GetMktDataFieldCellRect();
            return !rc.IsEmpty && rc.Contains(clientPt);
        }



        /// <summary>
        /// Returnerar hur många decimaler som UI *visar* för Spot i Deal-kolumnen.
        /// - Minst 4.
        /// - Om användaren skrev fler än 4, returneras det högsta antalet d.p. som syns.
        /// Robust mot både "mid"-visning och "bid/ask"-visning.
        /// </summary>
        public int GetSpotUiDecimals()
        {
            int rSpot = FindRow(L.Spot);
            if (rSpot < 0) return 4;

            var val = Convert.ToString(_dgv.Rows[rSpot].Cells["Deal"].Value ?? "");
            if (string.IsNullOrWhiteSpace(val))
            {
                // Deal kan vara tom vid push till legs – ta första leg som finns
                foreach (var lg in _legs)
                {
                    if (!_dgv.Columns.Contains(lg)) continue;
                    var s = Convert.ToString(_dgv.Rows[rSpot].Cells[lg].Value ?? "");
                    var d = CountDecimalsInSpotDisplay(s);
                    if (d > 0) return Math.Max(4, d);
                }
                return 4;
            }

            var decs = CountDecimalsInSpotDisplay(val);
            return Math.Max(4, decs);
        }

        /// <summary>
        /// Räknar decimaler i en spot-displaysträng. Stödjer "x.y" och "x.y/z.w".
        /// Komma tolkas som decimalpunkt.
        /// </summary>
        private static int CountDecimalsInSpotDisplay(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            s = s.Trim().Replace(',', '.');

            // Two-way?
            var sep = s.IndexOf('/');
            if (sep >= 0)
            {
                var left = s.Substring(0, sep).Trim();
                var right = s.Substring(sep + 1).Trim();
                return Math.Max(DecimalsOfNumber(left), DecimalsOfNumber(right));
            }

            // Mid
            return DecimalsOfNumber(s);
        }

        private static int DecimalsOfNumber(string num)
        {
            if (string.IsNullOrWhiteSpace(num)) return 0;
            var p = num.IndexOf('.');
            if (p < 0) return 0;
            int count = 0;
            for (int i = p + 1; i < num.Length; i++)
            {
                var c = num[i];
                if (c >= '0' && c <= '9') count++;
                else break; // sluta vid första icke-siffra
            }
            return count;
        }


        // Om du senare ritar en mindre "chip" i cellen, uppdatera denna
        private bool IsInsideMktDataButton(Point clientPt) => IsInsideMktDataFieldCell(clientPt);


        private bool IsInsideMktHeaderButton(DataGridViewCellMouseEventArgs e)
        {
            if (_mktHeaderRow < 0) return false;
            if (e.RowIndex != _mktHeaderRow) return false;
            if (!_dgv.Columns.Contains("FIELD")) return false;
            if (_mktBtnRect == Rectangle.Empty) return false;

            // Cellens display-rect (absolut)
            var cellRect = _dgv.GetCellDisplayRectangle(_dgv.Columns["FIELD"].Index, e.RowIndex, true);
            if (cellRect.Width <= 0 || cellRect.Height <= 0) return false;

            // Knappens absoluta rect = cellens abs + lokal knapp-rect
            var btnAbs = new Rectangle(cellRect.X + _mktBtnRect.X, cellRect.Y + _mktBtnRect.Y, _mktBtnRect.Width, _mktBtnRect.Height);
            var pt = _dgv.PointToClient(Cursor.Position);
            return btnAbs.Contains(pt);
        }



        #region === Spot ===



        // Invalidera hela Spot-raden (Deal + alla ben)
        private void InvalidateSpotRow()
        {
            int r = R(L.Spot);
            if (r >= 0) _dgv.InvalidateRow(r);
        }

        /// <summary>
        /// Ser till att Spot-celler (Deal + alla ben) har SpotCellData i Tag.
        /// Om Tag saknas byggs den från visad mid (Value) med bid=ask=mid.
        /// </summary>
        private void EnsureSpotSnapshotsForAll()
        {
            int rSpot = R(L.Spot);
            if (rSpot < 0) return;

            void ensure(string col)
            {
                var cell = _dgv.Rows[rSpot].Cells[col];

                // --- Viktigt: skriv aldrig över befintligt tvåvägs-Tag ---
                if (cell.Tag is SpotCellData) return;

                // Skapa minsta möjliga snapshot (mid/mid) från visat värde om Tag saknas
                double mid;
                if (!TryParseCellNumber(cell.Value, out mid) || mid <= 0.0) return;

                cell.Tag = new SpotCellData
                {
                    Bid = mid,
                    Mid = mid,
                    Ask = mid,
                    TimeUtc = DateTime.UtcNow,
                    Source = "Derived"
                };
            }

            ensure("Deal");
            for (int i = 0; i < _legs.Length; i++)
                ensure(_legs[i]);
        }

        /// <summary>
        /// Visar spot från FEED (F5) i Deal + alla ben, med exakt 4 d.p. på display.
        /// Viktigt: använder WriteSpotTwoWayToCell för att:
        ///  - uppdatera feed-baseline (för jämförelse mot manuella ändringar),
        ///  - släcka eventuell lila override-färg (MarkOverride(..., false)) för FEED.
        /// Därmed försvinner lila efter F5 och Deal-kolumnen visar färskt värde.
        /// </summary>
        public void ShowSpotFeedFixed4(double bid, double ask)
        {
            int rSpot = FindRow(L.Spot);
            if (rSpot < 0) return;

            // Deal – låt helpern räkna mid och sköta baseline + override off
            WriteSpotTwoWayToCell("Deal", bid, 0.0, ask, source: "Feed");

            // Ben – samma visning, baseline & override-off per kolumn
            for (int i = 0; i < _legs.Length; i++)
            {
                var lg = _legs[i];
                if (!_dgv.Columns.Contains(lg)) continue;

                WriteSpotTwoWayToCell(lg, bid, 0.0, ask, source: "Feed");
            }
        }


        /// <summary>
        /// Visar spot som kommer från USER (manuell input) i Deal + alla ben, med exakt 4 d.p.
        /// Viktigt: sätter override (lila) PÅ. Baseline lämnas oförändrad så att
        /// lila-indikeringen kvarstår tills feed tar över (F5) eller användaren nollställer.
        /// </summary>
        public void ShowSpotUserFixed4(double bid, double ask)
        {
            int rSpot = FindRow(L.Spot);
            if (rSpot < 0) return;

            // Deal – markera override = true (lila)
            WriteSpotTwoWayToCell("Deal", bid, 0.0, ask, source: "User");
            MarkOverride(L.Spot, "Deal", true);

            // Ben – samma markering, per kolumn
            for (int i = 0; i < _legs.Length; i++)
            {
                var lg = _legs[i];
                if (!_dgv.Columns.Contains(lg)) continue;

                WriteSpotTwoWayToCell(lg, bid, 0.0, ask, source: "User");
                MarkOverride(L.Spot, lg, true);
            }
        }


        #endregion




        public override Size GetPreferredSize(Size proposedSize)
        {
            try
            {
                int w = _dgv.RowHeadersWidth + 2;
                foreach (DataGridViewColumn c in _dgv.Columns)
                    if (c.Visible) w += c.Width;

                int h = _dgv.ColumnHeadersHeight + 2;
                foreach (DataGridViewRow r in _dgv.Rows)
                    if (r.Visible) h += r.Height;

                // Ta höjd för scrollbars om de behövs
                if (_dgv.DisplayedRowCount(false) < _dgv.RowCount)
                    w += SystemInformation.VerticalScrollBarWidth;
                if (_dgv.DisplayedColumnCount(false) < _dgv.ColumnCount)
                    h += SystemInformation.HorizontalScrollBarHeight;

                // Rimliga gränser (justera vid behov)
                var min = new Size(1200, 600);
                var max = new Size(1700, 1050);

                w = Math.Min(Math.Max(w, min.Width), max.Width);
                h = Math.Min(Math.Max(h, min.Height), max.Height);

                return new Size(w, h);
            }
            catch
            {
                return new Size(1000, 600);
            }
        }

        /// <summary>
        /// Visar Forward och Swap Points för ett visst ben.
        /// Fwd och Pts anges i prisunits (ej pips); du kan justera format om du vill visa pips.
        /// </summary>
        public void ShowForwardById(Guid legId, double? fwd, double? pts)
        {
            var col = TryGetLabel(legId);
            if (string.IsNullOrWhiteSpace(col)) return;

            int rFwd = R(L.FwdRate);   // se till att dessa rader finns i layouten
            int rPts = R(L.FwdPts);
            if (rFwd < 0 || rPts < 0) return;

            var ci = System.Globalization.CultureInfo.InvariantCulture;

            var cFwd = _dgv.Rows[rFwd].Cells[col];
            var cPts = _dgv.Rows[rPts].Cells[col];

            cFwd.Value = fwd.HasValue ? fwd.Value.ToString("F6", ci) : "";
            cPts.Value = pts.HasValue ? pts.Value.ToString("F6", ci) : "";

            _dgv.InvalidateRow(rFwd);
            _dgv.InvalidateRow(rPts);

            System.Diagnostics.Debug.WriteLine(
                $"[View.ShowForward] leg={legId} col={col} " +
                $"fwd={(fwd.HasValue ? fwd.Value.ToString("F6", ci) : "-")} " +
                $"pts={(pts.HasValue ? pts.Value.ToString("F6", ci) : "-")}");
        }


        /// <summary>
        /// Visar RD/RF (decimaler) för ett specifikt ben.
        /// - Formaterar som procent med 3 d.p.
        /// - Lagrar feed-baseline i Tag (double) för korrekt override-hantering.
        /// - Om raderna inte finns än, eller kolumnen saknas, görs inget.
        /// </summary>
        public void ShowRatesById(Guid legId, double? rdDec, double? rfDec, bool staleRd, bool staleRf)
        {
            var col = TryGetLabel(legId);
            if (string.IsNullOrWhiteSpace(col)) return;

            int rRd = R(L.Rd);
            int rRf = R(L.Rf);
            if (rRd < 0 || rRf < 0) return; // rd/rf-rader saknas i layouten

            // RD
            var cRd = _dgv.Rows[rRd].Cells[col];
            if (rdDec.HasValue)
            {
                cRd.Tag = rdDec.Value;                 // feed-baseline som decimal
                cRd.Value = FormatPercent(rdDec.Value, 3);
            }
            else
            {
                cRd.Tag = null;
                cRd.Value = "";
            }
            // feed ska inte markeras som override
            MarkOverride(L.Rd, col, false);

            // RF
            var cRf = _dgv.Rows[rRf].Cells[col];
            if (rfDec.HasValue)
            {
                cRf.Tag = rfDec.Value;                 // feed-baseline som decimal
                cRf.Value = FormatPercent(rfDec.Value, 3);
            }
            else
            {
                cRf.Tag = null;
                cRf.Value = "";
            }
            MarkOverride(L.Rf, col, false);

            // (valfritt) Indikera stale i tooltip – icke-blockerande visuell hint
            if (staleRd) cRd.ToolTipText = "Stale RD (cache TTL passerad)"; else cRd.ToolTipText = null;
            if (staleRf) cRf.ToolTipText = "Stale RF (cache TTL passerad)"; else cRf.ToolTipText = null;

            _dgv.InvalidateRow(rRd);
            _dgv.InvalidateRow(rRf);

            Debug.WriteLine($"[View.ShowRates] leg={legId} col={col} rd={rdDec?.ToString("P3") ?? "-"} rf={rfDec?.ToString("P3") ?? "-"} staleRd={staleRd} staleRf={staleRf}");
        }







    }



    #region Cell Data Classes

    internal sealed class ExpiryCellData
    {
        public string Raw; public string Iso; public string Wd;
    }

    internal sealed class VolCellData
    {
        public double Bid; // decimal, t.ex. 0.050
        public double Ask; // decimal, t.ex. 0.060
    }

    internal sealed class PremiumCellData
    {
        public double Bid;     // per-unit eller total (i display-valuta)
        public double Ask;
        public int Decimals;   // hur många decimals vid FmtSpaces
    }

    internal sealed class SpotCellData
    {
        public double Bid;   // orundat
        public double Mid;   // orundat
        public double Ask;   // orundat
        public DateTime TimeUtc;   // när snapshotet skrevs
        public string Source;      // "Manual" | "Feed" | valfritt
    }

    #endregion

}
