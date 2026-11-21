using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Seam.WindowsHost.Bootstrap;
using Seam.WindowsHost.Gui;
using Seam.WindowsHost.Host;
using Seam.WindowsHost.Input;
using Seam.WindowsHost.Protocol;
using Seam.WindowsHost.Testing;
using Seam.WindowsHost.Transport;

namespace Seam.WindowsHost
{
    internal sealed class Options
    {
        public bool ShowHelp;
        public bool SelfTest;
        public bool Diagnose;
        public bool LaunchGui;
        public bool UiSmoke;
        public string? ScreenshotPath;
        public bool Trajectory;
        public bool ForceCapture;
        public string? Connect;
        public string? JarPath;
        public string? AdbPath;
        public string? Serial;
        public byte Edge = SeamWireProtocol.EdgeRight;
    }

    internal static class Program
    {
        // STAThread is required by WinForms; the console paths do not care.
        [STAThread]
        private static int Main(string[] args)
        {
            Options options = ParseArgs(args);

            if (options.ShowHelp)
            {
                PrepareConsole();
                PrintUsage();
                return 0;
            }
            if (options.SelfTest)
            {
                PrepareConsole();
                return SelfTest.Run();
            }
            if (options.Diagnose)
            {
                PrepareConsole();
                return RunDiagnose(options);
            }
            if (options.UiSmoke)
            {
                // Renders the real window without a message loop; used by CI.
                PrepareConsole();
                return SeamGui.RunSmoke(options.ScreenshotPath);
            }
            if (options.LaunchGui)
            {
                // Double-click / Start Menu / shortcut: a window, never a console.
                return SeamGui.Run();
            }

            PrepareConsole();

            try
            {
                Console.WriteLine("=== SEAM Windows host ===");

                // Resolve every bundled resource from the executable's own directory. This is the
                // fix for the "adb not found / jar expected in %TEMP%" failures: it never looks at
                // the current directory and never relies on PATH.
                AppLayout layout = AppLayout.Resolve(options.AdbPath, options.JarPath);
                Console.WriteLine($"[Boot ] app dir: {layout.AppRoot}");
                Console.WriteLine($"[Boot ] adb    : {layout.AdbPath}");
                Console.WriteLine($"[Boot ] agent  : {layout.AgentJarPath}");

                SeamAuthenticator authenticator = SeamAuthenticator.LoadOrCreatePersistentKey();
                Console.WriteLine($"[Auth ] pairing key fingerprint {authenticator.KeyFingerprint} " +
                                  $"(store: {SeamAuthenticator.KeyStorePath})");

                (string host, int port, string label) = ResolveEndpoint(options, authenticator, layout);
                Console.WriteLine($"[Link ] {label}");

                using SeamConnection connection = SeamConnection.Connect(host, port, authenticator);
                Console.WriteLine($"[Auth ] mutual HMAC-SHA256 OK - session 0x{connection.SessionId:X16}");

                AndroidDisplayInfo? display = null;
                var displayReady = new ManualResetEventSlim(false);
                connection.DisplayInfoReceived += d =>
                {
                    display = d;
                    Console.WriteLine($"[Displ] DISPLAY_INFO  {d}");
                    displayReady.Set();
                };
                connection.ClipboardReceived += text =>
                    Console.WriteLine($"[Clip ] <- Android: '{Truncate(text)}'");
                connection.EdgeReturnReceived += evt =>
                    Console.WriteLine($"[Edge ] <- Android: {evt}");
                connection.Closed += reason =>
                    Console.WriteLine($"[Link ] closed: {reason}");

                // Handlers are attached: only now is it safe to start reading (the agent sends
                // DISPLAY_INFO immediately after auth and a frame read by nobody is lost).
                connection.Start();

                if (!displayReady.Wait(3000))
                {
                    Console.WriteLine("[WARN] No DISPLAY_INFO within 3 s - continuing anyway.");
                }

                if (options.Trajectory)
                {
                    return RunTrajectory(connection, display);
                }

                return RunInteractive(connection, display, options);
            }
            catch (SeamResourceException ex)
            {
                // Missing bundled resources are an installation problem, not a bug: say so once,
                // point at the diagnostics, and exit with a distinct code.
                Console.Error.WriteLine($"[FATAL] {ex.Message}");
                Console.Error.WriteLine();
                Console.Error.WriteLine("Run `seam-host.exe --diagnose` to see every path SEAM searched.");
                return 3;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[FATAL] {ex.Message}");
                return 1;
            }
        }

        // ---- Connection resolution -------------------------------------------------------

