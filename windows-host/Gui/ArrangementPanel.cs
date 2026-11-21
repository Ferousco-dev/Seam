using System;
using System.Drawing;
using System.Windows.Forms;

namespace Seam.WindowsHost.Gui
{
    /// <summary>
    /// Read-only arrangement diagram: a PC tile and a phone tile placed on the screen edge the
    /// user picked in Settings, with the tile that currently owns the pointer drawn in the
    /// accent colour.
    ///
    /// It reflects real state only — <see cref="Edge"/>, <see cref="IsConnected"/> and
    /// <see cref="RoutingToPhone"/> all come from the live session. Nothing here is clickable:
    /// the edge is changed in Settings, so the diagram must not pretend to be a control.
    /// </summary>
    internal sealed class ArrangementPanel : Control
    {
        private string _edge = "right";
        private bool _connected;
        private bool _routingToPhone;

        public ArrangementPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
            AccessibleName = "Arrangement";
        }

        /// <summary>Screen edge the phone sits on: right, left, top or bottom.</summary>
        public string Edge
        {
            get => _edge;
            set
            {
                string normalized = (value ?? "right").Trim().ToLowerInvariant();
                if (normalized != "left" && normalized != "top" && normalized != "bottom")
                {
                    normalized = "right";
                }

                if (_edge == normalized)
                {
                    return;
                }

                _edge = normalized;
                Invalidate();
            }
        }

        public bool IsConnected
        {
            get => _connected;
            set { if (_connected != value) { _connected = value; Invalidate(); } }
        }

        public bool RoutingToPhone
        {
            get => _routingToPhone;
            set { if (_routingToPhone != value) { _routingToPhone = value; Invalidate(); } }
        }

        /// <summary>
        /// K = 1 at 96 dpi. The panel is scaled by the form's auto-scaling, but the tiles are
        /// drawn here in device pixels, so every measurement below is multiplied by this factor:
        /// otherwise the diagram keeps its 100% size inside a 125%-scaled card.
        /// </summary>
        private float Scale => DeviceDpi / 96f;

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            using (var background = new SolidBrush(Theme.Surface))
            {
                g.FillRectangle(background, ClientRectangle);
            }

            float s = Scale;
            int macWidth = (int)Math.Round(144 * s);
            int macHeight = (int)Math.Round(90 * s);
            int phoneLong = (int)Math.Round(70 * s);
            int phoneShort = (int)Math.Round(38 * s);
            int gap = (int)Math.Round(12 * s);

            bool verticalPhone = _edge == "left" || _edge == "right";
            Size phone = verticalPhone ? new Size(phoneShort, phoneLong) : new Size(phoneLong, phoneShort);

            int groupWidth = verticalPhone ? macWidth + gap + phone.Width : Math.Max(macWidth, phone.Width);
            int groupHeight = verticalPhone ? Math.Max(macHeight, phone.Height) : macHeight + gap + phone.Height;

            int originX = (ClientSize.Width - groupWidth) / 2;
            int originY = (ClientSize.Height - groupHeight) / 2;

            Rectangle mac;
            Rectangle phoneRect;
            switch (_edge)
            {
                case "left":
                    mac = new Rectangle(originX + phone.Width + gap, originY + (groupHeight - macHeight) / 2, macWidth, macHeight);
                    phoneRect = new Rectangle(originX, originY + (groupHeight - phone.Height) / 2, phone.Width, phone.Height);
                    break;
                case "top":
                    phoneRect = new Rectangle(originX + (groupWidth - phone.Width) / 2, originY, phone.Width, phone.Height);
                    mac = new Rectangle(originX + (groupWidth - macWidth) / 2, originY + phone.Height + gap, macWidth, macHeight);
                    break;
                case "bottom":
                    mac = new Rectangle(originX + (groupWidth - macWidth) / 2, originY, macWidth, macHeight);
                    phoneRect = new Rectangle(originX + (groupWidth - phone.Width) / 2, originY + macHeight + gap, phone.Width, phone.Height);
                    break;
                default:
                    mac = new Rectangle(originX, originY + (groupHeight - macHeight) / 2, macWidth, macHeight);
                    phoneRect = new Rectangle(originX + macWidth + gap, originY + (groupHeight - phone.Height) / 2, phone.Width, phone.Height);
                    break;
            }

