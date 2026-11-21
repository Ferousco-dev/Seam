# Installing SEAM on Windows

SEAM for Windows ships in two forms, built from the same folder:

| You want | Download | What it does |
|---|---|---|
| The normal install | `SEAM-Setup-x64.exe` | Installs to `C:\Program Files\SEAM`, adds Start Menu shortcuts, registers in **Installed apps**, ships an uninstaller |
| No installation | `SEAM-Windows-x64.zip` | Unzip anywhere and run `seam-host.exe`; moving the folder is fine |

Both contain the same files: the self-contained host (`seam-host.exe` plus the .NET 8
runtime DLLs), `seam-agent.jar`, `platform-tools\adb.exe`, and the documentation below.
**You never need to install .NET, edit `PATH`, or copy anything into `%TEMP%`.**

## Supported Windows versions

Windows 10 version 22H2 or later, or Windows 11 - **64-bit (x64) only**. The build targets
`net8.0-windows` and is published self-contained, so no .NET runtime, no Visual Studio and no
Android SDK is required. ARM64 Windows runs it under emulation but is not a tested target; one
monitor is assumed for the first run (multi-monitor edge routing is untested).

---

## Install with the installer

1. Run `SEAM-Setup-x64.exe`. Windows SmartScreen will warn you because the build is not
   code-signed: **More info → Run anyway**.
2. Accept the default `C:\Program Files\SEAM`, or choose another folder (paths with spaces are
   fine). A desktop shortcut is optional.
3. Start **SEAM** from the Start Menu.
4. Prepare your phone once (**USB debugging**, see below), then accept the
   *"Allow USB debugging?"* prompt that appears on the phone.

Nothing else is required. Verify the install at any time:

```
"C:\Program Files\SEAM\seam-host.exe" --diagnose
```

### What the installer touches

| Location | What |
|---|---|
| `C:\Program Files\SEAM` | The application and all bundled resources |
| Start Menu → `SEAM` | Launch, `SEAM diagnostics`, `SEAM quick start`, `Uninstall SEAM` |
| `Desktop\SEAM.lnk` | Only if you ticked the box |
| `HKLM\...\CurrentVersion\Uninstall\SEAM_is1` | The **Installed apps** entry |
| `%USERPROFILE%\.seam\auth_key` | Created by the *application* on first run — not by the installer |

### Upgrading

Run a newer `SEAM-Setup-x64.exe`. It reuses the existing folder and replaces the program
files; `%USERPROFILE%\.seam\auth_key` is untouched, so the pairing with your phone survives.

### Uninstalling

**Settings → Apps → Installed apps → SEAM → Uninstall**, or Start Menu → *Uninstall SEAM*.

The program folder and the *Installed apps* entry are removed. Your pairing key at
`%USERPROFILE%\.seam\auth_key` is **kept**: the uninstaller offers to delete it and the
default answer is **No**. Silent uninstalls (`/VERYSILENT`) never delete it. Delete the file
manually if you want it gone.

---

## Portable ZIP

1. Right-click the ZIP → **Extract All**. Explorer creates one folder named
   `SEAM-Windows-x64` with `seam-host.exe` directly inside it (the archive is flat on
   purpose — there is no nested folder).
