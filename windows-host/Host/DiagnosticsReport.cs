using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Seam.WindowsHost.Bootstrap;
using Seam.WindowsHost.Protocol;

namespace Seam.WindowsHost.Host
{
    /// <summary>
    /// Builds the diagnostics text shown by <c>seam-host.exe --diagnose</c> and by the desktop
    /// application's Diagnostics window. One implementation, so the two can never disagree.
    ///
    /// It never prints key material: the pairing key appears only as a SHA-256 prefix.
    /// </summary>
    public static class DiagnosticsReport
    {
        public static string Build(string? adbOverride, string? jarOverride, out bool ok)
        {
            var text = new StringBuilder();
            void Line(string s = "") => text.AppendLine(s);

            Line("=== SEAM Windows host - diagnostics ===");
            Line($"version         : {VersionString}");
            Line($"process path    : {Environment.ProcessPath ?? "(unknown)"}");
            Line($"app context dir : {AppContext.BaseDirectory}");
            Line($"current dir     : {Environment.CurrentDirectory}");
            Line($"ui settings     : {UiSettings.DefaultPath}");
            Line();

            AppLayout layout;
            try
            {
                layout = AppLayout.ResolveForRoot(
                    AppLayout.ExecutableDirectory(), adbOverride, jarOverride, requireResources: false);
            }
            catch (SeamResourceException ex)
            {
                Line($"[FAIL] {ex.Message}");
                ok = false;
                return text.ToString();
            }

            ok = true;
            Line($"app dir         : {layout.AppRoot}");

            // ---- adb ----
            Line();
            Line("ADB");
            if (layout.AdbPath == null)
            {
                ok = false;
                Line("  [FAIL] adb.exe not found. Searched:");
                foreach (string candidate in layout.AdbCandidates)
                {
                    Line($"         - {candidate}");
                }
            }
            else
            {
                Line($"  path     : {layout.AdbPath}");
                Line($"  source   : {(layout.AdbIsBundled ? "bundled with SEAM" : "system / PATH (not bundled)")}");
                try
                {
                    var bootstrapper = new AdbBootstrapper(layout.AgentJarPath ?? "unused", layout.AdbPath);
                    foreach (string line in bootstrapper.AdbVersion())
                    {
                        Line($"  version  : {line}");
                    }
                }
                catch (Exception ex)
                {
                    ok = false;
                    Line($"  [FAIL] adb could not be executed: {ex.Message}");
                }
            }

            string platformTools = Path.Combine(layout.AppRoot, AppLayout.PlatformToolsFolder);
            int toolCount = Directory.Exists(platformTools) ? Directory.GetFiles(platformTools).Length : 0;
            Line($"  platform-tools folder: {platformTools} ({(Directory.Exists(platformTools) ? toolCount + " files" : "missing")})");

            // ---- agent jar ----
            Line();
            Line("ANDROID AGENT");
            if (layout.AgentJarPath == null)
            {
                ok = false;
                Line("  [FAIL] seam-agent.jar not found. Searched:");
                foreach (string candidate in layout.JarCandidates)
                {
                    Line($"         - {candidate}");
                }
            }
            else
            {
                var info = new FileInfo(layout.AgentJarPath);
                Line($"  path     : {layout.AgentJarPath}");
                Line($"  size     : {info.Length:n0} bytes");
            }

            // ---- pairing key (never printed) ----
            Line();
            Line("PAIRING KEY");
            string keyPath = SeamAuthenticator.KeyStorePath;
            if (File.Exists(keyPath))
            {
                try
                {
                    SeamAuthenticator auth = SeamAuthenticator.LoadOrCreatePersistentKey();
                    Line($"  store      : {keyPath}");
                    Line($"  fingerprint: {auth.KeyFingerprint}  (SHA-256 prefix; the key is never printed)");
                }
                catch (Exception ex)
                {
                    Line($"  [WARN] could not read {keyPath}: {ex.Message}");
                }
            }
            else
            {
                Line($"  store      : {keyPath} (not created yet; it is generated on first run)");
            }

            // ---- devices ----
            Line();
            Line("DEVICES");
            if (layout.AdbPath != null)
            {
                try
                {
                    var bootstrapper = new AdbBootstrapper(layout.AgentJarPath ?? "unused", layout.AdbPath);
                    List<AdbDevice> devices = bootstrapper.ListDevices();
                    if (devices.Count == 0)
                    {
                        Line("  none visible to adb (enable USB debugging, accept the phone prompt, or use Wireless debugging)");
                    }
                    else
                    {
                        foreach (AdbDevice d in devices)
                        {
                            Line($"  {d.Serial}  state={d.State}  model={d.Model}  authorized={d.IsAuthorized}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Line($"  [WARN] {ex.Message}");
                }
            }
            else
            {
                Line("  skipped (no adb)");
            }

            Line();
            Line(ok ? "RESULT: all bundled resources found." : "RESULT: a required resource is missing (see FAIL above).");
            return text.ToString();
        }

        public static string VersionString
        {
            get
            {
                try
                {
                    return typeof(DiagnosticsReport).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
                }
                catch
                {
                    return "0.0.0";
                }
            }
        }
    }
}
