using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Seam.WindowsHost.Bootstrap;
using Seam.WindowsHost.Host;
using Seam.WindowsHost.Protocol;
using Seam.WindowsHost.Transport;

namespace Seam.WindowsHost.Testing
{
    /// <summary>
    /// Offline conformance tests. Runnable with `seam-host --self-test` on any OS that has
    /// the .NET 8 SDK — no phone, no adb, no Windows APIs required.
    ///
    /// 1. Golden vectors: the 68-byte client response and 41-byte ack verification are
    ///    checked against constants derived independently from the Java server source
    ///    (fixed key + fixed nonces), so a nonce-ordering or domain-string drift fails loudly.
    /// 2. Frame layout: DISPLAY_INFO / MOUSE_REPORT / KEY_REPORT bytes match the Android
    ///    agent's declared payload lengths and little-endian field order.
    /// 3. End-to-end loopback: a real TCP handshake + frame exchange against a fixture that
    ///    transliterates the agent's server code.
    /// </summary>
    public static class SelfTest
    {
        private static int _passed;
        private static int _failed;
        private static int _skipped;

        // ---- Golden vectors (computed from SeamAuthenticator.java semantics) --------------
        private const string KeyHex =
            "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";
        private const string ServerNonceHex =
            "a0a1a2a3a4a5a6a7a8a9aaabacadaeafb0b1b2b3b4b5b6b7b8b9babbbcbdbebf";
        private const string ClientNonceHex =
            "303132333435363738393a3b3c3d3e3f404142434445464748494a4b4c4d4e4f";
        private const string ExpectedClientHmacHex =
            "e9c7aec0a81566c23cdd13fe335c8cda5fc4ce80b69ee6bc9e1f55c3171ec9e7";
        private const string ExpectedServerHmacHex =
            "c44d92e6dc72f81024bad8eb57dcc2a85b290c9f07c05ef80d2410b4e1657caf";
        private const ulong ExpectedSessionId = 0x1122334455667788UL;

        public static int Run()
        {
            Console.WriteLine("=== SEAM Windows host - offline self-test ===");

            TestGoldenHandshakeVector();
            TestFrameLayouts();
            TestLoopbackHandshakeAndFrames();
            TestResourceResolution();
            TestDesktopSupport();
            TestConnectionGuidance();
            TestWirelessDiscovery();

            Console.WriteLine();
            Console.WriteLine($"=== {_passed} passed, {_failed} failed, {_skipped} skipped ===");
            return _failed == 0 ? 0 : 1;
        }

        private static void TestGoldenHandshakeVector()
        {
            byte[] key = Convert.FromHexString(KeyHex);
            byte[] serverNonce = Convert.FromHexString(ServerNonceHex);
            byte[] clientNonce = Convert.FromHexString(ClientNonceHex);
            byte[] sessionIdLe = BitConverter.GetBytes(ExpectedSessionId);
            if (!BitConverter.IsLittleEndian) Array.Reverse(sessionIdLe);

            byte[] challenge = new byte[38];
            Array.Copy(SeamWireProtocol.Magic, 0, challenge, 0, 4);
            challenge[4] = SeamWireProtocol.ProtocolVersion;
            challenge[5] = SeamWireProtocol.TransportUsb;
            Array.Copy(serverNonce, 0, challenge, 6, 32);

            byte[] ack = new byte[41];
            ack[0] = SeamWireProtocol.AuthStatusOk;
            Array.Copy(sessionIdLe, 0, ack, 1, 8);
            Array.Copy(Convert.FromHexString(ExpectedServerHmacHex), 0, ack, 9, 32);

            var reads = new Queue<byte[]>(new[] { challenge, ack });
            byte[]? written = null;

            var auth = new SeamAuthenticator(key);
            ulong sessionId = auth.AuthenticateClientStream(
                _ => reads.Dequeue(),
                bytes => written = bytes,
                clientNonce);

            Check("handshake: client response is 68 bytes", written != null && written.Length == 68);
            Check("handshake: response magic 'SEAM'",
                written != null && written.Take(4).SequenceEqual(SeamWireProtocol.Magic));
            Check("handshake: client nonce echoed after magic",
                written != null && written.Skip(4).Take(32).SequenceEqual(clientNonce));
            Check("handshake: client HMAC matches Java-derived vector",
                written != null && Convert.ToHexString(written.Skip(36).Take(32).ToArray())
                    .Equals(ExpectedClientHmacHex, StringComparison.OrdinalIgnoreCase));
            Check("handshake: server HMAC verified and session id decoded",
                sessionId == ExpectedSessionId);
        }