        private static (string host, int port, string label) ResolveEndpoint(
            Options options, SeamAuthenticator authenticator, AppLayout layout)
        {
            if (!string.IsNullOrEmpty(options.Connect))
            {
                string connect = options.Connect!;
                int colon = connect.LastIndexOf(':');
                if (colon <= 0 || !int.TryParse(connect.Substring(colon + 1), out int parsedPort))
                {
                    throw new ArgumentException($"--connect expects host:port, got '{connect}'");
                }
                return (connect.Substring(0, colon), parsedPort,
                        $"direct TCP -> {connect} (bypassing ADB)");
            }

            // jar/agent paths come from AppLayout, which already proved they exist.
            var bootstrapper = new AdbBootstrapper(layout.AgentJarPath!, layout.AdbPath!);

            var devices = bootstrapper.ListDevices();

            if (devices.Count == 0)
            {
                throw new InvalidOperationException(NoDeviceMessage(bootstrapper.AdbPath));
            }

            if (devices.Count > 1 && options.Serial == null)
            {
                Console.WriteLine("[Dev ] several devices are connected:");
                foreach (AdbDevice d in devices)
                {
                    Console.WriteLine($"        {d}");
                }
                Console.WriteLine("[Dev ] using the first authorized one; pass --device SERIAL to choose.");
            }

            AdbDevice device = options.Serial != null
                ? devices.Find(d => d.Serial == options.Serial)
                  ?? throw new InvalidOperationException(
                      $"Device '{options.Serial}' is not in `adb devices`. Connected: {string.Join(", ", devices)}")
                : devices.Find(d => d.IsAuthorized)
                  ?? throw new InvalidOperationException(DescribeDeviceProblem(devices));

            Console.WriteLine($"[Dev ] {device}");

            var endpoints = bootstrapper.BootstrapSession(device, authenticator);
            if (endpoints.Count > 0)
            {
                Console.WriteLine($"[Boot ] agent Wi-Fi endpoints: {string.Join(", ", endpoints)}");
            }

            int forwardedPort = bootstrapper.SetupUsbForward(device.Serial);
            Console.WriteLine($"[Boot ] adb forward tcp:{forwardedPort} -> localabstract:seam_uhid");

            return ("127.0.0.1", forwardedPort,
                    $"USB via ADB forward -> 127.0.0.1:{forwardedPort} ({device.Model})");
        }

        /// <summary>Actionable text shown when `adb devices` returns nothing.</summary>
        private static string NoDeviceMessage(string adbPath) =>
            "No Android device is visible to adb.\n" +
            $"  adb used: {adbPath}\n" +
            "  1. Enable Developer options -> USB debugging on the phone.\n" +
            "  2. Plug the cable in and accept the 'Allow USB debugging?' prompt ON THE PHONE.\n" +
            "  3. " + ConnectionDiagnosis.Guidance(ConnectionProblem.NoDevice) + "\n" +
            "  4. Wireless: this host dials adb forwards over USB; it does not create a wireless\n" +
            "     debugging pairing. --connect <phone-ip>:27186 works once the agent is running.\n" +
            "  5. Run  seam-host.exe --diagnose  to see which adb was picked and what it reports.";

        /// <summary>Turns adb device states into instructions instead of a bare list.</summary>
        private static string DescribeDeviceProblem(List<AdbDevice> devices)
        {
            var lines = new List<string> { "The phone is connected but SEAM cannot use it yet." };
            foreach (AdbDevice d in devices)
            {
                lines.Add($"  {d.Serial}  state={d.State}  model={d.Model}");
                switch (d.State)
                {
                    case "unauthorized":
                        lines.Add("    -> Unlock the phone and tap ALLOW on the 'Allow USB debugging?' dialog.");
                        lines.Add("    -> If no dialog appears, run:  adb kill-server  then re-plug the cable.");
                        lines.Add("    -> 'Always allow from this computer' avoids repeating this every time.");
                        break;
                    case "offline":
                        lines.Add("    -> The device answered but the connection is stale: re-plug the USB cable,");
                        lines.Add("       or run  adb kill-server  and try again.");
                        break;
                    case "no permissions":
                        // This state really is Windows refusing the device, so a driver is the fix.
                        lines.Add("    -> Windows denied access to the USB device: install the OEM ADB USB");
                        lines.Add("       driver for this phone model, then re-plug the cable.");
                        break;
                    default:
                        lines.Add("    -> Unknown state; run  adb devices -l  and  seam-host.exe --diagnose.");
                        break;
                }
            }
            return string.Join(Environment.NewLine, lines);
        }

        private static string VersionString => DiagnosticsReport.VersionString;

        /// <summary>
        /// `--diagnose`: prints exactly where SEAM looked for its resources and what it found.
        /// Exists because the first prototype failed on real hardware with "adb not found" and
        /// "jar expected in %TEMP%", and there was no way for a user to see why.
        /// </summary>
        private static int RunDiagnose(Options options)
        {
            string report = DiagnosticsReport.Build(options.AdbPath, options.JarPath, out bool ok);
            Console.WriteLine(report);
            return ok ? 0 : 2;
        }

