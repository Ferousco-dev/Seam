using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Seam.WindowsHost.Bootstrap;
using Seam.WindowsHost.Host;

namespace Seam.WindowsHost.Gui
{
    /// <summary>
    /// The SEAM desktop window.
    ///
    /// Shell: a compact navigation rail (Home / Activity / Settings / Diagnostics) and one page
    /// host, so Activity and Diagnostics no longer compete with the connection controls for
    /// space. It is a thin view over <see cref="SessionController"/>: every connection,
    /// bootstrap and input-routing action still goes through the controller, which uses the same
    /// primitives the hardware-verified console mode uses. No protocol or control logic lives
    /// here — the window only renders state and forwards clicks.
    /// </summary>
    internal sealed class MainForm : Form
    {
        private readonly UiSettings _settings;
        private readonly SessionController _session;
        private readonly bool _interactive;

        private readonly System.Windows.Forms.Timer _hookTimer = new() { Interval = 250 };
        private readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = 2500 };

        private bool _polling;
        private bool _wirelessBusy;
        private bool _shuttingDown;
        private bool _diagnosticsRequested;
        private SessionState _lastState = SessionState.Disconnected;
        private AdbDevice? _device;
        private string? _autoConnectSerial;

        // ---- shell ----
        private readonly NavRail _nav = new();
        private readonly Panel _pageHost = new();
        private readonly StatusPill _pill = new();
        private readonly Label _lblRailVersion = Theme.MakeLabel(string.Empty, Theme.Caption, Theme.TextTertiary, ContentAlignment.MiddleLeft);

        // ---- home ----
        private readonly ArrangementPanel _arrangement = new();
        private readonly PillButton _btnConnect = new();
        private readonly PillButton _btnDisconnect = new();
        private readonly PillButton _btnRefresh = new();
        private readonly PillButton _btnToggleRouting = new();
        private readonly Label _lblStateTitle = Theme.MakeLabel(string.Empty, Theme.Title, Theme.TextPrimary, ContentAlignment.MiddleLeft);
        private readonly Label _lblStateDetail = Theme.MakeLabel(string.Empty, Theme.Small, Theme.TextSecondary, ContentAlignment.TopLeft);
        private readonly Label _lblDeviceMeta = Theme.MakeLabel(string.Empty, Theme.Mono, Theme.TextTertiary, ContentAlignment.MiddleLeft);
        private readonly Label _lblArrangementCaption = Theme.MakeLabel(string.Empty, Theme.Caption, Theme.TextTertiary, ContentAlignment.MiddleCenter);
        private readonly Label _lblRoutingOwner = Theme.MakeLabel("Windows", Theme.Heading, Theme.TextPrimary, ContentAlignment.MiddleLeft);
        private readonly Label _lblRoutingHint = Theme.MakeLabel(string.Empty, Theme.Small, Theme.TextSecondary, ContentAlignment.TopLeft);
        private readonly Label _lblStats = Theme.MakeLabel(string.Empty, Theme.Mono, Theme.TextTertiary, ContentAlignment.MiddleLeft);
        private readonly Label _lblShortcut = Theme.MakeLabel("Ctrl + Alt + Q returns control to Windows", Theme.Caption, Theme.TextTertiary, ContentAlignment.MiddleLeft);
        private readonly AlertBanner _banner = new();

        // ---- activity ----
        private readonly LogView _log = new();
        private readonly Label _logPlaceholder = Theme.MakeLabel(
            "Connection activity will appear here.",
            Theme.Body, Theme.TextTertiary, ContentAlignment.MiddleCenter);
        private readonly PillButton _btnCopyLog = new();
        private readonly PillButton _btnClearLog = new();
        private readonly CheckBox _chkFollow = new();

        // ---- settings ----
        private readonly ComboBox _edge = new();
        private readonly CheckBox _routingOnConnect = new();
        private readonly CheckBox _autoConnect = new();
        private readonly NumericUpDown _maxLines = new();
        private readonly PillButton _btnSaveSettings = new();
        private readonly Label _lblSettingsStatus = Theme.MakeLabel(string.Empty, Theme.Caption, Theme.TextTertiary, ContentAlignment.MiddleLeft);
        private readonly Label _lblAppInfo = Theme.MakeLabel(string.Empty, Theme.Mono, Theme.TextTertiary, ContentAlignment.TopLeft);

        // ---- diagnostics ----
        private readonly TextBox _diagOutput = new();
        private readonly Label _diagStatus = Theme.MakeLabel(string.Empty, Theme.Caption, Theme.TextTertiary, ContentAlignment.MiddleLeft);
        private readonly PillButton _btnCopyDiag = new();
        private readonly PillButton _btnRefreshDiag = new();

        public MainForm(UiSettings settings, bool interactive)
        {
            _settings = settings ?? new UiSettings();
            _settings.Normalize();
            _interactive = interactive;
            _log.MaxEntries = _settings.MaxLogLines;
            _session = new SessionController(_settings);

            SuspendLayout();
            Text = "SEAM";
            BackColor = Theme.Background;
            ForeColor = Theme.TextPrimary;
            Font = Theme.Body;
            // The window is laid out at 100% for a 1040x700 client. AutoScaleDimensions must say
            // so: without it a code-built form is created with AutoScaleDimensions equal to the
            // *current* DPI, which means "this layout was designed here" and nothing is scaled -
            // while point-sized fonts still grow with the DPI. On a 125% display that left the
            // identity block and the page heading clipped by their own boxes.
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1040, 700);
            MinimumSize = new Size(940, 640);
            DoubleBuffered = true;
            ApplyIcon();
            DarkTitleBar.Apply(this);

            BuildUi();
            NameControls();
            LoadSettingsIntoPage();
            WireEvents();

            _session.Initialize();
            ApplyState(_session.State, _session.Detail);
            ApplyDevices(_session.Devices);
            AppendLog("SEAM " + Host.DiagnosticsReport.VersionString + " ready.", LogLevel.Good);
            AppendLog($"Display: {DeviceDpi} dpi, layout scaled {AutoScaleFactor.Width:0.##}x " +
                      $"(window {Width}x{Height}, rail 216, text {Theme.TextFamily}).", LogLevel.Info);

