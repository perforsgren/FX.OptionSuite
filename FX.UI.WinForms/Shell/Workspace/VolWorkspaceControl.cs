using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Svg;

namespace FX.UI.WinForms
{
    /// <summary>
    /// Arbetsyta för Vol Manager (samma mönster som PricerWorkspaceControl):
    /// - Menyrad överst (File: New, Open, Close, Rename – minimalistiskt i MVP).
    /// - Vänster plats för framtida verktygslist (placeholder).
    /// - TabControl med en flik per vol-session (VolSessionControl).
    /// - Stöd för att byta namn på flikar via meny (in-place overlay textbox kan läggas senare).
    /// </summary>
    public sealed class VolWorkspaceControl : UserControl
    {
        #region === Fields & constants ===

        private readonly Func<int, VolSessionControl> _sessionFactory;

        private readonly Panel _menuTopSpacer;   // Flyttar ner menyraden (hosten tar denna som MainMenuStrip)
        private readonly MenuStrip _menu;

        private readonly Panel _sideBarHost;
        private readonly ToolStrip _sideBar;

        private readonly TabControl _tabs;

        private readonly TextBox _renameBox;

        private int _sessionCounter;

        private static readonly Color TabBgActive = ColorTranslator.FromHtml("#293955");
        private static readonly Color TabBgInactive = ColorTranslator.FromHtml("#2F4366");
        private static readonly Color TabText = Color.White;
        private static readonly Color TabOutline = Color.FromArgb(30, Color.Black);
        private const int TabCornerRadius = 8;

        #endregion

        #region === Constructor & initialization ===

        /// <summary>
        /// Skapar Vol-Workspace med meny, vänster verktygslist och sessionsflikar i botten.
        /// Flikarna ritas som blå "pills" (identiskt med Pricern) med stäng-kryss och
        /// dubbelklick för rename.
        /// </summary>
        /// <param name="sessionFactory">Fabrik som skapar nya vol-sessions (en per flik).</param>
        public VolWorkspaceControl(Func<int, VolSessionControl> sessionFactory)
        {
            if (sessionFactory == null) throw new ArgumentNullException(nameof(sessionFactory));
            _sessionFactory = sessionFactory;

            Dock = DockStyle.Fill;

            // --- Meny överst (hostar MainMenuStrip) ---
            _menuTopSpacer = new Panel { Dock = DockStyle.Top, Height = 0, BackColor = SystemColors.Control };
            Controls.Add(_menuTopSpacer);

            // --- Meny ---
            _menu = BuildMenu();
            _menu.Dock = DockStyle.Top;
            Controls.Add(_menu);

            // --- Vänster toolbar i wrapper-panel (för dynamisk topp-padding) ---
            _sideBar = BuildSideBar();

            _sideBarHost = new Panel
            {
                Dock = DockStyle.Left,
                Width = 48,
                Padding = new Padding(0, 0, 0, 0) // Top sätts i UpdateSidebarOffset()
            };
            _sideBar.Dock = DockStyle.Fill;
            _sideBarHost.Controls.Add(_sideBar);
            Controls.Add(_sideBarHost);

            // --- Sessionsflikar (ritas av vår nästlade PillTabControl) ---
            _tabs = new PillTabControl
            {
                Dock = DockStyle.Fill,
                Alignment = TabAlignment.Bottom,
                SizeMode = TabSizeMode.Fixed,
                ItemSize = new Size(100, 26),
                Padding = new Point(16, 6) // plats för text + stäng-kryss
            };

            // Stäng via klick på kryss
            _tabs.MouseDown += Tabs_MouseDown;

            // Dubbeklick = byt namn
            _tabs.MouseDoubleClick += Tabs_MouseDoubleClick;

            // Flikbyte: stäng ev. pågående rename-läge + uppdatera UI
            _tabs.SelectedIndexChanged += (s, e) =>
            {
                CommitRename(save: false);
                UpdateUiEnabled();
                UpdateSidebarOffset();
            };

            Controls.Add(_tabs);
            Controls.SetChildIndex(_tabs, 0);

            // Lite luft under flikarna
            Padding = new Padding(0, 0, 0, 6);

            // In-place rename overlay
            _renameBox = new TextBox
            {
                BorderStyle = BorderStyle.FixedSingle,
                Visible = false,
                TabStop = true,
                Font = Font,
                BackColor = Color.White,
                ForeColor = Color.Black
            };
            _renameBox.KeyDown += RenameBox_KeyDown;
            _renameBox.LostFocus += (s, e) => CommitRename(true);
            Controls.Add(_renameBox);
            _renameBox.BringToFront();

            // Håll vänsterverktygen i linje med aktiv vyn
            Resize += (s, e) => UpdateSidebarOffset();
            Layout += (s, e) => UpdateSidebarOffset();

            UpdateUiEnabled();
            UpdateSidebarOffset(); // initial placering
        }

