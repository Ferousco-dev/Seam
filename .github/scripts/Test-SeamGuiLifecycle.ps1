<#
.SYNOPSIS
    Interactive desktop lifecycle test for the SEAM Windows application.

.DESCRIPTION
    Launches seam-host.exe with NO arguments -- exactly what a double-click in File Explorer, the
    Start Menu shortcut and the desktop shortcut all do -- and asserts that the APPLICATION works,
    not merely that a process was created:

      1. the process starts and stays alive;
      2. a visible top-level window owned by that process appears (the main form);
      3. that window answers SendMessageTimeout(WM_NULL), which is only possible when the WinForms
         message loop started by Application.Run is pumping messages. A window that exists but is
         never pumped fails here, so "the process started" is never mistaken for "the GUI works";
      4. the window survives a few seconds and is still answering (no crash after startup, no
         blocked UI thread);
      5. PostMessage(WM_CLOSE) makes the application exit on its own;
      6. the exit code is 0;
      7. %APPDATA%\SEAM\ui-settings.json was rewritten on the way out, which happens only in
         MainForm.OnFormClosing -> SessionController.Dispose -- proof the close path ran instead of
         the process being torn down;
      8. no seam-host.exe process is left behind.

    Why this exists next to --ui-smoke: --ui-smoke builds and renders the form synchronously and
    returns WITHOUT entering Application.Run, so it cannot see the interactive path (message loop,
    the 250 ms hook timer, the 2.5 s device poll, the Shown handler, the close path).

    Environment guard: a window can be created on any window station, but on a non-interactive one
    (a service session) nobody can see, use or close it, so these assertions would be meaningless.
    When the job is not on WinSta0 / not user-interactive, the script says so with a warning
    annotation and skips instead of reporting a failure it cannot distinguish from a regression.

.PARAMETER ExePath
    Path to seam-host.exe (normally build\app\seam-host.exe).

.PARAMETER StartupTimeoutSeconds
    How long to wait for the main window to appear. Default 90.

