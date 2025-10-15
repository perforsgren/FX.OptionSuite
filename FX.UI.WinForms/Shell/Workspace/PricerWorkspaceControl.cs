using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Svg;

namespace FX.UI.WinForms
{
    /// <summary>
    /// Arbetsytan för pricer-appar: innehåller meny, vänster verktygslist och ett
    /// flikfönster med sessioner (ritade som "pills" med rundade nederhörn).
    /// Stöd för att byta namn på flikar via dubbelklick eller meny (in-place TextBox-overlay).
    /// </summary>
    public sealed class PricerWorkspaceControl : UserControl
    {
        #region === Fields & constants ===

        private readonly Func<int, PricerSessionControl> _sessionFactory;

        // UI-ramverk
        private readonly Panel _menuTopSpacer;   // Flyttar ner menyraden (hosten tar denna som MainMenuStrip)
        private readonly MenuStrip _menu;

        private readonly Panel _sideBarHost;     // Wrapper-panel för vänster ToolStrip (för att kunna flytta ner den)
        private readonly ToolStrip _sideBar;

        private readonly TabControl _tabs;       // Vår nästlade PillTabControl ritar flikarna

        // Sessioner
        private int _sessionCounter;

        // Host-form och tidigare meny (för att växla MainMenuStrip vid (de)aktivering)
        private Form _hostForm;
        private MenuStrip _prevMainMenu;

        // Färg-/stilkonstant för flik-rendering (används av nested control)
        private static readonly Color TabBgActive = ColorTranslator.FromHtml("#293955");
        private static readonly Color TabBgInactive = ColorTranslator.FromHtml("#2F4366");
        private static readonly Color TabText = Color.White;
        private static readonly Color TabOutline = Color.FromArgb(30, Color.Black);
        private const int TabCornerRadius = 8;

        // In-place rename-overlay för fliknamn
        private TextBox _tabRenameBox;
        private int _renamingIndex = -1;

        #endregion

        #region === Ctor & initialization ===

        /// <summary>
        /// Skapar arbetsytan. <paramref name="sessionFactory"/> används för att skapa nya sessioner/flikar.
        /// </summary>
        public PricerWorkspaceControl(Func<int, PricerSessionControl> sessionFactory)
        {
            if (sessionFactory == null) throw new ArgumentNullException(nameof(sessionFactory));
            _sessionFactory = sessionFactory;

            Dock = DockStyle.Fill;

            // --- Meny överst (hostar MainMenuStrip) ---
            _menuTopSpacer = new Panel { Dock = DockStyle.Top, Height = 0, BackColor = SystemColors.Control };
            Controls.Add(_menuTopSpacer);

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

            // Flikbyte: stäng ev. pågående rename-läge + uppdatera UI/offests
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

            // In-place TextBox-overlay för rename (läggs i denna kontroll, inte i TabControl)
            _tabRenameBox = new TextBox
            {
                BorderStyle = BorderStyle.FixedSingle,
                Visible = false,
                TabStop = true,
                Font = Font,
                BackColor = Color.White,
                ForeColor = Color.Black
            };
            _tabRenameBox.KeyDown += TabRenameBox_KeyDown;          // Enter/Esc
            _tabRenameBox.Leave += (s, e) => CommitRename(true);   // Spara vid förlust av fokus
            Controls.Add(_tabRenameBox);
            _tabRenameBox.BringToFront();

            // Håll vänsterverktygen i linje med aktiv vyn
            Resize += (s, e) => UpdateSidebarOffset();
            Layout += (s, e) => UpdateSidebarOffset();

            NewSession();
        }

