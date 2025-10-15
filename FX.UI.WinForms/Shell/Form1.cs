using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using WeifenLuo.WinFormsUI.Docking;
using System.Runtime.InteropServices;
using Svg;
using System.Drawing.Drawing2D;
using System.IO;

namespace FX.UI.WinForms
{
    /// <summary>
    /// Shell som hostar appar via DockPanel Suite.
    /// - Meny överst, toolbar under.
    /// - Window Mode: Attached/Detached (migrerar alla öppna fönster).
    /// - En-instans-regel för både “riktiga” och “placeholder”-appar.
    /// - Detached: fönster får vettig startstorlek utifrån innehåll + MinimumSize.
    /// - Form1 krymper i Detached (bara meny+toolbar), återställs i Attached.
    /// - Layout (storlek/position + dock-layout) sparas och återladdas.
    /// </summary>
    public sealed partial class Form1 : Form
    {
        #region === Win32 / DWM för titelradsfärger ===

        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        #endregion

        #region === Fält & konstanter ===

        // App-färger (matchar float window headers)
        private static readonly Color TitleBarCaption = ColorTranslator.FromHtml("#293955"); // mörkblå
        private static readonly Color TitleBarText = Color.White;

        private readonly IServiceProvider _sp;
        private readonly DockPanel _dockPanel;
        private readonly MenuStrip _menu;
        private readonly ToolStrip _toolbar;

        private bool _layoutCorrected;
        private bool _attachedMode = true;               // startläge
        private ToolStripMenuItem _miWindowModeToggle;
        private Rectangle? _attachedBoundsBeforeDetach;  // för att återställa storlek

        // En-instans-regel för riktiga appar
        private readonly Dictionary<string, (DockContent Doc, IAppInstance Instance)> _apps =
            new Dictionary<string, (DockContent, IAppInstance)>(StringComparer.OrdinalIgnoreCase);

        // En-instans-regel för placeholders
        private readonly Dictionary<string, DockContent> _placeholders =
            new Dictionary<string, DockContent>(StringComparer.OrdinalIgnoreCase);

        // Ikon-cache (vit 16x16 från inbäddade SVG:er)
        private readonly Dictionary<string, Icon> _iconCache =
            new Dictionary<string, Icon>(StringComparer.OrdinalIgnoreCase);

        #endregion

        #region === Ctor & init ===

        public Form1(IServiceProvider serviceProvider)
        {
            _sp = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

            Text = "FX Option Suite";
            StartPosition = FormStartPosition.WindowsDefaultLocation;
            AutoScaleMode = AutoScaleMode.Dpi;
            MinimumSize = new Size(640, 200);

            // DockPanel + tema
            _dockPanel = new DockPanel
            {
                Dock = DockStyle.Fill,
                DocumentStyle = DocumentStyle.DockingWindow
            };
            var theme = new VS2015BlueTheme();
            _dockPanel.Theme = theme;

            // FloatWindow-fabrik (för headerfärger/text)
            var vsBlue = Color.FromArgb(41, 57, 85);
            var titleCol = Color.White;
            var appIcon = this.Icon;
            var floatFactory = new CustomFloatWindowFactory(vsBlue, titleCol, appIcon);
            theme.Extender.FloatWindowFactory = floatFactory;

            // Huvudfönstrets titelradsfärger
            this.HandleCreated += (s, e) => ApplyMainTitleBarColorsSafe();
            this.Shown += (s, e) => ApplyMainTitleBarColorsSafe();

            // Toolbar (app-knappar) – under menyn
            _toolbar = BuildToolbar();
            _toolbar.Dock = DockStyle.Top;

            // Meny – överst
            _menu = BuildMenu();
            _menu.Dock = DockStyle.Top;

            // Lägg kontroller i ordning: menyn överst, sedan toolbar, sedan dockpanel
            Controls.Add(_dockPanel);
            Controls.Add(_toolbar);
            Controls.Add(_menu);
            MainMenuStrip = _menu;

            UpdateWindowModeMenuText();

            // Ladda/spara layout & state
            this.Load += (s, e) => LoadLayoutAndState();
            this.FormClosing += (s, e) => SaveLayoutAndState();
        }

