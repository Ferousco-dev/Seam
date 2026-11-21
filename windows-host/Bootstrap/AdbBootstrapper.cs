using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Seam.WindowsHost.Protocol;

namespace Seam.WindowsHost.Bootstrap
{
    /// <summary>An Android device as reported by `adb devices -l`.</summary>
    public sealed class AdbDevice
    {
        public string Serial { get; init; } = "";
        public string State { get; init; } = "";
        public string Model { get; init; } = "Android";
        public string TransportId { get; init; } = "0";

        public bool IsAuthorized => State == "device";
        public bool IsWirelessAdb => Serial.Contains(':') || Serial.Contains("._adb-tls-connect._tcp");

        public override string ToString() =>
            $"{Serial} [state={State}, model={Model}, transport_id={TransportId}]";
    }

    /// <summary>
    /// Windows port of Bootstrap/UsbBootstrapper.swift — the ADB half.
    ///
    /// The command *semantics* are identical to macOS because they are all `adb` CLI calls;
    /// only binary discovery and the detached-daemon launch script quoting differ. The agent
    /// jar, remote paths, log markers and stamp scheme are copied verbatim so an agent
    /// started by the Mac host is reused rather than duplicated.
    /// </summary>
    public sealed class AdbBootstrapper
    {
        public const string RemoteJarPath = "/data/local/tmp/seam-agent.jar";
        public const string RemoteLogPath = "/data/local/tmp/seam-agent.log";
        public const string RemoteStampPath = "/data/local/tmp/seam-agent.stamp";
        private const string ReadyMarker = "SEAM_AGENT_READY";
        private const string WifiEndpointPrefix = "SEAM_WIFI_ENDPOINT:";

        /// <summary>Upper bound for one adb invocation. adb is slow at worst by seconds.</summary>
        private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(60);

        private readonly string _adbPath;
        private readonly string _localJarPath;
        private bool _serverStarted;

        /// <summary>
        /// Both paths must be absolute and already discovered by <see cref="Host.AppLayout"/>.
        /// There is deliberately no fallback here: guessing a bare file name is what made the
        /// prototype pick up a PATH adb.exe (or none at all) instead of the bundled one.
        /// </summary>
        public AdbBootstrapper(string localJarPath, string adbPath)
        {
            if (string.IsNullOrWhiteSpace(adbPath) || !System.IO.Path.IsPathRooted(adbPath))
            {
                throw new ArgumentException($"adb path must be absolute, got '{adbPath}'", nameof(adbPath));
            }
            if (string.IsNullOrWhiteSpace(localJarPath))
            {
                throw new ArgumentException("agent jar path is required", nameof(localJarPath));
            }
            if (!File.Exists(adbPath))
            {
                throw new FileNotFoundException($"adb.exe not found at {adbPath}", adbPath);
            }

            _localJarPath = localJarPath;
            _adbPath = adbPath;
        }

        public string AdbPath => _adbPath;

        /// <summary>`adb devices -l` — same parsing rules as UsbBootstrapper.listDevices().</summary>
        public List<AdbDevice> ListDevices()
        {
            string output = Run(new[] { "devices", "-l" });
            var devices = new List<AdbDevice>();

            foreach (string rawLine in output.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("List of devices") || line.StartsWith("*"))
                {
                    continue;
                }

                string[] tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 2) continue;

                string model = "Android";
                string transportId = "0";
                for (int i = 2; i < tokens.Length; i++)
                {
                    if (tokens[i].StartsWith("model:"))
                    {
                        model = tokens[i].Substring("model:".Length);
                    }
                    else if (tokens[i].StartsWith("transport_id:"))
                    {
                        transportId = tokens[i].Substring("transport_id:".Length);
                    }
                }