            if (_interactive)
            {
                _hookTimer.Tick += (_, _) => OnHookTick();
                _pollTimer.Tick += (_, _) => { RefreshDevicesAsync(); TickWirelessAsync(); };
                _hookTimer.Start();
                _pollTimer.Start();
                Shown += (_, _) => RefreshDevicesAsync();
            }

            ResumeLayout(true);
        }

        /// <summary>
        /// Stable names for the controls the CI layout dump reports on. They double as the
        /// identifiers used by accessibility tooling, so they are worth having anyway.
        /// </summary>
        private void NameControls()
        {
            _nav.Name = "nav";
            _pill.Name = "statusPill";
            _btnConnect.Name = "connect";
            _btnDisconnect.Name = "disconnect";
            _btnRefresh.Name = "refresh";
            _btnToggleRouting.Name = "toggleRouting";
            _arrangement.Name = "arrangement";
            _lblStateTitle.Name = "heroTitle";
            _lblStateDetail.Name = "heroDetail";
            _lblRoutingOwner.Name = "routingOwner";
            _log.Name = "activityLog";
            _chkFollow.Name = "followToggle";
            _btnCopyLog.Name = "copyLog";
            _btnClearLog.Name = "clearLog";
            _edge.Name = "edgeSelector";
            _autoConnect.Name = "autoConnect";
            _routingOnConnect.Name = "routingOnConnect";
            _maxLines.Name = "linesKept";
            _btnSaveSettings.Name = "saveSettings";
            _diagOutput.Name = "diagOutput";
            _btnCopyDiag.Name = "copyReport";
            _banner.Name = "banner";
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            // Too tall for the display (large text scaling, small laptop screen): the layout is
            // all percent-based below the header, so it compresses instead of running off-screen.
            Rectangle work = Screen.FromControl(this).WorkingArea;
            int maxWidth = Math.Max(880, work.Width - 40);
            int maxHeight = Math.Max(600, work.Height - 60);
            if (Width > maxWidth || Height > maxHeight)
            {
                Size = new Size(Math.Min(Width, maxWidth), Math.Min(Height, maxHeight));
                Location = new Point(
                    work.Left + Math.Max(0, (work.Width - Width) / 2),
                    work.Top + Math.Max(0, (work.Height - Height) / 2));
            }
        }

        private void ApplyIcon()
        {
            try
            {
                Icon? icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (icon != null)
                {
                    Icon = icon;
                }
            }
            catch
            {
                // The embedded icon is a nicety; a missing one must not stop the window.
            }
        }

        // ---- layout ------------------------------------------------------------------

        private void BuildUi()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Background,
                ColumnCount = 2,
                RowCount = 1,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 216f));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            root.Controls.Add(BuildRail(), 0, 0);
            root.Controls.Add(BuildPageArea(), 1, 0);
            Controls.Add(root);
        }

        private Control BuildRail()
        {
            var rail = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.NavBackground,
                ColumnCount = 1,
                RowCount = 4,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            rail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 96f));  // identity
            rail.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));  // navigation
            rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 44f));  // connection status
            rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 44f));  // version

            rail.Controls.Add(BuildRailIdentity(), 0, 0);
            _nav.Dock = DockStyle.Fill;
            _nav.Margin = Padding.Empty;
            rail.Controls.Add(_nav, 0, 1);

            _pill.Dock = DockStyle.None;
            _pill.Anchor = AnchorStyles.Left;
            _pill.State = SessionState.Disconnected;
            _pill.Label = StatusPill.LabelFor(SessionState.Disconnected);
            var pillHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.NavBackground, Margin = Padding.Empty };
            pillHost.Controls.Add(_pill);
            pillHost.Resize += (_, _) => _pill.Location = new Point(20, Math.Max(0, (pillHost.Height - _pill.Height) / 2));
            rail.Controls.Add(pillHost, 0, 2);

            _lblRailVersion.Dock = DockStyle.Fill;
            _lblRailVersion.AutoEllipsis = true;
            _lblRailVersion.Padding = new Padding(22, 0, 8, 10);
            rail.Controls.Add(_lblRailVersion, 0, 3);

            rail.Paint += (_, e) =>
            {
                using var pen = new Pen(Theme.Border, 1f);
                e.Graphics.DrawLine(pen, rail.Width - 1, 0, rail.Width - 1, rail.Height);
            };

            return rail;
        }

        private Control BuildRailIdentity()
        {
            var identity = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.NavBackground,
                ColumnCount = 2,
                RowCount = 2,
                Margin = Padding.Empty,
                Padding = new Padding(20, 20, 14, 8)
            };
            identity.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36f));
            identity.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            identity.RowStyles.Add(new RowStyle(SizeType.Absolute, 32f));
            identity.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            var logo = new PictureBox
            {
                Size = new Size(28, 28),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Transparent,
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
                Margin = new Padding(0, 1, 8, 0)
            };
            try
            {
                Icon? icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (icon != null)
                {
                    logo.Image = icon.ToBitmap();
                }
            }
            catch
            {
                // Rendered as an empty square instead; not worth failing the window over.
            }

            var wordmark = Theme.MakeLabel("SEAM", Theme.Title, Theme.TextPrimary, ContentAlignment.MiddleLeft);
            wordmark.AutoSize = true;
            wordmark.Margin = new Padding(0, 0, 0, 0);

            var tagline = Theme.MakeLabel("Mouse & keyboard sharing", Theme.Caption, Theme.TextTertiary, ContentAlignment.TopLeft);
            tagline.AutoSize = true;
            tagline.AutoEllipsis = true;
            tagline.Margin = new Padding(1, 2, 0, 0);

            identity.Controls.Add(logo, 0, 0);
            identity.Controls.Add(wordmark, 1, 0);
            identity.Controls.Add(tagline, 1, 1);
            return identity;
        }

        private Control BuildPageArea()
        {
            var area = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Background,
                ColumnCount = 1,
                RowCount = 2,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            area.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            area.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            area.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            _banner.Dock = DockStyle.Top;
            _banner.Margin = new Padding(28, 18, 28, 0);
            area.Controls.Add(_banner, 0, 0);

            _pageHost.Dock = DockStyle.Fill;
            _pageHost.BackColor = Theme.Background;
            _pageHost.Margin = Padding.Empty;
            _pageHost.Controls.Add(BuildHomePage());
            _pageHost.Controls.Add(BuildActivityPage());
            _pageHost.Controls.Add(BuildSettingsPage());
            _pageHost.Controls.Add(BuildDiagnosticsPage());
            area.Controls.Add(_pageHost, 0, 1);

            ShowPage(GuiPage.Home);
            return area;
        }

        /// <summary>Switches the visible page and refreshes anything that is loaded lazily.</summary>
        internal void ShowPage(GuiPage page)
        {
            _nav.Selected = page;
            foreach (Control control in _pageHost.Controls)
            {
                if (control.Tag is GuiPage tag)
                {
                    control.Visible = tag == page;
                }
            }

            if (page == GuiPage.Settings)
            {
                LoadSettingsIntoPage();
            }

            if (page == GuiPage.Diagnostics)
            {
                LoadDiagnosticsAsync();
            }

            _pageHost.PerformLayout();
        }

        internal GuiPage CurrentPage => _nav.Selected;

        /// <summary>
        /// Builds the title/description block every page starts with. The block measures itself
        /// (AutoSize rows, AutoSize labels) so it can never clip its own text at a different DPI
        /// or with a longer translation - the failure the 125% screenshot showed on Home.
        /// </summary>
        private static TableLayoutPanel PageHeader(string title, string description, out FlowLayoutPanel actions)
        {
            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Theme.Background,
                ColumnCount = 2,
                RowCount = 1,
                Margin = Padding.Empty,
                Padding = new Padding(28, 20, 28, 12)
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var text = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Theme.Background,
                ColumnCount = 1,
                RowCount = 2,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            text.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            text.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            text.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var titleLabel = Theme.MakeLabel(title, Theme.PageTitle, Theme.TextPrimary, ContentAlignment.MiddleLeft);
            titleLabel.AutoSize = true;
            titleLabel.Margin = new Padding(0, 0, 0, 2);

            var descriptionLabel = Theme.MakeLabel(description, Theme.Small, Theme.TextSecondary, ContentAlignment.MiddleLeft);
            descriptionLabel.AutoSize = true;
            descriptionLabel.Margin = Padding.Empty;

            text.Controls.Add(titleLabel, 0, 0);
            text.Controls.Add(descriptionLabel, 0, 1);

            actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Theme.Background,
                Margin = Padding.Empty,
                Padding = new Padding(12, 6, 0, 0)
            };

            header.Controls.Add(text, 0, 0);
            header.Controls.Add(actions, 1, 0);
            return header;
        }

        private Control BuildHomePage()
        {
            var page = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Background,
                ColumnCount = 1,
                RowCount = 2,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                Tag = GuiPage.Home
            };
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            page.Controls.Add(PageHeader("Home", "Share one mouse and keyboard with your Android phone.", out _), 0, 0);

            var content = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Background,
                ColumnCount = 1,
                RowCount = 4,
                Margin = Padding.Empty,
                Padding = new Padding(28, 2, 28, 24)
            };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 150f));   // status hero
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 16f));    // gap
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));    // arrangement + input sharing
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 26f));    // shortcut hint

            content.Controls.Add(BuildStatusCard(), 0, 0);

            var cards = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Background,
                ColumnCount = 3,
                RowCount = 1,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340f));
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 16f));
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            cards.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            cards.Controls.Add(BuildArrangementCard(), 0, 0);
            cards.Controls.Add(BuildRoutingCard(), 2, 0);
            content.Controls.Add(cards, 0, 2);

            _lblShortcut.Dock = DockStyle.Fill;
            content.Controls.Add(_lblShortcut, 0, 3);

            page.Controls.Add(content, 0, 1);
            return page;
        }

        private Control BuildStatusCard()
        {
            var card = new CardPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                ColumnCount = 2,
                RowCount = 1,
                Margin = Padding.Empty,
                Padding = new Padding(22, 18, 22, 18)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 352f));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            var text = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                ColumnCount = 1,
                RowCount = 3,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            text.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            text.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // device name or state
            text.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));   // what to do next
            text.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // serial / endpoint

            // The two single-line labels measure themselves; only the sentence in the middle is
            // allowed to wrap, so no text can be cut off by a fixed row height.
            _lblStateTitle.Dock = DockStyle.None;
            _lblStateTitle.AutoSize = true;
            _lblStateTitle.Margin = new Padding(0, 0, 0, 2);
            _lblStateDetail.Dock = DockStyle.Fill;
            _lblStateDetail.Margin = new Padding(1, 2, 0, 2);
            _lblDeviceMeta.Dock = DockStyle.None;
            _lblDeviceMeta.AutoSize = true;
            _lblDeviceMeta.Margin = new Padding(0);

            text.Controls.Add(_lblStateTitle, 0, 0);
            text.Controls.Add(_lblStateDetail, 0, 1);
            text.Controls.Add(_lblDeviceMeta, 0, 2);

            // The buttons get their own column with a percent row above and below, so they sit
            // in the middle without any width maths at paint time.
            var buttons = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                ColumnCount = 1,
                RowCount = 3,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            buttons.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
            buttons.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
            buttons.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));

            var row = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };

            _btnConnect.Kind = ButtonKind.Primary;
            _btnConnect.Text = "Connect";
            _btnConnect.Height = 34;
            _btnConnect.Width = 128;
            _btnConnect.Margin = new Padding(8, 0, 0, 0);

            _btnDisconnect.Kind = ButtonKind.Secondary;
            _btnDisconnect.Text = "Disconnect";
            _btnDisconnect.Height = 34;
            _btnDisconnect.Width = 112;
            _btnDisconnect.Margin = new Padding(8, 0, 0, 0);

            _btnRefresh.Kind = ButtonKind.Ghost;
            _btnRefresh.Text = "Refresh";
            _btnRefresh.Font = Theme.Small;
            _btnRefresh.Height = 34;
            _btnRefresh.Width = 84;
            _btnRefresh.Margin = new Padding(0);

            // Right-to-left flow: the primary action ends up rightmost.
            row.Controls.Add(_btnConnect);
            row.Controls.Add(_btnDisconnect);
            row.Controls.Add(_btnRefresh);

            buttons.Controls.Add(row, 0, 1);

            root.Controls.Add(text, 0, 0);
            root.Controls.Add(buttons, 1, 0);
            card.Controls.Add(root);

            return card;
        }

        private Control BuildArrangementCard()
        {
            var card = new CardPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 0) };
            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                ColumnCount = 1,
                RowCount = 3,
                Margin = Padding.Empty,
                Padding = new Padding(18, 14, 18, 12)
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 24f));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));

            grid.Controls.Add(Theme.SectionLabel("ARRANGEMENT"), 0, 0);

            _arrangement.Dock = DockStyle.Fill;
            _arrangement.Margin = Padding.Empty;
            grid.Controls.Add(_arrangement, 0, 1);

            _lblArrangementCaption.Dock = DockStyle.Fill;
            grid.Controls.Add(_lblArrangementCaption, 0, 2);

            card.Controls.Add(grid);
            return card;
        }

        private Control BuildRoutingCard()
        {
            var card = new CardPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                ColumnCount = 1,
                RowCount = 5,
                Margin = Padding.Empty,
                Padding = new Padding(20, 14, 20, 16)
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 24f));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32f));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 22f));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 44f));

            grid.Controls.Add(Theme.SectionLabel("INPUT SHARING"), 0, 0);

            _lblRoutingOwner.Dock = DockStyle.Fill;
            grid.Controls.Add(_lblRoutingOwner, 0, 1);

            _lblRoutingHint.Dock = DockStyle.Fill;
            grid.Controls.Add(_lblRoutingHint, 0, 2);

            _lblStats.Dock = DockStyle.Fill;
            grid.Controls.Add(_lblStats, 0, 3);

            _btnToggleRouting.Kind = ButtonKind.Primary;
            _btnToggleRouting.Text = "Enable input sharing";
            _btnToggleRouting.Width = 200;
            _btnToggleRouting.Dock = DockStyle.Left;
            _btnToggleRouting.Margin = new Padding(0, 4, 0, 4);
            grid.Controls.Add(_btnToggleRouting, 0, 4);

            card.Controls.Add(grid);
            return card;
        }

        private Control BuildActivityPage()
        {
            var page = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Background,
                ColumnCount = 1,
                RowCount = 2,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                Tag = GuiPage.Activity
            };
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            Control header = PageHeader("Activity", "Everything SEAM did this session, newest at the bottom.", out FlowLayoutPanel actions);

            _btnCopyLog.Kind = ButtonKind.Ghost;
            _btnCopyLog.Text = "Copy log";
            _btnCopyLog.Font = Theme.Small;
            _btnCopyLog.Height = 30;
            _btnCopyLog.Margin = new Padding(8, 0, 0, 0);

            _btnClearLog.Kind = ButtonKind.Ghost;
            _btnClearLog.Text = "Clear";
            _btnClearLog.Font = Theme.Small;
            _btnClearLog.Height = 30;
            _btnClearLog.Margin = new Padding(4, 0, 0, 0);

            _chkFollow.Text = "Follow latest";
            _chkFollow.Checked = true;
            StyleCheckBox(_chkFollow);
            _chkFollow.AutoSize = true;
            _chkFollow.Margin = new Padding(0, 7, 8, 0);

            actions.Controls.Add(_chkFollow);
            actions.Controls.Add(_btnCopyLog);
            actions.Controls.Add(_btnClearLog);

            page.Controls.Add(header, 0, 0);

            var host = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Background,
                Margin = Padding.Empty,
                Padding = new Padding(28, 2, 28, 24)
            };

            var card = new CardPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            var logHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(10, 8, 6, 8) };
            _log.Dock = DockStyle.Fill;
            _log.Margin = Padding.Empty;
            _logPlaceholder.Dock = DockStyle.Fill;
            logHost.Controls.Add(_logPlaceholder);
            logHost.Controls.Add(_log);
            _logPlaceholder.BringToFront();
            card.Controls.Add(logHost);
            host.Controls.Add(card);

            page.Controls.Add(host, 0, 1);
            return page;
        }

        private Control BuildSettingsPage()
        {
            var page = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Background,
                ColumnCount = 1,
                RowCount = 2,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                Tag = GuiPage.Settings
            };
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            page.Controls.Add(PageHeader("Settings", "Preferences are stored next to the application and apply to this PC.", out _), 0, 0);

            var content = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Background,
                ColumnCount = 1,
                RowCount = 9,
                Margin = Padding.Empty,
                Padding = new Padding(28, 2, 28, 24)
            };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 132f));  // input sharing
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 14f));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 104f));  // connection
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 14f));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 104f));  // activity
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 14f));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 108f));  // application
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 52f));

            content.Controls.Add(BuildInputSettingsCard(), 0, 0);
            content.Controls.Add(BuildConnectionSettingsCard(), 0, 2);
            content.Controls.Add(BuildActivitySettingsCard(), 0, 4);
            content.Controls.Add(BuildApplicationCard(), 0, 6);

            var footer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Background,
                ColumnCount = 2,
                RowCount = 1,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 124f));

            _lblSettingsStatus.Dock = DockStyle.Fill;
            footer.Controls.Add(_lblSettingsStatus, 0, 0);

            _btnSaveSettings.Kind = ButtonKind.Primary;
            _btnSaveSettings.Text = "Save settings";
            _btnSaveSettings.Dock = DockStyle.Fill;
            _btnSaveSettings.Height = 34;
            _btnSaveSettings.Margin = new Padding(0, 8, 0, 8);
            footer.Controls.Add(_btnSaveSettings, 1, 0);
            content.Controls.Add(footer, 0, 8);

            page.Controls.Add(content, 0, 1);
            return page;
        }

        private Control BuildInputSettingsCard()
        {
            var card = new CardPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            var grid = SettingsGrid(3);

            grid.Controls.Add(Theme.SectionLabel("INPUT SHARING"), 0, 0);

            var row = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                ColumnCount = 2,
                RowCount = 1,
                Margin = Padding.Empty
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 168f));

            var edgeText = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Margin = Padding.Empty };
            var edgeLabel = Theme.MakeLabel("Screen edge", Theme.Body, Theme.TextPrimary, ContentAlignment.MiddleLeft);
            edgeLabel.SetBounds(0, 0, 260, 22);
            var edgeHint = Theme.MakeLabel("Where the pointer hands over to the phone.", Theme.Caption, Theme.TextTertiary, ContentAlignment.MiddleLeft);
            edgeHint.SetBounds(0, 22, 320, 18);
            edgeText.Controls.Add(edgeLabel);
            edgeText.Controls.Add(edgeHint);

            _edge.DropDownStyle = ComboBoxStyle.DropDownList;
            _edge.FlatStyle = FlatStyle.Flat;
            _edge.Font = Theme.Body;
            _edge.BackColor = Theme.SurfaceMuted;
            _edge.ForeColor = Theme.TextPrimary;
            _edge.Dock = DockStyle.Fill;
            _edge.Margin = new Padding(0, 4, 0, 4);
            _edge.Items.AddRange(new object[] { "right", "left", "top", "bottom" });

            row.Controls.Add(edgeText, 0, 0);
            row.Controls.Add(_edge, 1, 0);
            grid.Controls.Add(row, 0, 1);

            _routingOnConnect.Text = "Start sharing input when a session connects";
            StyleCheckBox(_routingOnConnect);
            _routingOnConnect.Dock = DockStyle.Fill;
            grid.Controls.Add(_routingOnConnect, 0, 2);

            card.Controls.Add(grid);
            return card;
        }

        private Control BuildConnectionSettingsCard()
        {
            var card = new CardPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            var grid = SettingsGrid(3);

            grid.Controls.Add(Theme.SectionLabel("CONNECTION"), 0, 0);

            _autoConnect.Text = "Connect automatically, including phones on Wi-Fi (Wireless debugging)";
            StyleCheckBox(_autoConnect);
            _autoConnect.Dock = DockStyle.Fill;
            grid.Controls.Add(_autoConnect, 0, 1);

            var hint = Theme.MakeLabel(
                "USB debugging must be enabled once on the phone. SEAM pushes its agent on connect; nothing is installed.",
                Theme.Caption, Theme.TextTertiary, ContentAlignment.MiddleLeft);
            hint.Dock = DockStyle.Fill;
            grid.Controls.Add(hint, 0, 2);

            card.Controls.Add(grid);
            return card;
        }

        private Control BuildActivitySettingsCard()
        {
            var card = new CardPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            var grid = SettingsGrid(3);

            grid.Controls.Add(Theme.SectionLabel("ACTIVITY"), 0, 0);

            var row = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                ColumnCount = 2,
                RowCount = 1,
                Margin = Padding.Empty
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120f));

            var text = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Margin = Padding.Empty };
            var linesLabel = Theme.MakeLabel("Lines kept in Activity", Theme.Body, Theme.TextPrimary, ContentAlignment.MiddleLeft);
            linesLabel.SetBounds(0, 0, 260, 22);
            var linesHint = Theme.MakeLabel("Older lines are dropped first. Copy before they roll off.", Theme.Caption, Theme.TextTertiary, ContentAlignment.MiddleLeft);
            linesHint.SetBounds(0, 22, 340, 18);
            text.Controls.Add(linesLabel);
            text.Controls.Add(linesHint);

            _maxLines.Minimum = 50;
            _maxLines.Maximum = 5000;
            _maxLines.Increment = 50;
            _maxLines.Font = Theme.Body;
            _maxLines.BackColor = Theme.SurfaceMuted;
            _maxLines.ForeColor = Theme.TextPrimary;
            _maxLines.BorderStyle = BorderStyle.FixedSingle;
            _maxLines.Dock = DockStyle.Left;
            _maxLines.Width = 96;
            _maxLines.Margin = new Padding(0, 4, 0, 4);

            row.Controls.Add(text, 0, 0);
            row.Controls.Add(_maxLines, 1, 0);
            grid.Controls.Add(row, 0, 1);

            card.Controls.Add(grid);
            return card;
        }

        private Control BuildApplicationCard()
        {
            var card = new CardPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            var grid = SettingsGrid(3);

            grid.Controls.Add(Theme.SectionLabel("APPLICATION"), 0, 0);

            _lblAppInfo.Dock = DockStyle.Fill;
            grid.Controls.Add(_lblAppInfo, 0, 1);

            var hint = Theme.MakeLabel(
                "Settings live in %APPDATA%\\SEAM\\ui-settings.json. Closing the window ends the session.",
                Theme.Caption, Theme.TextTertiary, ContentAlignment.BottomLeft);
            hint.Dock = DockStyle.Fill;
            grid.Controls.Add(hint, 0, 2);

            card.Controls.Add(grid);
            return card;
        }

        private static TableLayoutPanel SettingsGrid(int rows)
        {
            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                ColumnCount = 1,
                RowCount = rows,
                Margin = Padding.Empty,
                Padding = new Padding(20, 14, 20, 14)
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 24f));
            for (int i = 1; i < rows; i++)
            {
                grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            }

            return grid;
        }

        private Control BuildDiagnosticsPage()
        {
            var page = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Background,
                ColumnCount = 1,
                RowCount = 2,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                Tag = GuiPage.Diagnostics
            };
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            Control header = PageHeader("Diagnostics",
                "Read-only report. It never contains your pairing key - only a fingerprint.",
                out FlowLayoutPanel actions);

            _btnCopyDiag.Kind = ButtonKind.Ghost;
            _btnCopyDiag.Text = "Copy report";
            _btnCopyDiag.Font = Theme.Small;
            _btnCopyDiag.Height = 30;
            _btnCopyDiag.Margin = new Padding(8, 0, 0, 0);

            _btnRefreshDiag.Kind = ButtonKind.Ghost;
            _btnRefreshDiag.Text = "Refresh";
            _btnRefreshDiag.Font = Theme.Small;
            _btnRefreshDiag.Height = 30;
            _btnRefreshDiag.Margin = new Padding(4, 0, 0, 0);

            actions.Controls.Add(_btnCopyDiag);
            actions.Controls.Add(_btnRefreshDiag);

            page.Controls.Add(header, 0, 0);

            var content = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Background,
                ColumnCount = 1,
                RowCount = 2,
                Margin = Padding.Empty,
                Padding = new Padding(28, 2, 28, 24)
            };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 26f));

            var card = new CardPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(14) };
            _diagOutput.Multiline = true;
            _diagOutput.ReadOnly = true;
            _diagOutput.ScrollBars = ScrollBars.Both;
            _diagOutput.WordWrap = false;
            _diagOutput.Font = Theme.Mono;
            _diagOutput.BackColor = Theme.Surface;
            _diagOutput.ForeColor = Theme.TextSecondary;
            _diagOutput.BorderStyle = BorderStyle.None;
            _diagOutput.Dock = DockStyle.Fill;
            _diagOutput.Text = "Collecting information...";
            card.Controls.Add(_diagOutput);

            content.Controls.Add(card, 0, 0);
            _diagStatus.Dock = DockStyle.Fill;
            content.Controls.Add(_diagStatus, 0, 1);

            page.Controls.Add(content, 0, 1);
            return page;
        }

        private static void StyleCheckBox(CheckBox box)
        {
            box.Font = Theme.Body;
            box.ForeColor = Theme.TextPrimary;
            box.BackColor = Color.Transparent;
            box.AutoSize = false;
            box.FlatStyle = FlatStyle.System;
        }

        // ---- wiring ------------------------------------------------------------------

        private void WireEvents()
        {
            _session.Log += line => Post(() => AppendLog(line, LevelFor(line)));
            _session.StateChanged += (state, detail) => Post(() => ApplyState(state, detail));
            _session.DevicesChanged += devices => Post(() => ApplyDevices(devices));
            _session.DisplayInfoReceived += info => Post(() =>
                AppendLog($"DISPLAY_INFO {info.WidthPx}x{info.HeightPx} @ {info.DensityDpi} dpi", LogLevel.Info));

            _nav.PageSelected += ShowPage;
            _btnConnect.Click += (_, _) => Connect();
            _btnDisconnect.Click += (_, _) => _session.Disconnect();
            _btnRefresh.Click += (_, _) => RefreshDevicesAsync();
            _btnToggleRouting.Click += (_, _) => ToggleRouting();
            _btnCopyLog.Click += (_, _) => CopyLog();
            _btnClearLog.Click += (_, _) => { _log.Items.Clear(); UpdateLogPlaceholder(); };
            _chkFollow.CheckedChanged += (_, _) => _log.FollowTail = _chkFollow.Checked;
            _btnSaveSettings.Click += (_, _) => SaveSettings();
            _btnCopyDiag.Click += (_, _) => CopyDiagnostics();
            _btnRefreshDiag.Click += (_, _) => { _diagnosticsRequested = false; LoadDiagnosticsAsync(); };
            _banner.Dismissed += () => PerformLayout();
        }

        private void Post(Action action)
        {
            if (IsDisposed || _shuttingDown)
            {
                return;
            }

            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke(action);
                }
                else
                {
                    action();
                }
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private static LogLevel LevelFor(string line)
        {
            if (line.Contains("[Error]", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("[FATAL]", StringComparison.OrdinalIgnoreCase))
            {
                return LogLevel.Error;
            }
            if (line.Contains("[Warn]", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase))
            {
                return LogLevel.Warn;
            }
            if (line.Contains("[Auth]", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Connected", StringComparison.OrdinalIgnoreCase))
            {
                return LogLevel.Good;
            }
            return LogLevel.Info;
        }

        private void AppendLog(string line, LogLevel level)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }
            _log.Append(line, level);
            UpdateLogPlaceholder();
        }

        private void UpdateLogPlaceholder()
        {
            bool empty = _log.Items.Count == 0;
            _logPlaceholder.Visible = empty;
            if (empty)
            {
                _logPlaceholder.BringToFront();
            }
        }

        // ---- state -------------------------------------------------------------------

        private void ApplyState(SessionState state, string detail)
        {
            bool changed = state != _lastState;
            _lastState = state;

            _pill.State = state;
            _pill.Label = StatusPill.LabelFor(state);

            switch (state)
            {
                case SessionState.Connected:
                    _banner.HideBanner();
                    break;
                case SessionState.Unauthorized:
                    ShowProblemBanner(ConnectionProblem.Unauthorized, warning: true);
                    break;
                case SessionState.Error:
                    ShowProblemBanner(ConnectionDiagnosis.ClassifyFailure(detail), warning: false);
                    break;
                case SessionState.PairingRequired:
                    ShowProblemBanner(ConnectionProblem.PairingRequired, warning: true);
                    break;
                case SessionState.Connecting:
                    _banner.HideBanner();
                    break;
                default:
                    if (!string.IsNullOrEmpty(detail) && detail.StartsWith("The phone disconnected", StringComparison.Ordinal))
                    {
                        // The cause stays in Activity; the banner shows what it means and what to do.
                        ShowProblemBanner(ConnectionDiagnosis.ClassifyFailure(detail), warning: true);
                    }
                    else
                    {
                        _banner.HideBanner();
                    }
                    break;
            }

            UpdateHome();
            UpdateRoutingCard();
            UpdateButtons();

            if (changed)
            {
                PerformLayout();
            }
        }

        /// <summary>One line: what SEAM found, then the single next action.</summary>
        private void ShowProblemBanner(ConnectionProblem problem, bool warning)
        {
            string text = ConnectionDiagnosis.Headline(problem) + " - " + ConnectionDiagnosis.ShortGuidance(problem);
            if (warning)
            {
                _banner.ShowWarning(text);
            }
            else
            {
                _banner.ShowError(text);
            }
        }

        private void ApplyDevices(IReadOnlyList<AdbDevice> devices)
        {
            _device = null;
            for (int i = 0; i < devices.Count; i++)
            {
                if (devices[i].IsAuthorized)
                {
                    _device = devices[i];
                    break;
                }
            }
            if (_device == null && devices.Count > 0)
            {
                _device = devices[0];
            }

            UpdateHome();
            UpdateButtons();

            // Optional auto-connect: once per newly seen authorised device.
            if (_settings.AutoConnect && _device != null && _device.IsAuthorized &&
                !_session.IsConnected && !_session.IsBusy &&
                !string.Equals(_autoConnectSerial, _device.Serial, StringComparison.Ordinal))
            {
                _autoConnectSerial = _device.Serial;
                Connect();
            }
            else if (_device == null)
            {
                _autoConnectSerial = null;
            }
        }

        /// <summary>
        /// Fills the Home status card. Everything here is quoted from the session: when there is
        /// no device it says what to do next instead of showing an empty shell.
        /// </summary>
        private void UpdateHome()
        {
            IReadOnlyList<AdbDevice> devices = _session.Devices;

            if (_session.IsConnected && _device != null)
            {
                _lblStateTitle.Text = SessionController.DeviceLabel(_device);
                _lblStateDetail.Text = _device.IsWirelessAdb
                    ? "Connected over Wi-Fi through adb, authenticated with a mutual HMAC-SHA256 key."
                    : "Connected over USB via ADB forward, authenticated with a mutual HMAC-SHA256 key.";
                // Both transports reach the agent the same way on Windows: an adb forward.
                _lblDeviceMeta.Text = _device.Serial + " · " + (_session.Endpoint ?? "adb forward");
            }
            else if (_session.State == SessionState.Connecting)
            {
                _lblStateTitle.Text = _device != null ? SessionController.DeviceLabel(_device) : "Connecting...";
                _lblStateDetail.Text = "Pushing and starting the SEAM agent on the phone. The first time takes a few seconds.";
                _lblDeviceMeta.Text = _device?.Serial ?? "starting the agent";
            }
            else if (_session.State == SessionState.Error)
            {
                // A failed attempt: say what it means, keep the exact error one line down and in Activity.
                ConnectionProblem problem = ConnectionDiagnosis.ClassifyFailure(_session.Detail);
                _lblStateTitle.Text = ConnectionDiagnosis.Headline(problem);
                _lblStateDetail.Text = ConnectionDiagnosis.Guidance(problem);
                _lblDeviceMeta.Text = Shorten(_session.Detail, 110);
            }
            else if (_session.State == SessionState.PairingRequired)
            {
                _lblStateTitle.Text = ConnectionDiagnosis.Headline(ConnectionProblem.PairingRequired);
                _lblStateDetail.Text = ConnectionDiagnosis.Guidance(ConnectionProblem.PairingRequired);
                _lblDeviceMeta.Text = ConnectionDiagnosis.TechnicalHint(ConnectionProblem.PairingRequired);
            }
            else
            {
                ConnectionProblem problem = ConnectionDiagnosis.ClassifyDevices(_session.AdbPath != null, devices);

                if (problem == ConnectionProblem.None)
                {
                    // adb already sees an authorised phone on its own transport (USB or a TCP
                    // transport another tool created); all that is left is the button.
                    AdbDevice ready = _device ?? devices[0];
                    _lblStateTitle.Text = SessionController.DeviceLabel(ready);
                    _lblStateDetail.Text = devices.Count > 1
                        ? devices.Count + " phones detected. Connect uses the first authorised one; " +
                          "unplug the others to choose a different phone."
                        : "Ready. Connect pushes the agent and starts the session.";
                    _lblDeviceMeta.Text = ready.Serial + " · " +
                        (ready.IsWirelessAdb ? "Wi-Fi · authorised" : "USB · authorised");
                }
                else
                {
                    _lblStateTitle.Text = ConnectionDiagnosis.Headline(problem);
                    _lblStateDetail.Text = ConnectionDiagnosis.Guidance(problem);

                    string hint = ConnectionDiagnosis.TechnicalHint(problem);
                    if (problem == ConnectionProblem.Unauthorized && devices.Count > 0)
                    {
                        hint = (devices[0].Model ?? devices[0].Serial) + " · adb state: " + devices[0].State;
                    }
                    else if (problem == ConnectionProblem.WirelessUnavailable && devices.Count > 0)
                    {
                        hint = devices[0].Serial + " · " + hint;
                    }

                    _lblDeviceMeta.Text = hint;
                }
            }

            _arrangement.Edge = _settings.Edge;
            _arrangement.IsConnected = _session.IsConnected;
            _arrangement.RoutingToPhone = _session.IsRouting;
        }

        /// <summary>First line of a possibly multi-line error, cut to a readable length.</summary>
        private static string Shorten(string? text, int limit)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string firstLine = text.Split('\n')[0].Trim();
            return firstLine.Length <= limit ? firstLine : firstLine.Substring(0, limit - 1) + "…";
        }

        private void UpdateRoutingCard()
        {
            bool connected = _session.IsConnected;
            bool routing = _session.IsRouting;

            if (!connected)
            {
                _lblRoutingOwner.Text = "Windows";
                _lblRoutingHint.Text = "Control stays on Windows until a session is connected. " +
                                       "The phone sits on the " + _settings.EdgeName + " edge once you connect.";
                _lblStats.Text = string.Empty;
                _lblArrangementCaption.Text = "Arrangement set to the " + _settings.EdgeName + " edge.";
                _btnToggleRouting.SetText("Enable input sharing");
                return;
            }

            _lblRoutingOwner.Text = routing ? "Android phone" : "Windows";
            _lblRoutingHint.Text = routing
                ? "Move the pointer to the " + _settings.EdgeName + " edge to take control of the phone."
                : "Press Enable input sharing to arm the " + _settings.EdgeName + " edge. " +
                  "Windows input is unaffected until you cross it.";
            _lblStats.Text = $"sent {_session.FramesSent:n0} · recv {_session.FramesReceived:n0} · " +
                             $"input {_session.EventsForwarded:n0} · uptime {_session.SessionUptime:hh\\:mm\\:ss}";
            _lblArrangementCaption.Text = routing
                ? "Control is on the phone now."
                : "Arrangement set to the " + _settings.EdgeName + " edge.";
            _btnToggleRouting.SetText(routing ? "Stop sharing input" : "Enable input sharing");
        }

        private void UpdateRail()
        {
            // One short line: the rail is 216px wide and must survive 150% text scaling. The adb
            // source is spelled out on the Settings page instead.
            _lblRailVersion.Text = "Version " + Host.DiagnosticsReport.VersionString;

            string adbPath = _session.AdbPath == null
                ? "adb: not found - reinstall SEAM"
                : "adb: " + (_session.AdbIsBundled ? "bundled with SEAM" : "system install");
            _lblAppInfo.Text = adbPath + "\r\n" + _session.AppDirectory;
        }

        private void UpdateButtons()
        {
            bool connected = _session.IsConnected;
            bool busy = _session.IsBusy;
            bool deviceReady = _device != null && _device.IsAuthorized;

            _btnConnect.Visible = !connected;
            _btnDisconnect.Visible = connected || busy;
            _btnRefresh.Visible = !connected;

            _btnConnect.Enabled = !connected && !busy && deviceReady;
            _btnConnect.SetText(_settings.LastDeviceSerial != null ? "Reconnect" : "Connect");
            _btnDisconnect.Enabled = connected || busy;
            _btnRefresh.Enabled = !connected && !busy;
            _btnToggleRouting.Enabled = connected;

            UpdateRail();
        }

        // ---- settings ----------------------------------------------------------------

        private void LoadSettingsIntoPage()
        {
            _edge.SelectedItem = _settings.Edge;
            if (_edge.SelectedIndex < 0)
            {
                _edge.SelectedIndex = 0;
            }

            _routingOnConnect.Checked = _settings.RoutingOnConnect;
            _autoConnect.Checked = _settings.AutoConnect;
            _maxLines.Value = Math.Min(5000, Math.Max(50, _settings.MaxLogLines));
        }

        private void SaveSettings()
        {
            _settings.Edge = _edge.SelectedItem as string ?? "right";
            _settings.RoutingOnConnect = _routingOnConnect.Checked;
            _settings.AutoConnect = _autoConnect.Checked;
            _settings.MaxLogLines = (int)_maxLines.Value;
            _settings.Normalize();
            _settings.Save();

            _log.MaxEntries = _settings.MaxLogLines;

            bool wasRouting = _session.IsRouting || _settings.RoutingOnConnect;
            if (wasRouting && _session.IsConnected)
            {
                _session.StopRouting();
                _session.StartRouting();
            }

            _lblSettingsStatus.Text = "Saved at " + DateTime.Now.ToString("HH:mm:ss") + " · applied immediately.";
            AppendLog("Settings saved (edge: " + _settings.EdgeName +
                      ", auto-connect: " + (_settings.AutoConnect ? "on" : "off") +
                      ", input sharing on connect: " + (_settings.RoutingOnConnect ? "on" : "off") + ").", LogLevel.Info);
            UpdateHome();
            UpdateRoutingCard();
        }

        // ---- actions -----------------------------------------------------------------

        private void Connect()
        {
            AdbDevice? device = _device;
            if (device == null)
            {
                AppendLog("No phone to connect to. Plug the cable in and accept the prompt on the phone.", LogLevel.Warn);
                return;
            }
            _settings.Save();
            _session.Connect(device);
        }

        private void ToggleRouting()
        {
            if (_session.IsRouting)
            {
                _session.StopRouting();
                AppendLog("Input sharing stopped; control is on Windows.", LogLevel.Info);
            }
            else if (_session.StartRouting())
            {
                AppendLog("Input sharing armed on the " + _settings.EdgeName + " edge.", LogLevel.Info);
            }
            else
            {
                AppendLog("Could not arm input sharing. Try running SEAM as administrator.", LogLevel.Error);
            }
            UpdateHome();
            UpdateRoutingCard();
            UpdateButtons();
        }

        /// <summary>
        /// Background half of the wireless autopilot (macOS runAutoPilotTick): one tick per poll,
        /// mDNS every third tick, all of it off the UI thread. Overlapping ticks are skipped so a
        /// slow adb call can never queue a second session request.
        /// </summary>
        private void TickWirelessAsync()
        {
            if (_wirelessBusy || _polling || _shuttingDown)
            {
                return;
            }

            _wirelessBusy = true;
            Task.Run(() =>
            {
                try
                {
                    _session.TickWireless();
                }
                catch (Exception ex)
                {
                    _session.LogLine("[Wi-Fi] autopilot tick failed: " + ex.Message);
                }
                finally
                {
                    _wirelessBusy = false;
                }
            });
        }

        private void RefreshDevicesAsync()
        {
            if (_polling || _shuttingDown)
            {
                return;
            }
            _polling = true;
            Task.Run(() =>
            {
                try
                {
                    _session.RefreshDevices();
                }
                finally
                {
                    _polling = false;
                }
            });
        }

        private void OnHookTick()
        {
            if (_shuttingDown)
            {
                return;
            }

            _session.PumpHooks();
            UpdateRoutingCard();
            UpdateButtons();
        }

        private void CopyLog()
        {
            try
            {
                string text = _log.AllText();
                if (!string.IsNullOrEmpty(text))
                {
                    Clipboard.SetText(text);
                }
            }
            catch
            {
                // Clipboard access can fail if another process holds it; not worth a dialog.
            }
        }

        private void LoadDiagnosticsAsync()
        {
            if (_diagnosticsRequested || _shuttingDown)
            {
                return;
            }
            _diagnosticsRequested = true;
            _diagStatus.Text = "Collecting information...";

            Task.Run(() =>
            {
                string text;
                try
                {
                    text = DiagnosticsReport.Build(null, null, out bool ok);
                    if (!ok)
                    {
                        text = text.Replace("RESULT: a required resource is missing (see FAIL above).",
                            "RESULT: a required resource is missing (see FAIL above).\r\nReinstall SEAM to restore the bundled files.");
                    }
                }
                catch (Exception ex)
                {
                    text = "Diagnostics failed: " + ex.Message;
                }

                Post(() =>
                {
                    _diagOutput.Text = text.Replace("\n", "\r\n");
                    _diagOutput.SelectionStart = 0;
                    _diagStatus.Text = "Generated " + DateTime.Now.ToString("HH:mm:ss") + " - copy this into a bug report.";
                });
            });
        }

        private void CopyDiagnostics()
        {
            try
            {
                if (!string.IsNullOrEmpty(_diagOutput.Text))
                {
                    Clipboard.SetText(_diagOutput.Text);
                    _diagStatus.Text = "Copied to the clipboard.";
                }
            }
            catch
            {
                _diagStatus.Text = "Could not access the clipboard.";
            }
        }

        // ---- lifecycle ----------------------------------------------------------------

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _shuttingDown = true;
            _hookTimer.Stop();
            _pollTimer.Stop();

            try
            {
                // Removes the low-level hooks, closes the TCP session and stops the reader.
                _session.Dispose();
            }
            catch
            {
                // Shutdown must complete even if a component is already gone.
            }

            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hookTimer.Dispose();
                _pollTimer.Dispose();
                if (!_shuttingDown)
                {
                    try { _session.Dispose(); } catch { }
                }
            }
            base.Dispose(disposing);
        }
    }
}