        private static void TestFrameLayouts()
        {
            byte[] mouse = SeamFrames.MouseReport(0x01, 5, -3, 0, 0);
            Check("frame: MOUSE_REPORT is 8 bytes (opcode+7)", mouse.Length == 8);
            Check("frame: MOUSE_REPORT bytes",
                Convert.ToHexString(mouse).Equals("01010500FDFF0000", StringComparison.OrdinalIgnoreCase));

            byte[] key = SeamFrames.KeyReport(0x02, 0x04, 0, 0, 0, 0, 0);
            Check("frame: KEY_REPORT is 9 bytes (opcode+8)", key.Length == 9);
            Check("frame: KEY_REPORT bytes",
                Convert.ToHexString(key).Equals("020200040000000000", StringComparison.OrdinalIgnoreCase));

            byte[] enter = SeamFrames.CursorEnter(SeamWireProtocol.EdgeLeft, 360, 32768, 0);
            Check("frame: CURSOR_ENTER is 7 bytes (opcode+6)", enter.Length == 7);
            Check("frame: CURSOR_ENTER little-endian normX/normY",
                Convert.ToHexString(enter).Equals("03006801008000", StringComparison.OrdinalIgnoreCase));

            byte[] clipboard = SeamFrames.ClipboardSet("hi");
            Check("frame: CLIPBOARD_SET is 5+len bytes", clipboard.Length == 7);
            Check("frame: CLIPBOARD_SET length prefix is LE",
                Convert.ToHexString(clipboard).Equals("05020000006869", StringComparison.OrdinalIgnoreCase));

            byte[] ping = SeamFrames.Ping(0x0102030405060708UL);
            Check("frame: PING is 9 bytes with LE uint64",
                ping.Length == 9 &&
                Convert.ToHexString(ping).Equals("060807060504030201", StringComparison.OrdinalIgnoreCase));

            // DISPLAY_INFO parse, mirroring UsbChannel.sendDisplayInfo layout.
            byte[] payload = Convert.FromHexString("38046009a40100780000");
            AndroidDisplayInfo info = AndroidDisplayInfo.Parse(payload);
            Check("parse: DISPLAY_INFO 1080x2400 @420dpi 120Hz",
                info.WidthPx == 1080 && info.HeightPx == 2400 &&
                info.DensityDpi == 420 && info.RefreshRateHz == 120 && info.Rotation == 0);

            byte[] edgeReturn = Convert.FromHexString("8200aabb008010");
            AndroidEdgeReturnEvent evt = AndroidEdgeReturnEvent.Parse(edgeReturn.Skip(1).ToArray());
            Check("parse: EDGE_RETURN edge/normY/velocity",
                evt.Edge == SeamWireProtocol.EdgeLeft && evt.NormY == 0x8000 && evt.Velocity == 0x10);
        }