        /// <summary>Menyn: File/New/Open/Close/Rename – minimalistisk i MVP.</summary>
        private MenuStrip BuildMenu()
        {
            var ms = new MenuStrip();

            var mFile = new ToolStripMenuItem("File");
            var miNew = new ToolStripMenuItem("New Session", null, (s, e) => NewSession())
            { ShortcutKeys = Keys.Control | Keys.T, ShowShortcutKeys = true };
            var miOpen = new ToolStripMenuItem("Open", null, (s, e) => {/*TODO: öppna från definition */})
            { ShortcutKeys = Keys.Control | Keys.O, ShowShortcutKeys = true };
            var miClose = new ToolStripMenuItem("Close", null, (s, e) => CloseSession())
            { ShortcutKeys = Keys.Control | Keys.W, ShowShortcutKeys = true };
            var miRename = new ToolStripMenuItem("Rename", null, (s, e) => BeginRename());
            mFile.DropDownItems.AddRange(new ToolStripItem[] { miNew, miOpen, miClose, miRename });

            var mEdit = new ToolStripMenuItem("Edit");
            var mTools = new ToolStripMenuItem("Tools");


            ms.Items.Add(mFile);
            ms.Items.Add(mEdit);
            ms.Items.Add(mTools);
            return ms;
        }

        /// <summary>
        /// Bygger vänster verktygslist (snabbkommandon motsvarande menyalternativ).
        /// </summary>
        private ToolStrip BuildSideBar()
        {

            // Tunables
            const int barWidth = 28;  // total bredd på vänsterlisten (tidigare 44)
            const int iconSize = 24;  // ikon 20x20 (tidigare 24)
            const int buttonWidth = 28;  // klick-yta
            const int buttonHeight = 24;

            var ts = new ToolStrip
            {
                GripStyle = ToolStripGripStyle.Hidden,
                LayoutStyle = ToolStripLayoutStyle.VerticalStackWithOverflow,
                AutoSize = false,
                Width = barWidth,
                ImageScalingSize = new Size(iconSize, iconSize),
                Padding = new Padding(0),
                Margin = Padding.Empty
            };

            // helper: kompakt separator
            ToolStripSeparator Sep(int top = 4, int bottom = 4)
            {
                var s = new ToolStripSeparator
                {
                    AutoSize = false,
                    //Margin = new Padding(6, top, 6, bottom),
                    Size = new Size(barWidth - 4, 6) // kort linje
                };
                return s;
            }

            // färg: samma blå som headern/temat
            var headerBlue = ColorTranslator.FromHtml("#293955");

            // --- New Session (ikon) ---
            var newIcon = RenderSvgIconBitmapFromEmbeddedFile("NewSession.svg", headerBlue, iconSize);
            var bNew = new ToolStripButton
            {
                DisplayStyle = ToolStripItemDisplayStyle.Image,
                Image = newIcon,
                AutoToolTip = true,
                ToolTipText = "New Session (Ctrl+T)",
                AutoSize = false,
                Width = buttonWidth,
                Height = buttonHeight + 2,
                //Margin = new Padding(2, 4, 2, 2),
                Padding = Padding.Empty
            };
            bNew.Click += (s, e) => NewSession();


            var closeIcon = RenderSvgIconBitmapFromEmbeddedFile("CloseSession.svg", headerBlue, iconSize);
            var bClose = new ToolStripButton
            {
                DisplayStyle = ToolStripItemDisplayStyle.Image,
                Image = closeIcon,
                AutoToolTip = true,
                ToolTipText = "Close Session (Ctrl+W)",
                AutoSize = false,
                Width = buttonWidth,
                Height = buttonHeight + 2,
                //Margin = new Padding(2, 4, 2, 2),
                Padding = Padding.Empty
            };
            bClose.Click += (s, e) => CloseSession();


            // Sessions
            // --- resten som tidigare, men smalare ---
            ts.Items.Add(bNew);
            ts.Items.Add(bClose);
            ts.Items.Add(Sep());


            return ts;
        }

