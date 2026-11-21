using System;
using System.Drawing;
using System.Windows.Forms;

namespace Seam.WindowsHost.Gui
{
    internal enum ButtonKind
    {
        Primary,
        Secondary,
        Ghost
    }

    /// <summary>
    /// Flat rounded button with hover/press states, sized from its text.
    /// WinForms' stock Button cannot do rounded corners without a custom Paint handler, and a
    /// themed owner-drawn control keeps every button in the window consistent.
    /// </summary>
    internal sealed class PillButton : Control
    {
        private bool _hover;
        private bool _pressed;

        public PillButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor | ControlStyles.StandardClick |
                     ControlStyles.Selectable, true);
            BackColor = Color.Transparent;
            Font = Theme.BodyStrong;
            Height = 34;
            Cursor = Cursors.Hand;
            TabStop = true;
            Kind = ButtonKind.Secondary;
        }

        public ButtonKind Kind { get; set; }

        public int Radius { get; set; } = 6;

        public override string Text
        {
            get => base.Text;
            set
            {
                base.Text = value ?? string.Empty;
                Width = PreferredWidth;
                Invalidate();
            }
        }

        /// <summary>Width that fits the current text plus comfortable padding.</summary>
        public int PreferredWidth
        {
            get
            {
                Size size = TextRenderer.MeasureText(Text, Font);
                return Math.Max(64, size.Width + (Kind == ButtonKind.Ghost ? 20 : 34));
            }
        }

        public void SetText(string text)
        {
            if (Text == text)
            {
                return;
            }

            Text = text;
            Width = PreferredWidth;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { _pressed = true; Focus(); Invalidate(); }
            base.OnMouseDown(e);
        }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                _pressed = true;
                Invalidate();
            }
            base.OnKeyDown(e);
        }
        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (_pressed && (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter))
            {
                _pressed = false;
                Invalidate();
                OnClick(EventArgs.Empty);
            }
            base.OnKeyUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            Color fill, border, text;
            bool hasBorder = true;

            if (!Enabled)
            {
                fill = Theme.SurfaceMuted;
                border = Theme.Border;
                text = Theme.TextTertiary;
            }
            else if (Kind == ButtonKind.Primary)
            {
                fill = _pressed ? Theme.AccentPressed : _hover ? Theme.AccentHover : Theme.Accent;
                border = fill;
                text = Color.White;
            }
            else if (Kind == ButtonKind.Ghost)
            {
                fill = _pressed ? Theme.AccentSoft : _hover ? Theme.AccentSoft : Color.Transparent;
                border = Color.Transparent;
                text = Theme.Accent;
                hasBorder = false;
            }
            else
            {
                fill = _pressed ? Theme.SurfaceRaised : _hover ? Theme.SurfaceMuted : Theme.Surface;
                border = Theme.BorderStrong;
                text = Theme.TextPrimary;
            }

            var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            if (fill != Color.Transparent)
            {
                Theme.FillRounded(g, bounds, Radius, fill, hasBorder ? border : (Color?)null);
            }
            else if (_hover || _pressed)
            {
                Theme.FillRounded(g, bounds, Radius, fill, null);
            }

            if (Focused && Enabled)
            {
                using var focusPen = new Pen(Color.FromArgb(90, Theme.Accent), 1f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
                using var focusPath = Theme.RoundedRect(new Rectangle(1, 1, Width - 3, Height - 3), Radius);
                g.DrawPath(focusPen, focusPath);
            }

            TextRenderer.DrawText(g, Text, Font, bounds, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