        #endregion

        #region === Titelrad (DWM) ===

        private void ApplyMainTitleBarColorsSafe()
        {
            if (!IsHandleCreated) return;
            try
            {
                int c = (TitleBarCaption.R) | (TitleBarCaption.G << 8) | (TitleBarCaption.B << 16);
                int t = (TitleBarText.R) | (TitleBarText.G << 8) | (TitleBarText.B << 16);
                DwmSetWindowAttribute(this.Handle, DWMWA_CAPTION_COLOR, ref c, sizeof(int));
                DwmSetWindowAttribute(this.Handle, DWMWA_TEXT_COLOR, ref t, sizeof(int));
            }
            catch { /* no-op på äldre builds */ }
        }

        #endregion

        #region === Layout & state: ladda/spara ===

        /// <summary>Laddar huvudfönstrets bounds & state, fönsterläge samt dock-layout (om fil finns).</summary>
        private void LoadLayoutAndState()
        {
            // Återställ main-fönster
            try
            {
                var b = Properties.Settings.Default.MainBounds;
                var st = (FormWindowState)Properties.Settings.Default.MainWindowState;

                if (b.Width > 0 && b.Height > 0)
                {
                    var clamped = ClampToScreens(b);
                    if (clamped != b) _layoutCorrected = true;

                    StartPosition = FormStartPosition.Manual;
                    Bounds = clamped;
                    WindowState = st;
                }
            }
            catch { /* best effort */ }

            // Återställ Window Mode
            _attachedMode = Properties.Settings.Default.AttachedMode;
            UpdateWindowModeMenuText();

            // Hämta layout-fil
            var xmlPath = Properties.Settings.Default.DockLayoutPath;
            if (string.IsNullOrEmpty(xmlPath))
            {
                var appdir = Application.UserAppDataPath;
                Directory.CreateDirectory(appdir);
                xmlPath = Path.Combine(appdir, "docklayout.xml");
                Properties.Settings.Default.DockLayoutPath = xmlPath;
                Properties.Settings.Default.Save();
            }

            // Ladda layout (om finns)
            if (File.Exists(xmlPath))
            {
                try
                {
                    _dockPanel.LoadFromXml(xmlPath, new DeserializeDockContent(DeserializeContent));
                    ClampAllWindowsToScreens();
                }
                catch
                {
                    // trasig layout → ignorera
                }
            }
        }

        /// <summary>Sparar huvudfönstrets bounds & state samt dock-layout till fil.</summary>
        private void SaveLayoutAndState()
        {
            try
            {
                if (_layoutCorrected) return; // spara inte korrigerad “nöd-layout”

                // Spara fönsterläge/storlek
                var state = WindowState;
                Properties.Settings.Default.MainWindowState = (int)state;
                Properties.Settings.Default.AttachedMode = _attachedMode;

                Rectangle toSave = (state == FormWindowState.Normal) ? Bounds : RestoreBounds;
                Properties.Settings.Default.MainBounds = toSave;

                // Layout-fil
                var xmlPath = Properties.Settings.Default.DockLayoutPath;
                if (string.IsNullOrEmpty(xmlPath))
                {
                    var appdir = Application.UserAppDataPath;
                    Directory.CreateDirectory(appdir);
                    xmlPath = Path.Combine(appdir, "docklayout.xml");
                    Properties.Settings.Default.DockLayoutPath = xmlPath;
                }

                _dockPanel.SaveAsXml(xmlPath);
                Properties.Settings.Default.Save();
            }
            catch
            {
                // best effort
            }
        }