        /// <summary>
        /// Skapar menystrukturen (File/Edit/Tools) och binder kommandon.
        /// </summary>
        private MenuStrip BuildMenu()
        {
            var ms = new MenuStrip();

            // File
            var mFile = new ToolStripMenuItem("File");
            var miNew = new ToolStripMenuItem("New", null, (s, e) => NewSession())
            { ShortcutKeys = Keys.Control | Keys.T, ShowShortcutKeys = true };
            var miOpen = new ToolStripMenuItem("Open", null, (s, e) => OpenSession())
            { ShortcutKeys = Keys.Control | Keys.O, ShowShortcutKeys = true };
            var miClose = new ToolStripMenuItem("Close", null, (s, e) => CloseSession())
            { ShortcutKeys = Keys.Control | Keys.W, ShowShortcutKeys = true };
            var miRename = new ToolStripMenuItem("Rename", null, (s, e) => RenameSession());
            var miSave = new ToolStripMenuItem("Save", null, (s, e) => {/*TODO*/})
            { ShortcutKeys = Keys.Control | Keys.S, ShowShortcutKeys = true };
            var miSaveAs = new ToolStripMenuItem("Save As", null, (s, e) => {/*TODO*/});
            var miClone = new ToolStripMenuItem("Clone", null, (s, e) => {/*TODO*/});

            mFile.DropDownItems.AddRange(new ToolStripItem[] { miNew, miOpen, miClose, miRename, miSave, miSaveAs, miClone });

            // Edit
            var mEdit = new ToolStripMenuItem("Edit");
            var miAdd = new ToolStripMenuItem("Add Leg", null, (s, e) => ActiveSession?.AddLeg());
            var miCloneLeg = new ToolStripMenuItem("Clone Leg", null, (s, e) => ActiveSession?.CloneActiveLeg());
            var miRem = new ToolStripMenuItem("Remove Leg", null, (s, e) => ActiveSession?.RemoveActiveLeg());
            mEdit.DropDownItems.AddRange(new ToolStripItem[] { miAdd, miCloneLeg, miRem });

            // Tools
            var mTool = new ToolStripMenuItem("Tools");
            var miReprice = new ToolStripMenuItem("Reprice", null, (s, e) => ActiveSession?.RepriceAll())
            { ShortcutKeys = Keys.F9, ShowShortcutKeys = true };
            mTool.DropDownItems.Add(miReprice);

            ms.Items.AddRange(new ToolStripItem[] { mFile, mEdit, mTool });
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


            var refreshIcon = RenderSvgIconBitmapFromEmbeddedFile("RefreshMD.svg", headerBlue, iconSize);
            var bRefresh = new ToolStripButton
            {
                DisplayStyle = ToolStripItemDisplayStyle.Image,
                Image = refreshIcon,
                AutoToolTip = true,
                ToolTipText = "Refresh marketdata (F9)",
                AutoSize = false,
                Width = buttonWidth,
                Height = buttonHeight + 2,
                //Margin = new Padding(2, 4, 2, 2),
                Padding = Padding.Empty
            };
            bRefresh.Click += (s, e) => ActiveSession?.RepriceAll();


            var addLegIcon = RenderSvgIconBitmapFromEmbeddedFile("AddLeg.svg", headerBlue, iconSize);
            var bAddLeg = new ToolStripButton
            {
                DisplayStyle = ToolStripItemDisplayStyle.Image,
                Image = addLegIcon,
                AutoToolTip = true,
                ToolTipText = "Add Leg",
                AutoSize = false,
                Width = buttonWidth,
                Height = buttonHeight + 2,
                //Margin = new Padding(2, 4, 2, 2),
                Padding = Padding.Empty
            };
            bAddLeg.Click += (s, e) => ActiveSession?.AddLeg();


            var cloneLegIcon = RenderSvgIconBitmapFromEmbeddedFile("CloneLeg.svg", headerBlue, iconSize);
            var bCloneLeg = new ToolStripButton
            {
                DisplayStyle = ToolStripItemDisplayStyle.Image,
                Image = cloneLegIcon,
                AutoToolTip = true,
                ToolTipText = "Clone Leg",
                AutoSize = false,
                Width = buttonWidth,
                Height = buttonHeight + 2,
                //Margin = new Padding(2, 4, 2, 2),
                Padding = Padding.Empty
            };
            bCloneLeg.Click += (s, e) => ActiveSession?.CloneActiveLeg();


            var removeLegIcon = RenderSvgIconBitmapFromEmbeddedFile("RemoveLeg.svg", headerBlue, iconSize);
            var bRemoveLeg = new ToolStripButton
            {
                DisplayStyle = ToolStripItemDisplayStyle.Image,
                Image = removeLegIcon,
                AutoToolTip = true,
                ToolTipText = "Remove Leg",
                AutoSize = false,
                Width = buttonWidth,
                Height = buttonHeight + 2,
                //Margin = new Padding(2, 4, 2, 2),
                Padding = Padding.Empty
            };
            bRemoveLeg.Click += (s, e) => ActiveSession?.RemoveActiveLeg();




            // Sessions
            // --- resten som tidigare, men smalare ---
            ts.Items.Add(bNew);
            ts.Items.Add(bClose);
            ts.Items.Add(Sep());

            ts.Items.Add(bRefresh);
            ts.Items.Add(Sep());

            ts.Items.Add(bAddLeg);
            ts.Items.Add(bRemoveLeg);
            ts.Items.Add(bCloneLeg);
            

            return ts;
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

        #endregion

        #region === Public API (activation & sizing) ===

        /// <summary>
        /// Kallas av värden när arbetsytan blir aktiv – växlar in vår meny och notifierar aktiv session.
        /// </summary>
        public void OnActivated()
        {
            ActiveSession?.OnBecameActive();

            var form = FindForm();
            if (form != null)
            {
                if (!ReferenceEquals(_hostForm, form))
                {
                    if (_hostForm != null && ReferenceEquals(_hostForm.MainMenuStrip, _menu))
                        _hostForm.MainMenuStrip = _prevMainMenu;

                    _hostForm = form;
                    _prevMainMenu = _hostForm.MainMenuStrip;
                }
                _hostForm.MainMenuStrip = _menu;
            }

            UpdateSidebarOffset(); // säkerställ rätt position direkt
        }

        /// <summary>
        /// Kallas av värden när arbetsytan tappar fokus – växlar tillbaka tidigare meny och notifierar session.
        /// </summary>
        public void OnDeactivated()
        {
            ActiveSession?.OnBecameInactive();

            if (_hostForm != null && ReferenceEquals(_hostForm.MainMenuStrip, _menu))
                _hostForm.MainMenuStrip = _prevMainMenu;
        }

        /// <summary>
        /// Föreslår en “lagom” storlek för arbetsytan (används bl.a. i separata fönster).
        /// Beräknar utifrån den aktiva vy-/gridens preferens + krom runt omkring.
        /// </summary>
        public override Size GetPreferredSize(Size proposedSize)
        {
            var min = new Size(1000, 600);
            var ceil = new Size(1700, 1050);

            try
            {
                var sess = ActiveSession;
                if (sess == null) return min;

                var content = sess.ContentRoot;
                if (content == null) return min;

                var want = content.GetPreferredSize(new Size(1920, 1080));

                int w = want.Width + _sideBarHost.Width + 8;
                int h = want.Height + _menu.Height + 8;

                w = Math.Min(Math.Max(w, min.Width), ceil.Width);
                h = Math.Min(Math.Max(h, min.Height), ceil.Height);

                return new Size(w, h);
            }
            catch
            {
                // Best effort – återgå till minsta rimliga
                return min;
            }
        }

        #endregion

        #region === Sessions & tabs ===

        /// <summary>Hämtar aktiv <see cref="PricerSessionControl"/> från vald flik (om någon).</summary>
        private PricerSessionControl ActiveSession
        {
            get
            {
                if (_tabs.TabPages.Count == 0) return null;
                return _tabs.SelectedTab?.Tag as PricerSessionControl;
            }
        }

        /// <summary>Skapar en ny session i en ny flik och gör den aktiv.</summary>
        public void NewSession()
        {
            var idx = ++_sessionCounter;
            var session = _sessionFactory(idx);

            var page = new TabPage
            {
                Text = session.TabTitle, // session uppdaterar själv vid Pair-byte
                Tag = session,
                Padding = new Padding(0)
            };

            session.Dock = DockStyle.Fill;
            page.Controls.Add(session);

            _tabs.TabPages.Add(page);
            _tabs.SelectedTab = page;

            session.OnBecameActive();
            UpdateUiEnabled();
            UpdateSidebarOffset();
        }

        /// <summary>Öppna en session (placeholder – fylls när filformat/protokoll finns).</summary>
        public void OpenSession()
        {
            // TODO: Ladda session från fil eller liknande när format är bestämt.
        }

        /// <summary>Startar in-place-rename för aktiv flik (via menyn).</summary>
        public void RenameSession() => BeginRenameAtIndex(_tabs.SelectedIndex);

        /// <summary>Stänger vald flik.</summary>
        private void CloseSession() => CloseSessionAt(_tabs.SelectedIndex);

        private void SaveSessionAs() => CloseSessionAt(_tabs.SelectedIndex);

        private void SaveSession() => CloseSessionAt(_tabs.SelectedIndex);

        private void CloneSession() => CloseSessionAt(_tabs.SelectedIndex);

        /// <summary>
        /// Stänger fliken på angivet index. Gör fliken före den stängda aktiv (om möjligt).
        /// </summary>
        private void CloseSessionAt(int index)
        {
            var pages = _tabs.TabPages;
            int count = pages.Count;
            if (index < 0 || index >= count) return;

            int nextIndex = (index > 0) ? index - 1 : 0;

            var page = pages[index];
            var closedSession = page.Tag as PricerSessionControl;

            try { closedSession?.Dispose(); } catch { /* best effort */ }
            pages.RemoveAt(index);
            try { page.Dispose(); } catch { /* best effort */ }

            if (pages.Count > 0)
            {
                if (nextIndex >= pages.Count) nextIndex = pages.Count - 1;
                _tabs.SelectedIndex = nextIndex;

                var newActive = pages[nextIndex].Tag as PricerSessionControl;
                try { newActive?.OnBecameActive(); } catch { /* best effort */ }
            }

            UpdateUiEnabled();
            UpdateSidebarOffset();
        }

        /// <summary>
        /// Klick på flik-kryss (X) stänger motsvarande flik.
        /// </summary>
        private void Tabs_MouseDown(object sender, MouseEventArgs e)
        {
            for (int i = 0; i < _tabs.TabPages.Count; i++)
            {
                var r = GetTabRect(i);
                var pill = Rectangle.Inflate(r, -2, -3);
                pill.X -= 1; pill.Width += 2;  // överlappa 1 px per sida
                pill.Height += 2;

                var close = GetCloseRectTight(pill);
                if (close.Contains(e.Location) && e.Button == MouseButtons.Left)
                {
                    CloseSessionAt(i);
                    return;
                }
            }
        }

        /// <summary>
        /// Dubbeklick på flik startar rename-läge (TextBox- overlay).
        /// </summary>
        private void Tabs_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            for (int i = 0; i < _tabs.TabPages.Count; i++)
            {
                var rect = GetTabRect(i);
                if (rect.Contains(e.Location))
                {
                    BeginRenameAtIndex(i);
                    return;
                }
            }
        }

