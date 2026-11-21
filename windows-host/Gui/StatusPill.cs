using System;
using System.Drawing;
using System.Windows.Forms;
using Seam.WindowsHost.Host;

namespace Seam.WindowsHost.Gui
{
    /// <summary>
    /// Connection status pill: coloured dot + label on a soft tinted capsule.
    /// The label text is what a user reads; the colour is a secondary cue only.
    /// </summary>
    internal sealed class StatusPill : BufferedPanel
    {
        private SessionState _state = SessionState.Disconnected;
        private string _text = "Not connected";

        public StatusPill()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Height = 26;
            Width = 128;
            Font = Theme.BodyStrong;
            DoubleBuffered = true;
        }

        public SessionState State
        {
            get => _state;
            set { _state = value; Invalidate(); }
        }

        public string Label
        {
            get => _text;
            set
            {
                _text = value ?? string.Empty;
                Width = PreferredWidth();
                Invalidate();
            }
        }

        public static string LabelFor(SessionState state) => state switch
        {
            SessionState.Connected => "Connected",
            SessionState.Connecting => "Connecting...",
            SessionState.Unauthorized => "Unauthorized",
            SessionState.PairingRequired => "Waiting for pairing",
            SessionState.Error => "Connection error",
            _ => "Not connected"
        };

        public static Color ColorFor(SessionState state) => state switch
        {
            SessionState.Connected => Theme.StatusConnected,
            SessionState.Connecting => Theme.StatusBusy,
            SessionState.Unauthorized => Theme.StatusBusy,
            SessionState.PairingRequired => Theme.StatusBusy,
            SessionState.Error => Theme.StatusError,
            _ => Theme.StatusIdle
        };

        public static Color SoftColorFor(SessionState state) => state switch
        {
            SessionState.Connected => Theme.StatusConnectedSoft,
            SessionState.Connecting => Theme.StatusBusySoft,
            SessionState.Unauthorized => Theme.StatusBusySoft,
            SessionState.PairingRequired => Theme.StatusBusySoft,
            SessionState.Error => Theme.StatusErrorSoft,
            _ => Theme.StatusIdleSoft
        };

        private int PreferredWidth()
        {
            Size text = TextRenderer.MeasureText(_text, Font);
            return text.Width + 40;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.FillRounded(g, bounds, Height / 2, SoftColorFor(_state));

            int dot = 7;
            using (var dotBrush = new SolidBrush(ColorFor(_state)))
            {
                g.FillEllipse(dotBrush, 11, (Height - dot) / 2, dot, dot);
            }

            TextRenderer.DrawText(g, _text, Font,
                new Rectangle(24, 0, Width - 30, Height), ColorFor(_state),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
