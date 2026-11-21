using System;
using System.Collections.Generic;
using System.IO;

namespace Seam.WindowsHost.Host
{
    /// <summary>
    /// Thrown when a required on-disk resource cannot be located. The message always lists every
    /// path that was searched, because the only sane fix is "reinstall / stop deleting files".
    /// </summary>
    public sealed class SeamResourceException : Exception
    {
        public SeamResourceException(string message) : base(message) { }
    }

    /// <summary>
    /// Application-relative resource discovery.
    ///
    /// WHY THIS EXISTS
    /// ---------------
    /// The first prototype resolved everything from <c>AppContext.BaseDirectory</c>. That is fine
    /// for a normal build, but a self-contained *single-file* publish with
    /// <c>IncludeNativeLibrariesForSelfExtract=true</c> makes the host extract the runtime into
    /// <c>%TEMP%\.net\seam-host\&lt;bundle-id&gt;\</c> and run from there, so
    /// <c>AppContext.BaseDirectory</c> points at that temporary directory. The result on real
    /// hardware was exactly the reported failure:
    ///   * adb.exe was never found in the bundled platform-tools\ folder, and
    ///   * seam-agent.jar was reported as expected somewhere under %TEMP%.
    ///
    /// The fix is to anchor everything to the *actual executable's* directory
    /// (<see cref="Environment.ProcessPath"/>, .NET 6+), which is stable no matter how SEAM was
    /// launched: desktop shortcut, Start Menu, Explorer, PowerShell, or a different working
    /// directory. Nothing in this class consults <see cref="Directory.GetCurrentDirectory"/>.
    ///
    /// Resolution order (first hit wins, every candidate is an absolute path):
    ///   adb  : explicit --adb  ->  AppRoot\platform-tools\adb.exe  ->  AppRoot\adb.exe
    ///          ->  %LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe
    ///          ->  %ProgramFiles(x86)%\Android\android-sdk\platform-tools\adb.exe
    ///          ->  each directory on PATH (resolved to an absolute path, so a later PATH change
    ///              cannot make the process pick a different binary mid-run)
    ///   agent: explicit --jar  ->  AppRoot\seam-agent.jar  ->  AppRoot\Resources\seam-agent.jar
    ///
    /// The bundled copies are always preferred over PATH: the shipped package must behave the
    /// same on a machine that also has Android Studio installed.
    /// </summary>
    public sealed class AppLayout
    {
        public const string PlatformToolsFolder = "platform-tools";
        public const string AdbFileName = "adb.exe";
        public const string AgentJarFileName = "seam-agent.jar";
        public const string ResourcesFolder = "Resources";

        private readonly List<string> _adbCandidates = new List<string>();
        private readonly List<string> _jarCandidates = new List<string>();

        /// <summary>Directory that contains seam-host.exe. Never the current directory.</summary>
        public string AppRoot { get; private set; } = string.Empty;

        /// <summary>Absolute path to adb.exe, or null when it could not be found.</summary>
        public string? AdbPath { get; private set; }

        /// <summary>Absolute path to seam-agent.jar, or null when it could not be found.</summary>
        public string? AgentJarPath { get; private set; }

        /// <summary>True when adb came from the bundle next to the executable (not PATH).</summary>
        public bool AdbIsBundled { get; private set; }

        public IReadOnlyList<string> AdbCandidates => _adbCandidates;
        public IReadOnlyList<string> JarCandidates => _jarCandidates;

        /// <summary>
        /// Resolves the layout for the running executable and throws when adb or the agent jar
        /// is missing. This is what the application uses.
        /// </summary>
        public static AppLayout Resolve(string? adbOverride = null, string? agentJarOverride = null)
        {
            return ResolveForRoot(ExecutableDirectory(), adbOverride, agentJarOverride, requireResources: true);
        }

