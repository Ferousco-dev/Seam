using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Seam.WindowsHost.Bootstrap;
using Seam.WindowsHost.Input;
using Seam.WindowsHost.Protocol;
using Seam.WindowsHost.Transport;

namespace Seam.WindowsHost.Host
{
    /// <summary>What the user should see in the status indicator.</summary>
    public enum SessionState
    {
        Disconnected,
        Connecting,
        Connected,
        Unauthorized,
        PairingRequired,
        Error
    }

    /// <summary>
    /// Owns everything that talks to the phone: adb discovery of devices, the agent bootstrap,
    /// the authenticated <see cref="SeamConnection"/>, and the input hooks.
    ///
    /// The desktop application drives this class instead of re-implementing the sequence, so
    /// there is exactly one place where a session is established, torn down and reported. The
    /// console modes in Program.cs keep their own (hardware-verified) flow untouched; both build
    /// on the same primitives - AppLayout, SeamAuthenticator, AdbBootstrapper, SeamConnection,
    /// WindowsInputCapture - and neither duplicates the protocol or the control routing.
    ///
    /// Threading: <see cref="RefreshDevices"/> is blocking and must be called off the UI thread;
    /// <see cref="Connect"/> returns immediately and reports progress through events, which are
    /// raised on background threads. The UI is responsible for marshalling them.
    /// </summary>
    public sealed class SessionController : IDisposable
    {
        private readonly object _gate = new object();
        private readonly UiSettings _settings;

        private AppLayout? _layout;
        private string? _resourceError;
        private AdbBootstrapper? _adb;
        private SeamConnection? _connection;
        private WindowsInputCapture? _capture;
        private List<AdbDevice> _devices = new List<AdbDevice>();
        private SessionState _state = SessionState.Disconnected;
        private string _detail = "Not connected";
        private bool _connecting;
        private bool _disposed;
        private long _sessionStartedTicks;

        public SessionController(UiSettings settings)
        {
            _settings = settings ?? new UiSettings();
            _settings.Normalize();
        }

        // ---- events (raised on background threads) --------------------------------------

        public event Action<SessionState, string>? StateChanged;
        public event Action<string>? Log;
        public event Action<IReadOnlyList<AdbDevice>>? DevicesChanged;
        public event Action? StatsChanged;
        public event Action<AndroidDisplayInfo>? DisplayInfoReceived;

        // ---- observable state -----------------------------------------------------------

        public SessionState State => _state;
        public string Detail => _detail;
        public bool IsConnected => _connection != null && _connection.IsRunning;
        public bool IsRouting => _capture != null && _capture.IsRoutingToAndroid;
        public bool IsBusy => _connecting;
        public AndroidDisplayInfo? Display { get; private set; }
        public string? Endpoint => _connection?.Endpoint;
        public ulong SessionId => _connection?.SessionId ?? 0;
        public long FramesSent => _connection?.FramesSent ?? 0;
        public long FramesReceived => _connection?.FramesReceived ?? 0;
        public long EventsForwarded => _capture?.EventsForwarded ?? 0;
        public long UnsupportedKeys => _capture?.UnsupportedKeys ?? 0;
        public TimeSpan SessionUptime => _sessionStartedTicks == 0
            ? TimeSpan.Zero
            : TimeSpan.FromMilliseconds(Environment.TickCount64 - _sessionStartedTicks);

        public IReadOnlyList<AdbDevice> Devices
        {
            get { lock (_gate) { return _devices.ToArray(); } }
        }

        public string? ResourceError { get { lock (_gate) { return _resourceError; } } }

        public string AppDirectory => _layout?.AppRoot ?? "(not resolved)";
        public string? AdbPath => _layout?.AdbPath;
        public bool AdbIsBundled => _layout?.AdbIsBundled ?? false;

        // ---- resources -----------------------------------------------------------------