.PARAMETER CloseTimeoutSeconds
    How long to wait for the process to exit after WM_CLOSE. Default 45.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExePath,
    [int]$StartupTimeoutSeconds = 90,
    [int]$CloseTimeoutSeconds = 45
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------------------------
# Win32 interop. Kept in C# so no PowerShell delegate has to be marshalled into a callback:
# a failure inside an EnumWindows callback would be invisible otherwise.
# ---------------------------------------------------------------------------------------------
$interop = @'
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace SeamCi
{
    public static class Win
    {
        public const uint WM_CLOSE = 0x0010;
        public const uint WM_NULL = 0x0000;
        public const uint SMTO_ABORTIFHUNG = 0x0002;
        private const int UOI_NAME = 2;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetClassNameW(IntPtr hWnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SendMessageTimeoutW(
            IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern IntPtr GetProcessWindowStation();

        [DllImport("user32.dll")]
        private static extern IntPtr GetThreadDesktop(uint threadId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetUserObjectInformationW(
            IntPtr handle, int index, StringBuilder info, int length, out int needed);

        public static string WindowStationName() { return ObjectName(GetProcessWindowStation()); }

        public static string DesktopName() { return ObjectName(GetThreadDesktop(GetCurrentThreadId())); }

        private static string ObjectName(IntPtr handle)
        {
            try
            {
                StringBuilder sb = new StringBuilder(512);
                int needed;
                if (GetUserObjectInformationW(handle, UOI_NAME, sb, sb.Capacity * 2, out needed))
                {
                    return sb.ToString();
                }
                return "<unavailable:" + Marshal.GetLastWin32Error() + ">";
            }
            catch (Exception ex)
            {
                return "<error:" + ex.GetType().Name + ">";
            }
        }

        // First VISIBLE top-level window owned by pid. Titles are read separately, after the
        // message loop has answered a probe: GetWindowText on a hung window can block the caller.
        public static IntPtr FindVisibleWindow(int pid, out string size)
        {
            IntPtr found = IntPtr.Zero;
            string foundSize = "?";

            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                uint windowPid;
                GetWindowThreadProcessId(hWnd, out windowPid);
                if (windowPid != (uint)pid) { return true; }
                if (!IsWindowVisible(hWnd)) { return true; }

                RECT rect;
                if (GetWindowRect(hWnd, out rect))
                {
                    foundSize = (rect.Right - rect.Left).ToString() + "x" + (rect.Bottom - rect.Top).ToString();
                }
                found = hWnd;
                return false;
            }, IntPtr.Zero);

            size = foundSize;
            return found;
        }

        public static string WindowTitle(IntPtr hWnd) { return WindowText(hWnd, true); }

        public static string WindowClass(IntPtr hWnd) { return WindowText(hWnd, false); }

        private static string WindowText(IntPtr hWnd, bool title)
        {
            StringBuilder sb = new StringBuilder(512);
            if (title) { GetWindowTextW(hWnd, sb, sb.Capacity); }
            else { GetClassNameW(hWnd, sb, sb.Capacity); }
            return sb.ToString();
        }

        // True only when the UI thread answered WM_NULL within the timeout, i.e. the message loop
        // is running. False means the window exists but nothing pumps its queue.
        public static bool PumpsMessages(IntPtr hWnd, uint timeoutMs, out string detail)
        {
            IntPtr result;
            IntPtr ok = SendMessageTimeoutW(
                hWnd, WM_NULL, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, timeoutMs, out result);

            if (ok == IntPtr.Zero)
            {
                detail = "WM_NULL was not answered (GetLastError=" + Marshal.GetLastWin32Error() + ")";
                return false;
            }
            detail = "WM_NULL answered";
            return true;
        }

        public static bool RequestClose(IntPtr hWnd, out string detail)
        {
            bool ok = PostMessageW(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            detail = ok
                ? "WM_CLOSE posted"
                : "PostMessage(WM_CLOSE) failed (GetLastError=" + Marshal.GetLastWin32Error() + ")";
            return ok;
        }
    }
}
'@

Add-Type -TypeDefinition $interop -Language CSharp

function Write-Section([string]$Text) {
    Write-Host ''
    Write-Host "--- $Text ---"
}

function Show-Captured([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        $text = [string](Get-Content -LiteralPath $Path -Raw)
        if ([string]::IsNullOrWhiteSpace($text)) { Write-Host '(empty)' } else { Write-Host $text }
    } else {
        Write-Host '(no output file)'
    }
}

function Complete-Skipped([string]$Reason) {
    Write-Host ''
    Write-Host "SKIPPED: $Reason"
    Write-Host "::warning title=Desktop UI lifecycle::$Reason"
    if ($env:GITHUB_STEP_SUMMARY) {
        @(
            '',
            '### Desktop UI lifecycle test - SKIPPED',
            '',
            $Reason,
            '',
            'The window is still covered by `--ui-smoke`; only the interactive message-loop assertions',
            'are skipped. Run the GUI section of `docs/windows/MANUAL-VERIFICATION.md` by hand.'
        ) | Add-Content -Path $env:GITHUB_STEP_SUMMARY
    }
    exit 0
}

if (-not (Test-Path -LiteralPath $ExePath)) { throw "executable not found: $ExePath" }

$exe = (Resolve-Path -LiteralPath $ExePath).Path
$appDir = Split-Path -Parent $exe

$windowStation = [SeamCi.Win]::WindowStationName()
$desktop = [SeamCi.Win]::DesktopName()
$userInteractive = [Environment]::UserInteractive
$sessionId = (Get-Process -Id $PID).SessionId

Write-Host ("runner: session={0} userInteractive={1} windowStation={2} desktop={3}" -f `
        $sessionId, $userInteractive, $windowStation, $desktop)

if ($windowStation -ne 'WinSta0' -or -not $userInteractive) {
    Complete-Skipped ("the job is not on an interactive desktop " +
        "(windowStation=$windowStation, desktop=$desktop, userInteractive=$userInteractive), " +
        "so a window can be created but not used or closed; the interactive assertions would be meaningless")
}

$stale = @(Get-Process -Name 'seam-host' -ErrorAction SilentlyContinue)
if ($stale.Count -gt 0) {
    throw "a seam-host.exe process was already running before the test (pid $($stale.Id -join ', '))"
}

$settingsFile = Join-Path $env:APPDATA 'SEAM\ui-settings.json'
$settingsBefore = if (Test-Path -LiteralPath $settingsFile) {
    (Get-Item -LiteralPath $settingsFile).LastWriteTimeUtc
} else {
    $null
}

$stamp = [guid]::NewGuid().ToString('N')
$outFile = Join-Path $env:RUNNER_TEMP "seam-gui-$stamp.out.txt"
$errFile = Join-Path $env:RUNNER_TEMP "seam-gui-$stamp.err.txt"

$process = $null

try {
    Write-Section 'launch with zero arguments (what a double-click does)'
    $process = Start-Process -FilePath $exe -WorkingDirectory $appDir -PassThru `
        -RedirectStandardOutput $outFile -RedirectStandardError $errFile
    Write-Host "started pid $($process.Id): $exe"
    Write-Host "working directory: $appDir   (no arguments passed)"

    Write-Section "wait for the main window (timeout ${StartupTimeoutSeconds}s)"
    $deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
    $hwnd = [IntPtr]::Zero
    $size = '?'

    while ((Get-Date) -lt $deadline) {
        $process.Refresh()
        if ($process.HasExited) { break }
        $hwnd = [SeamCi.Win]::FindVisibleWindow($process.Id, [ref]$size)
        if ($hwnd -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 200
    }

    if ($hwnd -eq [IntPtr]::Zero) {
        $process.Refresh()
        if ($process.HasExited) {
            throw ("seam-host.exe exited with code $($process.ExitCode) without ever showing a " +
                   "window: the application crashed during startup")
        }
        throw ("seam-host.exe is running (pid $($process.Id)) but no visible top-level window " +
               "appeared within ${StartupTimeoutSeconds}s: Application.Run never presented a form")
    }

    Write-Host ("main window: handle 0x{0:X}, size {1}" -f $hwnd.ToInt64(), $size)

    Write-Section 'prove the WinForms message loop is running'
    $detail = ''
    if (-not ([SeamCi.Win]::PumpsMessages($hwnd, 5000, [ref]$detail))) {
        throw ("the main window exists but $detail - the WinForms message loop (Application.Run) " +
               "is not pumping messages")
    }
    Write-Host $detail

    $title = [SeamCi.Win]::WindowTitle($hwnd)
    $class = [SeamCi.Win]::WindowClass($hwnd)
    Write-Host "window title: '$title'"
    Write-Host "window class: '$class'"

    # A WinForms form has a class name of the form "WindowsForms10.Window.8.app...". Anything
    # else - notably "#32770", the standard dialog class - means the application showed an error
    # MessageBox instead of its window: a failed start must never be reported as a good one.
    if ($class -notlike 'WindowsForms*') {
        throw ("the window found (class '$class', title '$title') is not the SEAM form - " +
               "this is a dialog, most likely a startup error message")
    }

    if ($title -ne 'SEAM') {
        throw "the main window title is '$title', expected 'SEAM'"
    }

    if ($size -match '^(\d+)x(\d+)$') {
        $width = [int]$Matches[1]
        $height = [int]$Matches[2]
        if ($width -lt 600 -or $height -lt 400) {
            throw ("the main window is only ${width}x${height}: too small to be the 1000x680 SEAM " +
                   "window, so this is a dialog rather than the application")
        }
    }

    Write-Section 'confirm the application stays alive and responsive'
    Start-Sleep -Seconds 3
    $process.Refresh()
    if ($process.HasExited) {
        throw "seam-host.exe exited by itself with code $($process.ExitCode) while it should have stayed open"
    }
    if (-not ([SeamCi.Win]::PumpsMessages($hwnd, 5000, [ref]$detail))) {
        throw "the window stopped answering after 3 s: the UI thread is blocked or hung ($detail)"
    }
    Write-Host "still alive and answering after 3 s (pid $($process.Id))"

    Write-Section 'close the window the way a user does (WM_CLOSE)'
    $detail = ''
    if (-not ([SeamCi.Win]::RequestClose($hwnd, [ref]$detail))) {
        throw "could not ask the window to close: $detail"
    }
    Write-Host $detail

    if (-not $process.WaitForExit($CloseTimeoutSeconds * 1000)) {
        throw "the application did not exit within ${CloseTimeoutSeconds}s of WM_CLOSE: closing the window hangs"
    }

    Write-Host "exit code: $($process.ExitCode)"
    if ($process.ExitCode -ne 0) {
        throw "the application exited with code $($process.ExitCode) after WM_CLOSE, expected 0"
    }

    Write-Section 'no process left behind'
    Start-Sleep -Milliseconds 500
    $leftover = @(Get-Process -Name 'seam-host' -ErrorAction SilentlyContinue)
    if ($leftover.Count -gt 0) {
        throw "seam-host.exe is still running after a clean close (pid $($leftover.Id -join ', '))"
    }
    Write-Host 'no seam-host.exe process remains'

    Write-Section 'the close path ran (settings are saved on exit)'
    if (-not (Test-Path -LiteralPath $settingsFile)) {
        throw "the settings file $settingsFile does not exist after the close: MainForm.OnFormClosing did not run"
    }
    $settingsAfter = (Get-Item -LiteralPath $settingsFile).LastWriteTimeUtc
    if ($settingsBefore -and $settingsAfter -le $settingsBefore) {
        throw ("the settings file was not rewritten by the close path (timestamp still " +
               "$settingsAfter): OnFormClosing did not complete")
    }
    Write-Host "settings rewritten: $settingsFile -> $settingsAfter"

    Write-Section 'captured output'
    Write-Host '--- stdout ---'
    Show-Captured $outFile
    Write-Host '--- stderr ---'
    Show-Captured $errFile

    Write-Host ("::notice title=Desktop UI lifecycle::Application.Run exercised: window '{0}' ({1}), message loop answering, WM_CLOSE -> exit 0, settings saved, no leftover process" -f $title, $size)

    if ($env:GITHUB_STEP_SUMMARY) {
        @(
            '',
            '### Desktop UI lifecycle (interactive)',
            '',
            '| check | result |',
            '|---|---|',
            "| launched with no arguments | ``$exe`` |",
            "| main window | ``'$title'``, $size |",
            '| Application.Run message loop | answered WM_NULL (twice) |',
            '| WM_CLOSE | exit code 0 |',
            '| leftover processes | none |',
            '| close path | settings rewritten |'
        ) | Add-Content -Path $env:GITHUB_STEP_SUMMARY
    }

    exit 0
}
catch {
    $message = $_.Exception.Message
    Write-Host ''
    Write-Host "FAILED: $message"

    if ($process) {
        try { $process.Refresh() } catch { }
        $state = 'unknown'
        if ($process.HasExited) { $state = "exited (code $($process.ExitCode))" }
        else { $state = "still running (pid $($process.Id))" }
        Write-Host "process state: $state"
    }

    Write-Host '--- stdout ---'
    Show-Captured $outFile
    Write-Host '--- stderr ---'
    Show-Captured $errFile

    Write-Host ("::error title=Desktop UI lifecycle::{0}" -f ($message -replace "\r?\n", " | "))
    exit 1
}
finally {
    if ($process) {
        try {
            $process.Refresh()
            if (-not $process.HasExited) {
                Write-Host "cleanup: terminating pid $($process.Id) so the job can continue"
                $process.Kill($true)
                [void]$process.WaitForExit(10000)
            }
        }
        catch {
            # Nothing useful to add: the test has already reported its verdict.
        }
    }

    Remove-Item -LiteralPath $outFile, $errFile -Force -ErrorAction SilentlyContinue
}
