using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WeifenLuo.WinFormsUI.Docking;

namespace FX.UI.WinForms   // <-- låt vara samma namespace som i Form1.cs
{
    /// <summary>
    /// Eget FloatWindow som:
    /// - visas i Taskbar (TopLevel, ingen Owner)
    /// - har min/max/restore
    /// - sätter titelradsfärger via DWM (Win 10/11) om möjligt
    /// - uppdaterar både FÖNSTER-TEXT och FÖNSTER-IKON från aktivt DockContent
    /// </summary>
    internal sealed class CustomFloatWindow : FloatWindow
    {
        private readonly Color _captionColor;
        private readonly Color _textColor;
        private readonly Icon _fallbackIcon;

        private DockPane _paneRef; // referens så vi kan läsa ActiveContent löpande

        // ---- DWM (Win10/11) för titelradsfärger ----
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        // ---- Hit-test för större träffyta (minimera) ----
        private const int WM_NCHITTEST = 0x0084;
        private const int HTMINBUTTON = 8;

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        // ---- Ctors (matchar IFloatWindowFactory) ----
        public CustomFloatWindow(DockPanel dockPanel, DockPane pane, Color captionColor, Color textColor, Icon fallbackIcon)
            : base(dockPanel, pane)
        {
            _captionColor = captionColor;
            _textColor = textColor;
            _fallbackIcon = fallbackIcon;

            _paneRef = pane;
            InitializeWindowChrome();
            HookPane(pane);
            UpdateTitleAndIconFromPane();
        }

        public CustomFloatWindow(DockPanel dockPanel, DockPane pane, Rectangle bounds, Color captionColor, Color textColor, Icon fallbackIcon)
            : base(dockPanel, pane, bounds)
        {
            _captionColor = captionColor;
            _textColor = textColor;
            _fallbackIcon = fallbackIcon;

            _paneRef = pane;
            InitializeWindowChrome();
            HookPane(pane);
            UpdateTitleAndIconFromPane();
        }

        // ---- Init / basbeteende ----
        private void InitializeWindowChrome()
        {
            ShowInTaskbar = true;
            FormBorderStyle = FormBorderStyle.Sizable;
            ControlBox = true;
            MinimizeBox = true;
            MaximizeBox = true;

            // fallback-ikon (byter vi ändå när aktivt content finns)
            try
            {
                if (_fallbackIcon != null) Icon = _fallbackIcon;
                else if (Application.OpenForms.Count > 0 && Application.OpenForms[0].Icon != null)
                    Icon = Application.OpenForms[0].Icon;
            }
            catch { /* no-op */ }

            TryDetachFromOwner();
            this.HandleCreated += (s, e) => { TryDetachFromOwner(); ApplyTitleBarColorsSafe(); UpdateTitleAndIconFromPane(); };
            this.Shown += (s, e) => { TryDetachFromOwner(); ApplyTitleBarColorsSafe(); UpdateTitleAndIconFromPane(); };
        }

        private void TryDetachFromOwner()
        {
            try
            {
                if (this.Owner != null) this.Owner = null;
                if (!this.TopLevel) this.TopLevel = true;
                this.ShowInTaskbar = true;
            }
            catch { /* no-op */ }
        }

        private void HookPane(DockPane pane)
        {
            try
            {
                if (pane == null) return;

                // Lyssna på panelens globala ActiveContentChanged (finns i alla versioner)
                var dp = pane.DockPanel;
                if (dp != null)
                    dp.ActiveContentChanged += (s, e) => UpdateTitleAndIconFromPane();

                // För säkerhets skull: uppdatera även när FloatWindow aktiveras
                this.Activated += (s, e) => UpdateTitleAndIconFromPane();
            }
            catch
            {
                /* no-op */
            }
        }


        private static string SafeDockText(DockContent dc)
        {
            try { return string.IsNullOrWhiteSpace(dc?.Text) ? null : dc.Text; }
            catch { return null; }
        }

        /// <summary>
        /// Läser aktiv DockContent och uppdaterar fönstrets Text och Icon.
        /// </summary>
        private void UpdateTitleAndIconFromPane()
        {
            try
            {
                var dc = _paneRef?.ActiveContent as DockContent;

                // Titel
                var title = SafeDockText(dc);
                if (string.IsNullOrWhiteSpace(title) && _paneRef != null && _paneRef.Contents.Count > 0)
                    title = SafeDockText(_paneRef.Contents[0] as DockContent);
                if (!string.IsNullOrWhiteSpace(title))
                    this.Text = title;

                // Ikon (använd per-dokument-ikon om satt; annars fallback)
                if (dc != null && dc.Icon != null)
                    this.Icon = dc.Icon;
                else if (_fallbackIcon != null)
                    this.Icon = _fallbackIcon;
            }
            catch
            {
                // best effort – behåll tidigare text/ikon
            }
        }

        // ---- NCHITTEST → större träffyta på Minimize ----
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST)
            {
                int x = (short)(m.LParam.ToInt32() & 0xFFFF);
                int y = (short)((m.LParam.ToInt32() >> 16) & 0xFFFF);
                var ptScreen = new Point(x, y);

                var rcMin = GetMinimizeButtonRectScreen();
                if (!rcMin.IsEmpty && rcMin.Contains(ptScreen))
                {
                    m.Result = (IntPtr)HTMINBUTTON; // rapportera som min-knappen
                    return;
                }
            }
            base.WndProc(ref m);
        }

        private Rectangle GetMinimizeButtonRectScreen()
        {
            if (!IsHandleCreated) return Rectangle.Empty;
            if (!GetWindowRect(this.Handle, out var wr)) return Rectangle.Empty;

            var btn = SystemInformation.CaptionButtonSize;
            var frame = SystemInformation.FrameBorderSize;
            var padX = SystemInformation.BorderSize.Width;

            // [Close][Max/Restore][Minimize] – från höger
            int right = wr.Right - frame.Width - padX;
            int top = wr.Top + frame.Height;

            var rcClose = new Rectangle(right - btn.Width, top, btn.Width, btn.Height);
            var rcMax = new Rectangle(rcClose.Left - btn.Width, top, btn.Width, btn.Height);
            var rcMin = new Rectangle(rcMax.Left - btn.Width, top, btn.Width, btn.Height);

            rcMin.Inflate(8, 8); // lite generösare träffyta
            return rcMin;
        }

        // ---- Titelradsfärger ----
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ApplyTitleBarColorsSafe();
            UpdateTitleAndIconFromPane();
        }

        private void ApplyTitleBarColorsSafe()
        {
            if (!IsHandleCreated) return;
            try
            {
                int caption = ToCOLORREF(_captionColor);
                int text = ToCOLORREF(_textColor);
                DwmSetWindowAttribute(this.Handle, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
                DwmSetWindowAttribute(this.Handle, DWMWA_TEXT_COLOR, ref text, sizeof(int));
            }
            catch { /* no-op */ }
        }

        // COLORREF = 0x00BBGGRR
        private static int ToCOLORREF(Color c) => (c.R) | (c.G << 8) | (c.B << 16);
    }
}