        /// <summary>
        /// Resolves adb, the agent jar and the app folder once. Never throws: a missing resource
        /// is a state the UI has to explain, not a crash. Returns true when everything is present.
        /// </summary>
        public bool Initialize()
        {
            try
            {
                AppLayout layout = AppLayout.ResolveForRoot(
                    AppLayout.ExecutableDirectory(), null, null, requireResources: false);

                lock (_gate)
                {
                    _layout = layout;
                    _resourceError = layout.AdbPath == null
                        ? "adb.exe is missing from the SEAM folder (" + layout.AppRoot + "). Reinstall SEAM, or run Diagnostics."
                        : layout.AgentJarPath == null
                            ? "seam-agent.jar is missing from the SEAM folder (" + layout.AppRoot + "). Reinstall SEAM, or run Diagnostics."
                            : null;
                }

                if (_resourceError == null)
                {
                    _adb = new AdbBootstrapper(layout.AgentJarPath!, layout.AdbPath!);
                    Emit("[Boot] app dir: " + layout.AppRoot);
                    Emit("[Boot] adb    : " + layout.AdbPath + (layout.AdbIsBundled ? " (bundled)" : " (system)"));
                    Emit("[Boot] agent  : " + layout.AgentJarPath);
                    return true;
                }

                Emit("[Error] " + _resourceError);
                SetState(SessionState.Error, _resourceError!);
                return false;
            }
            catch (Exception ex)
            {
                lock (_gate) { _resourceError = ex.Message; }
                Emit("[Error] " + ex.Message);
                SetState(SessionState.Error, ex.Message);
                return false;
            }
        }

        // ---- device discovery ------------------------------------------------------------

        /// <summary>Blocking `adb devices`. Call from a worker thread.</summary>
        public void RefreshDevices()
        {
            if (_disposed || _adb == null || IsConnected || _connecting)
            {
                return;
            }

            List<AdbDevice> devices;
            try
            {
                devices = _adb.ListDevices();
            }
            catch (Exception ex)
            {
                Emit("[Dev] could not list devices: " + ex.Message);
                SetState(SessionState.Error, ex.Message);
                return;
            }

            bool changed;
            lock (_gate)
            {
                changed = !SameDevices(_devices, devices);
                _devices = devices;
            }

            if (changed)
            {
                DevicesChanged?.Invoke(Devices);
            }

            if (IsConnected || _connecting)
            {
                return;
            }

            if (devices.Count == 0)
            {
                // A phone advertising Wireless debugging but not paired yet keeps saying so;
                // the poll must not wipe that message on its way past.
                if (_state != SessionState.PairingRequired)
                {
                    SetState(SessionState.Disconnected, "No phone detected");
                }
            }
            else if (!devices.Exists(d => d.IsAuthorized))
            {
                AdbDevice first = devices[0];
                SetState(SessionState.Unauthorized,
                    first.State == "offline"
                        ? "The phone is not responding. Re-plug the USB cable."
                        : "Unlock the phone and tap ALLOW on the \"Allow USB debugging?\" prompt.");
            }
            else
            {
                AdbDevice? authorized = devices.Find(d => d.IsAuthorized);
                SetState(SessionState.Disconnected,
                    authorized != null ? "Ready to connect to " + DeviceLabel(authorized) : "Ready to connect");
            }
        }

        // ---- connect / disconnect --------------------------------------------------------

        /// <summary>
        /// Starts a session in the background. <paramref name="device"/> may be null, in which
        /// case the first authorised device is used.
        /// </summary>
        public void Connect(AdbDevice? device)
        {
            if (_disposed || _adb == null)
            {
                SetState(SessionState.Error, ResourceError ?? "SEAM cannot start a session.");
                return;
            }

            // A deliberate Connect re-arms automatic discovery and reconnection.
            UserDisconnected = false;

            lock (_gate)
            {
                if (_connecting || IsConnected)
                {
                    return;
                }
                _connecting = true;
            }

            AdbDevice target = device ?? FirstAuthorized();
            string label = target != null ? DeviceLabel(target) : "the phone";
            SetState(SessionState.Connecting, "Starting the Android agent on " + label + "...");
            Emit("[Link] connecting to " + label);

            Task.Run(() => ConnectWorker(target));
        }

        private AdbDevice? FirstAuthorized()
        {
            lock (_gate)
            {
                return _devices.Find(d => d.IsAuthorized);
            }
        }

        /// <summary>
        /// How long a freshly dialled session may stay silent before the attempt is reported as
        /// "connected but quiet". A close inside this window is treated as a failed attempt, not
        /// as a live session that dropped.
        /// </summary>
        private const int FirstFrameTimeoutMs = 3000;