        #endregion

        #region === Rename overlay (in-place) ===

        /// <summary>
        /// Startar edit-läge för fliktitel på givet index (lägger TextBox ovanpå textrect).
        /// </summary>
        private void BeginRenameAtIndex(int index)
        {
            if (index < 0 || index >= _tabs.TabPages.Count) return;

            _renamingIndex = index;
            var page = _tabs.TabPages[index];

            // 1) Hämta ungefärlig text-yta i _tabs-koordinater
            var textRectTabs = GetTabTextRectApprox(index);
            if (textRectTabs == Rectangle.Empty)
            {
                var rect = GetTabRect(index);
                textRectTabs = Rectangle.Inflate(rect, -12, -6);
            }

            // 2) Konvertera till denna kontrolls koordinatsystem (overlayn ligger på "this")
            var textRectScreen = _tabs.RectangleToScreen(textRectTabs);
            var textRectLocal = RectangleToClient(textRectScreen);

            // 3) Bygg bounds och visa overlay
            var bounds = new Rectangle(
                textRectLocal.X,
                textRectLocal.Y,
                Math.Max(40, _tabRenameBox.PreferredSize.Width > 0 ? Math.Min(textRectLocal.Width, 400) : textRectLocal.Width),
                Math.Max(_tabRenameBox.PreferredHeight, textRectLocal.Height)
            );

            _tabRenameBox.Text = page.Text ?? string.Empty;
            _tabRenameBox.Bounds = bounds;
            _tabRenameBox.Visible = true;
            _tabRenameBox.BringToFront();
            _tabRenameBox.Focus();
            _tabRenameBox.SelectAll();
        }

