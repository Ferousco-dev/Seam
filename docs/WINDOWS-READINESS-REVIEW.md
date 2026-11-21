# Preparing the Windows prototype for actual Windows execution

**Scope:** every file under `windows-host/` (10 C# files, 2,845 lines) plus the three verification
tools. **No new features.** Only correctness and safety changes.
**Date:** 2026-11-20 ·
**Status of `macos-host/` and `android-agent/`: untouched.**

> **Update, same day — the compile question is now answered.** The project was built for real on
> a GitHub Actions `windows-latest` runner with the .NET 8 SDK
> ([run 44728342098](https://github.com/CalebPiusC/Seam/actions/runs/44728342098)):
> `dotnet restore` → `dotnet build -c Release` → `seam-host.exe --self-test` → `dotnet publish
> -r win-x64 --self-contained -p:PublishSingleFile=true` → self-test again on the published
> binary → `SEAM-Windows-x64.zip`. **All steps passed on the first attempt**, so the nine fixes in
> §3 were sufficient for a clean build and no compiler diagnostics were needed.
>
> Everything in §§4–6 about *runtime* behaviour on Windows, and §5.4 about the test oracle, still
> stands unchanged: a green CI run says nothing about hooks, cursor pinning, DPI, ADB bootstrap or
> a real phone. The procedure in §7 is still what to follow.

---

## 0. Bottom line, up front

| Question | Honest answer |
|---|---|
| Does it compile? | **Yes.** `dotnet build -c Release` on GitHub's `windows-latest` with .NET 8: clean, all steps green ([run 44728342098](https://github.com/CalebPiusC/Seam/actions/runs/44728342098)). |
| Does it run on Windows? | **Partly.** It runs headless on a Windows runner and its `--self-test` passes there, twice (build output and published single-file exe). The interactive paths — hooks, cursor pinning, DPI, adb bootstrap — have still never executed on a desktop with a pointer. |
| Are the protocol constants right? | **Yes, now checked against the upstream Java itself** — 44/44 static parity checks (§5.2). |
| Are the handshake golden vectors trustworthy? | **Yes** — reproduced by OpenSSL, which shares no code with the Java, the Python simulator, or the C# (§5.3). |
| Are the 33 conformance checks a proof of Android compatibility? | **No.** They prove the C# client agrees with a Python fixture I wrote. See §5.4 for exactly where that can lie. |
| Can the *compile* signal be obtained without a local SDK? | **Yes, and it was.** GitHub Actions does it; the developer installs nothing (§7 Gate 1 is now served by CI). |
| Is it ready for a first real-device test? | **After §8.** Nine defects were fixed in this pass; three of them would have made the first test fail or mislead badly. |

---

## 1. How this review was done

I read every file end to end, then attacked four specific risk classes:

1. **Compile risk that can be checked without a compiler** — namespace/`using` resolution against
   `<ImplicitUsings>enable</ImplicitUsings>`, type resolution against the declared project types,
   overload resolution at every call site (`short`/`ushort`/`sbyte` argument conversions,
   `int` → `short` is *not* implicit for variables, only for literals), nullable flows, `volatile`
   legality, `out _` discards, index-from-end, `HMACSHA256.TransformBlock(null, 0)`.
2. **Win32 ABI and API-contract correctness** — struct layouts by hand against the Windows headers,
   `SetLastError` pairing with `Marshal.GetLastWin32Error`, message-queue requirements for
   low-level hooks, `ClipCursor` focus semantics.
3. **Concurrency** — the hook thread, the reader thread, the heartbeat `Timer`, `Dispose`, and the
   console loop all touch the same objects.
4. **Oracle honesty** — what the tests actually assert versus what a reader might assume.

Everything I changed is a defect fix. No behaviour was added, no feature was redesigned, and
nothing was made to *look* like it works.

---

## 2. Compilation: not attempted, and why that is not a judgement call

```
$ dotnet --version        → dotnet: command not found
$ mono / csc / mcs / msbuild --version → not found
$ curl -sS -o /dev/null -w '%{http_code}' https://api.nuget.org/v3/index.json          → 000
$ curl -sS -o /dev/null -w '%{http_code}' https://builds.dotnet.microsoft.com/...      → 000
```

No SDK, no JVM, no root, and the only reachable hosts are `github.com`, `codeload.github.com`,
`api.github.com`, `registry.npmjs.org`, `pypi.org`, `files.pythonhosted.org`. The .NET SDK is not
published on any of them. **I therefore did not build, and I do not claim a build.**

What *is* checkable statically, and was checked:

| Check | Result |
|---|---|
| `<TargetFramework>` / project shape | `net8.0` (deliberately **not** `net8.0-windows`), `OutputType=Exe`, `Nullable=enable`, `ImplicitUsings=enable`, `AllowUnsafeBlocks=false`, `TreatWarningsAsErrors=false` |
| `unsafe`/`fixed`/`stackalloc` present while `AllowUnsafeBlocks=false` | none — consistent |
| `Registry` / `System.Drawing` / `Windows.Forms` / `System.Management` | none — so no package reference is needed |
| Every project type referenced resolves to a declared type | yes; `SeamConnection`, `SeamFrames`, `AndroidDisplayInfo`, `HidKeyState`, `LoopbackAgent`… all declared |
| `using` coverage | adequate: the BCL namespaces used outside the implicit set (`System.Runtime.InteropServices`, `System.Net.Sockets`, `System.Collections.Concurrent`) are all explicitly imported |
| Hook struct sizes vs. the SDK headers | `MSLLHOOKSTRUCT` 32 B on x64 (POINT/3×DWORD/pad/ULONG_PTR), `KBDLLHOOKSTRUCT` 24 B, `MSG` `{hwnd,msg,wParam,lParam,time,pt}` in the correct order, `RECT`/`POINT` 4×int — **all correct** |
| `SetLastError=true` on every P/Invoke whose error code is read | `SetWindowsHookEx`, `UnhookWindowsHookEx`, `SetCursorPos`, `PostThreadMessage` all carry it; `Marshal.GetLastWin32Error()` is only read after `SetWindowsHookEx` — consistent |
| Narrowing conversions at every `SeamFrames.*` call | all casts present; literal arguments are constant-folded legally |

**Residual compile risk (cannot be excluded without a compiler):** the above cannot catch a syntax
error inside a method body, an overload that exists but with a different argument type, or a
language-version mistake. `tools/check_csharp_structure.py` only balances delimiters, rejects
Python-isms, and looks for duplicate types — it is explicitly **not** a compiler. Expect the first
`dotnet build` to produce a handful of small fixes; that is normal and is why Gate 1 exists.

**An important consequence:** because the TFM is plain `net8.0` and no file touches a
Windows-only *managed* API, the whole project — including `--self-test` — **builds and runs on
macOS**. The first compile attempt does not need a Windows machine at all. Only the P/Invoke paths
require Windows at runtime.

---

## 3. Defects found and fixed in this pass

Ordered by "would this have ruined the first test".

### D1 — `DISPLAY_INFO` could be dropped on every connection *(critical)*
`Transport/SeamConnection.cs`

The constructor started the reader thread before `Connect()` returned, so the agent's
`DISPLAY_INFO` — sent immediately after authentication — could be read and dispatched while
`Program`/`SelfTest` had **zero handlers attached**. `DisplayInfoReceived?.Invoke(...)` on a null
event is a no-op, so the frame was silently lost and the run continued with `display == null`.
Every downstream size assumption (`radius = min(w,h) * 0.25`, normalisation, park position) would
then silently fall back to defaults.

*Fix:* `Connect()` now only completes the handshake. A new `Start()` begins the reader and the
heartbeat; both call sites (`Program.cs:70`, `Testing/SelfTest.cs:158`) call it **after** wiring
handlers.

### D2 — A stalled write inside a hook callback could get the hook silently uninstalled *(critical)*
`Transport/SeamConnection.cs:31`

Mouse/key frames are written from inside `WH_MOUSE_LL`/`WH_KEYBOARD_LL` callbacks. Windows removes
a low-level hook that exceeds `LowLevelHooksTimeout` (~300 ms, 1000 ms max since 1709) **without
any notification**. `NetworkStream` writes previously had no timeout, so a full socket buffer
(USB unplugged, phone asleep, adb forwarding stalled) would block the hook thread indefinitely:
input dies, and nothing in the log says why.

*Fix:* `WriteTimeoutMs = 250` on the stream. A timed-out write now *faults* the session
(`_running = false`, `Closed` event, resources closed) instead of leaving a half-written stream
that would corrupt framing.

### D3 — `adb` output read could deadlock *(high)*
`Bootstrap/AdbBootstrapper.cs:307`

```
ReadToEnd(stdout)  then  ReadToEnd(stderr)
```
If the child fills the stderr pipe buffer (64 KB) while we are still draining stdout, both sides
block — the classic redirected-stream deadlock. `adb push`/`install` on a slow device is exactly
the shape that produces this.

*Fix:* both pipes are drained concurrently, then `Task.WaitAll` after `WaitForExit()`.

### D4 — Console I/O from inside the hook callback *(high)*
`Input/WindowsInputCapture.cs` (`LogAsync`/`DrainLog`)

`Console.WriteLine` **blocks indefinitely** when the console window is in QuickEdit mode with text
selected — a very easy state to reach while a tester is reading the log. Called from a hook
callback, that is >300 ms and the hook is removed (see D2). The unmapped-key warning on
`WindowsInputCapture.cs:681` fires per keystroke, so this was not theoretical.

*Fix:* hook-thread messages are queued (bounded at 256) and drained by the console loop on the
main thread (`Program.cs` idle loop → `capture.DrainLog()`). No console call happens on the hook
thread any more.

### D5 — Hook thread had no message queue when `SetWindowsHookEx` was called *(high)*
`Input/WindowsInputCapture.cs:245`

`WH_MOUSE_LL`/`WH_KEYBOARD_LL` callbacks are dispatched through the installing thread's message
queue. A thread is not a GUI thread until it has created one; installing the hook first is a
well-known intermittent failure (works on one machine, `SetWindowsHookEx` returns 0 on another).

*Fix:* `PeekMessage(..., PM_NOREMOVE)` before installing, forcing queue creation.

### D6 — Top-edge crossing was dead at the last pixel *(functional)*
`Input/WindowsInputCapture.cs:398`

`if (distance <= EdgeZonePx && inSafeBand && pt.Y > MenuBarExclusionPx)` with
`MenuBarExclusionPx = 0.0` means `pt.Y > 0`: a crossing at exactly `y = 0` — the row you reach when
you *do* hit the top — was rejected, and `_outwardPush` was reset by the `else` branch. With
`EdgeZonePx = 3` the edge still worked at y = 1..3 but not at 0, which is where the cursor lands
when it is pushed hard.

*Fix:* the macOS menu-bar gate is removed entirely (Windows has no top menu bar) and the dead
constant is deleted.

### D7 — `HidKeyState.PressedCount` always returned 6 *(latent)*
`Input/WindowsKeyTable.cs:191`

`public int PressedCount => _pressed.Length;` returns the array *capacity* (always 6). It is
currently unused, which is exactly why it would have been trusted later. Now counts non-zero
usages under the lock.

### D8 — `_threadId` published without `volatile` *(latent)*
Written on the hook thread, read by `Dispose()` on the caller's thread. Without a barrier the
release of `PostThreadMessage(WM_QUIT)` could observe `0` and skip the shutdown, leaving the hook
thread alive with hooks installed after `Dispose` returns. Now `private volatile uint _threadId;`.

### D9 — `Console.KeyAvailable` throws when stdin is redirected *(crash path)*
`Program.cs:242`

`Console.KeyAvailable`/`ReadKey` throw `InvalidOperationException` when stdin is not a console —
which happens under a service wrapper, an IDE runner, or `... | tee log.txt`. That exception
unwinds `RunInteractive` *while input is routed*, tearing down hooks mid-session instead of
returning control cleanly.

*Fix:* skip console polling when `Console.IsInputRedirected`; `Ctrl+Alt+Q` and `Ctrl+C` still work.

### Corrections to my own earlier findings
- **`Testing/LoopbackAgent.cs` does *not* need `using System.Collections.Generic;`** — it uses no
  generic collections, and `<ImplicitUsings>enable</ImplicitUsings>` would supply it anyway. This
  was an unverified suspicion in an earlier note; it is closed as **not an issue**.
- The handshake/AEAD-style comparisons use a hand-written `ConstantTimeEquals`, and
  `HMACSHA256.TransformBlock(..., null, 0)` is legal for the intermediate blocks. Both check out.

---

## 4. Findings deliberately **not** fixed

These are real, and they are documented rather than papered over. None is a regression; all were
out of scope for "no new features" or would require a decision from you.

| # | Finding | Effect on the first test |
|---|---|---|
| N1 | **No clipboard monitor.** `SeamFrames.ClipboardSet` is correct and tested, but nothing calls it in the capture path: there is no hidden window, no `AddClipboardFormatListener`, no `WM_CLIPBOARDUPDATE`. | Host→Android paste cannot work yet. `README.md` already says "no monitor" — accurate. Don't test it; build it after the input path is proven. |
| N2 | **Hook-install failure is logged but not propagated.** If `SetWindowsHookEx` fails (elevation, another LL hook at the limit, secure desktop), `MessageLoop` logs and returns; the process keeps running and looks healthy. | Gate 5 must explicitly look for `[Input] Low-level mouse + keyboard hooks installed.` Fixing this properly needs a startup handshake — I did not want to invent API in a review pass. |
| N3 | **Multi-monitor is not supported.** `GetSystemMetrics(0/1)` is the primary monitor only; the `ClipCursor` box and the park point are primary-relative; a monitor *left of* primary yields negative `pt.X`, which makes the "left edge" distance negative and inside the zone immediately. | Test on a single-monitor setup first. |
| N4 | **`ClipCursor` is not re-asserted.** It is set once in `BeginAndroid` and cleared by `DefWindowProc` on focus changes. Recentring masks it, but a cleared clip means the pointer can visibly escape between events. | Expect minor cursor jumps if you alt-tab while routed. |
| N5 | **`Execute` has no timeout.** A wedged `adb` hangs bootstrap with no output. | Symptom to recognise: "nothing happens after `[Link ]`". Ctrl+C and check `adb devices`. |
| N6 | **`ResolveAdbPath` misses `%ProgramFiles%`** (only `ProgramFilesX86` is probed) and returns `adb.exe` from PATH without verifying it exists. | A `Win32Exception: cannot find the file specified` is the symptom; pass `--adb`. |
| N7 | **No UDP fast path and no reconnect.** Advertising-hz mice degrade behind `adb forward` + TCP; a dropped link ends the run. | Known, documented in `README.md` §5. Redundant for a first test. |
| N8 | `BitConverter.GetBytes(ExpectedSessionId)` in `SelfTest` is endianness-dependent (correct on every realistic .NET target, but it should be `BinaryPrimitives.WriteUInt64LittleEndian`). | Cosmetic; test-only. |
| N9 | Pointer deltas are sampled from `MSLLHOOKSTRUCT.pt`, i.e. **after** Windows' pointer acceleration. Removing it requires `WM_INPUT` (Raw Input), which is incompatible with swallowing the event. | Feel may be non-linear on the phone; it is not a bug in the port. |

---

## 5. Test-oracle audit — where a false positive can still come from

### 5.1 What the 33 checks actually assert

`tools/verify_protocol.py` runs three groups:

1. **6 golden handshake vectors** — the 68-byte client response and 41-byte ack are compared to
   fixed hex, with the client nonce pinned via the `clientNonceOverride` test hook.
2. **14 frame-layout checks** — byte-exact frames from `SeamFrames`.
3. **13 end-to-end checks over a real TCP socket** against `tools/agent_sim.py`.

Group 3 is the one that reads as "compatible with Android". It is not: the "device" is
`agent_sim.py`, a Python transliteration **I** wrote from `SeamAuthenticator.java`,
`UsbLocalSocketServer.java` and the shadow-cursor part of `SeamInputEngine.java`.

### 5.2 What has been added to break the circularity

`tools/check_java_parity.py` (new) parses the **actual Java sources** and compares them with both
the C# and the simulator: magic, protocol version, transport ids, auth status codes, all 10
opcodes, 7 payload lengths, 4 edge ids, the 256 KiB clipboard cap, heartbeat timeout, nonce and
HMAC sizes, both auth-domain strings, and the payload field order **taken from the agent's own
readers** (`SeamInputEngine` for mouse/key offsets, `UhidDevice` for the 8-byte HID report).

```
$ python3 tools/check_java_parity.py
=== 44 passed, 0 failed ===
```

(It found nothing; its first run reported 5 failures that were all defects *in my parser* —
tuple constant assignments and `256 * 1024` arithmetic — which I fixed and re-ran. Worth
knowing: a green result only means something once the checker itself has been debugged.)

### 5.3 The golden vectors are now independently confirmed

The vectors were produced by the same Python that encodes my reading of the Java, so "C# matches
the vectors" only pinned *regression*. `tools/verify_vectors_openssl.sh` (new) rebuilds the HMAC
inputs from the composition rule in `SeamAuthenticator.java:110`/`:127` and hashes them with
OpenSSL:

```
$ bash tools/verify_vectors_openssl.sh
  [PASS] clientHmac  e9c7aec0a81566c2…1ec9e7
  [PASS] serverHmac  c44d92e6dc72f810…e1657caf
=== 2 passed, 0 failed ===
```

Three independent implementations now agree on the handshake bytes. That is a genuine reduction
in risk, and it is limited to the handshake.

### 5.4 Residual false-positive risks (read before trusting a green run)

1. **Shared misreading.** Any place where my reading of the Java is wrong *in the same way* in the
   C# and the simulator passes parity (constants) and end-to-end (behaviour). Parity compares
   literals and offsets, not state machines: which opcode is legal when, what the agent does with
   an unknown opcode, heartbeat/keep-alive semantics, reply timing.
2. **Nothing above `/dev/uhid`.** Neither fixture has `UuuidDevice`/uhid, `InputManager`
   injection, or a real Android `MotionEvent`. "Protocol path proven" never means "the phone's
   cursor moved".
3. **Lenient fixtures.** `LoopbackAgent` and `agent_sim.py` accept any well-formed frame. If the
   Java agent would reject a frame the host sends, both fixtures still pass.
4. **No fragmentation test.** Both fixtures write a whole frame per `send()`; `ReadExact`'s
   partial-read loop is never genuinely exercised, even though the loop is correct by inspection.
5. **Never tested, anywhere:** `DISPLAY_INFO` with rotation ≠ 0, clipboard at/over 256 KiB,
   invalid UTF-8 payloads, sub-120 wheel deltas, `normX = 65535`, reconnect after a disconnect,
   unknown-opcode handling, and **every Windows-specific path** (§6).
6. **`--self-test`'s loopback group** is a C# client talking to a C# server written from the same
   notes — the strongest statement it supports is "the client is self-consistent".

The only real oracle is the phone. That is what Gates 3–5 below are for.

---

## 6. Windows runtime readiness, subsystem by subsystem

### 6.1 Low-level hooks — the highest-risk area
*Design:* `WH_MOUSE_LL` + `WH_KEYBOARD_LL`, installed on a dedicated message-pumping thread,
swallowing events while routed.
*Why it can fail:* the 300 ms `LowLevelHooksTimeout` removal (silent), UIPI (a non-elevated process
cannot interact with elevated windows — our events are dropped, not delivered), the secure desktop
(UAC prompt, lock screen: **no events at all**, by Microsoft's design), Raw-Input/exclusive-fullscreen
games (they ignore our synthetic coordinates and we swallow their real input).
*Expect:* routing appears to "freeze" during a UAC prompt or lock — that is correct behaviour, not
a bug. After D1–D5 the *avoidable* causes are gone; the platform ones remain.

### 6.2 Cursor pinning and recentring
There is no `CGAssociateMouseAndMouseCursorPosition` on Windows. The port re-centres with
`SetCursorPos` after each swallowed event plus a 5×5 `ClipCursor` box — the Deskflow/Input-Leap
approach. `SetCursorPos` succeeding is not evidence of correctness; a jumpy pointer on the phone
means the delta bookkeeping (`_lastX/_lastY`, `_haveLast`, `_skipInitialDeltas`, residual) is off.
Watch `fwd=` in the status line: it should track your real mouse movement rate, and `sent=` should
be in the same order.

### 6.3 DPI
`SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)` with `SetProcessDPIAware()` fallback, called
at the top of `RunInteractive` before any coordinate is read — so hook coordinates are physical
pixels. Check: at 150 % scaling the park point must be the middle of the screen, and the edge zone
must be ~3 physical px, not 3 logical px. `--trajectory`/`--self-test` do not set it (they never
read screen coordinates).

### 6.4 Keyboard
Scan code → HID usage via `WindowsKeyTable`, 6KRO with oldest-eviction, modifiers in byte 0. The
open risk is unmapped keys (`[WARN] Unmapped key scan=…`), which the port does *not* forward rather
than guessing. Media keys, `Print Screen`, IME-specific keys and some laptop Fn combinations are
the expected gaps. The panic reset (`_keys.Reset()`) on return is what prevents a stuck modifier
on the phone — worth deliberately verifying with a held `Shift` + `Ctrl+Alt+Q`.

### 6.5 Clipboard
Communication is implemented at the frame level and unit-tested; **no OS integration exists** (N1).
Nothing to test on Windows yet.

### 6.6 ADB bootstrap
`adb devices -l` → stamp probe → `push` → `nohup … app_process … --server --detach` → ready marker
→ `adb forward tcp:0 localabstract:seam_uhid`. Every command string and the probe expression are
copied from `UsbBootstrapper.swift` (`:275`, `:280`, `:323`) and match it character for character,
`tcp:0` included (`UsbBootstrapper.swift:144`). Arguments go through `ProcessStartInfo.ArgumentList`,
so the single quotes inside the launch line reach the device's `sh` unmodified.
*Windows-specific risk:* the OEM driver. Transsion/TECNO/Infinix devices ship no ADB driver; the
device shows up unknown and `adb devices` stays empty. Wire debugging bypasses the driver entirely
(`adb pair` / `adb connect` + `--connect`), which is also the fastest way to isolate a bootstrap
failure from a protocol failure.

### 6.7 TCP / link management
`NoDelay`, 500 ms heartbeat, 3000 ms agent-side timeout, 10 s read timeout, 250 ms write timeout,
single fault-and-close path (idempotent via `Interlocked`), `Closed` raised exactly once, reader and
timer both stopped on `Dispose`. No reconnect: a drop ends the run — deliberate, and visible.

### 6.8 Panic key
`Ctrl+Alt+Q` is tested inside the keyboard hook before any forwarding; `ForceReturn` is idempotent
and no-ops when not routing, sends `CURSOR_LEAVE`, releases `ClipCursor`, restores the pointer,
and resets all held keys. `q` / `r` / `Ctrl+C` in the console are the fallbacks. Caveat: because
the hook swallows while routing, the panic combination must be recognised *before* dispatch — it
is — but if the hook has been silently removed by Windows (6.1), the panic key will not work either,
which is why the `q` fallback matters.

---

## 7. Exact Windows test procedure

**Prerequisites** (record the exact versions you use):
- Windows 10 22H2 or Windows 11, x64, **single monitor**, **local session** (not RDP).
- .NET 8 SDK (build) or .NET 8 Desktop Runtime (run): `dotnet --info`.
- Android Platform-Tools (`adb version` — `forward tcp:0` needs a recent one), on `PATH`.
- `seam-agent.jar` built on macOS with `scripts/build-agent.sh` (javac 17 + d8 `--min-api 30`).
- Phone: USB debugging on, "Disable adb auth timeout" preferred, screen unlocked.
- Do **not** run the first attempts elevated; record whether elevation changes anything.

### Gate 1 — build (5 min, no phone, and it can be done on the Mac)
```bat
cd windows-host
dotnet build -c Release
```
**Pass:** exit 0, `bin\Release\net8.0\seam-host.exe` exists.
**Record** every warning verbatim — with `TreatWarningsAsErrors=false` they will not stop you, and
this list is the input to the next pass. If the build fails, fix only what the compiler says; send
me the output and I will do it against the real errors rather than by inspection.

### Gate 2 — offline self-test (2 min, no phone)
```bat
seam-host.exe --self-test
echo %ERRORLEVEL%
```
**Pass:** three groups, `=== 3x passed, 0 failed ===`-style totals, `ERRORLEVEL 0`. Specifically
look for the lines `handshake: client HMAC matches Java-derived vector`,
`handshake: server HMAC verified and session id decoded`, all six `frame: …` checks, and the
loopback block ending in `agent observed a clean mutually-authenticated session`.
**This gate proves:** the C# handshake, frame encoders, reader loop, heartbeat, and clean shutdown
work against a socket. **It proves nothing about Windows or Android.**

### Gate 3 — real device, wire path only
```bat
adb devices -l                                       REM must list your phone as "device"
seam-host.exe --trajectory --device <serial>         REM jar next to the exe, or --jar PATH
```
**Pass:** `[Displ] DISPLAY_INFO` with *your* panel's real width/height/DPI/Hz, `trajectory complete`
with `frames sent≈360`, and — the only end-to-end proof — **the phone's cursor traces a figure-eight
and vanishes at the left bezel** (`CURSOR_LEAVE`).
**Fail meaning:** empty `adb devices` → driver/USB (6.6). Handshake error → key mismatch (delete
`%USERPROFILE%\.seam\auth_key`, re-run; the agent will regenerate its side). `DISPLAY_INFO` never
arrives → D1-style reader problem or agent not the expected build (check the log file on the phone:
`/data/local/tmp/seam-agent.log`).

### Gate 4 — isolate ADB from the protocol (2 min, do this if Gate 3 fails)
```bat
adb -s <serial> forward tcp:27186 localabstract:seam_uhid
seam-host.exe --connect 127.0.0.1:27186 --trajectory
```
Same expectation as Gate 3 with no bootstrap code involved. Passing here + failing Gate 3 localises
the fault to `Bootstrap/AdbBootstrapper.cs`.

### Gate 5 — interactive capture
```bat
seam-host.exe --capture          REM route on the first movement: deterministic
seam-host.exe --edge right       REM then the real behaviour
```
Checklist, in order (each item is one observation, write down what you actually saw):
1. Console prints `[Input] Low-level mouse + keyboard hooks installed.` — if absent, stop: N2.
2. `[Routing] -> Android` appears on entry; `routing=ANDROID` in the status line.
3. Windows pointer reappears at screen centre and stays put while you move the mouse.
4. Phone cursor moves in the same direction as the mouse, at a plausible gain, no stutter at
   moderate speed (`sent=` should grow steadily).
5. Left click, right click, middle, X1/X2 on the phone.
6. Scroll a long list — smooth, no "dead zone then jump".
7. Type `hello`, then `Ctrl+C`, then a held `Shift`; check the phone sees exactly that, and that
   nothing sticks after `Ctrl+Alt+Q`.
8. `Ctrl+Alt+Q` — Windows pointer is back, `[Routing] -> Windows`, `[Exit ]` on quit, and the phone
   shows no stuck key.
9. Push past the bezel on the phone side — `[Edge ] <- Android: …` and the pointer returns to where
   you entered.
10. Alt-tab while routed, then check the pointer again (N4).
11. Unplug USB while routed — expect a clean `[Link ] closed: …`, no hang, hooks released, process
    exits with a message.

### Gate 6 — failure modes, only after 1–5 pass
Lock the screen while routed (expect total silence, then recovery), run against an elevated app
(expect dropped input, correctly), and repeat Gate 5 on a 150 % DPI display. Then record the
`--edge top` case explicitly, since D6 changed that path.

### Symptom → likely cause
| Symptom | Look at |
|---|---|
| `SetWindowsHookEx failed` | elevation / another LL hook, §6.1, N2 |
| Hooks install, then input dies after a few seconds | hook timeout — D2/D4 regression, check for blocking work in the callback |
| `routing=ANDROID` but the phone cursor never moves | Gate 3/4; then `sent=` growth vs. `MOUSE_REPORT` contents |
| Pointer fights you / drifts | recentring bookkeeping §6.2 |
| Top edge unreachable | §6.4 + D6 verification |
| Stuck modifier on the phone after exit | `_keys.Reset()` on the return path |
| Nothing after `[Link ]` | N5 (wedged adb) |

---

## 8. Corrections required **before** the first real-device test

Only the first three are blockers for a *useful* first test; the rest are cheap and prevent
misleading observations.

1. **Get a compile signal** (Gate 1). Do this before any device work — a build error found on the
   Mac costs minutes; the same error found mid-session costs the session.
2. **Make hook-install failure loud** (N2): fail the run (non-zero exit) instead of continuing.
   Otherwise a silent non-routing session will be misread as a protocol fault.
3. **Clear the pairing key once** before Gate 3 if the agent's key was regenerated, and confirm the
   agent's build stamp matches the jar you pushed.
4. Add a bounded `adb` timeout (N5) so bootstrap failures surface as an error, not a hang.
5. Re-assert `ClipCursor` on each routed event (N4) — 3 lines, removes pointer escapes.
6. Guard multi-monitor (N3): either refuse to start with more than one monitor, or compute
   `SM_XVIRTUALSCREEN`/`SM_CXVIRTUALSCREEN` bounds.

---

## 9. Reproducing every claim in this document

```
python3 tools/verify_protocol.py        → 33 passed, 0 failed   (protocol vs. Python fixture)
python3 tools/check_csharp_structure.py → 10 files, 0 problems  (lint, NOT a compiler)
python3 tools/check_java_parity.py      → 44 passed, 0 failed   (C#/sim vs. upstream Java)
bash tools/verify_vectors_openssl.sh    → 2 passed, 0 failed    (independent HMAC check)
dotnet build windows-host/              → NOT RUN. No SDK here. (§2)
```

**Files changed in this pass** — all inside `windows-host/`, plus two new tools:
`Transport/SeamConnection.cs` (rewritten: `Start()`, timeouts, single close path),
`Input/WindowsInputCapture.cs` (message queue, queued logging, top-edge fix, `volatile`),
`Input/WindowsKeyTable.cs` (`PressedCount`), `Bootstrap/AdbBootstrapper.cs` (pipe deadlock),
`Program.cs` (`Start()`, `DrainLog()`, stdin redirect), `Testing/SelfTest.cs` (`conn.Start()`),
`README.md` (§6 verification log), `tools/check_java_parity.py` (new),
`tools/verify_vectors_openssl.sh` (new).

`macos-host/` and `android-agent/` remain byte-identical to `eb792752`.

---

## 10. What I still cannot tell you

- Whether it compiles. (§2)
- Whether the hooks behave on your hardware, at your DPI, with your mouse. (§6.1–6.3)
- Whether the Android agent actually injects what we send — no test above touches `/dev/uhid`.
  (§5.4)
- Whether feel (gain, acceleration, scroll units) matches the macOS host. That comparison needs
  both hosts driving the same phone, which is the natural *next* experiment once Gates 1–5 pass.