        private static void TestLoopbackHandshakeAndFrames()
        {
            byte[] key = Convert.FromHexString(KeyHex);

            using var agent = new LoopbackAgent(key);
            agent.Start();

            SeamConnection? conn = null;
            try
            {
                var auth = new SeamAuthenticator(key);
                conn = SeamConnection.Connect("127.0.0.1", agent.Port, auth);
                Check("loopback: mutual HMAC handshake succeeded", conn.SessionId != 0);

                AndroidDisplayInfo? display = null;
                ulong pongSent = 0;
                bool pongSeen = false;
                var displaySeen = new ManualResetEventSlim(false);
                var clipboardSeen = new ManualResetEventSlim(false);

                conn.DisplayInfoReceived += d => { display = d; displaySeen.Set(); };
                conn.PongReceived += ts => { pongSent = ts; pongSeen = true; };
                conn.ClipboardReceived += _ => clipboardSeen.Set();

                // Reader must not start before the handlers exist, or DISPLAY_INFO is lost.
                conn.Start();

                Check("loopback: DISPLAY_INFO received within 2s", displaySeen.Wait(2000));
                Check("loopback: DISPLAY_INFO parsed as 1080x2400@420/120",
                    display != null && display.WidthPx == 1080 && display.HeightPx == 2400 &&
                    display.DensityDpi == 420 && display.RefreshRateHz == 120);

                conn.SendPing();
                Thread.Sleep(120);
                Check("loopback: PING answered with PONG", pongSeen && pongSent > 0);

                // 50 reliable mouse frames, as the real host would send.
                for (int i = 0; i < 50; i++)
                {
                    conn.Send(SeamFrames.MouseReport(0, 4, 2, 0, 0));
                }
                Thread.Sleep(200);
                Check("loopback: 50 MOUSE_REPORT frames reached the agent", agent.MouseFrames >= 50);

                // Keyboard: Ctrl+C (modifier + usage 0x06)
                conn.Send(SeamFrames.KeyReport(0x01, 0x06, 0, 0, 0, 0, 0));
                conn.Send(SeamFrames.KeyReport(0x00, 0, 0, 0, 0, 0, 0));
                Thread.Sleep(120);
                Check("loopback: KEY_REPORT frames reached the agent", agent.KeyFrames >= 2);

                conn.Send(SeamFrames.ClipboardSet("seam-from-windows"));
                Thread.Sleep(150);
                Check("loopback: CLIPBOARD_SET text decoded by agent",
                    agent.LastClipboardText == "seam-from-windows");

                Check("loopback: agent observed no auth failure", agent.AuthSucceeded && agent.AuthFailure == null);
            }
            catch (Exception ex)
            {
                Check($"loopback: exception {ex.GetType().Name}: {ex.Message}", false);
            }
            finally
            {
                conn?.Dispose();
            }
        }

