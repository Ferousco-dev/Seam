using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Seam.WindowsHost.Gui
{
    /// <summary>
    /// Colours, fonts and small drawing helpers. One place for the visual language so the
    /// window, the pages and the custom controls cannot drift apart.
    ///
    /// The palette is the SEAM graphite identity taken from the macOS app icon
    /// (a <c>#292A2E → #09090B</c> gradient): a dark workspace, a small ramp of surface
    /// levels, one restrained blue for "the pointer is here" states, and green/amber/red
    /// only where they mean connected / attention / failure.
    /// </summary>
    internal static class Theme
    {
        // ---- surfaces: four steps of graphite, each one clearly lighter than the last ----
        public static readonly Color Background = Color.FromArgb(0x16, 0x17, 0x1A);
        public static readonly Color NavBackground = Color.FromArgb(0x11, 0x12, 0x15);
        public static readonly Color Surface = Color.FromArgb(0x1E, 0x1F, 0x24);
        public static readonly Color SurfaceMuted = Color.FromArgb(0x23, 0x25, 0x2A);
        public static readonly Color SurfaceRaised = Color.FromArgb(0x27, 0x29, 0x2F);
        public static readonly Color Border = Color.FromArgb(0x2C, 0x2E, 0x35);
        public static readonly Color BorderStrong = Color.FromArgb(0x3A, 0x3D, 0x46);

        // ---- text ----
        public static readonly Color TextPrimary = Color.FromArgb(0xF3, 0xF4, 0xF6);
        public static readonly Color TextSecondary = Color.FromArgb(0xA6, 0xAB, 0xB5);
        public static readonly Color TextTertiary = Color.FromArgb(0x7A, 0x7F, 0x8A);

        // ---- accent: one blue, used for selection and for the device that owns the cursor ----
        public static readonly Color Accent = Color.FromArgb(0x4C, 0x8D, 0xFF);
        public static readonly Color AccentHover = Color.FromArgb(0x6A, 0xA0, 0xFF);
        public static readonly Color AccentPressed = Color.FromArgb(0x3B, 0x79, 0xE3);
        public static readonly Color AccentSoft = Color.FromArgb(0x1C, 0x29, 0x42);
        public static readonly Color AccentOnDark = Color.FromArgb(0x0A, 0x14, 0x24);

        // ---- status ----
        public static readonly Color StatusConnected = Color.FromArgb(0x46, 0xB8, 0x5B);
        public static readonly Color StatusConnectedSoft = Color.FromArgb(0x16, 0x30, 0x1E);
        public static readonly Color StatusBusy = Color.FromArgb(0xD8, 0xA0, 0x2A);
        public static readonly Color StatusBusySoft = Color.FromArgb(0x32, 0x28, 0x0F);
        public static readonly Color StatusError = Color.FromArgb(0xF0, 0x63, 0x5B);
        public static readonly Color StatusErrorSoft = Color.FromArgb(0x38, 0x20, 0x1E);
        public static readonly Color StatusIdle = Color.FromArgb(0x8A, 0x8F, 0x99);
        public static readonly Color StatusIdleSoft = Color.FromArgb(0x23, 0x25, 0x29);

        // ---- typography: Segoe UI Variable where the OS has it, Segoe UI otherwise ----
        public static readonly string TextFamily = PickFamily("Segoe UI Variable Text", "Segoe UI");
        public static readonly string DisplayFamily = PickFamily("Segoe UI Variable Display", TextFamily);
        public static readonly string GlyphFamily = PickFamily("Segoe Fluent Icons", "Segoe MDL2 Assets", "Segoe UI Symbol");

        public static readonly Font Body = new(TextFamily, 9.75F, FontStyle.Regular, GraphicsUnit.Point);
        public static readonly Font BodyStrong = new(TextFamily, 9.75F, FontStyle.Bold, GraphicsUnit.Point);
        public static readonly Font Small = new(TextFamily, 9F, FontStyle.Regular, GraphicsUnit.Point);
        public static readonly Font Caption = new(TextFamily, 8.25F, FontStyle.Regular, GraphicsUnit.Point);
        public static readonly Font Section = new(TextFamily, 8F, FontStyle.Bold, GraphicsUnit.Point);
        public static readonly Font Heading = new(DisplayFamily, 12F, FontStyle.Bold, GraphicsUnit.Point);
        public static readonly Font PageTitle = new(DisplayFamily, 18F, FontStyle.Regular, GraphicsUnit.Point);
        public static readonly Font Title = new(DisplayFamily, 15F, FontStyle.Bold, GraphicsUnit.Point);
        public static readonly Font Mono = new("Consolas", 9F, FontStyle.Regular, GraphicsUnit.Point);
        public static readonly Font Glyphs = new(GlyphFamily, 11F, FontStyle.Regular, GraphicsUnit.Point);

        /// <summary>Returns the first installed family from <paramref name="candidates"/>.</summary>
        private static string PickFamily(params string[] candidates)
        {
            var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FontFamily family in FontFamily.Families)
            {
                installed.Add(family.Name);
            }

            foreach (string candidate in candidates)
            {
                if (installed.Contains(candidate))
                {
                    return candidate;
                }
            }

            return "Segoe UI";
        }

        /// <summary>Rounded rectangle path; radius is clamped so tiny controls still render.</summary>
        public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            int r = Math.Max(0, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2));
            if (r == 0)
            {
                path.AddRectangle(bounds);
                return path;
            }

            int d = r * 2;
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void FillRounded(Graphics g, Rectangle bounds, int radius, Color fill, Color? border = null)
        {
            using GraphicsPath path = RoundedRect(bounds, radius);
            using var brush = new SolidBrush(fill);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillPath(brush, path);
            if (border.HasValue)
            {
                using var pen = new Pen(border.Value, 1f);
                g.DrawPath(pen, path);
            }
        }

        public static Label MakeLabel(string text, Font font, Color color, ContentAlignment align = ContentAlignment.TopLeft)
        {
            return new Label
            {
                Text = text,
                Font = font,
                ForeColor = color,
                AutoSize = false,
                TextAlign = align,
                BackColor = Color.Transparent
            };
        }

        /// <summary>Small all-caps section label used at the top of every card.</summary>
        public static Label SectionLabel(string text)
        {
            Label label = MakeLabel(text, Section, TextTertiary, ContentAlignment.MiddleLeft);
            label.Dock = DockStyle.Fill;
            return label;
        }
    }

    /// <summary>Panel with double buffering on, so custom painting does not flicker.</summary>
    internal class BufferedPanel : Panel
    {
        public BufferedPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
            BackColor = Theme.Surface;
        }
    }

    /// <summary>
    /// A content card: one surface level above the page, a 1px border, and a soft 8px corner.
    /// Children that paint their own background use the same colour as the card, so the
    /// corners stay clean without clipping every child control.
    /// </summary>
    internal sealed class CardPanel : BufferedPanel
    {
        public CardPanel()
        {
            BackColor = Theme.Surface;
            Padding = new Padding(1);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Rectangle bounds = new(0, 0, Width - 1, Height - 1);
            Theme.FillRounded(e.Graphics, bounds, 8, Theme.Surface, Theme.Border);
        }
    }
}