        // ---- Scripted mouse stream (phone-free / hook-free verification) -----------------

        private static int RunTrajectory(SeamConnection connection, AndroidDisplayInfo? display)
        {
            Console.WriteLine("[Test ] Streaming a scripted figure-eight to Android (no input hooks).");

            connection.Send(SeamFrames.CursorEnter(SeamWireProtocol.EdgeLeft, 360, 32768, 0));
            Thread.Sleep(60);

            int frames = 240;
            double radius = Math.Min(display?.WidthPx ?? 720, display?.HeightPx ?? 1600) * 0.25;
            int prevX = 0, prevY = 0;

            for (int i = 1; i <= frames; i++)
            {
                double theta = (2.0 * Math.PI * i) / frames;
                int curX = (int)Math.Round(radius * Math.Sin(theta));
                int curY = (int)Math.Round(radius * Math.Sin(2.0 * theta) * 0.5);
                short dx = (short)(curX - prevX);
                short dy = (short)(curY - prevY);
                prevX = curX;
                prevY = curY;

                connection.Send(SeamFrames.MouseReport(0, dx, dy, 0, 0));
                Thread.Sleep(4);
            }

            // Push hard to the left to provoke EDGE_RETURN from the agent.
            for (int i = 0; i < 120; i++)
            {
                connection.Send(SeamFrames.MouseReport(0, -4, 0, 0, 0));
                Thread.Sleep(4);
            }
            Thread.Sleep(200);

            Console.WriteLine($"[Test ] trajectory complete - frames sent={connection.FramesSent}, " +
                              $"frames received={connection.FramesReceived}");

            connection.Send(SeamFrames.CursorLeave(1));
            Thread.Sleep(100);
            Console.WriteLine("[Test ] CURSOR_LEAVE sent. If the phone's cursor moved, the " +
                              "protocol path is proven end-to-end.");
            return 0;
        }

        // ---- Interactive desktop mode ----------------------------------------------------

        private static int RunInteractive(SeamConnection connection, AndroidDisplayInfo? display, Options options)
        {
            WindowsInputCapture.EnableDpiAwareness();

            using var capture = new WindowsInputCapture(connection, options.Edge, options.ForceCapture,
                                                        msg => Console.WriteLine(msg));
            connection.EdgeReturnReceived += evt => capture.HandleEdgeReturn(evt);

            capture.Start();

            Console.WriteLine();
            Console.WriteLine("  Move the pointer to the " + EdgeName(options.Edge) + " edge to take control of the phone.");
            Console.WriteLine("  Ctrl+Alt+Q returns control to Windows. Press 'q' here to quit.");
            if (options.ForceCapture)
            {
                Console.WriteLine("  --capture: the FIRST mouse movement will route to Android immediately.");
            }
            Console.WriteLine();

            var quit = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                quit.Set();
            };

            DateTime nextStatus = DateTime.UtcNow.AddSeconds(2);
            while (!quit.IsSet && connection.IsRunning)
            {
                // Console.KeyAvailable/ReadKey throw InvalidOperationException when stdin is
                // redirected (service wrapper, IDE runner, `... | tee`). Skip polling then; the
                // Ctrl+Alt+Q panic hotkey and Ctrl+C still work.
                if (!Console.IsInputRedirected && Console.KeyAvailable)
                {
                    ConsoleKeyInfo key = Console.ReadKey(true);
                    if (key.Key == ConsoleKey.Q) break;
                    if (key.Key == ConsoleKey.R) capture.ForceReturn();
                }

                // Polling keeps the loop responsive without a busy spin.
                Thread.Sleep(40);

                // Hook-thread messages are queued, not written from the callback (console I/O
                // inside a low-level hook can block past LowLevelHooksTimeout and be unhooked).
                capture.DrainLog();

                if (DateTime.UtcNow >= nextStatus)
                {
                    nextStatus = DateTime.UtcNow.AddSeconds(2);
                    Console.WriteLine(
                        $"  [status] routing={(capture.IsRoutingToAndroid ? "ANDROID" : "windows")} " +
                        $"fwd={capture.EventsForwarded} recv={connection.FramesReceived} " +
                        $"sent={connection.FramesSent} unmappedKeys={capture.UnsupportedKeys}");
                }
            }

            capture.ForceReturn();
            Console.WriteLine("[Exit ] control returned to Windows; hooks removed.");
            return 0;
        }

        private static string EdgeName(byte edge) => edge switch
        {
            SeamWireProtocol.EdgeLeft => "left",
            SeamWireProtocol.EdgeTop => "top",
            SeamWireProtocol.EdgeBottom => "bottom",
            _ => "right"
        };

        // (pairing keys are never logged; KeyFingerprint replaces the old partial mask)

