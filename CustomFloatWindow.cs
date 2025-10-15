using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WeifenLuo.WinFormsUI.Docking;

namespace FX.Shell
{
    /// <summary>
    /// Egen FloatWindow:
    /// - Visas i Taskbar
    /// - Har min/max/restore
    /// - Försöker sätta blå titelrad + vit text (Windows 11 API; fail-safear på äldre versioner)
    /// </summary>
    internal sealed class CustomFloatWindow : FloatWindow
    {
        private readonly Color _captionColor;
        private readonly Color _textColor;
        private readonly Icon _fallbackIcon;

        // DWMWA attribut (Windows 11 22000+)
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        public CustomFloatWindow(
            DockPanel dockPanel,
            DockPane pane,
            Rectangle bounds,
            Color captionColor,
            Color textColor,
            Icon fallbackIcon)
            : base(dockPanel, pane, bounds)
        {
            _captionColor = captionColor;
            _textColor = textColor;
            _fallbackIcon = fallbackIcon;

            InitializeWindowChrome();
        }

        public CustomFloatWindow(
            DockPanel dockPanel,
            IDockContent content,
            Rectangle bounds,
            Color captionColor,
            Color textColor,
            Icon fallbackIcon)
            : base(dockPanel, content, bounds)
        {
            _captionColor = captionColor;
            _textColor = textColor;
            _fallbackIcon = fallbackIcon;

            InitializeWindowChrome();
        }

        private void InitializeWindowChrome()
        {
            // Viktigt: annars syns inte varje float som egen taskbar-item
            ShowInTaskbar = true;

            // Systemknappar + klassisk krom
            FormBorderStyle = FormBorderStyle.Sizable;
            ControlBox = true;
            MinimizeBox = true;
            MaximizeBox = true;

            // Sätt ikon (använd shell/app-ikon om inget annat)
            try
            {
                if (_fallbackIcon != null)
                    Icon = _fallbackIcon;
                else if (Application.OpenForms.Count > 0 && Application.OpenForms[0].Icon != null)
                    Icon = Application.OpenForms[0].Icon;
            }
            catch { /* no-op */ }

            // Visa vettig text i titeln
            UpdateTitleFromActiveContent();

            // Häng på event så titeln följer aktivt innehåll
            this.ActiveContentChanged += (s, e) => UpdateTitleFromActiveContent();

            // Applicera titelradsfärger när handtaget finns
            this.HandleCreated += (s, e) => ApplyTitleBarColorsSafe();
        }

        private void UpdateTitleFromActiveContent()
        {
            var txt = this.Text;
            try
            {
                if (this.ActiveContent is DockContent dc && !string.IsNullOrWhiteSpace(dc.Text))
                    txt = dc.Text;
                else if (this.DockPanel?.ActiveContent is DockContent ac && !string.IsNullOrWhiteSpace(ac.Text))
                    txt = ac.Text;
            }
            catch { /* no-op */ }
            this.Text = txt;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ApplyTitleBarColorsSafe();
        }

        private void ApplyTitleBarColorsSafe()
        {
            if (!IsHandleCreated) return;

            // Dessa attribut finns officiellt från Windows 11 build 22000+.
            // På äldre system gör vi bara inget (fallback till systemets standard).
            try
            {
                int caption = ToCOLORREF(_captionColor);
                int text = ToCOLORREF(_textColor);

                // Ignorera returvärden; på OS som saknar attributen blir detta bara ett no-op.
                DwmSetWindowAttribute(this.Handle, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
                DwmSetWindowAttribute(this.Handle, DWMWA_TEXT_COLOR, ref text, sizeof(int));
            }
            catch
            {
                // no-op: kör vidare med standardtitel (temat VS2015Blue färgsätter ändå
                // DockPanel-innehållet; detta fixar bara själva OS-titelraden).
            }
        }

        // COLORREF = 0x00BBGGRR
        private static int ToCOLORREF(Color c)
        {
            return (c.R) | (c.G << 8) | (c.B << 16);
        }
    }
}
