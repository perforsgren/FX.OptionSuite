using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FX.UI.WinForms
{
    [ToolboxItem(true)]
    public class CustomTabControl : TabControl
    {
        public CustomTabControl()
        {
            Alignment = TabAlignment.Bottom;
            DrawMode = TabDrawMode.OwnerDrawFixed;
            SizeMode = TabSizeMode.Fixed;
            ItemSize = new Size(130, 28);

            SetStyle(ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);

            DoubleBuffered = true;

            BackColor = Color.FromArgb(32, 32, 36);

            TabBackColor = Color.FromArgb(45, 45, 50);
            TabBackColorSelected = Color.FromArgb(65, 65, 72);
            TabBorderColor = Color.FromArgb(80, 80, 88);
            TabTextColor = Color.Gainsboro;
            TabTextColorSelected = Color.White;

            TabCornerRadius = 6;
            IconTextSpacing = 4;


            // Gör ApplyPageStyle först när tabbar finns och inte i designern
            if (!IsReallyInDesignMode)
            {
                ApplyPageStyle();
            }
        }

        private bool IsReallyInDesignMode
        {
            get
            {
                if (LicenseManager.UsageMode == LicenseUsageMode.Designtime)
                    return true;

                return Site?.DesignMode ?? false;
            }
        }

        /// <summary>
        /// Justerar client-ytan så att innehållet fyller rätt yta beroende på
        /// om tabsen ligger överst eller nederst.
        /// </summary>
        public override Rectangle DisplayRectangle
        {
            get
            {
                var rect = base.DisplayRectangle;

                // För tabs överst vill vi låta innehållet fylla området under tabsen
                if (Alignment == TabAlignment.Top)
                {
                    return new Rectangle(
                        0,
                        rect.Top,
                        ClientRectangle.Width,
                        ClientRectangle.Height - rect.Top);
                }

                // För tabs nederst fungerar base redan bra – vi vill bara ta bort ev. sidpadding
                if (Alignment == TabAlignment.Bottom)
                {
                    return new Rectangle(
                        0,
                        rect.Y,
                        ClientRectangle.Width,
                        rect.Height);
                }

                // Övriga alignments använder basbeteendet rakt av
                return rect;
            }
        }


        // Färger + layout
        public Color TabBackColor { get; set; }
        public Color TabBackColorSelected { get; set; }
        public Color TabBorderColor { get; set; }
        public Color TabTextColor { get; set; }
        public Color TabTextColorSelected { get; set; }
        public int TabCornerRadius { get; set; }

        // Extra: avstånd mellan ikon och text
        public int IconTextSpacing { get; set; }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var b = new SolidBrush(this.BackColor))
            {
                e.Graphics.FillRectangle(b, this.ClientRectangle);
            }
        }


        /// <summary>
        /// Skapar en rektangel med rundade hörn upptill (rak nederkant),
        /// används när tabs ligger längst upp.
        /// </summary>
        private static GraphicsPath CreateTopRoundedRect(Rectangle r, int radius)
        {
            var path = new GraphicsPath();

            if (radius <= 0)
            {
                path.AddRectangle(r);
                return path;
            }

            int d = radius * 2;

            // Övre vänster hörn (rundat)
            path.AddArc(r.X, r.Y, d, d, 180, 90);

            // Övre höger hörn (rundat)
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);

            // Nedre högra sidan – rak linje
            path.AddLine(r.Right, r.Y + radius, r.Right, r.Bottom);

            // Botten – rak
            path.AddLine(r.Right, r.Bottom, r.X, r.Bottom);

            // Nedre vänstra sidan – rak upp till där rundningen börjar
            path.AddLine(r.X, r.Bottom, r.X, r.Y + radius);

            path.CloseFigure();
            return path;
        }

        /// <summary>
        /// Skapar en rektangel med rundade hörn nedtill (rak överkant),
        /// används när tabs ligger längst ned.
        /// </summary>
        private static GraphicsPath CreateBottomRoundedRect(Rectangle r, int radius)
        {
            var path = new GraphicsPath();

            if (radius <= 0)
            {
                path.AddRectangle(r);
                return path;
            }

            int d = radius * 2;

            // Överkant – helt rak
            path.AddLine(r.X, r.Y, r.Right, r.Y);

            // Höger sida ned till där rundningen börjar
            path.AddLine(r.Right, r.Y, r.Right, r.Bottom - radius);

            // Nedre höger hörn (rundat)
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);

            // Nedre vänster hörn (rundat)
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);

            // Vänster sida upp
            path.AddLine(r.X, r.Bottom - radius, r.X, r.Y);

            path.CloseFigure();
            return path;
        }

        /// <summary>
        /// Skapar GraphicsPath för en tab-pill beroende på Alignment:
        /// rundning upptill för Top, rundning nedtill för Bottom.
        /// </summary>
        private GraphicsPath CreateTabPath(Rectangle r, int radius)
        {
            if (Alignment == TabAlignment.Bottom)
            {
                return CreateBottomRoundedRect(r, radius);
            }

            // Default: rundning upptill (Top/Left/Right)
            return CreateTopRoundedRect(r, radius);
        }




        private void ApplyPageStyle()
        {
            foreach (TabPage page in TabPages)
            {
                page.UseVisualStyleBackColor = false;   // stäng av OS-temat
                page.BackColor = Color.FromArgb(32, 32, 36); // eller annan content-färg
            }
        }

        protected override void OnControlAdded(ControlEventArgs e)
        {
            base.OnControlAdded(e);
            if (!IsReallyInDesignMode && e.Control is TabPage)
            {
                ApplyPageStyle();
            }
        }


        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            bool selected = (e.Index == SelectedIndex);
            Rectangle tabRect = GetTabRect(e.Index);
            tabRect.Inflate(-3, -2); // lite padding inåt

            using (GraphicsPath path = CreateTabPath(tabRect, TabCornerRadius))
            using (var fill = new SolidBrush(selected ? TabBackColorSelected : TabBackColor))
            using (var border = new Pen(TabBorderColor))
            {
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(border, path);
            }

            var page = TabPages[e.Index];
            Color textColor = selected ? TabTextColorSelected : TabTextColor;

            Rectangle textRect = tabRect;

            // --- Ikonhantering ---
            Image icon = GetPageImage(page);
            if (icon != null)
            {
                // Ikonstorlek – skala ned lite om den är större än ItemSize.Height
                int iconSize = tabRect.Height - 8;
                if (iconSize > 16) iconSize = 16; // rimlig max

                Rectangle iconRect = new Rectangle(
                    tabRect.X + 8,
                    tabRect.Y + (tabRect.Height - iconSize) / 2,
                    iconSize,
                    iconSize);

                // Rita ikon
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                e.Graphics.DrawImage(icon, iconRect);

                // Justera textrektangel så den börjar efter ikonen
                textRect.X = iconRect.Right + IconTextSpacing;
                textRect.Width = tabRect.Right - textRect.X - 4;
            }

            // --- Text ---
            TextRenderer.DrawText(
                e.Graphics,
                page.Text,
                Font,
                textRect,
                textColor,
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.Left |
                TextFormatFlags.EndEllipsis);
        }

        /// <summary>
        /// Ritar bakgrund, content-ytan och tabs i pill-stil.
        /// Stödjer både tabs överst och nederst.
        /// </summary>
        protected override void OnPaint(PaintEventArgs e)
        {

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            if (TabCount == 0)
                return;

            // Hitta vertikalt band där tabsen ligger
            int bandTop = ClientRectangle.Bottom;
            int bandBottom = 0;
            for (int i = 0; i < TabPages.Count; i++)
            {
                Rectangle tabRect = GetTabRect(i);
                bandTop = Math.Min(bandTop, tabRect.Top);
                bandBottom = Math.Max(bandBottom, tabRect.Bottom);
            }

            int maxDownOverlap = 3;
            int maxUpOverlap = 3;

            // Fyll hela bakgrunden med parent-färg
            Color parentColor = Parent != null ? Parent.BackColor : BackColor;
            using (var parentBrush = new SolidBrush(parentColor))
            {
                g.FillRectangle(parentBrush, ClientRectangle);
            }

            // Fyll själva content-området (ytan där TabPages ritas)
            using (var b = new SolidBrush(this.BackColor))
            {
                Rectangle contentArea;

                if (Alignment == TabAlignment.Top)
                {
                    // Innehållet ligger under tabsen, med lite overlap så pillen "biter ned"
                    int startY = bandBottom + maxDownOverlap;
                    contentArea = new Rectangle(
                        0,
                        startY,
                        ClientRectangle.Width,
                        Math.Max(0, ClientRectangle.Height - startY));
                }
                else if (Alignment == TabAlignment.Bottom)
                {
                    // Innehållet ligger ovanför tabsen, med lite overlap så pillen "biter upp"
                    int endY = bandTop - maxUpOverlap;
                    contentArea = new Rectangle(
                        0,
                        0,
                        ClientRectangle.Width,
                        Math.Max(0, endY));
                }
                else
                {
                    // Fallback (Left/Right) – rita bara hela ytan
                    contentArea = ClientRectangle;
                }

                g.FillRectangle(b, contentArea);
            }

            // Rita tabs – samma stil, men pillens placering beror på alignment
            for (int i = 0; i < TabPages.Count; i++)
            {
                Rectangle rect = GetTabRect(i);
                bool isSel = (i == SelectedIndex);

                var pill = Rectangle.Inflate(rect, -2, -3);
                pill.X += 1;

                if (Alignment == TabAlignment.Top)
                {
                    // Som tidigare – pillen sticker ned lite i content
                    int upOverlap = isSel ? 2 : 1;
                    int downOverlap = isSel ? 3 : 2;

                    int pillTop = Math.Max(0, bandTop - upOverlap);
                    int pillBottom = rect.Bottom + downOverlap;
                    pill.Y = pillTop;
                    pill.Height = pillBottom - pillTop;
                }
                else if (Alignment == TabAlignment.Bottom)
                {
                    // Spegling: pillen sticker upp lite i content
                    int upOverlap = isSel ? 3 : 2;   // mer upp i content för vald tab
                    int downOverlap = isSel ? 2 : 1; // lite utanför nederkanten

                    int pillTop = rect.Top - upOverlap;
                    int pillBottom = Math.Min(ClientRectangle.Bottom, bandBottom + downOverlap);
                    pill.Y = pillTop;
                    pill.Height = pillBottom - pillTop;
                }

                using (var path = CreateTabPath(pill, TabCornerRadius))
                using (var fill = new SolidBrush(isSel ? TabBackColorSelected : TabBackColor))
                using (var border = new Pen(TabBorderColor))
                {
                    g.FillPath(fill, path);
                    g.DrawPath(border, path);
                }

                // --- Ikon + text – exakt som i din nuvarande OnPaint ---
                var page = TabPages[i];
                Color textColor = isSel ? TabTextColorSelected : TabTextColor;
                Rectangle textRect = pill;

                Image icon = GetPageImage(page);
                if (icon != null)
                {
                    int iconSize = pill.Height - 8;
                    if (iconSize > 16) iconSize = 16;

                    Rectangle iconRect = new Rectangle(
                        pill.X + 8,
                        pill.Y + (pill.Height - iconSize) / 2,
                        iconSize,
                        iconSize);

                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(icon, iconRect);

                    textRect.X = iconRect.Right + IconTextSpacing;
                    textRect.Width = pill.Right - textRect.X - 4;
                }

                TextRenderer.DrawText(
                    g,
                    page.Text,
                    Font,
                    textRect,
                    textColor,
                    TextFormatFlags.VerticalCenter |
                    TextFormatFlags.HorizontalCenter |
                    TextFormatFlags.EndEllipsis);
            }
        }



        private Image GetPageImage(TabPage page)
        {
            if (ImageList == null)
                return null;

            if (!string.IsNullOrEmpty(page.ImageKey) &&
                ImageList.Images.ContainsKey(page.ImageKey))
            {
                return ImageList.Images[page.ImageKey];
            }

            if (page.ImageIndex >= 0 && page.ImageIndex < ImageList.Images.Count)
            {
                return ImageList.Images[page.ImageIndex];
            }

            return null;
        }
    }
}