                devices.Add(new AdbDevice
                {
                    Serial = tokens[0],
                    State = tokens[1],
                    Model = model,
                    TransportId = transportId
                });
            }
            return devices;
        }

        /// <summary>`adb forward tcp:0 localabstract:seam_uhid` -> allocated local port.</summary>
        public int SetupUsbForward(string serial)
        {
            string output = Run(new[]
            {
                "-s", serial, "forward", "tcp:0", "localabstract:" + SeamWireProtocol.UsbAbstractSocketName
            }).Trim();

            if (!int.TryParse(output, out int port) || port <= 0)
            {
                throw new InvalidOperationException($"Failed to parse forwarded TCP port: '{output}'");
            }
            return port;
        }

        /// <summary>
        /// Pushes the agent (if needed) and launches it detached, returning any Wi-Fi endpoints
        /// it advertised. Mirrors bootstrapSession()/deployAndLaunchDetachedDaemon().
        /// </summary>
        public List<(string iface, string ip, int port)> BootstrapSession(
            AdbDevice device, SeamAuthenticator auth, bool forceRedeploy = false)
        {
            if (!File.Exists(_localJarPath))
            {
                throw new FileNotFoundException(
                    $"Local agent archive missing at {_localJarPath}. Build it with scripts/build-agent.sh on macOS, or ship the jar next to seam-host.");
            }

            string serial = device.Serial;

            // Keep the phone's on-screen keyboard working (CONTRIBUTING.md invariant).
            TryRun(new[] { "-s", serial, "shell", "settings put secure show_ime_with_hard_keyboard 1" });

            string expectedStamp = ComputeAgentStamp(_localJarPath, auth.KeyHex);
            string probe =
                "((pgrep -f seam_agent >/dev/null 2>&1 || ps -A 2>/dev/null | grep -q '[s]eam_agent') && cat " +
                RemoteStampPath + " 2>/dev/null) || true";
            string runningStamp = TryRun(new[] { "-s", serial, "shell", probe })?.Trim() ?? "";

            // Reuse the running agent only when it is *listening*, not merely when a process with
            // that name exists and a stamp file matches. A marker left behind by an agent that has
            // since died made the next `adb forward` accept the connection and close it again,
            // which reached the user as "Remote peer closed the connection (EOF)".
            if (!forceRedeploy && expectedStamp.Length > 0 && runningStamp == expectedStamp && IsAgentListening(serial))
            {
                var (ready, endpoints) = WaitForDaemonReady(serial, 0.5);
                if (ready) return endpoints;
            }

            return DeployAndLaunch(serial, auth.KeyHex, expectedStamp);
        }

        /// <summary>
        /// `adb mdns services`: the endpoints Android advertises for Wireless debugging. Mirrors
        /// the macOS call of the same name; parsing lives in WirelessDiscovery so it is testable.
        /// </summary>
        public List<Host.WirelessService> DiscoverWirelessServices()
        {
            string? output = TryRun(new[] { "mdns", "services" });
            return Host.WirelessDiscovery.ParseMdnsServices(output);
        }

        /// <summary>
        /// `adb connect <ip:port>` for an already-paired Wireless debugging endpoint. Returns the
        /// raw adb answer so the caller can log exactly what happened ("connected to ...",
        /// "already connected to ...", "failed to connect ...").
        /// </summary>
        public string ConnectWireless(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                return "no address";
            }

            EnsureServer();
            var (exitCode, stdout, stderr) = Execute(new[] { "connect", address });
            string answer = (stdout + " " + stderr).Trim();
            return answer.Length == 0 ? (exitCode == 0 ? "connected" : "adb connect failed") : answer;
        }

        /// <summary>Drops a stale Wi-Fi transport before re-dialling it.</summary>
        public void DisconnectWireless(string serial)
        {
            if (string.IsNullOrWhiteSpace(serial))
            {
                return;
            }

            TryRun(new[] { "disconnect", serial });
        }

        /// <summary>
        /// True when the agent's abstract socket exists on the phone (`@seam_uhid` in
        /// <c>/proc/net/unix</c>), i.e. when a forwarded TCP connection will actually be served.
        /// </summary>
        public bool IsAgentListening(string serial)
        {
            string? output = TryRun(new[]
            {
                "-s", serial, "shell",
                "grep -c " + SeamWireProtocol.UsbAbstractSocketName + " /proc/net/unix 2>/dev/null || true"
            });

            return int.TryParse((output ?? string.Empty).Trim(), out int matches) && matches > 0;
        }

        private List<(string, string, int)> DeployAndLaunch(string serial, string authKeyHex, string stamp)
        {
            TryRun(new[] { "-s", serial, "shell", "pkill -f seam_agent || true" });
            Run(new[] { "-s", serial, "push", _localJarPath, RemoteJarPath });

            // Same launch line as the macOS host: nohup + setsid-equivalent detach, uid 2000.
            string launch =
                $"rm -f {RemoteLogPath}; " +
                $"printf '%s' '{stamp}' > {RemoteStampPath}; " +
                "nohup sh -c 'ANDROID_NO_USE_FWMARK_CLIENT=1 " +
                $"CLASSPATH={RemoteJarPath} app_process /system/bin --nice-name=seam_agent " +
                "com.seam.agent.Main --server --detach " +
                $"--auth-key={authKeyHex}' >{RemoteLogPath} 2>&1 </dev/null &";

            TryRun(new[] { "-s", serial, "shell", launch });

            var (ready, endpoints) = WaitForDaemonReady(serial, 4.0);
            if (!ready)
            {
                string tail = TryRun(new[] { "-s", serial, "shell", "tail -n 20 " + RemoteLogPath + " 2>/dev/null" }) ?? "";
                throw new InvalidOperationException(
                    "Timed out waiting for the detached seam-agent daemon.\n--- agent log ---\n" + tail);
            }
            return endpoints;
        }

        private (bool, List<(string, string, int)>) WaitForDaemonReady(string serial, double timeoutSeconds)
        {
            var endpoints = new List<(string, string, int)>();
            DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

            while (DateTime.UtcNow < deadline)
            {
                string? log = TryRun(new[]
                {
                    "-s", serial, "shell", "cat " + RemoteLogPath + " 2>/dev/null || true"
                });

                if (log != null && log.Contains(ReadyMarker))
                {
                    foreach (string rawLine in log.Split('\n'))
                    {
                        string line = rawLine.Trim();
                        if (!line.StartsWith(WifiEndpointPrefix)) continue;

                        string[] parts = line.Split(':');
                        // SEAM_WIFI_ENDPOINT:<iface>:<ip>:<port>
                        if (parts.Length == 4 && int.TryParse(parts[3], out int port))
                        {
                            endpoints.Add((parts[1], parts[2], port));
                        }
                    }
                    return (true, endpoints);
                }
                Thread.Sleep(60);
            }
            return (false, endpoints);
        }

        /// <summary>Mirror of UsbBootstrapper.computeAgentStamp (jar + key digest prefix).</summary>
        private static string ComputeAgentStamp(string jarPath, string authKeyHex)
        {
            try
            {
                byte[] jar = File.ReadAllBytes(jarPath);
                byte[] jarHash = System.Security.Cryptography.SHA256.HashData(jar);
                byte[] keyHash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(authKeyHex));
                return $"{ToHexPrefix(jarHash, 8)}:{ToHexPrefix(keyHash, 8)}";
            }
            catch
            {
                return "";
            }
        }

        private static string ToHexPrefix(byte[] bytes, int count)
        {
            var sb = new StringBuilder(count * 2);
            for (int i = 0; i < count && i < bytes.Length; i++)
            {
                sb.Append(bytes[i].ToString("x2"));
            }
            return sb.ToString();
        }

        private string Run(string[] args)
        {
            if (NeedsServer(args)) EnsureServer();
            var (exitCode, stdout, stderr) = Execute(args);
            if (exitCode != 0)
            {
                throw new InvalidOperationException(
                    $"'{_adbPath} {string.Join(" ", Redact(args))}' exited with {exitCode}: {stderr.Trim()}");
            }
            return stdout;
        }

        private string? TryRun(string[] args)
        {
            try
            {
                if (NeedsServer(args)) EnsureServer();
                var (exitCode, stdout, _) = Execute(args);
                return exitCode == 0 ? stdout : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Starts the adb server exactly once before any command that needs it.
        ///
        /// The first server-backed command (`adb devices`, `adb push`, `adb shell`, ...) forks a
        /// long-lived adb daemon that inherits the client's stdout/stderr handles. Two failure
        /// modes follow from that, and both are avoided here:
        ///
        ///  * if the inherited handles are pipes read to end-of-file, the daemon keeps the write
        ///    end open and the read never finishes - the command looks hung forever;
        ///  * if the inherited handles are the *caller's* stdout (a CI pipeline, a shell pipe),
        ///    the daemon keeps that pipe open after SEAM exits, and whatever is reading it waits
        ///    forever too.
        ///
        /// So the server is started with private pipes that this method closes as soon as the
        /// client exits: no drain to EOF, and nothing of the caller's is ever handed to a daemon.
        /// </summary>
        public void EnsureServer()
        {
            if (_serverStarted) return;
            _serverStarted = true;

            var psi = new ProcessStartInfo
            {
                FileName = _adbPath,
                WorkingDirectory = System.IO.Path.GetDirectoryName(_adbPath) ?? Environment.CurrentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("start-server");

            try
            {
                using var process = Process.Start(psi);
                if (process == null) return;
                if (!process.WaitForExit(30000))
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                }
                // Deliberate: close our read ends instead of draining them to EOF.
                try { process.StandardOutput.Close(); } catch { }
                try { process.StandardError.Close(); } catch { }
            }
            catch
            {
                // Best effort: the command that needed the server reports the real error, with
                // the device-specific guidance in Program.cs / SessionController.
            }
        }

        /// <summary>Commands that talk to a device (and therefore need the daemon) vs. pure client ones.</summary>
        private static bool NeedsServer(string[] args)
        {
            if (args.Length == 0) return false;
            switch (args[0].ToLowerInvariant())
            {
                case "version":
                case "start-server":
                case "kill-server":
                case "help":
                    return false;
                default:
                    return true;
            }
        }

        /// <summary>Lines printed by `adb version` (diagnostics only).</summary>
        public string[] AdbVersion()
        {
            var (exitCode, stdout, stderr) = Execute(new[] { "version" });
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"adb version failed ({exitCode}): {stderr.Trim()}");
            }
            return stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>
        /// Hides pairing material from diagnostics. The agent is launched with
        /// --auth-key=&lt;64 hex chars&gt;, so any adb failure would otherwise copy the private
        /// key into the console and into logs.
        /// </summary>
        internal static string[] Redact(string[] args)
        {
            string[] copy = new string[args.Length];
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                int idx = a.IndexOf("--auth-key=", StringComparison.OrdinalIgnoreCase);
                copy[i] = idx >= 0 ? a.Substring(0, idx) + "--auth-key=***" : a;
            }
            return copy;
        }

        private (int exitCode, string stdout, string stderr) Execute(
            string[] args, TimeSpan? timeout = null)
        {
            TimeSpan limit = timeout ?? DefaultCommandTimeout;

            var psi = new ProcessStartInfo
            {
                // Absolute executable path (never a bare name) and an explicit working directory:
                // a shortcut launched from C:\Windows, a UNC path or a read-only folder must not
                // change behaviour, and adb.exe finds AdbWinApi.dll next to itself.
                FileName = _adbPath,
                WorkingDirectory = System.IO.Path.GetDirectoryName(_adbPath) ?? Environment.CurrentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // ArgumentList handles Windows quoting correctly, including the single quotes
            // inside the remote launch script above (they are consumed by the device's sh).
            foreach (string a in args) psi.ArgumentList.Add(a);

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to start '{_adbPath}'");

            // Drain both pipes concurrently: reading stdout to EOF first can deadlock when the
            // child fills the stderr buffer (classic redirected-stream deadlock).
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();

            // A hung adb must never hang SEAM: bound the invocation and kill the tree.
            if (!process.WaitForExit((int)limit.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                throw new TimeoutException(
                    $"'{System.IO.Path.GetFileName(_adbPath)} {string.Join(" ", Redact(args))}' did not " +
                    $"finish within {limit.TotalSeconds:n0} s and was stopped. Unplug/replug the phone, run " +
                    "platform-tools\\adb.exe kill-server, then try again.");
            }

            // Bounded: never wait for end-of-file on a handle a forked adb daemon may hold.
            Task.WaitAll(new Task[] { stdoutTask, stderrTask }, TimeSpan.FromSeconds(10));

            string stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : string.Empty;
            string stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : string.Empty;
            return (process.ExitCode, stdout, stderr);
        }
    }
}