        // Bakåtkompatibla wrappers (undvik dubblettlogik)
        private string GetLayoutPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "docklayout.xml");
        }

        private void SaveLayout()
        {
            // delegera till nya
            SaveLayoutAndState();
        }

        private void LoadLayout()
        {
            // delegera till nya
            LoadLayoutAndState();
        }

        #endregion

        #region === Deserialize: skapa IDockContent från persist-string ===

        // Skapa IDockContent från persist-string (Pricer eller kända placeholders)
        private IDockContent DeserializeContent(string persistString)
        {
            if (string.IsNullOrEmpty(persistString))
                return null;

            // === Pricer ============================================================
            if (string.Equals(persistString, "Pricer", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(persistString, typeof(PricerDockContent).FullName, StringComparison.Ordinal))
            {
                var pricer = _sp.GetRequiredService<PricerAppInstance>();
                var view = pricer.View;
                if (view == null)
                    return null;

                var doc = new PricerDockContent
                {
                    Text = pricer.Title ?? "Pricer"
                };

                try { doc.Icon = GetAppIcon("Pricer"); } catch { /* best effort */ }

                view.Dock = DockStyle.Fill;
                doc.Controls.Add(view);

                doc.Activated += (s, e) => Safe(pricer.OnActivated);
                doc.Deactivate += (s, e) => Safe(pricer.OnDeactivated);
                doc.FormClosed += (s, e) => Safe(pricer.Dispose);

                _apps["Pricer"] = (doc, pricer);

                return doc;
            }

            // === Placeholders: Blotter / Gamma Hedger / Volatility Management =======
            if (string.Equals(persistString, "Blotter", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(persistString, "Gamma Hedger", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(persistString, "Volatility Management", StringComparison.OrdinalIgnoreCase))
            {
                var title = persistString;

                var doc = new PlaceholderDockContent
                {
                    Text = title,
                    TitleKey = title
                };

                try { doc.Icon = GetAppIcon(title); } catch { /* best effort */ }

                var host = new UserControl { Dock = DockStyle.Fill };
                host.Controls.Add(new Label
                {
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Text = title + Environment.NewLine + "(Not implemented yet)"
                });
                doc.Controls.Add(host);

                doc.FormClosed += (s, e) => _placeholders.Remove(title);
                _placeholders[title] = doc;

                return doc;
            }

            // Okänd typ – låt DockPanel ignorera
            return null;
        }

        #endregion

        #region === Meny ===

        private MenuStrip BuildMenu()
        {
            var ms = new MenuStrip();
            var mFile = new ToolStripMenuItem("File");

            _miWindowModeToggle = new ToolStripMenuItem("", null, (s, e) => ToggleWindowMode());
            // Text sätts via UpdateWindowModeMenuText()

            var miOpenPricer = new ToolStripMenuItem("Open Pricer", null, (s, e) => OpenOrFocusPricer());

            mFile.DropDownItems.Add(_miWindowModeToggle);
            mFile.DropDownItems.Add(new ToolStripSeparator());
            mFile.DropDownItems.Add(miOpenPricer);

            ms.Items.Add(mFile);
            return ms;
        }

        private void UpdateWindowModeMenuText()
        {
            _miWindowModeToggle.Text = _attachedMode
                ? "Window Mode: Attached  (click to switch to Detached)"
                : "Window Mode: Detached  (click to switch to Attached)";
        }

        #endregion

        #region === Toolbar (app-knappar) ===

        private ToolStrip BuildToolbar()
        {
            var ts = new ToolStrip
            {
                GripStyle = ToolStripGripStyle.Hidden,
                ImageScalingSize = new Size(24, 24)
            };

            var headerBlue = ColorTranslator.FromHtml("#293955");

            // Pricer
            var pricerIcon = RenderSvgIconFromEmbedded(
                "FX.UI.WinForms.Features.Resources.Pricer.svg",
                headerBlue,
                24);
            var bPricer = new ToolStripButton
            {
                DisplayStyle = ToolStripItemDisplayStyle.Image,
                Image = pricerIcon,
                ToolTipText = "FX Option Pricer",
                AutoSize = false,
                Width = 28,
                Height = 28,
                Margin = new Padding(8, 0, 4, 0)
            };
            bPricer.Click += (s, e) => OpenOrFocusPricer();

            // Blotter
            var blotterIcon = RenderSvgIconFromEmbedded(
                "FX.UI.WinForms.Features.Resources.Blotter.svg",
                headerBlue,
                24);
            var bBlotter = new ToolStripButton
            {
                DisplayStyle = ToolStripItemDisplayStyle.Image,
                Image = blotterIcon,
                ToolTipText = "Trade Blotter",
                AutoSize = false,
                Width = 28,
                Height = 28,
                Margin = new Padding(0, 0, 4, 0)
            };
            bBlotter.Click += (s, e) => OpenPlaceholderOnce("Blotter");

            // Gamma Hedger
            var gammaIcon = RenderSvgIconFromEmbedded(
                "FX.UI.WinForms.Features.Resources.Gamma.svg",
                headerBlue,
                24);
            var bGamma = new ToolStripButton
            {
                DisplayStyle = ToolStripItemDisplayStyle.Image,
                Image = gammaIcon,
                ToolTipText = "Gamma Hedger",
                AutoSize = false,
                Width = 28,
                Height = 28,
                Margin = new Padding(0, 0, 4, 0)
            };
            bGamma.Click += (s, e) => OpenPlaceholderOnce("Gamma Hedger");

            // Volatility Management
            var volIcon = RenderSvgIconFromEmbedded(
                "FX.UI.WinForms.Features.Resources.Volatility.svg",
                headerBlue,
                24);
            var bVol = new ToolStripButton
            {
                DisplayStyle = ToolStripItemDisplayStyle.Image,
                Image = volIcon,
                ToolTipText = "Volatility Management",
                AutoSize = false,
                Width = 28,
                Height = 28,
                Margin = new Padding(0, 0, 4, 0)
            };
            bVol.Click += (s, e) => OpenPlaceholderOnce("Volatility Management");

            ts.Items.Add(bPricer);
            ts.Items.Add(bBlotter);
            ts.Items.Add(bGamma);
            ts.Items.Add(bVol);
            return ts;
        }

        #endregion

        #region === Ikon-hjälpare (SVG → Bitmap/Icon) ===

        private Icon GetAppIcon(string appKey)
        {
            Icon cached;
            if (_iconCache.TryGetValue(appKey, out cached))
                return cached;

            // Mappa app → filnamn (enbart filnamn; hjälparen letar rätt embedded resource)
            string fileName;
            if (string.Equals(appKey, "Pricer", StringComparison.OrdinalIgnoreCase))
                fileName = "Pricer.svg";
            else if (string.Equals(appKey, "Blotter", StringComparison.OrdinalIgnoreCase))
                fileName = "Blotter.svg";
            else if (string.Equals(appKey, "Gamma Hedger", StringComparison.OrdinalIgnoreCase))
                fileName = "Gamma.svg";
            else if (string.Equals(appKey, "Volatility Management", StringComparison.OrdinalIgnoreCase))
                fileName = "Volatility.svg";
            else
                fileName = appKey + ".svg";

            var icon = RenderSvgIconToIconFromEmbedded(fileName, Color.White, 16);
            _iconCache[appKey] = icon;
            return icon;
        }

        private static Bitmap RenderSvgIconFromEmbedded(string resourceName, Color color, int size)
        {
            var asm = typeof(Form1).Assembly;
            using (var stream = asm.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                    throw new InvalidOperationException("Hittar inte resurs: " + resourceName);

                var doc = SvgDocument.Open<SvgDocument>(stream);

                var paint = new SvgColourServer(color);
                void Recolor(SvgElement el)
                {
                    foreach (var c in el.Children) Recolor(c);
                    var vis = el as SvgVisualElement;
                    if (vis == null) return;

                    if (vis.Fill != null && vis.Fill != SvgPaintServer.None) vis.Fill = paint;
                    if (vis.Stroke != null && vis.Stroke != SvgPaintServer.None) vis.Stroke = paint;
                }
                Recolor(doc);

                var bmp = new Bitmap(size, size);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;

                    doc.Width = new SvgUnit(SvgUnitType.Pixel, size);
                    doc.Height = new SvgUnit(SvgUnitType.Pixel, size);
                    doc.Draw(g);
                }
                return bmp;
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool DestroyIcon(IntPtr handle);

        private static Icon RenderSvgIconToIconFromEmbedded(string resourceNameOrFile, Color color, int size)
        {
            var asm = typeof(Form1).Assembly;
            Stream stream = asm.GetManifestResourceStream(resourceNameOrFile);

            if (stream == null)
            {
                string[] names = asm.GetManifestResourceNames();

                // Extrahera filnamn (t.ex. "Pricer.svg") och leta via EndsWith
                string fileName = resourceNameOrFile;
                int slash = resourceNameOrFile.LastIndexOfAny(new[] { '/', '\\' });
                if (slash >= 0) fileName = resourceNameOrFile.Substring(slash + 1);

                string found = null;
                for (int i = 0; i < names.Length; i++)
                {
                    if (names[i].EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase))
                    {
                        found = names[i];
                        break;
                    }
                }

                if (found != null)
                    stream = asm.GetManifestResourceStream(found);

                if (stream == null)
                {
                    System.Diagnostics.Debug.WriteLine("Embedded resources:");
                    for (int i = 0; i < names.Length; i++)
                        System.Diagnostics.Debug.WriteLine("  " + names[i]);

                    throw new InvalidOperationException("Hittar inte embedded SVG-resurs för: " + resourceNameOrFile);
                }
            }

            using (stream)
            {
                var doc = SvgDocument.Open<SvgDocument>(stream);

                // Färga fill/stroke
                var paint = new SvgColourServer(color);
                RecolorSvg(doc, paint);

                // Rendera ARGB-bitmap → HICON → Icon (klona innan DestroyIcon)
                using (var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;

                    doc.Width = new SvgUnit(SvgUnitType.Pixel, size);
                    doc.Height = new SvgUnit(SvgUnitType.Pixel, size);
                    doc.Draw(g);

                    IntPtr hIcon = bmp.GetHicon();
                    try
                    {
                        using (var tmp = Icon.FromHandle(hIcon))
                        {
                            return (Icon)tmp.Clone(); // viktigt: klona till managed kopia
                        }
                    }
                    finally
                    {
                        DestroyIcon(hIcon);
                    }
                }
            }
        }

        private static void RecolorSvg(SvgElement el, SvgColourServer paint)
        {
            for (int i = 0; i < el.Children.Count; i++)
                RecolorSvg(el.Children[i], paint);

            var vis = el as SvgVisualElement;
            if (vis == null) return;

            if (vis.Fill != null && vis.Fill != SvgPaintServer.None) vis.Fill = paint;
            if (vis.Stroke != null && vis.Stroke != SvgPaintServer.None) vis.Stroke = paint;
        }

        #endregion

        #region === Window Mode (toggle + migrering + form-respons) ===

        private void ToggleWindowMode()
        {
            _attachedMode = !_attachedMode;
            UpdateWindowModeMenuText();

            // Migrera riktiga appar
            foreach (var kv in _apps.Values)
                ShowInCurrentMode(kv.Doc);

            // Migrera placeholders
            foreach (var doc in _placeholders.Values)
                ShowInCurrentMode(doc);

            // Anpassa Form1-storlek
            if (!_attachedMode)
            {
                if (_attachedBoundsBeforeDetach == null)
                    _attachedBoundsBeforeDetach = Bounds;

                ShrinkShellToMenuAndToolbar();
            }
            else
            {
                if (_attachedBoundsBeforeDetach.HasValue)
                {
                    Bounds = _attachedBoundsBeforeDetach.Value;
                    _attachedBoundsBeforeDetach = null;
                }
            }
        }

        private void ShrinkShellToMenuAndToolbar()
        {
            var border = this.Size - this.ClientSize; // fönsterkrom
            int wantedHeight = _menu.Height + _toolbar.Height + 24; // liten marginal
            int wantedWidth = Math.Max(800, Width); // behåll vettig bredd

            var target = new Size(Math.Max(MinimumSize.Width, wantedWidth),
                                  Math.Max(MinimumSize.Height, wantedHeight));

            if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
            this.Size = new Size(target.Width, target.Height + border.Height);
        }

        private void ShowInCurrentMode(DockContent doc)
        {
            if (doc == null) return;

            if (_attachedMode)
            {
                doc.Show(_dockPanel, DockState.Document);
                EnsureShellFitsAttached(doc.ClientSize, new Size(1100, 350));
            }
            else
            {
                doc.Show(_dockPanel, DockState.Float);

                Control content = (doc.Controls.Count == 1) ? doc.Controls[0] : null;
                if (content != null)
                    ApplyDetachedWindowBounds(doc, content, doc.MinimumSize.IsEmpty ? new Size(1000, 600) : doc.MinimumSize);
            }
        }

        #endregion

        #region === Pricer (en-instans via DI) ===

        /// <summary>Öppnar eller fokuserar Pricer-appen (en instans).</summary>
        private void OpenOrFocusPricer()
        {
            const string key = "Pricer";
            if (_apps.TryGetValue(key, out var existing))
            {
                existing.Doc.Activate();
                return;
            }

            var pricer = _sp.GetRequiredService<PricerAppInstance>();
            var view = pricer.View ?? throw new InvalidOperationException("Pricer.View saknas.");

            var doc = new PricerDockContent { Text = pricer.Title ?? key };

            // vit ikon (16x16) från inbäddad SVG
            doc.Icon = GetAppIcon("Pricer");

            view.Dock = DockStyle.Fill;
            doc.Controls.Add(view);

            doc.Activated += (s, e) => Safe(pricer.OnActivated);
            doc.Deactivate += (s, e) => Safe(pricer.OnDeactivated);
            doc.FormClosed += (s, e) =>
            {
                if (_apps.ContainsKey(key)) _apps.Remove(key);
                Safe(pricer.Dispose);
            };

            _apps[key] = (doc, pricer);
            ShowInCurrentMode(doc);
        }

        #endregion

        #region === Placeholder (en-instans per knapp) ===

        /// <summary>Öppnar (eller fokuserar) en placeholder-app som enkel tab/fönster.</summary>
        private void OpenPlaceholderOnce(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) title = "Untitled";

            DockContent existing;
            if (_placeholders.TryGetValue(title, out existing))
            {
                existing.Activate();
                return;
            }

            var doc = new PlaceholderDockContent { Text = title, TitleKey = title };

            // Sätt ikon (vit 16x16 från inbäddad SVG)
            try { doc.Icon = GetAppIcon(title); } catch { /* best effort */ }

            var host = new UserControl { Dock = DockStyle.Fill };
            host.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Text = title + Environment.NewLine + "(Not implemented yet)"
            });

            doc.Controls.Add(host);

            doc.FormClosed += (s, e) => _placeholders.Remove(title);

            _placeholders[title] = doc;
            ShowInCurrentMode(doc);
        }

        #endregion

        #region === Hjälpare (storlek/placering) ===

        /// <summary>Korrigerar en rektangel så att den hamnar inom någon skärms arbetsyta.</summary>
        private Rectangle ClampToScreens(Rectangle r)
        {
            foreach (var scr in Screen.AllScreens)
            {
                if (scr.WorkingArea.IntersectsWith(r))
                    return r; // OK
            }

            var wa = Screen.PrimaryScreen.WorkingArea;
            var clamped = new Rectangle(
                Math.Max(wa.Left, Math.Min(wa.Right - Math.Max(100, r.Width), wa.Left)),
                Math.Max(wa.Top, Math.Min(wa.Bottom - Math.Max(100, r.Height), wa.Top)),
                Math.Min(r.Width, wa.Width),
                Math.Min(r.Height, wa.Height)
            );

            _layoutCorrected = true;
            return clamped;
        }

        /// <summary>Flyttar shell och alla float-fönster in i synlig yta om nödvändigt.</summary>
        private void ClampAllWindowsToScreens()
        {
            try
            {
                var clamped = ClampToScreens((WindowState == FormWindowState.Normal) ? Bounds : RestoreBounds);
                if (WindowState == FormWindowState.Normal) Bounds = clamped;

                foreach (var fw in _dockPanel.FloatWindows)
                {
                    var win = fw as FloatWindow;
                    if (win == null) continue;

                    var c = ClampToScreens(win.Bounds);
                    if (c != win.Bounds) win.Bounds = c;
                }
            }
            catch { /* best effort */ }
        }

        /// <summary>
        /// Beräknar en vettig startstorlek för ett dokument baserat på innehållets PreferredSize, med min-golv.
        /// </summary>
        private static Size CalcContentSize(Control content, Size min)
        {
            var ask = new Size(1920, 1080);
            var pref = content.GetPreferredSize(ask);
            if (pref.Width <= 0) pref.Width = Math.Max(content.Width, 800);
            if (pref.Height <= 0) pref.Height = Math.Max(content.Height, 600);

            pref = new Size(Math.Max(min.Width, pref.Width),
                            Math.Max(min.Height, pref.Height));

            var ceil = new Size(1600, 1000);
            pref = new Size(Math.Min(ceil.Width, pref.Width),
                            Math.Min(ceil.Height, pref.Height));

            return pref;
        }

        /// <summary>Sätter MinimumSize och ClientSize för ATTACHED-läge.</summary>
        private static void PrepareDocumentSizeForAttached(DockContent doc, Control content, Size min)
        {
            var size = CalcContentSize(content, min);
            doc.MinimumSize = min;
            doc.ClientSize = size;
        }

        /// <summary>Efter .Show(Float): anpassa float-fönstrets storlek till innehållet (+min).</summary>
        private static void ApplyDetachedWindowBounds(DockContent doc, Control content, Size min)
        {
            var win = doc.FloatPane != null ? doc.FloatPane.FloatWindow as FloatWindow : null;
            if (win == null) return;

            var wantedClient = CalcContentSize(content, min);
            var deltaW = win.Width - win.ClientSize.Width;
            var deltaH = win.Height - win.ClientSize.Height;

            var target = new Size(wantedClient.Width + deltaW,
                                  wantedClient.Height + deltaH);

            var minWithChrome = new Size(min.Width + deltaW, min.Height + deltaH);
            target = new Size(Math.Max(target.Width, minWithChrome.Width),
                              Math.Max(target.Height, minWithChrome.Height));

            win.MinimumSize = minWithChrome;
            win.Size = target;
        }

        /// <summary>Säkerställ att shell (Form1) rymmer en attached-tab av given storlek.</summary>
        private void EnsureShellFitsAttached(Size docClientSize, Size minShell)
        {
            var chrome = this.Size - this.ClientSize;
            var neededClient = new Size(
                Math.Max(minShell.Width, docClientSize.Width),
                Math.Max(minShell.Height, docClientSize.Height + _menu.Height + _toolbar.Height)
            );

            var neededTotal = new Size(neededClient.Width + chrome.Width,
                                       neededClient.Height + chrome.Height);

            if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;

            if (Width < neededTotal.Width || Height < neededTotal.Height)
                Size = new Size(Math.Max(Width, neededTotal.Width),
                                Math.Max(Height, neededTotal.Height));
        }

        private static void Safe(Action act)
        {
            try { act?.Invoke(); } catch { /* logga vid behov */ }
        }

        #endregion

        #region === DockContent-typer (persistens-nycklar) ===

        /// <summary>DockContent för Pricer (stabil persist-sträng).</summary>
        private sealed class PricerDockContent : DockContent
        {
            protected override string GetPersistString()
            {
                return "Pricer";
            }
        }

        /// <summary>DockContent för placeholders (Blotter, Gamma Hedger, Volatility Management).</summary>
        private sealed class PlaceholderDockContent : DockContent
        {
            /// <summary>Nyckeln som används som persist-sträng (sätts vid skapande).</summary>
            public string TitleKey { get; set; }

            protected override string GetPersistString()
            {
                return !string.IsNullOrEmpty(TitleKey) ? TitleKey
                     : !string.IsNullOrEmpty(Text) ? Text
                     : "Placeholder";
            }
        }

        #endregion
    }
}
