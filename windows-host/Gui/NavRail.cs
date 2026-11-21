using System;
using System.Drawing;
using System.Windows.Forms;

namespace Seam.WindowsHost.Gui
{
    /// <summary>Top-level destinations of the window. One page is visible at a time.</summary>
    internal enum GuiPage
    {
        Home,
        Activity,
        Settings,
        Diagnostics
    }

    /// <summary>
    /// Compact left navigation rail: four destinations, one line each, 34px rows.
    /// Owner-drawn so the selected row can carry a soft accent fill and a 3px marker without
    /// the platform's default selection colour turning blue-grey against the graphite surface.
    /// </summary>
    internal sealed class NavRail : Control
    {
        private sealed class Item
        {
            public Item(GuiPage page, string glyph, string label)
            {
                Page = page;
                Glyph = glyph;
                Label = label;
            }

            public GuiPage Page { get; }
            public string Glyph { get; }
            public string Label { get; }
        }

        private readonly Item[] _items =
        {
            new(GuiPage.Home, "\uE80F", "Home"),
            new(GuiPage.Activity, "\uE81C", "Activity"),
            new(GuiPage.Settings, "\uE713", "Settings"),
            new(GuiPage.Diagnostics, "\uE9D9", "Diagnostics")
        };

        private const int RowHeight = 40;
        private const int RowInset = 6;

        private GuiPage _selected = GuiPage.Home;
        private int _hoverIndex = -1;

        public NavRail()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
            BackColor = Theme.NavBackground;
            TabStop = true;
            Cursor = Cursors.Hand;
            AccessibleName = "Navigation";
        }

        /// <summary>Raised when the user picks a destination.</summary>
        public event Action<GuiPage>? PageSelected;

        public GuiPage Selected
        {
            get => _selected;
            set
            {
                if (_selected == value)
                {
                    return;
                }

                _selected = value;
                Invalidate();
            }
        }

        private Rectangle RowBounds(int index) =>
            new(RowInset, index * RowHeight, Math.Max(0, Width - (RowInset * 2)), RowHeight - 6);

        private int IndexAt(Point point)
        {
            for (int i = 0; i < _items.Length; i++)
            {
                if (RowBounds(i).Contains(point))
                {
                    return i;
                }
            }

            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int index = IndexAt(e.Location);
            if (index != _hoverIndex)
            {
                _hoverIndex = index;
                Invalidate();
            }

            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hoverIndex = -1;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                int index = IndexAt(e.Location);
                if (index >= 0)
                {
                    GuiPage page = _items[index].Page;
                    Selected = page;
                    PageSelected?.Invoke(page);
                }
            }

            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            using var background = new SolidBrush(Theme.NavBackground);
            g.FillRectangle(background, ClientRectangle);

            for (int i = 0; i < _items.Length; i++)
            {
                Rectangle row = RowBounds(i);
                bool selected = _items[i].Page == _selected;

                if (selected)
                {
                    Theme.FillRounded(g, row, 6, Theme.AccentSoft, null);
                    Theme.FillRounded(g, new Rectangle(row.X + 8, row.Y + 9, 3, row.Height - 18), 2, Theme.Accent, null);
                }
                else if (i == _hoverIndex)
                {
                    Theme.FillRounded(g, row, 6, Theme.SurfaceMuted, null);
                }

                Color text = selected ? Theme.TextPrimary : Theme.TextSecondary;
                Color glyph = selected ? Theme.Accent : Theme.TextTertiary;

                var glyphRect = new Rectangle(row.X + 20, row.Y, 22, row.Height);
                TextRenderer.DrawText(g, _items[i].Glyph, Theme.Glyphs, glyphRect, glyph,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

                var labelRect = new Rectangle(row.X + 48, row.Y, Math.Max(10, row.Width - 56), row.Height);
                TextRenderer.DrawText(g, _items[i].Label, Theme.Body, labelRect, text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
                    TextFormatFlags.EndEllipsis);
            }
        }
    }
}
