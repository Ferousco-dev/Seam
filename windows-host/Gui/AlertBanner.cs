using System;
using System.Drawing;
using System.Windows.Forms;

namespace Seam.WindowsHost.Gui
{
    /// <summary>
    /// Dismissible inline message used for unauthorised devices and connection errors.
    /// Lives in the layout flow, so the window never hides the reason behind a modal dialog.
    /// </summary>
    internal sealed class AlertBanner : BufferedPanel
    {
        private readonly Label _message;
        private readonly Label _icon;
        private readonly PillButton _dismiss;

        public AlertBanner()
        {
            // Children are created *before* any bound of this panel changes.
            // Why: WinForms flushes a pending layout from OnResize on the first bounds change
            // of a new control, and OnLayout positions exactly these three children. Setting
            // Height first therefore ran OnLayout while they were still null (NullReferenceException).
            _icon = Theme.MakeLabel("!", Theme.BodyStrong, Theme.StatusBusy, ContentAlignment.MiddleCenter);
            _icon.AutoSize = false;
            _icon.Width = 22;
            _icon.Height = 22;
            _icon.Dock = DockStyle.None;
            _icon.Paint += (_, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var brush = new SolidBrush(_icon.ForeColor);
                e.Graphics.FillEllipse(brush, 0, 0, _icon.Width - 1, _icon.Height - 1);
                TextRenderer.DrawText(e.Graphics, "!", Theme.BodyStrong,
                    new Rectangle(0, 0, _icon.Width, _icon.Height - 1), Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            };

            _message = Theme.MakeLabel(string.Empty, Theme.Body, Theme.TextPrimary, ContentAlignment.MiddleLeft);

            _dismiss = new PillButton { Kind = ButtonKind.Ghost, Text = "Dismiss", Font = Theme.Small, Height = 26, Width = 72 };

            Controls.Add(_message);
            Controls.Add(_icon);
            Controls.Add(_dismiss);
            _dismiss.Click += (_, _) => HideBanner();

            // Bounds and colours last, once every child exists: this is the first statement
            // that can trigger OnLayout.
            Height = 48;
            Visible = false;
            BackColor = Theme.StatusBusySoft;
            Padding = new Padding(12, 0, 8, 0);
        }

        public event Action? Dismissed;

        public void ShowError(string message)
        {
            _icon.ForeColor = Theme.StatusError;
            BackColor = Theme.StatusErrorSoft;
            _message.ForeColor = Theme.TextPrimary;
            SetMessage(message);
        }

        public void ShowWarning(string message)
        {
            _icon.ForeColor = Theme.StatusBusy;
            BackColor = Theme.StatusBusySoft;
            _message.ForeColor = Theme.TextPrimary;
            SetMessage(message);
        }

        private void SetMessage(string message)
        {
            Text = message ?? string.Empty;
            _message.Text = Text;
            Visible = !string.IsNullOrWhiteSpace(Text);
            PerformLayout();
        }

        public void HideBanner()
        {
            Visible = false;
            Text = string.Empty;
            _message.Text = string.Empty;
            Dismissed?.Invoke();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);

            // Defensive: layout can fire at any time, including from a bounds change the base
            // constructor makes before this constructor has assigned the children. There is
            // nothing to place until all three exist.
            if (_icon is null || _dismiss is null || _message is null)
            {
                return;
            }

            int pad = 12;
            _icon.SetBounds(pad + 2, (Height - _icon.Height) / 2, _icon.Width, _icon.Height);
            _dismiss.SetBounds(Width - _dismiss.Width - 8, (Height - _dismiss.Height) / 2, _dismiss.Width, _dismiss.Height);
            int textLeft = _icon.Right + 10;
            _message.SetBounds(textLeft, 4, Math.Max(40, _dismiss.Left - 10 - textLeft), Height - 8);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(Color.FromArgb(40, Theme.TextPrimary), 1f);
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }
}
