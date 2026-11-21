# SEAM for Windows

A Windows desktop application for SEAM that talks to the **existing, unmodified Android agent**
over the **existing `SEAM-Wire v1` protocol**.

The window is the product; the original command-line modes are still there for diagnostics and
for the automated tests.

Nothing outside `windows-host/`, the packaging assets under `windows-host/packaging/`, and
`.github/workflows/windows-build.yml` was changed: `macos-host/` and `android-agent/` are untouched.

---

## 0. Get a Windows build without installing anything

You do **not** need the .NET SDK, the .NET runtime, or Visual Studio to run SEAM on Windows.
A GitHub Actions workflow builds, tests and packages it on a Windows runner:

**Actions → "Windows build" → Run workflow** (manual trigger, nothing is published on its own).

When the run finishes, download either artifact from that run:

| Artifact | What it is |
|---|---|
| `SEAM-Setup-x64.exe` | Installer: Program Files, Start Menu, *Installed apps* entry, uninstaller |
| `SEAM-Windows-x64.zip` | Portable build: unzip anywhere, run `seam-host.exe` |

> The *Run workflow* button appears on the Actions tab once this workflow is on the default branch.
> Both artifacts are produced and attached on completion.

What the workflow actually does, in order, and fails on the first broken step:

```text
dotnet restore  →  dotnet build -c Release  →  seam-host.exe --self-test
   →  javac + d8 (builds seam-agent.jar from android-agent/ sources)
   →  dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=false  →  build\app
   →  add seam-agent.jar + platform-tools\ (adb) + docs to build\app    ← the ONE source of truth
   →  --diagnose + --self-test from build\app, from C:\, and from a path with spaces
   →  SEAM-Windows-x64.zip (flat)  →  unzip again, hash-compare, self-test the binary inside it
   →  Get-Adb.ps1 under real Windows PowerShell 5.1 (no-op + repair runs)
   →  SEAM-Setup-x64.exe (Inno Setup) from the same build\app
   →  silent install  →  --diagnose/--self-test from Program Files  →  reinstall  →  uninstall
   →  upload both artifacts
```

The ZIP and the installer are both produced from `build\app`, so they cannot drift apart.

### Why the build is a folder, not a single file

