SEAM for Windows - x64 build
============================

SEAM shares one mouse and keyboard between this PC and an Android phone.
This build needs no .NET installation, no PATH edits and nothing copied to %TEMP%.

IF YOU USED SEAM-Setup-x64.exe
------------------------------
SEAM is installed in C:\Program Files\SEAM. Start it from the Start Menu ("SEAM") or
from the desktop shortcut if you asked for one: the SEAM window opens. There is no
command to type and no console window.

  - "SEAM"                the application
  - "SEAM diagnostics"    the troubleshooting report, in a console window
  - "SEAM quick start"    this file
  - Uninstall: Settings -> Apps -> Installed apps -> SEAM

Uninstalling keeps your pairing key unless you say otherwise (see YOUR DATA below).

IF YOU USED THE PORTABLE ZIP
----------------------------
The ZIP is flat: Explorer's "Extract All" creates one SEAM-Windows-x64 folder with these
files directly inside it. Keep the folder together - seam-host.exe needs the same
folder's platform-tools\ and seam-agent.jar. You can move the folder anywhere (including
a path with spaces), then double-click seam-host.exe: the SEAM window opens.

WHEN WINDOWS WARNS ABOUT THE APPLICATION (ONCE)
-----------------------------------------------
The SEAM builds are not code-signed. The first time you run SEAM-Setup-x64.exe, or the
first time you run a seam-host.exe that came out of a downloaded ZIP, Windows may show
"Windows protected your PC" (SmartScreen):

  1. Click "More info".
  2. Click "Run anyway".

Windows remembers the choice for that file, so this happens once. It is the only warning
SEAM triggers: the application itself never asks for administrator rights (the installer
does, because it writes to Program Files and registers in Installed apps).

WHAT IS IN THE FOLDER
---------------------
  seam-host.exe              The SEAM desktop application (no console window)
  seam-host.dll, *.dll,      The self-contained .NET 8 desktop runtime.
  hostfxr.dll, hostpolicy.dll,   These stay in the same folder as seam-host.exe.
  coreclr.dll, System.*.dll
  seam-agent.jar             The Android agent, built from this repository's
                             android-agent/ sources (MIT). Pushed to the phone
                             automatically over ADB. Never edit or move it.
  platform-tools\            Android Platform-Tools: adb.exe + its two DLLs.
                             Used automatically from this folder.
  Get-Adb.ps1                Optional repair tool: re-downloads Google's official
                             Platform-Tools if platform-tools\ is missing/corrupt.
  LICENSE                    SEAM's MIT licence.
  THIRD-PARTY-NOTICES.md     What is bundled, what is not, and why.
  BUILD-INFO.txt             Exact commit / runner / SDK versions used to build.

USING THE APPLICATION
---------------------
  1. Enable Developer options -> USB debugging on the phone (tap "Build number"
     seven times in Settings -> About phone to unlock Developer options).
  2. Plug the phone in and tap ALLOW on the "Allow USB debugging?" prompt.
  3. Open SEAM. The window says "No phone detected" until a phone is visible; it does
     not need a phone to start.
  4. Press Connect. SEAM pushes and starts the agent, then authenticates with mutual
     HMAC-SHA256. The status pill turns green: Connected.
  5. Input sharing is enabled automatically after connecting. Move the pointer to the
     screen edge named in the window (right by default) to take control of the phone.
  6. Ctrl + Alt + Q returns control to Windows at any time. Disconnect (or unplug the
     cable) returns control as well.
  7. Unplug and re-plug: SEAM notices the phone again, and the Connect button becomes
     Reconnect. "Connect automatically" is on by default (Settings), so an authorised
     phone is picked up on its own - over USB or over Wi-Fi.

Settings live in %APPDATA%\SEAM\ui-settings.json (edge, auto-connect, activity length).
Nothing there is secret.

WI-FI INSTEAD OF A CABLE (WIRELESS DEBUGGING)
---------------------------------------------
SEAM finds phones on the local network the same way the macOS host does, but Android
only allows it after one authorised pairing:

  1. Phone and PC on the same network. Phone: Developer options -> Wireless debugging
     -> on. Android 11 or newer is required.
  2. One-time pairing, because only the phone can display the code: on the phone tap
     "Pair device with pairing code", then on the PC run
         "C:\Program Files\SEAM\platform-tools\adb.exe" pair <phone-ip>:<port>
     and type the six-digit code. This is the ONLY step that needs a command prompt,
     and it is never needed again for this PC.
  3. From then on SEAM does the rest by itself: it asks adb for the endpoints the phone
     advertises (adb mdns services), connects the one it is allowed to use, starts the
     agent over it and shows Connected over Wi-Fi.
  4. If Wi-Fi drops and comes back, or the phone sleeps and wakes, SEAM re-dials the
     saved endpoint within about half a minute. Nothing to press.
  5. Press Disconnect to stop: SEAM will not reconnect until you press Connect again.

If the phone is advertising itself but not paired yet, the window says exactly that
("Waiting for the first wireless pairing") instead of pretending it can connect.
Wi-Fi sessions still go through adb, so adb.exe and Wireless debugging must stay on.

IF SOMETHING DOES NOT WORK
--------------------------
The window shows inline messages for the three cases that actually happen:

  Unauthorized      Unlock the phone and tap ALLOW. No prompt? Run
                    "adb kill-server" then re-plug the cable.
  Connection error  Read the message; the Diagnostics button shows every path SEAM
                    searched and what adb reports.
  No phone detected USB debugging is off, the phone is not plugged in, or the cable is
                    charge-only. A USB driver only matters if Windows itself flags the
                    phone in Device Manager (warning triangle).

"SEAM diagnostics" (Start Menu) or this console command prints the same report:

    seam-host.exe --diagnose

It ends with "RESULT: all bundled resources found." when the installation is intact.
It never prints your pairing key - only a short SHA-256 fingerprint.

The original command-line modes are still there for troubleshooting:

    seam-host.exe --self-test        offline protocol/self-check, no phone needed
    seam-host.exe --trajectory       connect and stream a scripted mouse path
    seam-host.exe --capture          console mode: old-style interactive session
    seam-host.exe --help             every option

YOUR DATA
---------
The pairing key lives at %USERPROFILE%\.seam\auth_key (outside the install folder) and
is shared with SEAM on macOS. Upgrades never touch it; uninstalling asks before deleting
it and keeps it by default.

REQUIREMENTS
------------
  Windows 10 (22H2 or later) or Windows 11, 64-bit. One monitor for the first test.
  Android 11+ phone with USB or Wireless Debugging enabled.
  No .NET installation of any kind.

WARNING
-------
While input is being shared, SEAM installs system-wide low-level mouse and keyboard
hooks and swallows input for the phone. Ctrl+Alt+Q always returns control. Do not leave
a shared session unattended. UAC prompts and the lock screen are invisible to low-level
hooks - input pauses there by design.

WHAT IS NOT IMPLEMENTED
-----------------------
Screen mirroring, QR-code pairing and remote control of the phone's screen. Android
requires the pairing code to be typed on the PC side, so there is no one-click pairing.
Unlike the macOS host, Windows has no direct Wi-Fi transport of its own: a cable-free
session still runs through adb, so it needs Wireless debugging and adb.exe running. SEAM shares
one mouse and one keyboard with the phone's existing Android UI; it does not stream
video. There is no system-tray icon: closing the window ends the session and removes the
hooks, by design.