        /// <summary>
        /// Testable core: resolve against an explicit root. Production code should call
        /// <see cref="Resolve"/>; tests use this to point at temporary directories (including
        /// paths containing spaces) without copying the executable.
        /// </summary>
        public static AppLayout ResolveForRoot(
            string appRoot,
            string? adbOverride = null,
            string? agentJarOverride = null,
            bool requireResources = false)
        {
            if (string.IsNullOrWhiteSpace(appRoot))
            {
                throw new SeamResourceException("Cannot determine the SEAM application directory.");
            }

            var layout = new AppLayout { AppRoot = Path.GetFullPath(appRoot) };

            // ---- adb ---------------------------------------------------------------
            if (!string.IsNullOrWhiteSpace(adbOverride))
            {
                layout._adbCandidates.Add(Path.GetFullPath(adbOverride!));
                layout.AdbPath = layout._adbCandidates[0];
                if (!File.Exists(layout.AdbPath))
                {
                    throw new SeamResourceException(
                        $"--adb points at a file that does not exist: {layout.AdbPath}");
                }
            }
            else
            {
                string bundled = Path.Combine(layout.AppRoot, PlatformToolsFolder, AdbFileName);
                string beside = Path.Combine(layout.AppRoot, AdbFileName);
                layout._adbCandidates.Add(bundled);
                layout._adbCandidates.Add(beside);

                AddSdkCandidates(layout._adbCandidates);
                layout._adbCandidates.AddRange(PathCandidates(AdbFileName));

                string? found = FirstExisting(layout._adbCandidates);
                if (found != null)
                {
                    layout.AdbPath = found;
                    layout.AdbIsBundled =
                        string.Equals(found, bundled, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(found, beside, StringComparison.OrdinalIgnoreCase);
                }
            }

            // ---- agent jar ---------------------------------------------------------
            if (!string.IsNullOrWhiteSpace(agentJarOverride))
            {
                layout._jarCandidates.Add(Path.GetFullPath(agentJarOverride!));
                layout.AgentJarPath = layout._jarCandidates[0];
                if (!File.Exists(layout.AgentJarPath))
                {
                    throw new SeamResourceException(
                        $"--jar points at a file that does not exist: {layout.AgentJarPath}");
                }
            }
            else
            {
                layout._jarCandidates.Add(Path.Combine(layout.AppRoot, AgentJarFileName));
                layout._jarCandidates.Add(Path.Combine(layout.AppRoot, ResourcesFolder, AgentJarFileName));
                layout.AgentJarPath = FirstExisting(layout._jarCandidates);
            }

            if (requireResources)
            {
                if (layout.AdbPath == null)
                {
                    throw new SeamResourceException(
                        "adb.exe was not found. SEAM expects it inside the application folder:\n" +
                        $"  {Path.Combine(layout.AppRoot, PlatformToolsFolder, AdbFileName)}\n" +
                        "Searched:\n" + Bullet(layout._adbCandidates) + "\n" +
                        "Fix: reinstall SEAM (the installer ships Platform-Tools), or pass --adb <path>.");
                }
                if (layout.AgentJarPath == null)
                {
                    throw new SeamResourceException(
                        "seam-agent.jar was not found next to the application.\n" +
                        "Searched:\n" + Bullet(layout._jarCandidates) + "\n" +
                        "Fix: reinstall SEAM (the installer ships the agent), or pass --jar <path>.");
                }
            }

            return layout;
        }

        /// <summary>
        /// Directory of the running executable. Uses <see cref="Environment.ProcessPath"/> so that
        /// a self-extracting single-file build (which runs from %TEMP%) still resolves resources
        /// next to the real .exe. Falls back to AppContext.BaseDirectory for hosts that report
        /// 'dotnet' (e.g. `dotnet run`).
        /// </summary>
        public static string ExecutableDirectory()
        {
            string? processPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(processPath))
            {
                string name = Path.GetFileName(processPath);
                bool isDotnetHost = name.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
                                    name.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase);
                if (!isDotnetHost)
                {
                    string? dir = Path.GetDirectoryName(processPath);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        return Path.GetFullPath(dir!);
                    }
                }
            }

            return Path.GetFullPath(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        }

        private static void AddSdkCandidates(List<string> candidates)
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData))
            {
                candidates.Add(Path.Combine(localAppData, "Android", "Sdk", PlatformToolsFolder, AdbFileName));
            }

            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrEmpty(programFilesX86))
            {
                candidates.Add(Path.Combine(programFilesX86, "Android", "android-sdk", PlatformToolsFolder, AdbFileName));
            }
        }

        /// <summary>Expands PATH into absolute file paths. Never returns a bare file name.</summary>
        private static IEnumerable<string> PathCandidates(string fileName)
        {
            string? pathVariable = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathVariable))
            {
                yield break;
            }

            foreach (string raw in pathVariable.Split(Path.PathSeparator))
            {
                string dir = raw.Trim().Trim('"');
                if (dir.Length == 0 || !Directory.Exists(dir))
                {
                    continue;
                }

                string candidate;
                try
                {
                    candidate = Path.Combine(dir, fileName);
                }
                catch (ArgumentException)
                {
                    continue; // malformed PATH entry - ignore, never crash on the user's environment
                }

                yield return candidate;
            }
        }

        private static string? FirstExisting(IEnumerable<string> candidates)
        {
            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            return null;
        }

        private static string Bullet(IEnumerable<string> paths)
        {
            var text = new System.Text.StringBuilder();
            foreach (string path in paths)
            {
                text.Append("  - ").AppendLine(path);
            }
            return text.ToString().TrimEnd();
        }
    }
}
