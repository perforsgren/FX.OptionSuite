using System;
using System.Drawing;
using System.Windows.Forms;
using WeifenLuo.WinFormsUI.Docking;

namespace FX.Shell
{
    /// <summary>
    /// Skapar våra egna float-fönster så att de visas i taskbar och får systemknappar.
    /// </summary>
    internal sealed class CustomFloatWindowFactory : DockPanelExtender.IFloatWindowFactory
    {
        private readonly Color _captionColor;
        private readonly Color _textColor;
        private readonly Icon _icon;

        /// <param name="captionColor">Titelradsfärg (t.ex. VS-blå #007ACC)</param>
        /// <param name="textColor">Titelradens textfärg (oftast vit)</param>
        /// <param name="icon">Ikon att använda för float-fönster (kan vara appens ikon)</param>
        public CustomFloatWindowFactory(Color captionColor, Color textColor, Icon icon)
        {
            _captionColor = captionColor;
            _textColor = textColor;
            _icon = icon;
        }

        public FloatWindow CreateFloatWindow(DockPanel dockPanel, DockPane pane, Rectangle bounds)
        {
            return new CustomFloatWindow(dockPanel, pane, bounds, _captionColor, _textColor, _icon);
        }

        public FloatWindow CreateFloatWindow(DockPanel dockPanel, IDockContent content, Rectangle bounds)
        {
            return new CustomFloatWindow(dockPanel, content, bounds, _captionColor, _textColor, _icon);
        }
    }
}