        /// <summary>
        /// Enter bekräftar, Esc avbryter. (Handled i KeyDown för overlayn.)
        /// </summary>
        private void TabRenameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                CommitRename(save: true);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                CommitRename(save: false);
                e.Handled = true;
            }
        }

        /// <summary>
        /// Sparar/avbryter pågående namnbyte och stänger overlayn.
        /// </summary>
        private void CommitRename(bool save)
        {
            if (_renamingIndex < 0 || _renamingIndex >= _tabs.TabPages.Count)
            {
                _tabRenameBox.Visible = false;
                _renamingIndex = -1;
                return;
            }

            if (save)
            {
                var page = _tabs.TabPages[_renamingIndex];
                var newName = _tabRenameBox.Text?.Trim();
                if (!string.IsNullOrEmpty(newName))
                {
                    page.Text = newName;
                    // Om du i framtiden vill "låsa" titeln i sessionen kan du anropa ett API här.
                }
            }

            _tabRenameBox.Visible = false;
            _renamingIndex = -1;
            _tabs.Invalidate(); // Rita om flikarna
        }

        /// <summary>
        /// Beräknar en rimlig rektangel för flikens text (matchar pill-geometrin).
        /// </summary>
        private Rectangle GetTabTextRectApprox(int index)
        {
            var rect = GetTabRect(index);
            if (rect == Rectangle.Empty) return rect;

            var pill = Rectangle.Inflate(rect, -2, -3);
            pill.X -= 1; pill.Width += 2; // samma sidorand som ritningen
            int pillBottom = rect.Bottom + 2;
            pill.Height = pillBottom - pill.Top;

            const int paddingX = 12, paddingY = 4, closePad = 24;

            return new Rectangle(
                pill.X + paddingX,
                pill.Y + paddingY,
                pill.Width - paddingX - closePad,
                pill.Height - (paddingY * 2)
            );
        }

        #endregion

        #region === Layout & util ===

        /// <summary>
        /// Returnerar systemets tabrect, med defensiv fallback om något går snett.
        /// </summary>
        private Rectangle GetTabRect(int index)
        {
            try { return _tabs.GetTabRect(index); }
            catch { return Rectangle.Empty; }
        }

        /// <summary>
        /// Slår av/på verktygslist beroende på om en session är aktiv.
        /// </summary>
        private void UpdateUiEnabled()
        {
            _sideBar.Enabled = ActiveSession != null || _tabs.TabPages.Count > 0;
        }

        /// <summary>
        /// Sänker vänsterverktygen så att deras topp möter gridens topp i aktiv session.
        /// </summary>
        private void UpdateSidebarOffset()
        {
            try
            {
                var session = ActiveSession;
                if (session == null) return;

                var content = session.ContentRoot;
                if (content == null || !content.IsHandleCreated) return;

                var screenTop = content.PointToScreen(Point.Empty).Y;     // skärm-Y
                var targetY = PointToClient(new Point(0, screenTop)).Y; // lokalt Y

                int menuBottom = _menuTopSpacer.Height + _menu.Height;
                int top = Math.Max(menuBottom + 2, targetY + 2);

                _sideBarHost.Padding = new Padding(0, top, 0, 0);
            }
            catch
            {
                // Best effort – inga undantag till användaren
            }
        }

        /// <summary>
        /// Städar upp resurser och återställer meny i hosten om vi äger den för stunden.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_hostForm != null && ReferenceEquals(_hostForm.MainMenuStrip, _menu))
                    _hostForm.MainMenuStrip = _prevMainMenu;

                foreach (TabPage p in _tabs.TabPages)
                    (p.Tag as PricerSessionControl)?.Dispose();
            }
            base.Dispose(disposing);
        }

        #endregion

        #region === GDI helpers (pill & close-glyph) ===

        /// <summary>Bygger en path med rundade nederhörn (rak topp).</summary>
        private static GraphicsPath RoundedBottom(Rectangle r, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();

            // Topp – rak
            path.AddLine(r.Left, r.Top, r.Right, r.Top);

            // Höger sida ned
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

        /// <summary>Position för det lilla stäng-krysset till höger i pillen.</summary>
        private static Rectangle GetCloseRectTight(Rectangle pill)
        {
            const int s = 8;  // glyph-storlek
            const int padRight = 8;  // närmare höger kant
            int x = pill.Right - padRight - s;
            int y = pill.Top + (pill.Height - s) / 2;
            return new Rectangle(x, y, s, s);
        }

        /// <summary>Ritar ett litet rundat “X”.</summary>
        private static void DrawCloseGlyph(Graphics g, Rectangle r, Color color)
        {
            using (var pen = new Pen(color, 1.6f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;

                var old = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawLine(pen, r.Left, r.Top, r.Right, r.Bottom);
                g.DrawLine(pen, r.Left, r.Bottom, r.Right, r.Top);
                g.SmoothingMode = old;
            }
        }

        #endregion

        #region === Nested: PillTabControl (custom-drawn tabs) ===

        /// <summary>
        /// Egen TabControl som tar över all målning och ritar flikar som blå “pills”.
        /// Aktiv flik får gloss, highlight och diskret skugga; inaktiv är plan.
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

                // Fyll bandet (1 px överlapp uppåt för att dölja eventuella linjer)
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
                                   new Rectangle(pill.X, pill.Y + 2, pill.Width, pill.Height), TabCornerRadius))
                            using (var shPen = new Pen(Color.FromArgb(45, 0, 0, 0), 2f))
                                g.DrawPath(shPen, shadowPath);

                            // Gloss-gradient: ljusare upptill, mörkare nedtill
                            var top = ColorTranslator.FromHtml("#6FA3DF");
                            var bottom = TabBgActive;
                            using (var lg = new LinearGradientBrush(pill, top, bottom, 90f))
                                g.FillPath(lg, path);

                            // Vit highlight i överkant
                            using (var region = new Region(path))
                            {
                                g.SetClip(region, CombineMode.Replace);
                                var hiRect = new Rectangle(pill.X + 1, pill.Y + 1, pill.Width - 2,
                                                           Math.Max(6, pill.Height / 3));
                                using (var hi = new LinearGradientBrush(
                                            hiRect, Color.FromArgb(120, Color.White), Color.FromArgb(0, Color.White), 90f))
                                    g.FillRectangle(hi, hiRect);
                                g.ResetClip();
                            }

                            using (var pen = new Pen(TabOutline))
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

                    // Text – bold endast för vald (viktigt: DISPOSA endast skapad font)
                    Font drawFont = isSel ? new Font(Font, FontStyle.Bold) : Font;
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