The first prototype published `-p:PublishSingleFile=true` with
`IncludeNativeLibrariesForSelfExtract=true`. That build extracts the runtime to
`%TEMP%\.net\seam-host\<id>\` and runs from there, so `AppContext.BaseDirectory` pointed at
`%TEMP%` — which is exactly what the first real-device test reported: `adb.exe` was never
found in the bundled `platform-tools\`, and `seam-agent.jar` was "expected in `%TEMP%`".

Now the host resolves everything from the folder that contains `seam-host.exe`
(`Environment.ProcessPath`, see `Host/AppLayout.cs`), and the build is a plain self-contained
folder, so nothing is extracted at run time and there is no temp lifecycle to manage.

### What is inside (both artifacts, identical files)

```text
SEAM-Windows-x64/                 (Explorer's "Extract All" makes exactly this one folder)
├── seam-host.exe                 the host
├── seam-host.dll, *.dll,         self-contained .NET 8 runtime - keep them beside the exe
│   hostfxr.dll, coreclr.dll
├── seam-agent.jar                Android agent, built from this repo's sources
├── platform-tools\               Android Platform-Tools (adb.exe + 2 DLLs) - used from here
├── Get-Adb.ps1                   optional repair tool (re-downloads Platform-Tools)
├── README-WINDOWS.txt            quick start
├── THIRD-PARTY-NOTICES.md        what is bundled and under which licence
├── BUILD-INFO.txt                commit, runner, SDK versions, SHA-256s
└── LICENSE
```

**`adb.exe` is bundled** in both artifacts. SEAM never requires a `PATH` entry, never copies
anything into `%TEMP%`, and prefers its bundled Platform-Tools over any Android SDK already
installed on the machine. See `packaging/THIRD-PARTY-NOTICES.md` for the redistribution
notice that ships alongside them.

**`seam-agent.jar` *is* bundled,** because it is compiled from this repository's own MIT-licensed
`android-agent/` sources by the same workflow (the macOS app bundle embeds it the same way).

Install, upgrade, uninstall and troubleshooting are documented step by step in
[`INSTALL.md`](INSTALL.md); the physical-hardware checklist is
[`MANUAL-VERIFICATION.md`](MANUAL-VERIFICATION.md).

### Current verified build

| | |
|---|---|
| Run | [run 44743829261](https://github.com/CalebPiusC/Seam/actions/runs/44743829261) (commit `6b6795e`; `pull_request` and `push` both green, 21 steps) |
| Shape | **flat archive**: files sit at the ZIP root, so Explorer's *Extract All* yields one `SEAM-Windows-x64` folder, not two nested ones |
| Enforced by CI | `--diagnose` reports `source : bundled with SEAM` from the app folder, from `C:\`, from a spaced path **and from `C:\Program Files\SEAM`**; the ZIP hash-compares file-for-file against `build\app`; the binary extracted from the ZIP passes `--self-test`; the installer is silently installed, run from the install dir, reinstalled over (pairing key unchanged) and uninstalled (pairing key preserved, *Installed apps* entry removed) |
| Artifacts from that run | `SEAM-Windows-x64.zip` 38,922,185 B `sha256:65b3c1c028cf8cd3b21037c41be308c2cf14ab77208e94a5662014d6904c44dd` · `SEAM-Setup-x64.exe` 28,575,910 B `sha256:e0537bfe95606aece8301cad1d935e7417f6fa2824f237d3dcfc57225ec48bfd` (they change with every build — these are quoted with the run id) |
| Installed-apps entry seen by CI | `SEAM 0.9.0` / `0.9.0` / `C:\Program Files\Seam Verification (x64)` |

---

### The desktop application

One executable, two faces: a window by default, the original CLI when asked.

```text
seam-host.exe                    -> MainForm (WinExe: no console is ever created)
seam-host.exe --gui              -> same
seam-host.exe --self-test        -> console output (borrows the launching terminal)
seam-host.exe --diagnose         -> console output
seam-host.exe --trajectory       -> console mode (scripted mouse stream, no hooks)
seam-host.exe --capture          -> console mode (interactive, needs a terminal)
```

`Program.Main` attaches to the parent console (`AttachConsole`) only for those CLI modes, and is
a no-op when stdout is already redirected (CI, pipes, `> file`).

**Architecture.** The window is a view over `Host/SessionController.cs`, which owns exactly one
session: adb discovery, agent bootstrap, authentication, the TCP connection and the low-level
input hooks. It raises `StateChanged`, `Log`, `DevicesChanged` and `DisplayInfoReceived`; the form
only renders them and forwards clicks. The controller is built on the same primitives the
hardware-verified console mode uses (`AppLayout`, `SeamAuthenticator`, `AdbBootstrapper`,
`SeamConnection`, `WindowsInputCapture`) - no protocol, transport or control-routing code is
duplicated, and the console path is untouched.

**What the window shows.** A compact navigation rail (Home / Activity / Settings /
Diagnostics) and one page at a time, so the log no longer sits next to the connection controls.

| Area | Contents |
|---|---|
| Rail | SEAM mark, the four destinations, the status pill (`Not connected` / `Connecting...` / `Connected` / `Unauthorized` / `Connection error`), version and whether adb is bundled |
| Home - status | Phone model or `No phone detected`, one sentence of cause-and-fix, serial and endpoint, Connect (or Reconnect) / Disconnect / Refresh |
| Home - arrangement | Read-only diagram of this PC and the phone on the configured screen edge; the tile that owns the pointer is drawn in the accent colour |
| Home - input sharing | Who has the pointer right now (`Windows` / `Android phone`), what to do, live counters (sent / received / forwarded / uptime), Enable / Stop input sharing |
| Activity | Timestamped log with severity colours, follow-latest, Copy and Clear, capped at the configured length - collected whether or not the page is open |
| Settings | Screen edge, start sharing on connect, auto-connect, lines kept, and Save |
| Diagnostics | The same report as `--diagnose`, copyable, refreshed on open |
| Banner | Inline, dismissible message for unauthorised devices and connection errors - never a modal |

Empty, loading and error states are first class: with no phone the window says *"No phone
detected"* and lists the three steps that make one appear; while connecting the device card says
what is happening; failures stay on screen until dismissed.

**Lifecycle.** Closing the window stops the timers, removes the low-level hooks
(`WindowsInputCapture.Dispose`), closes the TCP session and saves preferences. There is no tray
icon by design - closing the window ends the session, so nothing is left running. The shared adb
server is *not* killed: other tools (Android Studio, scrcpy) may be using it.

**Preferences** live in `%APPDATA%\SEAM\ui-settings.json` (edge, auto-connect, input sharing on
connect, activity length). They are separate from the pairing key in
`%USERPROFILE%\.seam\auth_key`, which the UI only ever shows as a SHA-256 fingerprint.

**`--ui-smoke`** builds the real window, forces layout, optionally renders it to a PNG, and exits
without a message loop. CI runs it so that "the UI starts on a machine with no phone" and "the
window can be drawn" are executed facts rather than claims.

**Interactive launch** is exercised separately, because `--ui-smoke` never enters `Application.Run`:
`.github/scripts/Test-SeamGuiLifecycle.ps1` starts `seam-host.exe` with **no arguments** (what a
double-click, the Start Menu shortcut and the desktop shortcut all do), waits for a visible
`WindowsForms` top-level window owned by that process, requires it to answer
`SendMessageTimeout(WM_NULL)` - which only happens when the message loop is pumping - then posts
`WM_CLOSE` and asserts exit code 0, no leftover `seam-host.exe`, and a settings file rewritten by
`OnFormClosing -> SessionController.Dispose`. It also refuses to accept an error dialog (`#32770`)
as the main window, so a failed start can never be reported as a good one.

---

## 1. Honest status up front

The authoring sandbox was **Linux with no .NET SDK, no JVM, no `adb` and no Android device**,
so no local build was possible there. Compilation therefore happens on a GitHub Actions Windows
runner (`.github/workflows/windows-build.yml`), which is a real `dotnet build` on real Windows —
it is not a simulation, and it is also not a substitute for running the thing on your desk.

| | State |
|---|---|
| Protocol / auth / framing design | **Verified by execution** — 33 conformance checks pass (`tools/verify_protocol.py`) against a device-side simulator transliterated from the shipped Java |
| C# sources | **Compiled** — `dotnet build -c Release` on `windows-latest` (.NET 8), 2026-11-20 ([run](https://github.com/CalebPiusC/Seam/actions/runs/44743829261)) |
| `--self-test` | **Passes** — on the build output, on the published app folder, from a path with spaces, and on the binary extracted from the ZIP |
| Resource discovery (`adb`, agent jar) | **Executed on Windows** — `--diagnose` reports `source : bundled with SEAM` from the app folder, from `C:\`, from a spaced path and from the installed `Program Files` folder |
| Packaging | **Produced and tested** — self-contained `win-x64` folder, `SEAM-Windows-x64.zip`, and `SEAM-Setup-x64.exe`, which CI silently installs, re-runs, upgrades and uninstalls |
| Desktop window | **Built, rendered and opened** by CI — `--ui-smoke` creates the real `MainForm`, lays it out and renders it to a PNG artifact; a separate step launches it with no arguments and proves the `Application.Run` message loop answers, that `WM_CLOSE` closes it with exit code 0, and that no process is left behind. The PE subsystem is asserted to be GUI, so double-clicking cannot open a console |
| USB control on real hardware | **Verified on a physical machine** (HP EliteBook 830 G6, Windows 11 x64, Samsung Galaxy A05 / Android 15): bundled adb discovery, agent push/start, device detection, mutual HMAC-SHA256, mouse + keyboard control, Ctrl+Alt+Q. That verification was done with the **console** build; the desktop window drives the same `SessionController` primitives but has not itself been exercised against a phone |
| Desktop window against a phone | **Not demonstrated** — nobody has used the window with a phone attached; the launch/close lifecycle is CI-verified, the phone flows are not. See `MANUAL-VERIFICATION.md` section 2 |
| Wireless (Wi-Fi) session | **Not physically verified** — mDNS discovery, automatic `adb connect`, reconnection and Disconnect-suppression are implemented and covered by 17 offline self-test checks; no Wi-Fi session has been run on hardware yet |
| Windows low-level hooks, `ClipCursor`, DPI | **Executed on real hardware** in console mode; the same `WindowsInputCapture` instance is created by the window |

---

## 2. What was reused vs. written

**Reused unchanged**
- The Android agent (`seam-agent.jar`) — no modifications, no rebuild, same auth key scheme.
- `SEAM-Wire v1` — every opcode, payload length, field order, domain string and nonce ordering,
  transcribed from `Protocol/SeamWireProtocol.swift` / `SeamProtocol.java`.
- The transport topology — USB is still `adb forward tcp:0 localabstract:seam_uhid`; Wi-Fi is
  still a plain TCP connection to port 27186.
- Bootstrap semantics — remote paths, `SEAM_AGENT_READY` / `SEAM_WIFI_ENDPOINT:` log markers,
  the detached `nohup … app_process … --server --detach` launch, the jar+key stamp idempotency
  check, and the "never suppress the phone's on-screen keyboard" invariant.
- Cursor feel constants — `pointerVelocityGain = 1.85`, the `360`/`65175` entry insets, the
  4 px edge-return threshold, 160 ms entry grace, 1/32-notch scroll units, 500 ms heartbeat.

**Written fresh (all Windows-specific, ~1,500 lines)**
- `Input/WindowsInputCapture.cs` — `WH_MOUSE_LL` / `WH_KEYBOARD_LL` hooks, edge-crossing state
  machine, pointer pinning by `SetCursorPos` re-centring + `ClipCursor` guard box, panic hotkey.
- `Input/WindowsKeyTable.cs` — Set-1 scan code → HID Usage Page 0x07 table, 6KRO state.
- `Transport/SeamConnection.cs` — TCP session, frame reader, 500 ms heartbeat.
- `Bootstrap/AdbBootstrapper.cs` — `adb` discovery, device parsing, push/launch, forwarding.
- `Testing/` — in-process loopback agent + offline self-test.

**Deliberately not built:** tray UI, arrangement flyout, UDP fast path, Wi-Fi discovery probe,
clipboard monitoring, screen mirroring, QR pairing. Prototype scope only.

---

## 3. Build

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download) (Windows, macOS or Linux).
Only local *development* needs it — end users get the self-contained ZIP from the workflow above.
No NuGet packages are referenced — the project targets plain `net8.0` and uses only P/Invoke,
so it compiles anywhere and runs only on Windows.

```bat
cd windows-host
dotnet build -c Release
```

Output: `windows-host/bin/Release/net8.0/seam-host.exe`

To run it you need the **unchanged** agent jar and an `adb.exe`. For a shipped build both are
inside the package; for a from-source build, either run `windows-host\packaging\Get-Adb.ps1`
in `bin\Release\net8.0\` (it installs Platform-Tools into `platform-tools\` there) and copy
`seam-agent.jar` next to `seam-host.exe`, or pass `--adb` / `--jar`.

Resolution is app-relative and printed on demand (`Host/AppLayout.cs`):

```
seam-host.exe --diagnose
```

```
adb   : <app dir>\platform-tools\adb.exe   (then <app dir>\adb.exe, then PATH / Android SDK)
agent : <app dir>\seam-agent.jar           (then <app dir>\Resources\seam-agent.jar)
```

The current directory is never consulted; a bundled copy always wins over `PATH`.

---

## 4. Run

### 4a. No phone at all — offline self-test (start here)
```bat
seam-host.exe --self-test
```
Checks the golden handshake vectors, every frame layout, a full TCP handshake against the
in-process loopback agent, and resource discovery (executable directory, working-directory
independence, a path containing spaces, missing-resource errors, `--adb` override rejection,
`--auth-key` redaction, and - when `platform-tools\adb.exe` is present - actually launching adb).
Expected: `N passed, 0 failed, M skipped`, exit code 0. A `SKIP` means the check needs a file the
current folder does not have; it is never counted as a pass.

### 4b. Real phone, no input hooks — prove the wire path
```bat
REM USB (needs an ADB driver — Windows is not driverless like macOS)
seam-host.exe --trajectory

REM or over Wi-Fi / Wireless Debugging, bypassing ADB and the driver entirely
seam-host.exe --connect 192.168.1.42:27186 --trajectory
```
Streams `CURSOR_ENTER` + a 240-frame figure-eight + an edge-return push. **If the phone's real
hardware cursor moves, the Windows→Android path is proven end-to-end.** This is the acceptance
test that matters most.

### 4c. Interactive — capture Windows input
```bat
seam-host.exe                      REM phone on the right edge of the screen
seam-host.exe --edge left
seam-host.exe --capture            REM route on first movement (deterministic demo)
```
Move the pointer to the configured edge to take control of the phone. **`Ctrl+Alt+Q`** returns
control immediately. Press `q` in the console to quit, `r` to force a return.

> Safety: this installs system-wide input hooks that swallow mouse/keyboard while routed.
> Run it unelevated first, and keep the console window visible so `q`/Ctrl+Alt+Q are reachable.
> The hooks are removed on exit; `ClipCursor` is released in `Dispose` and on panic.

---

## 5. Feature status

| Capability | Implemented | Verified |
|---|---|---|
| ADB device discovery (`adb devices -l` parsing) | yes | **yes** (self-test parsing + `--diagnose` on Windows CI) |
| Agent deploy/launch + ready-marker wait | yes | physical USB run (console build); window untested |
| `adb forward tcp:0 → localabstract:seam_uhid` | yes | physical USB run (console build); window untested |
| Mutual HMAC-SHA256 handshake | yes | **yes** (byte-exact, both directions) |
| `DISPLAY_INFO` receive + parse | yes | **yes** |
| `PING`/`PONG` heartbeat + RTT plumbing | yes | **yes** |
| `EDGE_RETURN` receive + cursor restore | yes | **yes** (parse + receive); Windows restore untested |
| Mouse movement forwarding | yes | **protocol path yes**, hooks no |
| Mouse buttons (L/R/M/X1/X2) | yes | protocol only |
| Scroll (1/32-notch conversion) | yes | protocol only |
| Keyboard (scan code → HID, 6KRO) | yes | protocol only |
| Edge crossing + pointer pinning | yes | no |
| Clipboard host→Android | frame + `--` | protocol yes; no monitor |
| Clipboard Android→host | parse + log | protocol yes; no pasteboard write |
| Wireless discovery (`adb mdns services`) + automatic `adb connect` | yes | logic unit-tested (17 checks); **no hardware run** |
| Automatic reconnection after a Wi-Fi drop | yes | logic unit-tested; **no hardware run** |
| Disconnect suppresses automatic reconnect | yes | logic unit-tested |
| Initial pairing (`adb pair`, phone's code) | documented one-time step | manual by design — Android shows the code only on the phone |
| Stale-agent / early-close recovery on connect | yes | logic unit-tested; **no hardware run** |
| Connection states in the window (`SessionState` + guidance) | yes | text/state mapping unit-tested |
| Installer, portable ZIP, upgrade, uninstall | yes | **yes** (CI installs, re-runs, upgrades, uninstalls) |
| Code signing / auto-update | no | — (SmartScreen warning documented) |
| System-tray icon | no | by design: closing the window ends the session |

---

## 6. What was actually executed here

```
$ python3 tools/verify_protocol.py
=== SEAM-Wire v1 conformance verification ===
[1] Golden handshake vectors (client path, Java-derived)     6/6 PASS
[2] Frame layouts                                           14/14 PASS
[3] End-to-end against the device simulator (real TCP)       13/13 PASS
=== 33 passed, 0 failed ===
```

Covered by that run: 38-byte challenge parse, 68-byte response bytes, client HMAC against a
Java-derived vector, 41-byte ack verification, LE session id, `DISPLAY_INFO` 1080×2400@420/120,
50 mouse frames delivered, key frames delivered, clipboard text round-trip, PONG timestamp echo,
`EDGE_RETURN` triggered by pushing past the bezel.

```
$ python3 tools/check_csharp_structure.py windows-host
10 file(s) checked, 0 with structural problems
```
Balanced delimiters outside strings/comments, no Python-isms, no duplicate types. **Not a
compiler.**

```
$ python3 tools/check_java_parity.py
=== 44 passed, 0 failed ===
```
Escapes the "oracle is a Python transliteration" problem: it parses the **upstream Java**
(`SeamProtocol.java`, `SeamAuthenticator.java`, `SeamInputEngine.java`, `UhidDevice.java`) and
compares magic, version, transport ids, all 10 opcodes, payload lengths, edge ids, the 256 KiB
clipboard cap, heartbeat timeout, nonce/HMAC sizes and both auth-domain strings against the C#
*and* the simulator, plus the payload field order taken from the agent's own readers. All three
now agree; the check is a static text comparison, not a runtime test.

**None of this compiles or runs the C#.** See `WINDOWS-READINESS-REVIEW.md` (repo root) for the
file-by-file review, the bugs fixed in this pass, and the exact Windows test procedure.

`tools/agent_sim.py` is the fixture those tests run against: a transliteration of
`SeamAuthenticator.java` + `UsbLocalSocketServer.java` + the shadow-cursor part of
`SeamInputEngine.java`. It is a test double, clearly not the product, and it proves nothing
about `/dev/uhid` or Android input injection.

---

## 7. Blockers hit

1. **No .NET SDK obtainable.** `dot.net`, `builds.dotnet.microsoft.com` and `nuget.org` are all
   unreachable from the sandbox; the npm package `dotnet-sdk` is the 2017 SDK 2.0 wrapper and
   downloads from the same blocked hosts. Consequence: the C# has never been compiled.
2. **No Windows host.** The Windows-only behaviour (hook reliability, `ClipCursor` clearing on
   focus change, DPI handling, sub-notch wheel deltas) cannot be exercised from Linux at all.
3. **No `adb`, no device, no JVM.** So neither the real agent nor a real handset could be used;
   the Java transliteration in `tools/agent_sim.py` was the closest available oracle.
4. Tree-sitter's prebuilt C# WASM grammar would not load with any `web-tree-sitter` I could
   install (dylink/ABI mismatch), which is why the structural checker is hand-rolled.

---

## 8. Next three tasks

1. **`dotnet build` + `--self-test` on real Windows.** Fix whatever the compiler and the
   golden-vector assertions report. This converts the biggest unknown (does it even build and
   hand-shake?) into a known-good baseline, and needs no phone.
2. **`--trajectory` against the real handset over USB and over `--connect`,** capturing RTT and
   confirming the native cursor moves. This validates the ADB bootstrap, the agent reuse, and
   the frame path against the *actual* `seam-agent.jar` rather than the simulator.
3. **Harden the input capture path:** verify edge crossing and pointer pinning at 100/150/200 %
   DPI on a dual-monitor setup, add a hook-liveness watchdog (Windows removes a slow hook
   silently, with no notification), and release `ClipCursor` on session lock/desktop switch.

Then, in priority order: clipboard monitor (host→Android paste), Wi-Fi UDP fast path with
signed 42-byte datagrams, discovery probe + mDNS pairing, tray flyout, packaging/signing.

---

## 9. Source map

```
windows-host/
├── Program.cs                     entry point: GUI by default, CLI modes for diagnostics/tests
├── app.manifest                   PerMonitorV2 DPI, Windows 10/11, asInvoker
├── Gui/                           the desktop application (WinForms, no NuGet packages)
│   ├── SeamGui.cs                 Application entry, --ui-smoke / --ui-screenshot
│   ├── MainForm.cs                shell: rail, page host, Home page and all state rendering
│   ├── NavRail.cs                 Home / Activity / Settings / Diagnostics navigation
│   ├── ArrangementPanel.cs        read-only PC + phone diagram for the configured edge
│   ├── StatusPill.cs / PillButton.cs / AlertBanner.cs / LogView.cs   small themed controls
│   └── Theme.cs                   graphite palette, Segoe UI Variable ramp, card panel
├── Host/
│   ├── SessionController.cs       one place that owns adb, auth, session, wireless autopilot, hooks
│   ├── WirelessDiscovery.cs       adb mDNS parsing + the autopilot rule (pure, unit-tested)
│   ├── ConnectionDiagnosis.cs     detected condition -> headline/advice shown in the window
│   ├── DiagnosticsReport.cs       shared report (CLI + window)
│   ├── AppLayout.cs               app-relative adb/agent discovery
│   └── UiSettings.cs              %APPDATA%\SEAM\ui-settings.json
├── Protocol/SeamWireProtocol.cs   constants, coordinates, DISPLAY_INFO/EDGE_RETURN parsing
├── Protocol/SeamAuthenticator.cs  3-message mutual HMAC-SHA256 (client side)
├── Protocol/SeamFrames.cs         outbound frame serializers
├── Transport/SeamConnection.cs    TCP session, reader, heartbeat
├── Bootstrap/AdbBootstrapper.cs   adb discovery, deploy, launch, forward
├── Input/WindowsKeyTable.cs       scan code → HID table, 6KRO state
├── Input/WindowsInputCapture.cs   LL hooks, edge crossing, pointer pinning
├── Testing/                       LoopbackAgent (fixture) + SelfTest (oracle-driven, 80 checks)
├── installer/SEAM.iss             Inno Setup 6 installer (start-menu, Installed apps, uninstaller)
├── packaging/Get-Adb.ps1          fetch/manage Android Platform-Tools (PS 5.1-compatible)
├── INSTALL.md                     user-facing install, wireless and troubleshooting guide
└── MANUAL-VERIFICATION.md         the physical acceptance checklist

tools/
├── agent_sim.py                   device-side simulator (test fixture)
├── verify_protocol.py             33 executable conformance checks
└── check_csharp_structure.py      structural lint for the uncompiled C#
```

## 10. Licence

Inherits the repository's MIT licence (© 2026 Feranmi Oresajo); this directory is a derivative
work and must keep the original copyright notice.