        private static Bitmap RenderSvgIconBitmapFromEmbeddedFile(string fileName, Color color, int size)
        {
            var asm = typeof(PricerWorkspaceControl).Assembly;
            var resName = FindEmbeddedResourceNameByFile(fileName);

            using (var stream = asm.GetManifestResourceStream(resName))
            {
                if (stream == null)
                    throw new InvalidOperationException("Hittar inte resurs: " + resName);

                var doc = SvgDocument.Open<SvgDocument>(stream);

                // färga om fill/stroke rekursivt
                var paint = new SvgColourServer(color);
                Action<SvgElement> recolor = null;
                recolor = el =>
                {
                    for (int i = 0; i < el.Children.Count; i++)
                        recolor(el.Children[i]);
                    var vis = el as SvgVisualElement;
                    if (vis != null)
                    {
                        if (vis.Fill != null && vis.Fill != SvgPaintServer.None) vis.Fill = paint;
                        if (vis.Stroke != null && vis.Stroke != SvgPaintServer.None) vis.Stroke = paint;
                    }
                };
                recolor(doc);

                var bmp = new Bitmap(size, size);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                    doc.Width = new SvgUnit(SvgUnitType.Pixel, size);
                    doc.Height = new SvgUnit(SvgUnitType.Pixel, size);
                    doc.Draw(g);
                }
                return bmp;
            }
        }