        /// <summary>
        /// Resource discovery: the class of bug that broke the first real-device test
        /// (adb not found, agent jar expected under %TEMP%). Runs without a phone.
        /// </summary>
        private static void TestResourceResolution()
        {
            Console.WriteLine("\n[4] Resource discovery (install layout)");

            // -- the executable's own directory, never the current directory --
            string exeDir = AppLayout.ExecutableDirectory();
            Check("layout: executable directory is absolute", Path.IsPathRooted(exeDir));
            string? processPath = Environment.ProcessPath;
            if (processPath != null &&
                !Path.GetFileName(processPath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase) &&
                !Path.GetFileName(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                Check("layout: executable directory is the directory of the running .exe",
                    string.Equals(Path.GetFullPath(Path.GetDirectoryName(processPath)!),
                                  Path.GetFullPath(exeDir), StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                Skip("layout: executable directory is the directory of the running .exe",
                     "hosted by dotnet.exe (dotnet run)");
            }

            // -- resolution must not depend on the working directory --
            string originalCwd = Directory.GetCurrentDirectory();
            string scratch = CreateScratchDir("seam-cwd-independent");
            try
            {
                AppLayout before = AppLayout.ResolveForRoot(exeDir);
                Directory.SetCurrentDirectory(scratch);
                AppLayout after = AppLayout.ResolveForRoot(AppLayout.ExecutableDirectory());
                Check("layout: resolution is independent of the working directory",
                    string.Equals(before.AppRoot, after.AppRoot, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(before.AdbPath ?? "", after.AdbPath ?? "", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(before.AgentJarPath ?? "", after.AgentJarPath ?? "", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                Directory.SetCurrentDirectory(originalCwd);
                TryDelete(scratch);
            }

            // -- a path containing spaces resolves exactly --
            string spacedRoot = Path.Combine(CreateScratchDir("seam spaces"), "SEAM Windows x64 (portable)");
            try
            {
                Directory.CreateDirectory(Path.Combine(spacedRoot, "platform-tools"));
                File.WriteAllBytes(Path.Combine(spacedRoot, "platform-tools", "adb.exe"), new byte[] { 0x4D, 0x5A });
                File.WriteAllBytes(Path.Combine(spacedRoot, "seam-agent.jar"), new byte[] { 0x50, 0x4B });

                AppLayout spaced = AppLayout.ResolveForRoot(spacedRoot, requireResources: true);
                Check("layout: finds adb.exe in a path containing spaces",
                    spaced.AdbPath != null &&
                    string.Equals(spaced.AdbPath,
                        Path.Combine(spacedRoot, "platform-tools", "adb.exe"), StringComparison.OrdinalIgnoreCase));
                Check("layout: finds seam-agent.jar in a path containing spaces",
                    spaced.AgentJarPath != null &&
                    string.Equals(spaced.AgentJarPath,
                        Path.Combine(spacedRoot, "seam-agent.jar"), StringComparison.OrdinalIgnoreCase));
                Check("layout: bundled adb is marked as bundled (not PATH)",
                    spaced.AdbIsBundled);

                // -- missing resources produce an actionable error, not a silent fallback --
                File.Delete(Path.Combine(spacedRoot, "seam-agent.jar"));
                bool threw = false;
                string message = "";
                try
                {
                    AppLayout.ResolveForRoot(spacedRoot, requireResources: true);
                }
                catch (SeamResourceException ex)
                {
                    threw = true;
                    message = ex.Message;
                }
                Check("layout: missing seam-agent.jar raises a clear error", threw && message.Contains("seam-agent.jar"));
                Check("layout: the error lists where it looked", message.Contains("Searched"));

                // -- an explicit override that does not exist is rejected, not ignored --
                bool overrideThrew = false;
                try
                {
                    AppLayout.ResolveForRoot(spacedRoot, adbOverride: Path.Combine(spacedRoot, "nope", "adb.exe"));
                }
                catch (SeamResourceException)
                {
                    overrideThrew = true;
                }
                Check("layout: a bad --adb override is rejected", overrideThrew);
            }
            finally
            {
                TryDelete(spacedRoot);
            }

            // -- the real package: adb is executable and the agent jar is present --
            AppLayout real = AppLayout.ResolveForRoot(AppLayout.ExecutableDirectory());
            if (real.AdbPath != null)
            {
                try
                {
                    var bootstrapper = new AdbBootstrapper(real.AgentJarPath ?? "unused", real.AdbPath);
                    string[] version = bootstrapper.AdbVersion();
                    Check("process: the resolved adb.exe executes", version.Length > 0 && version[0].Contains("Android Debug Bridge"));
                }
                catch (Exception ex)
                {
                    Check($"process: the resolved adb.exe executes ({ex.Message})", false);
                }
            }
            else
            {
                Skip("process: the resolved adb.exe executes", "no adb next to this executable");
            }

            if (real.AgentJarPath != null)
            {
                long size = new FileInfo(real.AgentJarPath).Length;
                Check("package: the resolved seam-agent.jar looks like a jar", size > 4096);
            }
            else
            {
                Skip("package: the resolved seam-agent.jar looks like a jar", "no seam-agent.jar next to this executable");
            }

            // -- pairing material must never reach the log --
            string[] redacted = AdbBootstrapper.Redact(new[]
            {
                "-s", "R58M12345", "shell",
                "CLASSPATH=/data/local/tmp/seam-agent.jar app_process /system/bin com.seam.agent.Main --auth-key=0123456789abcdef"
            });
            Check("logging: --auth-key is redacted in adb diagnostics",
                !string.Join(" ", redacted).Contains("0123456789abcdef") &&
                string.Join(" ", redacted).Contains("--auth-key=***"));
        }

        /// <summary>
        /// The desktop application's non-visual pieces: preference storage (which must never
        /// touch the pairing key) and the diagnostics text (which must never contain a key).
        /// The window itself is exercised by `--ui-smoke` in CI.
        /// </summary>
        private static void TestDesktopSupport()
        {
            Console.WriteLine("\n[5] Desktop application support");

            string scratch = CreateScratchDir("seam-desktop");
            try
            {
                string path = Path.Combine(scratch, "ui-settings.json");

                UiSettings defaults = UiSettings.Load(path);
                Check("settings: sane defaults when no file exists",
                    defaults.Edge == "right" && defaults.RoutingOnConnect &&
                    defaults.AutoConnect && defaults.MaxLogLines == 500);

                var written = new UiSettings
                {
                    Edge = "left",
                    RoutingOnConnect = false,
                    AutoConnect = true,
                    MaxLogLines = 250,
                    LastDeviceSerial = "SM_A055F"
                };
                written.Save(path);
                Check("settings: written to disk", File.Exists(path));

                UiSettings loaded = UiSettings.Load(path);
                Check("settings: round-trips every field",
                    loaded.Edge == "left" && !loaded.RoutingOnConnect && loaded.AutoConnect &&
                    loaded.MaxLogLines == 250 && loaded.LastDeviceSerial == "SM_A055F");

                Check("settings: edge maps to the wire constant",
                    new UiSettings { Edge = "top" }.EdgeCode == SeamWireProtocol.EdgeTop &&
                    new UiSettings { Edge = "bottom" }.EdgeCode == SeamWireProtocol.EdgeBottom);

                File.WriteAllText(path, "{ this is not json");
                Check("settings: a corrupt file falls back to defaults",
                    UiSettings.Load(path).Edge == "right");

                File.WriteAllText(path, "{\"Edge\":\"sideways\",\"MaxLogLines\":0}");
                UiSettings clamped = UiSettings.Load(path);
                Check("settings: hand-edited values are clamped",
                    clamped.Edge == "right" && clamped.MaxLogLines == 50);

                Check("settings: preferences live outside the pairing-key folder",
                    !UiSettings.DefaultPath.Contains(".seam", StringComparison.OrdinalIgnoreCase));

                string report = DiagnosticsReport.Build(null, null, out _);
                Check("diagnostics: reports the app folder, adb and a result line",
                    report.Contains("app dir") && report.Contains("ADB") && report.Contains("RESULT:"));
                Check("diagnostics: reports the pairing-key location", report.Contains("PAIRING KEY"));
                Check("diagnostics: never prints 64 hex characters of key material",
                    !System.Text.RegularExpressions.Regex.IsMatch(report, "[0-9a-fA-F]{64}"));

                // When a key already exists (second run, or a developer machine) the report must
                // show a fingerprint instead of the key itself.
                if (File.Exists(SeamAuthenticator.KeyStorePath))
                {
                    Check("diagnostics: shows a fingerprint once a key exists", report.Contains("fingerprint"));
                }
                else
                {
                    Skip("diagnostics: shows a fingerprint once a key exists", "no pairing key on this machine yet");
                }
            }
            finally
            {
                TryDelete(scratch);
            }
        }


        /// <summary>
        /// The USB/wireless guidance split. These are the rules the Home page follows, tested
        /// without a phone or a desktop: the old window showed one paragraph for every empty
        /// device list, which told users to install an OEM USB driver even when their phone was
        /// simply not plugged in, and pointed at wireless debugging this host cannot set up.
        /// </summary>

        /// <summary>
        /// The wireless autopilot, ported from macOS `runAutoPilotTick`: which endpoint it dials,
        /// when it must do nothing, and how it treats the phone's pairing requirement. Pure logic,
        /// so it runs with no phone, no adb and no Wi-Fi.
        /// </summary>
        private static void TestWirelessDiscovery()
        {
            Console.WriteLine("\n[7] Wireless discovery and autopilot");

            const string mdnsSample =
                "List of discovered mdns services\r\n" +
                "adb-R58M12ABCDE-Xy12Zt\t_adb-tls-connect._tcp\t192.168.1.42:37231\r\n" +
                "adb-R58M12ABCDE-Qq77Vw\t_adb-tls-pairing._tcp\t192.168.1.42:41003\r\n";

            List<WirelessService> services = WirelessDiscovery.ParseMdnsServices(mdnsSample);
            Check("wireless: mDNS output yields the advertised endpoints",
                services.Count == 2 &&
                services[0].CanConnect && services[0].Address == "192.168.1.42:37231" &&
                services[1].CanPair && services[1].Address == "192.168.1.42:41003");
            Check("wireless: a header-only answer yields nothing",
                WirelessDiscovery.ParseMdnsServices("List of discovered mdns services\r\n").Count == 0);
            Check("wireless: an empty or missing answer is not a crash",
                WirelessDiscovery.ParseMdnsServices(null).Count == 0 &&
                WirelessDiscovery.ParseMdnsServices("").Count == 0);
            Check("wireless: IPv6 advertisements are not dialled",
                WirelessDiscovery.ParseMdnsServices(
                    "x\t_adb-tls-connect._tcp\t[fe80::1]:40000\r\n").Count == 0);

            var usbPhone = new AdbDevice { Serial = "R58M12ABCDE", State = "device", Model = "SM_A055F" };
            var wifiPhone = new AdbDevice { Serial = "192.168.1.42:37231", State = "device", Model = "SM_A055F" };
            var deadWifi = new AdbDevice { Serial = "192.168.1.42:37231", State = "offline", Model = "SM_A055F" };

            WirelessContext Ctx(bool auto = true, bool userDisconnected = false, bool connected = false,
                bool busy = false, bool mdnsDue = true, AdbDevice[]? devices = null,
                List<WirelessService>? mdns = null) => new WirelessContext
                {
                    AutoConnectEnabled = auto,
                    UserDisconnected = userDisconnected,
                    Connected = connected,
                    Busy = busy,
                    MdnsDue = mdnsDue,
                    Devices = devices ?? Array.Empty<AdbDevice>(),
                    Services = mdns ?? new List<WirelessService>()
                };

            Check("wireless: nothing advertised and nothing plugged in means nothing to do",
                WirelessDiscovery.Decide(Ctx()).Kind == WirelessActionKind.None);

            Check("wireless: an authorised USB phone is connected as before",
                WirelessDiscovery.Decide(Ctx(devices: new[] { usbPhone })) is
                    { Kind: WirelessActionKind.ConnectDevice, Target: "R58M12ABCDE" });

            Check("wireless: an already-paired Wi-Fi device is used without adb connect",
                WirelessDiscovery.Decide(Ctx(devices: new[] { wifiPhone })) is
                    { Kind: WirelessActionKind.ConnectDevice, Target: "192.168.1.42:37231" });

            Check("wireless: USB wins when both transports are present",
                WirelessDiscovery.Decide(Ctx(devices: new[] { wifiPhone, usbPhone })).Target == "R58M12ABCDE");

            Check("wireless: an advertised connect endpoint is dialled through adb",
                WirelessDiscovery.Decide(Ctx(mdns: services)) is
                    { Kind: WirelessActionKind.AdbConnect, Target: "192.168.1.42:37231" });

            Check("wireless: mDNS is only probed on its own tick",
                WirelessDiscovery.Decide(Ctx(mdnsDue: false, mdns: services)).Kind == WirelessActionKind.None);

            Check("wireless: pairing-only advertisement is reported as awaiting pairing",
                WirelessDiscovery.Decide(Ctx(mdns: new List<WirelessService> { services[1] })) is
                    { Kind: WirelessActionKind.NeedPairing, Target: "192.168.1.42:41003" });

            Check("wireless: an endpoint adb already lists is not dialled twice",
                WirelessDiscovery.Decide(Ctx(devices: new[] { wifiPhone }, mdns: services)).Kind ==
                WirelessActionKind.ConnectDevice);

            Check("wireless: a dropped Wi-Fi transport is re-dialled (automatic recovery)",
                WirelessDiscovery.Decide(Ctx(devices: new[] { deadWifi })) is
                    { Kind: WirelessActionKind.AdbConnect, Target: "192.168.1.42:37231" });

            Check("wireless: no duplicate session while connecting or connected",
                WirelessDiscovery.Decide(Ctx(busy: true, devices: new[] { usbPhone })).Kind == WirelessActionKind.None &&
                WirelessDiscovery.Decide(Ctx(connected: true, devices: new[] { wifiPhone })).Kind == WirelessActionKind.None);

            Check("wireless: Disconnect stops the autopilot until Connect is pressed",
                WirelessDiscovery.Decide(Ctx(userDisconnected: true, devices: new[] { wifiPhone })).Kind ==
                WirelessActionKind.None);

            Check("wireless: automatic connection can be switched off",
                WirelessDiscovery.Decide(Ctx(auto: false, devices: new[] { wifiPhone }, mdns: services)).Kind ==
                WirelessActionKind.None);

            string pairHeadline = ConnectionDiagnosis.Headline(ConnectionProblem.PairingRequired);
            string pairText = ConnectionDiagnosis.Guidance(ConnectionProblem.PairingRequired);
            Check("wireless: the pairing state explains the one-time code, not a driver",
                pairHeadline.Contains("pairing", StringComparison.OrdinalIgnoreCase) &&
                pairText.Contains("pairing code", StringComparison.OrdinalIgnoreCase) &&
                !pairText.Contains("OEM", StringComparison.OrdinalIgnoreCase) &&
                pairText.Length <= 180);
        }

        private static void TestConnectionGuidance()
        {
            Console.WriteLine("\n[6] Connection guidance (USB vs wireless)");

            var none = new List<AdbDevice>();
            Check("guidance: missing adb is its own condition",
                ConnectionDiagnosis.ClassifyDevices(adbAvailable: false, none) == ConnectionProblem.AdbMissing);
            Check("guidance: empty device list means no device",
                ConnectionDiagnosis.ClassifyDevices(adbAvailable: true, none) == ConnectionProblem.NoDevice);

            var unauthorized = new List<AdbDevice>
            {
                new AdbDevice { Serial = "R58M12ABCDE", State = "unauthorized", Model = "SM_A055F" }
            };
            Check("guidance: adb 'unauthorized' maps to the authorise step",
                ConnectionDiagnosis.ClassifyDevices(true, unauthorized) == ConnectionProblem.Unauthorized);

            var offline = new List<AdbDevice>
            {
                new AdbDevice { Serial = "R58M12ABCDE", State = "offline", Model = "SM_A055F" }
            };
            Check("guidance: adb 'offline' maps to the stale-cable step",
                ConnectionDiagnosis.ClassifyDevices(true, offline) == ConnectionProblem.Offline);

            var authorizedUsb = new List<AdbDevice>
            {
                new AdbDevice { Serial = "R58M12ABCDE", State = "device", Model = "Pixel_8" }
            };
            Check("guidance: an authorised USB phone is ready, not a problem",
                ConnectionDiagnosis.ClassifyDevices(true, authorizedUsb) == ConnectionProblem.None);

            var authorizedWireless = new List<AdbDevice>
            {
                new AdbDevice { Serial = "192.168.1.42:5555", State = "device", Model = "Pixel_8" }
            };
            Check("guidance: an authorised Wi-Fi transport is ready, not a problem",
                ConnectionDiagnosis.ClassifyDevices(true, authorizedWireless) == ConnectionProblem.None);

            var deadWireless = new List<AdbDevice>
            {
                new AdbDevice { Serial = "192.168.1.42:5555", State = "offline", Model = "Pixel_8" }
            };
            Check("guidance: a Wi-Fi address that stopped answering is its own condition",
                ConnectionDiagnosis.ClassifyDevices(true, deadWireless) == ConnectionProblem.WirelessUnavailable);

            // ---- the wording itself ----
            string noDevice = ConnectionDiagnosis.Guidance(ConnectionProblem.NoDevice);
            Check("guidance: no-device text asks for USB debugging and the ALLOW prompt",
                noDevice.Contains("USB debugging", StringComparison.Ordinal) &&
                noDevice.Contains("ALLOW", StringComparison.Ordinal));
            Check("guidance: no-device text does not prescribe an OEM driver",
                !noDevice.Contains("OEM", StringComparison.OrdinalIgnoreCase));

            string wireless = ConnectionDiagnosis.Guidance(ConnectionProblem.WirelessUnavailable);
            Check("guidance: unreachable Wi-Fi text offers adb connect or the cable, never a driver",
                wireless.Contains("adb connect", StringComparison.OrdinalIgnoreCase) &&
                wireless.Contains("cable", StringComparison.OrdinalIgnoreCase) &&
                !wireless.Contains("driver", StringComparison.OrdinalIgnoreCase));

            string unauthorizedText = ConnectionDiagnosis.Guidance(ConnectionProblem.Unauthorized);
            Check("guidance: the USB authorise text never mentions wireless",
                !unauthorizedText.Contains("wireless", StringComparison.OrdinalIgnoreCase) &&
                !unauthorizedText.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase));

            Check("guidance: every condition has its own headline",
                ConnectionDiagnosis.Headline(ConnectionProblem.NoDevice) !=
                ConnectionDiagnosis.Headline(ConnectionProblem.Unauthorized) &&
                ConnectionDiagnosis.Headline(ConnectionProblem.AgentClosed) !=
                ConnectionDiagnosis.Headline(ConnectionProblem.HandshakeFailed));

            Check("guidance: every condition fits the hero and banner (text bounded)",
                AllHaveText());

            // ---- classification of our own connect failures ----
            Check("guidance: the transport EOF is reported as the phone closing the session",
                ConnectionDiagnosis.ClassifyFailure("Remote peer closed the connection (EOF)") ==
                ConnectionProblem.AgentClosed);
            Check("guidance: an agent that never started is its own condition",
                ConnectionDiagnosis.ClassifyFailure(
                    "Timed out waiting for the detached seam-agent daemon.\n--- agent log ---\nnothing") ==
                ConnectionProblem.AgentNotReady);
            Check("guidance: a rejected handshake is its own condition",
                ConnectionDiagnosis.ClassifyFailure("Server HMAC mismatch during handshake") ==
                ConnectionProblem.HandshakeFailed);
            Check("guidance: an unplugged phone during connect maps to no-device",
                ConnectionDiagnosis.ClassifyFailure(
                    "The phone (Pixel 8) is no longer visible to adb. Re-plug the USB cable and press Connect again.") ==
                ConnectionProblem.NoDevice);
            Check("guidance: an unknown failure still gets user text",
                ConnectionDiagnosis.Headline(ConnectionDiagnosis.ClassifyFailure("something odd")) ==
                ConnectionDiagnosis.Headline(ConnectionProblem.TransportError) &&
                ConnectionDiagnosis.Guidance(ConnectionProblem.TransportError).Contains("Activity", StringComparison.Ordinal));
        }

        /// <summary>
        /// Every reported condition must be printable in the window. The Home sentence sits in a
        /// row that is about 3.8 lines tall at 96 dpi (heroDetail was 297x65 in the UI smoke run),
        /// so the copy is bounded rather than left to clip.
        /// </summary>
        private static bool AllHaveText()
        {
            foreach (ConnectionProblem problem in Enum.GetValues<ConnectionProblem>())
            {
                if (problem == ConnectionProblem.None)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(ConnectionDiagnosis.Headline(problem)) ||
                    string.IsNullOrWhiteSpace(ConnectionDiagnosis.Guidance(problem)) ||
                    string.IsNullOrWhiteSpace(ConnectionDiagnosis.ShortGuidance(problem)))
                {
                    return false;
                }

                // Measured room: ~180 characters wrap to three lines in the hero row; the banner
                // is a single line, so its text must stay well under that.
                if (ConnectionDiagnosis.Guidance(problem).Length > 180 ||
                    ConnectionDiagnosis.ShortGuidance(problem).Length > 90 ||
                    ConnectionDiagnosis.Headline(problem).Length > 45)
                {
                    return false;
                }
            }
            return true;
        }

        private static string CreateScratchDir(string name)
        {
            string dir = Path.Combine(Path.GetTempPath(), name.Replace(' ', '_') + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void TryDelete(string dir)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // best effort: a leftover temp folder must never fail the suite
            }
        }

        private static void Skip(string name, string reason)
        {
            _skipped++;
            Console.WriteLine($"  [SKIP] {name} -- {reason}");
        }

        private static void Check(string name, bool condition)
        {
            if (condition)
            {
                _passed++;
                Console.WriteLine($"  [PASS] {name}");
            }
            else
            {
                _failed++;
                Console.WriteLine($"  [FAIL] {name}");
            }
        }
    }
}