2. Keep the files together. You may move or rename the folder, including to a path with
   spaces; `seam-host.exe` finds `platform-tools\` and `seam-agent.jar` in **its own folder**.
3. Delete the folder to "uninstall". Your pairing key stays in `%USERPROFILE%\.seam\`.

A USB stick works, but SEAM writes to the phone (pushes the agent), so plug the stick into
a machine you trust.

---

## Phone preparation (once)

1. **Settings → About phone** → tap **Build number** seven times to unlock Developer options.
2. **Settings → Developer options → USB debugging** → on.
3. Plug the phone in and **accept the prompt on the phone** (tick *Always allow from this
   computer* if you trust the PC).

## Wireless debugging (no cable)

Wireless Debugging (**Developer options → Wireless debugging**) is an alternative when no USB
cable is at hand, or when a vendor USB driver will not cooperate. It is the same workflow the
macOS host uses, with one unavoidable difference: **Android requires one authorised pairing that
only the phone can display.**

**Prerequisites.** Phone and PC on the same network (a phone hotspot works), Android 11 or newer,
Developer options → USB debugging *and* Wireless debugging switched on, `platform-tools\adb.exe`
present (both packages ship it), and no firewall rule blocking outbound TCP from `adb.exe`.

**Initial pairing, once per PC.** On the phone open *Wireless debugging → Pair device with
pairing code*; the phone shows an address and a six-digit code. On the PC:

```
"C:\Program Files\SEAM\platform-tools\adb.exe" pair <phone-ip>:<port>
"C:\Program Files\SEAM\platform-tools\adb.exe" connect <phone-ip>:<debug-port>
```

The `connect` step is optional - SEAM performs it itself once the PC is paired. This is the only
step that needs a command prompt, and it never repeats for the same PC and phone. SEAM cannot
bypass it: the code is shown only on the phone.

**After pairing, everything is automatic.** SEAM asks adb which Wireless debugging endpoints the
phone advertises (`adb mdns services`) every few seconds, connects an already-paired one, starts
the agent over that transport and shows **Connected over Wi-Fi**. If Wi-Fi drops, the phone
sleeps, or the PC loses the network, SEAM re-dials the saved endpoint by itself - typically
within half a minute - without a second Connect press.

**Manual disconnect is respected.** Pressing **Disconnect** stops the session *and* the
automatic reconnection; SEAM stays quiet until **Connect** is pressed again.

**If the phone advertises pairing only,** the window shows **Waiting for the first wireless
pairing** with the instructions above. It does not claim to be connected and it does not fall
back to driver advice.

**One-time pairing can be revoked** on the phone at any time (Wireless debugging → paired
devices), and Android rotates the port on every reboot; SEAM handles the new port because it
rediscovers the endpoint instead of remembering it.

---

## Troubleshooting

**Setup stops with "DeleteFile failed; code 5. Access is denied." on
`platform-tools\adb.exe`.** Windows will not replace a running program, and the adb server
SEAM starts keeps that exact file open even after the SEAM window is closed. Close SEAM, then
in an Administrator Command Prompt:

```
"C:\Program Files\SEAM\platform-tools\adb.exe" kill-server
```

Re-run Setup. If it still refuses, reboot and install before starting SEAM. Setup now stops
the adb server itself before copying files and schedules any file that is still locked for
replacement at the next restart, so an upgrade should not hit this.

Start here — it prints what was found and where:

```
seam-host.exe --diagnose
```

| Symptom | Meaning | Fix |
|---|---|---|
| `[FATAL] adb.exe was not found ...` | `platform-tools\adb.exe` is missing from the install folder | Reinstall SEAM, or run `Get-Adb.ps1` in the install folder |
| `[FATAL] seam-agent.jar was not found next to the application` | The agent jar was moved or deleted | Reinstall SEAM, or pass `--jar <path>` |
| `adb` sees nothing, phone says nothing | Driver or cable | Try another cable/port; install your vendor's USB driver; or use Wireless Debugging |
| Device shows `state=unauthorized` | The phone has not accepted this PC | Unlock the phone, accept *Allow USB debugging?*, then `adb kill-server` |
| Device shows `state=offline` | Stale ADB session | Unplug/replug, or `platform-tools\adb.exe kill-server` |
| Several devices listed | SEAM refuses to guess | Pick one with `--device <serial>` |
| `--self-test` fails | The build itself is broken | Report the output in an issue |
| Antivirus/SmartScreen blocks the installer | Unsigned build | *More info → Run anyway*, or use the portable ZIP |

`Get-Adb.ps1` is a **repair** tool now, not a prerequisite: it re-downloads Google's official
Platform-Tools into `platform-tools\` if the bundled copy is missing or corrupt.

---

## Known limitations

- **Windows support is experimental.** The host passes its offline self-test, the desktop UI
  smoke test and the packaging/install/upgrade/uninstall cycle in CI, but it has not been
  exercised here against a physical phone over Wi-Fi (see `MANUAL-VERIFICATION.md`).
- **Wireless sessions still use adb.** Unlike the macOS host, the Windows host has no direct
  Wi-Fi transport of its own, so a cable-free session requires Wireless debugging to stay on and
  `adb.exe` to be running. Turning Wireless debugging off ends the session.
- **Pairing needs the phone's code once.** Android exposes it only on the phone's screen.
- **Not code-signed.** SmartScreen shows a warning on first launch of the installer or the
  downloaded `seam-host.exe`; there is no automatic update mechanism.
- **No screen mirroring, no tray icon, no multi-monitor edge choice** - one monitor is assumed.
- **ARM64 Windows** runs the x64 build under emulation and is not a tested target.

## For maintainers

```
# from the repository root, on Windows
dotnet publish windows-host/Seam.WindowsHost.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=false -o build\app
# (CI then adds seam-agent.jar + platform-tools\ + docs to build\app)

iscc /DAppVersion=0.9.0 /DSourceDir=%CD%\build\app /Obuild\installer windows-host\installer\SEAM.iss
```

`build\app` is the single source of truth: the ZIP is `Compress-Archive` of its contents and
`SEAM.iss` packages the same folder, so the two artifacts cannot drift. See
`.github/workflows/windows-build.yml` for the full sequence, including the silent
install → upgrade → uninstall test.