        private static string Truncate(string s) =>
            s.Length <= 48 ? s : s.Substring(0, 45) + "...";

        // ---- Console attachment -----------------------------------------------------------

        // The executable is a GUI-subsystem binary, so double-clicking it never flashes a
        // console. The CLI modes (--diagnose, --self-test, --trajectory, --capture, --help)
        // borrow the launching terminal's console instead, so their output lands where the user
        // expects. When stdout is redirected (CI, pipes, `> file`) the inherited handle already
        // works and nothing is attached.

        private const uint AttachParentProcess = 0xFFFFFFFF;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(uint dwProcessId);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        private static void PrepareConsole()
        {
            try
            {
                if (Console.IsOutputRedirected)
                {
                    return; // pipe or file: GetStdHandle is already valid
                }
                if (GetConsoleWindow() != IntPtr.Zero)
                {
                    return; // a console is already attached
                }
                if (!AttachConsole(AttachParentProcess))
                {
                    return; // launched from Explorer with no terminal; nothing to print to
                }

                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
            }
            catch
            {
                // Console plumbing is best effort; exit codes still carry the result.
            }
        }

        // ---- CLI -------------------------------------------------------------------------

        private static Options ParseArgs(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--self-test": o.SelfTest = true; break;
                    case "--trajectory": o.Trajectory = true; break;
                    case "--capture": o.ForceCapture = true; break;
                    case "--diagnose": o.Diagnose = true; break;
                    case "--gui": o.LaunchGui = true; break;
                    case "--ui-smoke": o.UiSmoke = true; break;
                    case "--ui-screenshot":
                        o.UiSmoke = true;
                        o.ScreenshotPath = Next(args, ref i);
                        break;
                    case "-h":
                    case "--help": o.ShowHelp = true; break;
                    case "--connect": o.Connect = Next(args, ref i); break;
                    case "--jar": o.JarPath = Next(args, ref i); break;
                    case "--adb": o.AdbPath = Next(args, ref i); break;
                    case "--device": o.Serial = Next(args, ref i); break;
                    case "--edge":
                        o.Edge = (Next(args, ref i) ?? "right") switch
                        {
                            "left" => SeamWireProtocol.EdgeLeft,
                            "top" => SeamWireProtocol.EdgeTop,
                            "bottom" => SeamWireProtocol.EdgeBottom,
                            _ => SeamWireProtocol.EdgeRight
                        };
                        break;
                    default:
                        Console.Error.WriteLine($"Unknown argument: {args[i]}");
                        o.ShowHelp = true;
                        break;
                }
            }

            // No arguments at all: this is the desktop application's normal launch path.
            if (args.Length == 0)
            {
                o.LaunchGui = true;
            }

            return o;
        }

        private static string? Next(string[] args, ref int i)
        {
            if (i + 1 >= args.Length) return null;
            return args[++i];
        }

        private static void PrintUsage()
        {
            Console.WriteLine(
@"
SEAM Windows host - talks to the existing, unmodified Android agent.

USAGE
  seam-host.exe [options]

MODES
  (default)        Opens the SEAM desktop window. This is what the Start Menu and
                   desktop shortcuts run; there is no console window.
  --gui            Force the desktop window (same as no arguments).
  --capture        Console mode: connect, then capture Windows input at the edge.
  --self-test      Offline protocol/auth/frame/resource tests (no phone needed).
  --diagnose       Show where SEAM looked for adb.exe, seam-agent.jar and the pairing
                   key, what it found, and which devices adb sees.
  --trajectory     Connect and stream a scripted mouse path (no input hooks).
                   Use this to prove the wire path on real hardware.
  --ui-smoke       Build the window, verify it initialises, exit. Used by CI.
  --ui-screenshot PATH
                   Same, but also render the window to a PNG at PATH.

OPTIONS
  --connect H:P    Bypass ADB and connect directly to H:P (e.g. phone-ip:27186).
  --edge E         Screen edge the phone sits on: right (default), left, top, bottom.
  --capture        Console mode: route to Android on the first mouse movement.
  --device SERIAL  Pick a specific adb serial.
  --jar PATH       Override seam-agent.jar (default: next to seam-host.exe).
  --adb PATH       Override adb.exe (default: platform-tools\adb.exe next to seam-host.exe,
                   then PATH and Android SDK locations).
  -h, --help       This text.

BUNDLED RESOURCES
  The installer and the portable ZIP both ship adb.exe under platform-tools\ and
  seam-agent.jar next to seam-host.exe. SEAM resolves them from the folder that
  contains seam-host.exe (not the current directory, not PATH, not %TEMP%), so it
  works from a shortcut, Explorer, PowerShell or any working directory.

PANIC
  Ctrl+Alt+Q returns control to Windows while routing.
");
        }
    }
}
