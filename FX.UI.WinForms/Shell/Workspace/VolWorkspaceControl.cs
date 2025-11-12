using System;
using System.Drawing;
using System.Windows.Forms;

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
        private readonly Func<int, VolSessionControl> _sessionFactory;

        private readonly MenuStrip _menu;
        private readonly Panel _sideBarHost;
        private readonly ToolStrip _sideBar;
        private readonly TabControl _tabs;

        private readonly TextBox _renameBox;
        private int _sessionCounter;

        /// <summary>
        /// Skapar Vol-Workspace med meny, vänster verktygslist och sessionsflikar i botten.
        /// Flikarna har stäng-kryss och dubbelklick för rename, samma beteende som Pricern.
        /// </summary>
        public VolWorkspaceControl(Func<int, VolSessionControl> sessionFactory)
        {
            if (sessionFactory == null) throw new ArgumentNullException(nameof(sessionFactory));
            _sessionFactory = sessionFactory;

            Dock = DockStyle.Fill;

            // --- Meny ---
            _menu = BuildMenu();
            _menu.Dock = DockStyle.Top;
            Controls.Add(_menu);

            // --- Vänster verktygslist (placeholder för framtiden) ---
            _sideBarHost = new Panel { Dock = DockStyle.Left, Width = 40, BackColor = SystemColors.Control };
            _sideBar = new ToolStrip { Dock = DockStyle.Fill, GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System };
            _sideBarHost.Controls.Add(_sideBar);
            Controls.Add(_sideBarHost);

            // --- Sessionsflikar (i botten) ---
            _tabs = new TabControl
            {
                Dock = DockStyle.Fill,
                Alignment = TabAlignment.Bottom,
                SizeMode = TabSizeMode.Fixed,
                ItemSize = new Size(110, 26),
                Padding = new Point(18, 6) // plats för text + stäng-kryss
            };

            // Stäng via klick på kryss
            _tabs.MouseDown += Tabs_MouseDown;

            // Dubbeklick = byt namn
            _tabs.MouseDoubleClick += Tabs_MouseDoubleClick;

            // Flikbyte: stäng ev. rename-läge + uppdatera UI
            _tabs.SelectedIndexChanged += (s, e) =>
            {
                if (_renameBox.Visible) CommitRename(save: true);
                UpdateUiEnabled();
            };

            Controls.Add(_tabs);

            // --- In-place rename overlay ---
            _renameBox = new TextBox { Visible = false, BorderStyle = BorderStyle.FixedSingle };
            _renameBox.KeyDown += RenameBox_KeyDown;
            _renameBox.LostFocus += (s, e) => CommitRename(save: true);
            Controls.Add(_renameBox);

            UpdateUiEnabled();
        }

        /// <summary>
        /// Returnerar en tight rektangel för stäng-krysset inom en tab-rect.
        /// </summary>
        private static Rectangle GetCloseRectTight(Rectangle tabRect)
        {
            // 14x14 kryss, centrerat vertikalt, 10 px från höger
            int w = 14, h = 14;
            int x = tabRect.Right - w - 10;
            int y = tabRect.Top + (tabRect.Height - h) / 2;
            return new Rectangle(x, y, w, h);
        }


        /// <summary>
        /// Stänger fliken om användaren klickar på stäng-krysset.
        /// </summary>
        private void Tabs_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;

            for (int i = 0; i < _tabs.TabPages.Count; i++)
            {
                var r = _tabs.GetTabRect(i);
                var close = GetCloseRectTight(r);
                if (close.Contains(e.Location))
                {
                    CloseSessionAt(i);
                    return;
                }
            }
        }


        /// <summary>
        /// Startar rename om användaren dubbelklickar på en flik.
        /// </summary>
        private void Tabs_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            for (int i = 0; i < _tabs.TabPages.Count; i++)
            {
                var r = _tabs.GetTabRect(i);
                if (r.Contains(e.Location))
                {
                    // Lägg rename-boxen ovanpå flikens rektangel
                    var rect = _tabs.GetTabRect(i);
                    var bounds = new Rectangle(_tabs.Left + rect.Left + 6, _tabs.Top + rect.Top + 4,
                                               Math.Max(80, rect.Width - 12), rect.Height - 6);
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

            ms.Items.Add(mFile);
            return ms;
        }

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
        public void NewSession()
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

        /// <summary>Aktiverad – reserverad för framtida fokus/auto-refresh.</summary>
        public void OnActivated() { /* no-op i MVP */ }

        /// <summary>Inaktiverad – reserverad för framtida paus av timer etc.</summary>
        public void OnDeactivated() { /* no-op i MVP */ }

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
    }
}