        private void ConnectWorker(AdbDevice? device)
        {
            AdbBootstrapper adb = _adb!;
            SeamConnection? connection = null;

            try
            {
                // Re-list: the device list may be stale (unplugged cable, revoked authorisation).
                List<AdbDevice> devices = adb.ListDevices();
                lock (_gate) { _devices = devices; }
                DevicesChanged?.Invoke(Devices);

                AdbDevice? target = device != null
                    ? devices.Find(d => d.Serial == device.Serial)
                      ?? throw new InvalidOperationException(
                          "The phone (" + (device.Model ?? device.Serial) + ") is no longer visible to adb. Re-plug the USB cable and press Connect again.")
                    : devices.Find(d => d.IsAuthorized)
                      ?? throw new InvalidOperationException(
                          devices.Count == 0
                              ? "No Android device is visible to adb. Enable USB debugging, plug the cable in, and accept the prompt on the phone."
                              : "The phone is connected but not authorised. Unlock it and tap ALLOW on the \"Allow USB debugging?\" prompt.");

                if (!target.IsAuthorized)
                {
                    throw new InvalidOperationException(
                        "The phone has not authorised this PC yet. Unlock it and tap ALLOW on the \"Allow USB debugging?\" prompt.");
                }

                _settings.LastDeviceSerial = target.Serial;
                _settings.Save();

                SeamAuthenticator auth = SeamAuthenticator.LoadOrCreatePersistentKey();
                // Fingerprint only: the private key never reaches the UI or a log file.
                Emit("[Auth] pairing key fingerprint " + auth.KeyFingerprint);

                SessionAttempt attempt = OpenSession(adb, target, auth, forceRedeploy: false);
                if (attempt.Connection == null)
                {
                    Emit("[Link] first attempt failed (" + attempt.Failure + ") - restarting the agent on the phone once.");
                    attempt = OpenSession(adb, target, auth, forceRedeploy: true);
                }

                if (attempt.Connection == null)
                {
                    throw new InvalidOperationException(attempt.Failure ?? "The phone did not accept the session.");
                }

                connection = attempt.Connection;

                lock (_gate)
                {
                    _connection = connection;
                    _connecting = false;
                    _sessionStartedTicks = Environment.TickCount64;
                }

                Emit("[Auth] mutual HMAC-SHA256 OK - session 0x" + connection.SessionId.ToString("X16"));
                SetState(SessionState.Connected, DeviceLabel(target) + " · " + target.Serial);

                if (_settings.RoutingOnConnect)
                {
                    StartRouting();
                }
            }
            catch (Exception ex)
            {
                connection?.Dispose();
                lock (_gate)
                {
                    _connection = null;
                    _connecting = false;
                }
                Emit("[Error] " + ex.Message);
                SetState(SessionState.Error, ex.Message);
            }
        }

        /// <summary>
        /// One connect attempt: start (or reuse) the agent, open the adb forward, dial it and wait
        /// for the first frame.
        ///
        /// A close that arrives before the session is published is returned as a failed attempt
        /// instead of being reported as "connected, then disconnected", and
        /// <paramref name="forceRedeploy"/> pushes and relaunches the agent so a stale agent, a
        /// dead listener or a rotated pairing key can clear itself.
        /// </summary>
        private SessionAttempt OpenSession(
            AdbBootstrapper adb, AdbDevice target, SeamAuthenticator auth, bool forceRedeploy)
        {
            var attempt = new SessionAttempt();

            try
            {
                List<(string iface, string ip, int port)> endpoints = adb.BootstrapSession(target, auth, forceRedeploy);
                foreach ((string iface, string ip, int port) in endpoints)
                {
                    Emit($"[Boot] agent Wi-Fi endpoint {ip}:{port} ({iface})");
                }

                int port2 = adb.SetupUsbForward(target.Serial);
                Emit($"[Boot] adb forward tcp:{port2} -> localabstract:seam_uhid");

                var ready = new ManualResetEventSlim(false);
                var closed = new ManualResetEventSlim(false);
                var closeGate = new object();
                string? earlyClose = null;

                SeamConnection connection = SeamConnection.Connect("127.0.0.1", port2, auth);

                connection.DisplayInfoReceived += info =>
                {
                    Display = info;
                    ready.Set();
                    DisplayInfoReceived?.Invoke(info);
                    StatsChanged?.Invoke();
                };
                connection.EdgeReturnReceived += evt =>
                {
                    _capture?.HandleEdgeReturn(evt);
                    StatsChanged?.Invoke();
                };
                connection.ClipboardReceived += text =>
                {
                    Emit("[Clip] <- Android: " + Truncate(text));
                };
                connection.Closed += reason =>
                {
                    bool published;
                    lock (_gate) { published = ReferenceEquals(_connection, connection); }

                    if (published)
                    {
                        Emit("[Link] closed: " + reason);
                        HandleConnectionClosed(reason);
                        return;
                    }

                    // Not handed to the session yet: this attempt failed, and the caller retries.
                    lock (closeGate) { earlyClose = reason; }
                    Emit("[Link] the phone closed the connection before the session was ready: " + reason);
                    closed.Set();
                    ready.Set();
                };

                connection.Start();

                WaitHandle.WaitAny(new[] { ready.WaitHandle, closed.WaitHandle }, FirstFrameTimeoutMs);

                string? failure;
                lock (closeGate) { failure = earlyClose; }

                if (failure != null)
                {
                    try { connection.Dispose(); } catch { /* already closed */ }
                    attempt.Failure = failure;
                    return attempt;
                }

                if (!ready.IsSet)
                {
                    Emit("[Warn] no DISPLAY_INFO within 3 s; continuing anyway.");
                }

                attempt.Connection = connection;
                return attempt;
            }
            catch (Exception ex)
            {
                Emit("[Error] " + ex.Message);
                attempt.Failure = ex.Message;
                return attempt;
            }
        }