            bool pcActive = _connected && !_routingToPhone;
            bool phoneActive = _connected && _routingToPhone;

            PaintMac(g, mac, pcActive, s);
            PaintPhone(g, phoneRect, phoneActive, _connected, s);
        }

        private static void PaintMac(Graphics g, Rectangle bounds, bool active, float s)
        {
            Color fill = active ? Blend(Theme.Accent, Theme.Surface, 0.82f) : Theme.SurfaceRaised;
            Color border = active ? Theme.Accent : Theme.Border;
            Theme.FillRounded(g, bounds, (int)Math.Round(10 * s), fill, border);

            // Screen, then the hinge bar, then the caption along the bottom edge of the tile.
            Rectangle screen = new(
                bounds.X + (int)Math.Round(16 * s),
                bounds.Y + (int)Math.Round(12 * s),
                bounds.Width - (int)Math.Round(32 * s),
                bounds.Height - (int)Math.Round(40 * s));
            Theme.FillRounded(g, screen, (int)Math.Round(5 * s),
                active ? Color.FromArgb(38, 255, 255, 255) : Theme.SurfaceMuted, null);

            Rectangle hinge = new(
                bounds.X + (int)Math.Round(8 * s),
                screen.Bottom + (int)Math.Round(5 * s),
                bounds.Width - (int)Math.Round(16 * s),
                Math.Max(2, (int)Math.Round(3 * s)));
            Theme.FillRounded(g, hinge, 2, active ? Theme.Accent : Theme.BorderStrong, null);

            var label = new Rectangle(
                bounds.X + (int)Math.Round(4 * s),
                bounds.Bottom - (int)Math.Round(26 * s),
                bounds.Width - (int)Math.Round(8 * s),
                (int)Math.Round(20 * s));
            TextRenderer.DrawText(g, "This PC", Theme.Caption, label,
                active ? Theme.TextPrimary : Theme.TextTertiary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            if (active)
            {
                FillDot(g, bounds.Right - (int)Math.Round(14 * s), bounds.Top + (int)Math.Round(14 * s), s);
            }
        }

        private static void PaintPhone(Graphics g, Rectangle bounds, bool active, bool connected, float s)
        {
            Color fill = active ? Blend(Theme.Accent, Theme.Surface, 0.82f)
                       : connected ? Theme.SurfaceRaised
                       : Blend(Theme.SurfaceRaised, Theme.Surface, 0.5f);
            Color border = active ? Theme.Accent : connected ? Theme.BorderStrong : Theme.Border;

            Theme.FillRounded(g, bounds, (int)Math.Round(8 * s), fill, border);

            bool vertical = bounds.Height >= bounds.Width;
            int inset = (int)Math.Round(7 * s);
            Rectangle speaker = vertical
                ? new Rectangle(bounds.X + bounds.Width / 2 - (int)Math.Round(6 * s), bounds.Y + inset, (int)Math.Round(12 * s), Math.Max(2, (int)Math.Round(2 * s)))
                : new Rectangle(bounds.X + inset, bounds.Y + bounds.Height / 2 - (int)Math.Round(6 * s), Math.Max(2, (int)Math.Round(2 * s)), (int)Math.Round(12 * s));
            Theme.FillRounded(g, speaker, 1,
                active ? Theme.Accent : connected ? Theme.BorderStrong : Theme.TextTertiary, null);

            if (active)
            {
                FillDot(g, bounds.Right - (int)Math.Round(12 * s), bounds.Top + (int)Math.Round(12 * s), s);
            }
        }

        private static void FillDot(Graphics g, int centerX, int centerY, float s)
        {
            int size = Math.Max(4, (int)Math.Round(6 * s));
            using var brush = new SolidBrush(Theme.TextPrimary);
            g.FillEllipse(brush, centerX - size / 2, centerY - size / 2, size, size);
        }

        /// <summary>Mixes <paramref name="fore"/> over <paramref name="back"/> at the given share.</summary>
        private static Color Blend(Color fore, Color back, float amount)
        {
            amount = Math.Max(0f, Math.Min(1f, amount));
            return Color.FromArgb(
                (int)Math.Round((fore.R * amount) + (back.R * (1 - amount))),
                (int)Math.Round((fore.G * amount) + (back.G * (1 - amount))),
                (int)Math.Round((fore.B * amount) + (back.B * (1 - amount))));
        }
    }
}
