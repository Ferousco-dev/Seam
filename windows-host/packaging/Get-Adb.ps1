<#
.SYNOPSIS
    Installs Android Platform-Tools (adb.exe) next to seam-host.exe.

.DESCRIPTION
    SEAM drives the phone through Android's adb. adb is a Google/AOSP tool and is not
    redistributed inside the SEAM Windows ZIP (see THIRD-PARTY-NOTICES.md). This helper
    downloads Google's official Platform-Tools archive on your behalf and unpacks it into
    .\platform-tools\, which is where seam-host.exe looks first.

    By running this you download and use Google's Platform-Tools under Google's terms:
    https://developer.android.com/studio/terms

    Compatibility notes (do not regress these):
      * Windows PowerShell 5.1 is the target, not only PowerShell 7.
      * This file is deliberately ASCII-only and is saved as UTF-8 WITH a BOM.
        Windows PowerShell 5.1 reads a BOM-less file using the ANSI code page, so a UTF-8
        em dash (E2 80 94) becomes the three characters "a-hat euro right-double-quote",
        and PowerShell treats that typographic quote as a string terminator. The result is
        a cascade of "unexpected token / missing }" / "unterminated string" ParserErrors.
      * No line-continuation characters, no Unicode punctuation, no emoji.

.PARAMETER Force
    Re-download and replace .\platform-tools\ even if adb.exe is already present.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Get-Adb.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Get-Adb.ps1 -Force
#>
[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest's progress bar is very slow on 5.1

$downloadUrl  = 'https://dl.google.com/android/repository/platform-tools-latest-windows.zip'
$scriptRoot   = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
$destDir      = Join-Path $scriptRoot 'platform-tools'
$adbExe       = Join-Path $destDir 'adb.exe'
$minimumBytes = 1MB

Write-Host 'SEAM: Android Platform-Tools installer' -ForegroundColor Cyan
Write-Host "  powershell : $($PSVersionTable.PSVersion)"
Write-Host "  target     : $destDir"

# ---------------------------------------------------------------------------
# 1. Already installed: use it, and check that it actually runs.
# ---------------------------------------------------------------------------
if ((Test-Path -LiteralPath $adbExe) -and (-not $Force)) {
    Write-Host '  adb.exe is already present - nothing to download.' -ForegroundColor Green
    Write-Host '  version output:'
    & $adbExe version
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "The existing adb.exe could not run (exit code $LASTEXITCODE)."
        Write-Warning 'Re-run with -Force to replace it:  powershell -ExecutionPolicy Bypass -File .\Get-Adb.ps1 -Force'
        exit 1
    }
    Write-Host ''
    Write-Host 'Platform-Tools are ready. Next:'
    Write-Host '  1. Enable USB debugging on the phone (Developer options).'
    Write-Host '  2. Plug the phone in and accept the "Allow USB debugging?" prompt on the phone.'
    Write-Host '  3. .\seam-host.exe --trajectory'
    exit 0
}

# ---------------------------------------------------------------------------
# 2. Download into a private scratch directory.
# ---------------------------------------------------------------------------
$tmpDir  = Join-Path ([System.IO.Path]::GetTempPath()) ('seam-adb-' + [guid]::NewGuid().ToString('N'))
$zipPath = Join-Path $tmpDir 'platform-tools.zip'

try {
    New-Item -ItemType Directory -Path $tmpDir -Force | Out-Null

    # Windows PowerShell 5.1 can default to TLS 1.0 on older builds; Google requires TLS 1.2+.
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    } catch {
        Write-Verbose 'Could not raise the TLS version; continuing with the system default.'
    }

    Write-Host "  source     : $downloadUrl"
    Write-Host '  downloading (about 12 MB)...'
    Invoke-WebRequest -Uri $downloadUrl -OutFile $zipPath -UseBasicParsing

    if (-not (Test-Path -LiteralPath $zipPath)) {
        throw 'The download produced no file.'
    }
    $size = (Get-Item -LiteralPath $zipPath).Length
    if ($size -lt $minimumBytes) {
        throw "Downloaded file is only $size bytes - a proxy or captive portal probably intercepted it."
    }
    Write-Host ('  downloaded : {0:N1} MB' -f ($size / 1MB))

    # -----------------------------------------------------------------------
    # 3. Extract and validate the archive layout before touching anything.
    # -----------------------------------------------------------------------
    Write-Host '  extracting...'
    Expand-Archive -LiteralPath $zipPath -DestinationPath $tmpDir -Force

    $srcDir = Join-Path $tmpDir 'platform-tools'
    if (-not (Test-Path -LiteralPath (Join-Path $srcDir 'adb.exe'))) {
        throw 'platform-tools\adb.exe is missing from the archive - the archive layout may have changed.'
    }

    # -----------------------------------------------------------------------
    # 4. Replace .\platform-tools\ (only after a good download was validated).
    # -----------------------------------------------------------------------
    if (Test-Path -LiteralPath $destDir) {
        Write-Host '  replacing the existing platform-tools folder...'
        try {
            Remove-Item -LiteralPath $destDir -Recurse -Force
        } catch {
            throw "Could not remove $destDir - an adb.exe is probably still running. Run 'adb kill-server', close any adb/Android Studio windows, and try again. ($($_.Exception.Message))"
        }
    }
    Move-Item -LiteralPath $srcDir -Destination $destDir

    # -----------------------------------------------------------------------
    # 5. Verify the installed copy really runs.
    # -----------------------------------------------------------------------
    Write-Host ''
    Write-Host 'Installed:' -ForegroundColor Green
    & $adbExe version
    if ($LASTEXITCODE -ne 0) {
        throw "The newly installed adb.exe did not run (exit code $LASTEXITCODE)."
    }

    $onPath = Get-Command -Name adb.exe -ErrorAction SilentlyContinue
    if ($onPath -and $onPath.Source) {
        $installedPath = (Resolve-Path -LiteralPath $adbExe).Path
        if ($onPath.Source -ne $installedPath) {
            Write-Warning "Another adb.exe is on PATH: $($onPath.Source)"
            Write-Warning 'SEAM always uses the copy in platform-tools\, so this is informational only.'
        }
    }

    Write-Host ''
    Write-Host 'Next steps:'
    Write-Host '  1. Enable USB debugging on the phone (Developer options).'
    Write-Host '  2. Plug the phone in and accept the "Allow USB debugging?" prompt on the phone.'
    Write-Host '  3. .\seam-host.exe --trajectory'
    Write-Host ''
    Write-Host 'If "adb devices" stays empty, the phone needs an OEM ADB USB driver.'
    Write-Host 'Wireless debugging avoids that entirely: Developer options -> Wireless debugging'
    Write-Host '-> Pair device with pairing code, then:'
    Write-Host '  .\platform-tools\adb.exe pair <phone-ip>:<port>'
    Write-Host '  .\platform-tools\adb.exe connect <phone-ip>:<port>'
    exit 0
}
catch {
    Write-Host ''
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ''
    Write-Host 'Nothing was changed next to seam-host.exe unless the message above says otherwise.'
    Write-Host 'If this was a network problem, check that dl.google.com is reachable from this'
    Write-Host 'machine (corporate proxies and offline installs block it). You can also install'
    Write-Host 'Platform-Tools yourself and copy the platform-tools folder next to seam-host.exe.'
    exit 1
}
finally {
    if (Test-Path -LiteralPath $tmpDir) {
        Remove-Item -LiteralPath $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