        private static string FindEmbeddedResourceNameByFile(string fileName)
        {
            var asm = typeof(PricerWorkspaceControl).Assembly;
            var names = asm.GetManifestResourceNames();
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i].EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase))
                    return names[i];
            }
            // Debug-hjälp: lista resurser vid behov
            // System.Diagnostics.Debug.WriteLine(string.Join("\n", names));
            throw new InvalidOperationException("Hittar inte embedded resource som slutar med: " + fileName);
        }

        #endregion

        #region === Public API (activation & sizing) ===

        /// <summary>Aktiverad – reserverad för framtida fokus/auto-refresh.</summary>
        public void OnActivated()
        {
            UpdateSidebarOffset();
        }

        /// <summary>Inaktiverad – reserverad för framtida paus av timer etc.</summary>
        public void OnDeactivated() { /* no-op i MVP */ }

        #endregion

        #region === Sessions & tabs ===

        /// <summary>Aktiv sessionskontroll (eller null om inga flikar).</summary>
        private VolSessionControl ActiveSession
        {
            get
            {
                if (_tabs.TabPages.Count == 0) return null;
                return _tabs.SelectedTab?.Tag as VolSessionControl;
            }
        }

        /// <summary>Skapar en ny vol-session i ny flik och gör den aktiv.</summary>
        public void NewSessionOLD()
        {
            var idx = ++_sessionCounter;
            var session = _sessionFactory(idx);

            var page = new TabPage
            {
                Text = session.TabTitle,
                Tag = session,
                Padding = new Padding(0)
            };

            session.Dock = DockStyle.Fill;
            page.Controls.Add(session);

            _tabs.TabPages.Add(page);
            _tabs.SelectedTab = page;

            UpdateUiEnabled();
            UpdateSidebarOffset(); // se till att vänsterlisten följer nya vyn
        }

        /// <summary>
        /// Skapar en ny vol-session i en ny flik och gör den aktiv.
        /// Sessionens vy placeras i en host-panel med svart 1px ram (FixedSingle),
        /// så att utseendet matchar Pricern (ramen ligger runt hela innehållet innanför flikbandet).
        /// </summary>
        public void NewSession()
        {
            var idx = ++_sessionCounter;
            var session = _sessionFactory(idx);

            var page = new TabPage
            {
                Text = session.TabTitle,
                Tag = session,
                Padding = new Padding(0),
                UseVisualStyleBackColor = true
            };

            // Host-panelen ritar “svart ram” runt allt innehåll
            var contentHost = CreateContentHostPanel();
            contentHost.Dock = DockStyle.Fill;

            session.Dock = DockStyle.Fill;
            contentHost.Controls.Add(session);
            page.Controls.Add(contentHost);

            _tabs.TabPages.Add(page);
            _tabs.SelectedTab = page;

            UpdateUiEnabled();
            UpdateSidebarOffset(); // se till att vänsterlisten följer nya vyn
        }

        /// <summary>
        /// Skapar en host-panel som omsluter sessionens innehåll och ritar
        /// en tunn svart ram (FixedSingle) runt inre ytan, identiskt med Pricern.
        /// </summary>
        /// <returns>Panel som ska dockas i fliksidan och hysa sessionens vy.</returns>
        private Panel CreateContentHostPanel()
        {
            return new Panel
            {
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.White,
                Margin = Padding.Empty,
                Padding = new Padding(0)
            };
        }


        /// <summary>Stänger aktiv session (om någon).</summary>
        public void CloseSession()
        {
            CloseSessionAt(_tabs.SelectedIndex);
        }

        private void CloseSessionAt(int index)
        {
            var pages = _tabs.TabPages;
            int count = pages.Count;
            if (index < 0 || index >= count) return;

            int nextIndex = (index > 0) ? index - 1 : 0;

            var page = pages[index];
            var closedSession = page.Tag as VolSessionControl;

            try { closedSession?.Dispose(); } catch { /* best effort */ }
            pages.RemoveAt(index);
            try { page.Dispose(); } catch { /* best effort */ }

            if (pages.Count > 0 && nextIndex < pages.Count)
                _tabs.SelectedIndex = nextIndex;

            UpdateUiEnabled();
        }

        /// <summary>
        /// Stänger fliken om användaren klickar på stäng-krysset i fliken.
        /// </summary>
        /// <param name="sender">TabControl.</param>
        /// <param name="e">Mus-argument.</param>
        private void Tabs_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != System.Windows.Forms.MouseButtons.Left) return;

            for (int i = 0; i < _tabs.TabPages.Count; i++)
            {
                var r = _tabs.GetTabRect(i);
                var pill = System.Drawing.Rectangle.Inflate(r, -2, -3);
                pill.X -= 1; pill.Width += 2;  // överlappa 1 px per sida
                pill.Height += 2;

                var close = GetCloseRectTight(pill);
                if (close.Contains(e.Location))
                {
                    CloseSessionAt(i);
                    return;
                }
            }
        }

        /// <summary>
        /// Startar rename-läge (TextBox-overlay) om användaren dubbelklickar på en flik.
        /// </summary>
        /// <param name="sender">TabControl.</param>
        /// <param name="e">Mus-argument.</param>
        private void Tabs_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            for (int i = 0; i < _tabs.TabPages.Count; i++)
            {
                var rect = _tabs.GetTabRect(i);
                if (rect.Contains(e.Location))
                {
                    // Lägg rename-boxen ovanpå flikens rektangel
                    var bounds = new System.Drawing.Rectangle(
                        _tabs.Left + rect.Left + 6,
                        _tabs.Top + rect.Top + 4,
                        System.Math.Max(80, rect.Width - 12),
                        rect.Height - 6);

                    _renameBox.Text = _tabs.TabPages[i].Text ?? string.Empty;
                    _renameBox.Bounds = bounds;
                    _renameBox.Visible = true;
                    _renameBox.BringToFront();
                    _renameBox.Focus();
                    _renameBox.SelectAll();
                    return;
                }
            }
        }


        #endregion

        #region === Rename overlay (in-place) ===

        /// <summary>Enter bekräftar, Esc avbryter.</summary>
        private void RenameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { CommitRename(save: true); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Escape) { CommitRename(save: false); e.SuppressKeyPress = true; }
        }

        /// <summary>Avslutar rename-läge och sätter nytt titelvärde om save=true.</summary>
        private void CommitRename(bool save)
        {
            if (!_renameBox.Visible) return;

            try
            {
                if (save && _tabs.TabPages.Count > 0 && _tabs.SelectedTab != null)
                {
                    var newTitle = (_renameBox.Text ?? string.Empty).Trim();
                    if (!string.IsNullOrEmpty(newTitle))
                    {
                        _tabs.SelectedTab.Text = newTitle;
                        // Om vi vill, synka sessionens TabTitle också:
                        var ses = _tabs.SelectedTab.Tag as VolSessionControl;
                        if (ses != null) ses.TabTitle = newTitle;
                    }
                }
            }
            finally
            {
                _renameBox.Visible = false;
            }
        }

        /// <summary>Startar in-place rename av aktiv flik.</summary>
        private void BeginRename()
        {
            if (_tabs.TabPages.Count == 0) return;
            var page = _tabs.SelectedTab;
            if (page == null) return;

            var rect = _tabs.GetTabRect(_tabs.SelectedIndex);
            var bounds = new Rectangle(_tabs.Left + rect.Left + 6, _tabs.Top + rect.Top + 4, Math.Max(80, rect.Width - 12), rect.Height - 6);
            _renameBox.Text = page.Text ?? string.Empty;
            _renameBox.Bounds = bounds;
            _renameBox.Visible = true;
            _renameBox.BringToFront();
            _renameBox.Focus();
            _renameBox.SelectAll();
        }

        #endregion

        #region === Layout & util ===

        /// <summary>
        /// Sänker vänster verktygslist så att dess topp möter vy-innehållets topp.
        /// Detta replikerar Pricerns känsla utan att kräva några special-API:er från sessionen.
        /// </summary>
        private void UpdateSidebarOffset()
        {
            try
            {
                // Bas: lägg listen under menyraden
                int menuBottom = _menu?.Height ?? 0;
                int topFromMenu = menuBottom + 6;

                // Tabbinnehållets synliga rektangel (där sessionen ligger dockad)
                int tabsContentTop = 0;
                if (_tabs != null && _tabs.IsHandleCreated)
                {
                    // DisplayRectangle.Top ger själva innehållsytans topp (inte flikbandet)
                    tabsContentTop = _tabs.DisplayRectangle.Top;
                }

                // Välj det som ligger längst ned (så vi aldrig hamnar ovanför menyn)
                int targetTop = Math.Max(topFromMenu, tabsContentTop + 2);

                // Applicera som top-padding på vänster host
                if (_sideBarHost != null)
                    _sideBarHost.Padding = new Padding(0, targetTop, 0, 0);
            }
            catch
            {
                // best effort – svälj grafiska edge-case-fel
            }
        }


        private void UpdateUiEnabled()
        {
            // Kan expandera när fler kommandon finns; i MVP räcker detta.
        }

        /// <summary>Frigör sessions och UI-resurser.</summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    foreach (TabPage p in _tabs.TabPages)
                    {
                        try { (p.Tag as VolSessionControl)?.Dispose(); } catch { /* best effort */ }
                    }
                }
                catch { /* best effort */ }
            }
            base.Dispose(disposing);
        }

        #endregion

        #region === GDI helpers (pill & close-glyph) ===

        /// <summary>
        /// Bygger en path med rundade nederhörn (rak topp) för pill-flikar.
        /// </summary>
        /// <param name="r">Rektangel att runda i botten.</param>
        /// <param name="radius">Radie i pixlar.</param>
        /// <returns>GraphicsPath för ritning.</returns>
        private static GraphicsPath RoundedBottom(Rectangle r, int radius)
        {
            int d = radius * 2;
            var path = new System.Drawing.Drawing2D.GraphicsPath();

            // Topp – rak
            path.AddLine(r.Left, r.Top, r.Right, r.Top);

            // Höger sida ned
            path.AddLine(r.Right, r.Top, r.Right, r.Bottom - radius);

            // Rundning nederkant höger
            path.AddArc(new System.Drawing.Rectangle(r.Right - d, r.Bottom - d, d, d), 0, 90);

            // Rundning nederkant vänster
            path.AddArc(new System.Drawing.Rectangle(r.Left, r.Bottom - d, d, d), 90, 90);

            // Vänster sida upp
            path.AddLine(r.Left, r.Bottom - radius, r.Left, r.Top);

            path.CloseFigure();
            return path;
        }

        /// <summary>
        /// Returnerar rektangel för stäng-krysset i pillen (tight placering).
        /// </summary>
        /// <param name="pill">Pill-rektangel.</param>
        private static Rectangle GetCloseRectTight(Rectangle pill)
        {
            const int s = 8;        // glyph-storlek
            const int padRight = 8; // närmare höger kant
            int x = pill.Right - padRight - s;
            int y = pill.Top + (pill.Height - s) / 2;
            return new System.Drawing.Rectangle(x, y, s, s);
        }

        /// <summary>
        /// Ritar ett litet rundat “X” – används för stäng-krysset.
        /// </summary>
        /// <param name="g">Graphics att rita på.</param>
        /// <param name="r">Glyph-rektangel.</param>
        /// <param name="color">Färg på kryss.</param>
        private static void DrawCloseGlyph(Graphics g, Rectangle r, Color color)
        {
            using (var pen = new System.Drawing.Pen(color, 1.6f))
            {
                pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;

                var old = g.SmoothingMode;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.DrawLine(pen, r.Left, r.Top, r.Right, r.Bottom);
                g.DrawLine(pen, r.Left, r.Bottom, r.Right, r.Top);
                g.SmoothingMode = old;
            }
        }

        #endregion


        #region === Nested: PillTabControl (custom-drawn tabs) ===

        /// <summary>
        /// Egen TabControl som ritar flikar som blå “pills”, identiskt med pricer.
        /// Aktiv flik får gloss och skugga, inaktiv är plan. Ligger alltid i botten.
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
                    BackColor = Parent.BackColor; // följ omgivningen
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                var g = e.Graphics;
                var bg = Parent?.BackColor ?? BackColor;

                // Backdrop
                g.Clear(bg);

                // Överkant för flikområdet (bandet)
                int bandTop = ClientRectangle.Bottom;
                for (int i = 0; i < TabPages.Count; i++)
                    bandTop = Math.Min(bandTop, GetTabRect(i).Top);

                // Fyll bandet (1 px överlapp uppåt för att dölja ev. linjer)
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
                    bandTop = Math.Min(bandTop, GetTabRect(i).Top);

                for (int i = 0; i < TabPages.Count; i++)
                {
                    var rect = GetTabRect(i);

                    // Sudda ev. systemmålning inom item-rect
                    using (var wipe = new SolidBrush(BackColor))
                        g.FillRectangle(wipe, rect);

                    bool isSel = (i == SelectedIndex);

                    // Överlappa grannen 1 px per sida (minskar glapp)
                    var pill = Rectangle.Inflate(rect, -2, -3);
                    pill.X -= 1; pill.Width += 2;

                    // Aktiv flik lite högre än inaktiv – ger visuell “upphöjd” känsla
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
                            // Svag skugga under aktiva
                            using (var shadowPath = RoundedBottom(
                                   new System.Drawing.Rectangle(pill.X, pill.Y + 2, pill.Width, pill.Height), TabCornerRadius))
                            using (var shPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(45, 0, 0, 0), 2f))
                                g.DrawPath(shPen, shadowPath);

                            // Gloss-gradient: ljusare upptill, mörkare nedtill
                            var top = System.Drawing.ColorTranslator.FromHtml("#6FA3DF");
                            var bottom = TabBgActive;
                            using (var lg = new System.Drawing.Drawing2D.LinearGradientBrush(pill, top, bottom, 90f))
                                g.FillPath(lg, path);

                            // Vit highlight i överkant
                            using (var region = new System.Drawing.Region(path))
                            {
                                g.SetClip(region, System.Drawing.Drawing2D.CombineMode.Replace);
                                var hiRect = new System.Drawing.Rectangle(pill.X + 1, pill.Y + 1, pill.Width - 2,
                                                                         System.Math.Max(6, pill.Height / 3));
                                using (var hi = new System.Drawing.Drawing2D.LinearGradientBrush(
                                            hiRect, System.Drawing.Color.FromArgb(120, System.Drawing.Color.White), System.Drawing.Color.FromArgb(0, System.Drawing.Color.White), 90f))
                                    g.FillRectangle(hi, hiRect);
                                g.ResetClip();
                            }

                            using (var pen = new System.Drawing.Pen(TabOutline))
                                g.DrawPath(pen, path);
                        }
                        else
                        {
                            // Inaktiv: platt, lite mörkare
                            using (var bg = new SolidBrush(TabBgInactive))
                                g.FillPath(bg, path);
                            using (var pen = new Pen(TabOutline))
                                g.DrawPath(pen, path);
                        }
                    }

                    // Text – bold endast för vald
                    var drawFont = isSel ? new Font(Font, FontStyle.Bold) : Font;
                    bool created = isSel;
                    try
                    {
                        const int paddingX = 12, paddingY = 4, closePad = 24;
                        var textRect = new Rectangle(
                            pill.X + paddingX, pill.Y + paddingY,
                            pill.Width - paddingX - closePad, pill.Height - (paddingY * 2));

                        TextRenderer.DrawText(
                            g,
                            TabPages[i].Text ?? string.Empty,
                            drawFont,
                            textRect,
                            TabText,
                            TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    }
                    finally
                    {
                        if (created) drawFont.Dispose();
                    }

                    // Stäng-kryss
                    var closeRect = GetCloseRectTight(pill);
                    DrawCloseGlyph(g, closeRect, TabText);


                }



            }
        }

        #endregion

    }
}