        /// <summary>Outcome of one <see cref="OpenSession"/> call.</summary>
        private sealed class SessionAttempt
        {
            public SeamConnection? Connection { get; set; }
            public string? Failure { get; set; }
        }

        public void Disconnect()
        {
            // An explicit Disconnect must not be undone by the autopilot a second later.
            UserDisconnected = true;
            StopRouting();

            SeamConnection? connection;
            lock (_gate)
            {
                connection = _connection;
                _connection = null;
                _connecting = false;
                _sessionStartedTicks = 0;
            }

            if (connection != null)
            {
                try { connection.Dispose(); } catch { /* already gone */ }
                Emit("[Link] disconnected");
            }

            Display = null;
            SetState(SessionState.Disconnected, "Not connected");
            StatsChanged?.Invoke();
        }

        private void HandleConnectionClosed(string reason)
        {
            lock (_gate)
            {
                if (_connection == null)
                {
                    return;
                }
                _sessionStartedTicks = 0;
            }

            StopRouting();

            SeamConnection? connection;
            lock (_gate)
            {
                connection = _connection;
                _connection = null;
            }
            try { connection?.Dispose(); } catch { }

            Display = null;
            SetState(SessionState.Disconnected,
                "The phone disconnected (" + reason + "). Re-plug the USB cable, then press Connect.");
            StatsChanged?.Invoke();
        }

        // ---- wireless autopilot -----------------------------------------------------------

        private int _wirelessTick;
        private string _lastAdbConnectTarget = string.Empty;
        private long _lastAdbConnectTicks;

        /// <summary>
        /// True from the moment the user presses Disconnect until they press Connect again.
        /// The autopilot treats it as an instruction and stops dialling until then.
        /// </summary>
        public bool UserDisconnected { get; private set; }

        /// <summary>
        /// One autopilot tick, ported from macOS <c>runAutoPilotTick</c>: connect a device that is
        /// already visible, otherwise every third tick let adb's mDNS discovery find an
        /// already-paired Wireless debugging endpoint and attach it. Blocking (adb calls) and
        /// therefore called from a worker thread; a laptop-sleep or cable-unplug just fails the
        /// call and the next tick tries again.
        /// </summary>
        public void TickWireless()
        {
            AdbBootstrapper? adb;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
                adb = _adb;
            }

            if (adb == null)
            {
                return;
            }

            int tick = Interlocked.Increment(ref _wirelessTick);
            bool mdnsDue = tick % 3 == 0;

            List<WirelessService> services = mdnsDue
                ? adb.DiscoverWirelessServices()
                : new List<WirelessService>();

            WirelessAction action;
            AdbDevice? target = null;

            lock (_gate)
            {
                action = WirelessDiscovery.Decide(new WirelessContext
                {
                    AutoConnectEnabled = _settings.AutoConnect,
                    UserDisconnected = UserDisconnected,
                    Connected = _connection != null,
                    Busy = _connecting,
                    MdnsDue = mdnsDue,
                    Devices = new List<AdbDevice>(_devices),
                    Services = services
                });

                if (action.Kind == WirelessActionKind.ConnectDevice)
                {
                    target = _devices.Find(d => d.Serial == action.Target);
                }
            }

