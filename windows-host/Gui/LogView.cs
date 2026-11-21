using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace Seam.WindowsHost.Gui
{
    internal enum LogLevel
    {
        Info,
        Good,
        Warn,
        Error
    }

    internal sealed class LogEntry
    {
        public DateTime Time { get; init; } = DateTime.Now;
        public string Text { get; init; } = string.Empty;
        public LogLevel Level { get; init; } = LogLevel.Info;

        public override string ToString() => $"{Time:HH:mm:ss}  {Text}";
    }

    /// <summary>
    /// Activity list: monospace timestamp, message coloured by severity, newest at the bottom,
    /// bounded so a long session cannot grow without limit.
    /// </summary>
    internal sealed class LogView : ListBox
    {
        private int _maxEntries = 500;
        private bool _followTail = true;

        /// <summary>When true (the default) new lines scroll the view to the newest entry.</summary>
        public bool FollowTail
        {
            get => _followTail;
            set
            {
                _followTail = value;
                if (value)
                {
                    ScrollToEnd();
                }
            }
        }

        public LogView()
        {
            DrawMode = DrawMode.OwnerDrawFixed;
            BorderStyle = BorderStyle.None;
            IntegralHeight = false;
            SelectionMode = SelectionMode.None;
            BackColor = Theme.Surface;
            ForeColor = Theme.TextPrimary;
            Font = Theme.Body;
            ItemHeight = Math.Max(18, (int)Math.Round(19 * (DeviceDpi / 96f)));
            HorizontalScrollbar = false;
            TabStop = false;
        }

        public int MaxEntries
        {
            get => _maxEntries;
            set => _maxEntries = Math.Max(50, value);
        }

        public void Append(LogEntry entry)
        {
            if (IsDisposed)
            {
                return;
            }

            Items.Add(entry);
            while (Items.Count > _maxEntries)
            {
                Items.RemoveAt(0);
            }

            if (_followTail)
            {
                ScrollToEnd();
            }
        }

        private void ScrollToEnd()
        {
            if (Items.Count == 0 || IsDisposed)
            {
                return;
            }

            TopIndex = Math.Max(0, Items.Count - (ClientSize.Height / Math.Max(1, ItemHeight)) + 1);
        }

        public void Append(string text, LogLevel level) =>
            Append(new LogEntry { Text = text, Level = level });

        public string AllText()
        {
            var sb = new StringBuilder();
            foreach (object item in Items)
            {
                if (item is LogEntry entry)
                {
                    sb.AppendLine(entry.ToString());
                }
            }
            return sb.ToString();
        }

        private static Color ColorForLevel(LogLevel level) => level switch
        {
            LogLevel.Good => Theme.StatusConnected,
            LogLevel.Warn => Theme.StatusBusy,
            LogLevel.Error => Theme.StatusError,
            _ => Theme.TextPrimary
        };

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= Items.Count)
            {
                return;
            }

            using (var background = new SolidBrush(Theme.Surface))
            {
                e.Graphics.FillRectangle(background, e.Bounds);
            }

            if (Items[e.Index] is not LogEntry entry)
            {
                return;
            }

            int pad = (int)Math.Round(12 * (DeviceDpi / 96f));
            var timeRect = new Rectangle(e.Bounds.X + pad, e.Bounds.Y, (int)Math.Round(58 * (DeviceDpi / 96f)), e.Bounds.Height);
            var textRect = new Rectangle(timeRect.Right, e.Bounds.Y, Math.Max(10, e.Bounds.Right - timeRect.Right - pad), e.Bounds.Height);

            TextRenderer.DrawText(e.Graphics, entry.Time.ToString("HH:mm:ss"), Theme.Mono, timeRect,
                Theme.TextTertiary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            TextRenderer.DrawText(e.Graphics, entry.Text, Theme.Body, textRect,
                ColorForLevel(entry.Level),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
                TextFormatFlags.EndEllipsis);
        }
    }
}