            switch (action.Kind)
            {
                case WirelessActionKind.ConnectDevice when target != null:
                    Emit("[Wi-Fi] " + (target.IsWirelessAdb ? "Wi-Fi" : "USB") + " device ready: " + target.Serial);
                    Connect(target);
                    break;

                case WirelessActionKind.AdbConnect:
                {
                    long now = Environment.TickCount64;
                    if (string.Equals(action.Target, _lastAdbConnectTarget, StringComparison.Ordinal) &&
                        now - _lastAdbConnectTicks < 20000)
                    {
                        return; // adb was just asked; do not hammer a failed endpoint
                    }

                    _lastAdbConnectTarget = action.Target;
                    _lastAdbConnectTicks = now;

                    if (!action.Target.Contains(':'))
                    {
                        // A Wi-Fi transport adb still remembers but whose socket died.
                        adb.DisconnectWireless(action.Target);
                    }

                    Emit("[Wi-Fi] adb connect " + action.Target);
                    Emit("[Wi-Fi] " + adb.ConnectWireless(action.Target));
                    RefreshDevices();
                    break;
                }

                case WirelessActionKind.NeedPairing:
                    if (_connection == null && !_connecting && _state != SessionState.PairingRequired)
                    {
                        Emit("[Wi-Fi] the phone advertises pairing, not an established link: " + action.Target);
                        SetState(SessionState.PairingRequired,
                            "Wireless debugging is on, but this PC and the phone are not paired yet. " +
                            "No driver is involved. Pair once with the pairing code the phone shows.");
                    }
                    break;
            }
        }

        // ---- input routing ----------------------------------------------------------------

        /// <summary>
        /// Installs the low-level hooks for the connected session. Until the pointer crosses the
        /// chosen edge, Windows input is untouched - the same behaviour as <c>--capture</c>.
        /// </summary>
        public bool StartRouting()
        {
            SeamConnection? connection;
            lock (_gate) { connection = _connection; }

            if (connection == null || _capture != null)
            {
                return _capture != null;
            }

            try
            {
                var capture = new WindowsInputCapture(
                    connection, _settings.EdgeCode, forceCapture: false, log: line => Emit(line));
                capture.Start();
                _capture = capture;
                Emit("[Input] " + _settings.EdgeName + " edge is armed - move the pointer there to control the phone.");
                StatsChanged?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                Emit("[Error] could not install input hooks: " + ex.Message);
                return false;
            }
        }

        public void StopRouting()
        {
            WindowsInputCapture? capture = _capture;
            _capture = null;
            if (capture == null)
            {
                return;
            }

            try
            {
                capture.ForceReturn();
                capture.Dispose();
                Emit("[Input] hooks removed; control is on Windows.");
            }
            catch
            {
                // Disposal is best effort during shutdown.
            }

            StatsChanged?.Invoke();
        }

        /// <summary>Returns the pointer to Windows without tearing the session down.</summary>
        public void ReturnToWindows()
        {
            _capture?.ForceReturn();
            StatsChanged?.Invoke();
        }

        /// <summary>Drains hook-thread log lines. Call from a UI timer (never from the hook).</summary>
        public void PumpHooks()
        {
            _capture?.DrainLog();
        }

        // ---- helpers ---------------------------------------------------------------------

        public static string DeviceLabel(AdbDevice device)
        {
            string model = string.IsNullOrWhiteSpace(device.Model) ? "Android device" : device.Model!;
            return model.Replace('_', ' ');
        }

        private static bool SameDevices(List<AdbDevice> a, List<AdbDevice> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].Serial != b[i].Serial || a[i].State != b[i].State || a[i].Model != b[i].Model)
                {
                    return false;
                }
            }
            return true;
        }

        private static string Truncate(string s) => s.Length <= 48 ? s : s.Substring(0, 45) + "...";

        private void SetState(SessionState state, string detail)
        {
            _state = state;
            _detail = detail;
            StateChanged?.Invoke(state, detail);
        }

        private void Emit(string line) => Log?.Invoke(line);

        /// <summary>Writes one line to the Activity log from outside the controller.</summary>
        public void LogLine(string line) => Emit(line);

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            try { Disconnect(); } catch { }
            try { _settings.Save(); } catch { }
        }
    }
}
