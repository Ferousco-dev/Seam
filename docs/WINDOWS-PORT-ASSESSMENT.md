# SEAM → Windows Host: Technical Feasibility Assessment

**Assessment date:** 2026-11-20
**Repository:** `Ferousco-dev/Seam`
**Scope:** Analysis only. No source files were modified, no commits were made.
**Question answered:** Can a reliable Windows host be built without damaging the existing macOS and Android implementations?

---

## 1. Project Overview

SEAM is a software KVM ("one cursor, every screen") for **macOS ↔ Android**. You move the Mac pointer past a configured screen edge and it continues onto an adjacent Android phone, which renders its **genuine native hardware pointer sprite** — not a mirroring window and not an overlay cursor. Keyboard, smooth scrolling, and a bidirectional clipboard follow the pointer. USB and Wi-Fi are simultaneously maintained, co-equal links.

The single most important architectural fact for this assessment:

> **The Android side is a headless `app_process` daemon (`seam-agent`, `uid=2000`, no APK, no root) that speaks a small fixed binary protocol. It has no knowledge of, or dependency on, the host operating system.**

That is what makes a Windows port genuinely tractable.

### How it actually works (verified from source)

1. **Bootstrap (host → phone, over ADB).** The host shells out to the `adb` CLI: `adb devices -l`, `adb mdns services`, `adb pair`, `adb connect`, `adb push`, `adb shell`, `adb forward tcp:0 localabstract:seam_uhid`. It pushes `seam-agent.jar` to `/data/local/tmp/`, then launches it detached with `nohup … app_process … --server --detach --auth-key=<hex>` (`UsbBootstrapper.deployAndLaunchDetachedDaemon`). It waits for the literal `SEAM_AGENT_READY` line in the remote log and scrapes `SEAM_WIFI_ENDPOINT:<iface>:<ip>:<port>` lines out of it.
2. **Authentication.** A 3-message mutual HMAC-SHA256 challenge/response on every link (`SeamAuthenticator.authenticateClientStream` / `authenticateServerSocket`). A 256-bit key persists at `~/.seam/auth_key`.
3. **Transport.** USB is *not* native USB: it is `adb forward` → `localabstract:seam_uhid` → a device-side `LocalServerSocket`, guarded device-side by `SO_PEERCRED` (accepts `uid 2000` shell or `uid 0` only). Wi-Fi is TCP (`:27186`) for reliable frames plus a 42-byte HMAC-signed UDP fast path on the same port for pure mouse deltas.
4. **Input capture (macOS).** A `CGEventTap` at `.cghidEventTap` (or, without TCC Accessibility, a 120 Hz poll + non-activating `NSPanel` "capture shield" + `NSEvent` monitors).
5. **Edge crossing.** When the pointer reaches the configured Mac edge (3 px zone, 2.0 cumulative outward-push accumulator, menu-bar/hot-corner exclusion bands, 180 ms re-entry cooldown), the host decouples and hides the Mac cursor, sends `CURSOR_ENTER`, and streams relative deltas + HID key reports.
6. **Android injection.** The agent writes `UHID_CREATE2`/`UHID_INPUT2` to `/dev/uhid`, so AOSP `InputReader` → `CursorInputMapper` → `PointerChoreographer` draws the real cursor. Scroll is lifted out of the kernel path and re-injected as fractional `ACTION_SCROLL` via `InputManagerGlobal` at **1/32-notch** resolution.
7. **Return.** The agent tracks shadow coordinates; pushing ~4 px past the return bezel emits `EDGE_RETURN`, and the host warps its cursor back to the matching Y position.

---

## 2. Existing Architecture and Component Structure

Two codebases, one protocol, zero shared source.

```
seam-host (Swift 6, macOS, ~4,390 lines)          seam-agent (Java 17 → DEX, Android, ~3,149 lines)
├── Main.swift            CLI: app|edge|devices|usb-test|wifi-test|switch-test
├── UI/                   NSStatusItem + NSPopover + SwiftUI Liquid Glass
├── Input/                QuartzEdgeEngine, BackgroundCursorController,
│                         MacKeycodeTranslator, MacClipboardSyncEngine
├── Protocol/             SeamWireProtocol (frames), SeamAuthenticator (HMAC)
├── Transport/            SeamTransport (interface), UsbAdbTransport,
│                         WifiHybridTransport, SeamTransportMultiplexer
└── Bootstrap/            UsbBootstrapper (adb CLI wrapper + discovery)
                                                    ├── Main.java            --server|--info|--self-test|--calibrate
                                                    ├── core/                SeamInputEngine (shadow tracker, edge return)
                                                    ├── uhid/                UhidDevice, HidDescriptors, HidKeycodes
                                                    ├── transport/           UsbLocalSocketServer, WifiTransportServer,
                                                    │                        DualTransportCoordinator, SeamTransportChannel
                                                    ├── clipboard/           AndroidClipboardController (IClipboard Binder)
                                                    ├── pointer/             SmoothScrollInjector, PointerLinearizer
                                                    └── telemetry/           Display*, Network*
```

**Clean seams already present** (these are the port's scaffolding, and they are good):

- `SeamTransport` protocol (`Transport/SeamTransport.swift`): `connect / send / sendPing / disconnect`, `SendReliability.reliableOrdered` vs `.unreliableSequenced`, `TransportState`, `SeamEndpoint`.
- `SeamTransportDelegate`: state / frame / RTT callbacks.
- `SeamTransportMultiplexer`: primary + hot-standby, mid-frame failover in `send()`, promotion in `promoteStandbyTransport()`.
- Android's `SeamTransportChannel` interface + `DualTransportCoordinator`, which **deliberately does not reset `/dev/uhid`** while any authenticated channel remains open — only when the last one closes (`onChannelClosed`).
- A byte-exact, documented wire contract (`research/architecture-recommendation.md` §3).

**Nothing in this architecture assumes a Mac.** The host-specific surface is confined to `Input/`, `UI/`, and parts of `Bootstrap/`.

---

## 3. Technologies and Dependencies

| Layer | Tech | External deps |
|---|---|---|
| macOS host | Swift 6, AppKit, SwiftUI, CoreGraphics, ApplicationServices, CryptoKit, Foundation | **None** — `Package.swift` declares zero package dependencies. Private-but-undocumented `CGSDefaultConnection` / `CGSSessionSetConnectionProperty("SetsCursorInBackground")` SPI (`BackgroundCursorController`). |
| Android agent | Java 17, `javac` + `d8` → DEX, `app_process`, `android.system.Os`, reflection into `InputManagerGlobal` / `DisplayManagerGlobal` / `IClipboard` | **None** — zero-Gradle, no APK, no AndroidX |
| Build | `make`, `scripts/build-agent.sh` (needs `android.jar` API 35 + `d8`), `scripts/build-host.sh` (needs Xcode `ictool`/`actool`, `codesign`, `iconutil`) | Android SDK Platform-Tools (`adb`) |
| Crypto | CryptoKit HMAC-SHA256 ↔ `javax.crypto` Mac HmacSHA256 | — |
| Transport | BSD sockets, `TCP_NODELAY`, `SO_NOSIGPIPE`, `IP_TOS` (0x10 / DSCP EF 0x2E), UDP | — |

**There is no test framework, no CI, and no `.github/` directory at all** — no issue templates, no PR template, no workflows. All verification is hardware-in-the-loop via the `make *-test` targets. This matters a great deal for a port (see §9).

Two documentation/code discrepancies found while reading the implementation (relevant because a Windows author would otherwise implement the wrong thing):

1. **The docs claim encryption the code does not do.** `research/README.md`, `dual-transport-architecture.md`, `security-analysis.md`, and `connection-protocols.md` specify Noise `XX` / ChaCha20-Poly1305 AEAD. The implementation performs **HMAC-SHA256 authentication only** — after the handshake, frames are **plaintext** on both TCP and UDP (`UsbAdbTransport.readExact/writeAll`, `WifiHybridTransport`, `SeamAuthenticator`). There is no `chacha`/`poly1305`/`noise` symbol anywhere in `macos-host/` or `android-agent/`. A Windows host must replicate the *actual* behaviour to interoperate.
2. `README.md` states macOS 14+; `Package.swift` is `.macOS(.v14)` but `scripts/build-host.sh` writes `LSMinimumSystemVersion 13.0`.

---

## 4. Contribution Rules (CONTRIBUTING.md, LICENSE, build docs)

**License:** MIT, © 2026 Feranmi Oresajo. Permissive: fork, modify, redistribute, and ship a Windows host — **provided the copyright notice and permission notice are retained** in all copies/substantial portions. Contributions are inbound-MIT.

**Explicit invitation.** `CONTRIBUTING.md` lists as its *first* way to contribute: *"**Windows Host Support:** Help bring SEAM's `SEAM-Wire v1` host experience (edge crossing, cursor decoupling, clipboard sync, and system tray UI) to Windows 11."* The README also says support for "Windows host support" is wanted. **Introducing a new platform implementation is explicitly permitted and desired** — this is not a "macOS-only" project.

**Stated expectations:**

| Requirement | Detail |
|---|---|
| Dev environment | macOS 14+ (Apple Silicon `arm64`), Xcode (Swift 6, `ictool`/`actool`), JDK 17+, Android SDK (`adb`, `d8`, `platforms/android-35/android.jar` in `~/Library/Android/sdk`), a real Android 11–15+ device with USB or Wireless Debugging. |
| Build | `make build` must pass; app launches via `open dist/SEAM.app`. |
| Verification | Run `make build` plus whichever of `usb-test`, `wifi-test`, `switch-test`, `device-info`, `self-test` covers the change; **test on real hardware** for input/scroll/clipboard/transport changes; state macOS version, Android version, device model in the PR. |
| Style | Match existing Swift 6 (2-space indent, `///` doc comments, `MARK:` sections, no external packages) and Java 17 (records, switch expressions, `///`-style Javadoc). |
| Branch/commit | Branch from `main` as `feat/your-feature-name`; PR into `main` describing what/why/how tested. No Conventional-Commit or DCO requirement is stated; no CLA. |
| Scope rules | Do not commit `build/` or `dist/` artifacts. **"Keep zero-root and non-intrusive phone behavior intact (never suppress Android's on-screen touch keyboard)."** |
| Issues | Include macOS version + Mac model, Android version + phone model, transport in use, and `adb shell cat /data/local/tmp/seam-agent.log` output. |

**Constraints this places on us before submitting upstream:**

1. **The documented verification path is macOS-only.** A Windows-only PR cannot run `make build`, and no target exercises a Windows host. We must either include a documented Windows verification procedure or partition the work (protocol/Android changes separately from Windows host additions). The honest framing: a Windows host PR will be **unverifiable by the maintainer's current tooling**, so it needs unusually strong evidence — logs, RTT numbers, a hardware matrix, and ideally a conformance test.
2. **Do not change existing behaviour.** No protocol version bump, no frame-layout change, no auth change in the same PR as the Windows host.
3. **Do not break the "never suppress the Android on-screen keyboard" invariant** (`PointerLinearizer` forces `show_ime_with_hard_keyboard 1`; `UsbBootstrapper` re-asserts it on every bootstrap).
4. Repository hygiene we should not imitate or should fix in a separate PR: `release/SEAM-macOS-arm64.{dmg,zip}` (4.2 MB of binaries) are tracked despite `.gitignore` ignoring `dist/`; `research/README.md` contains absolute `file:///Users/oresajooluwa…/Desktop/PROJECTS/SEAM/...` links that break for everyone else.

---

## 5. macOS-Specific Components

Everything below must be re-implemented against Windows APIs. This is ~61% of the Swift host by line count.

| Component | Lines | Why it is macOS-bound |
|---|---:|---|
| `Input/QuartzEdgeEngine.swift` | 841 | `CGEventTapCreate` at `.cghidEventTap`, `CGEvent` fields (`mouseEventDeltaX`, `scrollWheelEventFixedPtDeltaAxis1`), `NSPanel` non-activating capture shield, `NSEvent` local/global monitors, `CGEventMask` enable/disable-after-timeout recovery, `kCGEventTapDisabledByTimeout`, `NSScreen.screens` union. |
| `Input/MacKeycodeTranslator.swift` | 282 | Hardcoded `CGKeyCode` → HID Usage Page 0x07 table (macOS virtual keycodes are a Mac-only numbering), `CGEventFlags` masks, `Cmd→Ctrl` ergonomics. |
| `Input/BackgroundCursorController.swift` | 77 | `CGAssociateMouseAndMouseCursorPosition(0)`, `CGDisplayHideCursor`, `CGWarpMouseCursorPosition`, plus **private WindowServer SPI** (`_CGSDefaultConnection`, `CGSSetConnectionProperty`) to hide the cursor while another app is foreground. |
| `Input/MacClipboardSyncEngine.swift` | 92 | `NSPasteboard.general.changeCount`, `.string(forType: .string)`. |
| `UI/` (4 files) | 413 | `NSStatusItem` menu-bar extra, `NSPopover`, SwiftUI `GlassEffectContainer`/`.glassEffect` (macOS 26) with `.ultraThinMaterial` fallback, drag-to-arrange gesture canvas. |
| parts of `SeamAppViewModel.swift` | ~354 | `@MainActor`/`ObservableObject`, `UserDefaults`, `NSWorkspace.open`, TCC check `AXIsProcessTrustedWithOptions`. |
| parts of `Bootstrap/UsbBootstrapper.swift` | ~463 | `runCommand` via `/usr/bin/env`, default `adbPath = "/opt/homebrew/bin/adb"`, `netstat -rn -f inet` for the hotspot gateway, `~/.seam/` paths. |
| parts of `Main.swift` | ~528 | `NSApplication.run()`, `CFRunLoopRun()`, `exit()`, `Bundle.main`, `-psn_` LaunchServices arg filtering. |
| Socket layer | in transports | BSD `socket/connect/read/write`, `sockaddr_in.sin_len`, `SO_NOSIGPIPE`, `inet_addr`, `errno`/`EINTR`, `close`. |
| Build/packaging | — | `codesign --force --deep --sign -` ad-hoc signing, TCC Accessibility entitlement flow, `AppIcon.icon` → `ictool`/`actool`. |

---

## 6. Reusable Components (as specifications and as logic)

**Directly reusable as logic, ~1,340 lines (31% of the Swift host):**

| Component | Lines | Portability |
|---|---:|---|
| `Protocol/SeamWireProtocol.swift` | 207 | Fully portable. Magic `"SEAM"`, version `0x01`, opcodes `0x01–0x06` / `0x81–0x84`, exact little-endian layouts, `normalizeCoordinate`/`denormalizeCoordinate`, 256 KiB clipboard cap with UTF-8 boundary trimming. |
| `Protocol/SeamAuthenticator.swift` | 220 | Fully portable. 38 B challenge, 68 B response, 41 B ack; domain strings `SEAM-CLIENT-AUTH-v1` / `SEAM-SERVER-AUTH-v1` / `SEAM-UDP-AUTH-v1`; UDP = 26 B signed + 16 B truncated tag; constant-time compares; persistent key file. |
| `Transport/SeamTransport.swift` | 102 | Interface only — becomes an interface in the new language. |
| `Transport/UsbAdbTransport.swift` | 251 | Logic portable (localhost TCP → forwarded ADB socket); API layer not. |
| `Transport/WifiHybridTransport.swift` | 424 | Logic portable; dual-lane routing and TCP fallback on UDP failure are the valuable part. |
| `Transport/SeamTransportMultiplexer.swift` | 136 | Fully portable (pure state machine). |

**Reusable as an algorithm, ~700 more lines embedded in Mac files:**

- `evaluateBoundaryCrossing` — the 3 px edge zone, 2.0 push accumulator with the 0.55/tick dwell compensation, 24 pt menu-bar and 16 pt corner exclusion bands, 180 ms cooldown. Platform-agnostic maths, needs a Windows geometry source.
- `transitionToAndroid` / `returnToMacOS` — including the **magic entry insets `360` and `65175`** out of 65535 (≈0.55% inward) and the 6 pt / 26 pt return offsets. Must be copied **exactly** or edge entry/return will feel different.
- `forwardRelativeMouseDelta` — `pointerVelocityGain = 1.85`, sub-pixel residual accumulators, `skipInitialRemoteDeltas = 2`, and the 160 ms directional clamp after entry.
- `forwardScroll` — continuous `1/1.5` vs discrete `16.0` scaling into 1/32-notch sub-units (pairs with Android's `HI_RES_UNITS_PER_NOTCH = 32`).
- `MacClipboardSyncEngine` — change-counter + `lastSyncedText` echo suppression, plus explicit flush on Copy/Paste (`syncClipboardIfPasteShortcut`).
- `UsbBootstrapper` — the ADB command sequences, the `computeAgentStamp` (SHA-256 prefix of jar + key) idempotency check, log scraping, `SEAM_DISCOVER_V1` / `SEAM_OFFER_V1` UDP probe, Wireless-Debugging mDNS flow, and the detached-daemon launch script are ~all portable; only path discovery and the gateway lookup are Mac-specific.
- `SeamAppViewModel.runAutoPilotTick` — the Wi-Fi-first / ADB-second discovery priority and the "upgrade to USB as soon as a cable appears" logic.
- `Main.swift`'s three transport test harnesses (`streamFigureEightTrajectory`, `triggerAndVerifyEdgeReturn`, `printRttStats`) — these are exactly the cross-platform acceptance tests the port needs.

**Needs zero changes:**

- **All of `android-agent/` (3,149 lines).** Nothing in it references the host OS. `SO_PEERCRED` checks the *device-side* peer (adbd, `uid 2000`) — identical whether `adb.exe` or `/opt/homebrew/bin/adb` is driving. The `USER_ID`/`SHELL_PACKAGE` `IClipboard` reflection, `/dev/uhid` structs, `settings put`, and `InputManagerGlobal` injection are all host-agnostic.
- The `SEAM-Wire v1` contract itself.

---

## 7. Windows Feasibility, Capability by Capability

Legend: **Reuse** = can existing code/spec be reused · **Risk** = Low / Medium / High.

### 7.1 Mouse movement between Windows and Android — ✅ Feasible, Medium risk, moderate rework

- **Reuse:** delta scaling, residual accumulation, `MOUSE_REPORT` serialization, `CURSOR_ENTER`/`EDGE_RETURN` semantics.
- **Must be different:** the capture and cursor-parking mechanism. Windows has **no documented equivalent of `CGAssociateMouseAndMouseCursorPosition`** — you cannot decouple the physical mouse from the pointer. The industry workaround (Deskflow, Input Leap, Synergy, Mouse Without Borders) is a low-level hook (`SetWindowsHookEx(WH_MOUSE_LL)`) that swallows events by returning `1`, combined with `ClipCursor` + `SetCursorPos` to hold the pointer in place while real deltas are read from `MSLLHOOKSTRUCT.pt`.
- **Windows APIs:** `WH_MOUSE_LL`, `MSLLHOOKSTRUCT`, `ClipCursor`, `SetCursorPos`, `GetSystemMetrics`, `EnumDisplayMonitors`, `MonitorFromPoint`, `GetDpiForMonitor`, `SetThreadDpiAwarenessContext` + per-monitor-V2 manifest.
- **Limitations (confirmed, not speculative):**
  - The hook callback runs on the installing thread and **must pump messages**; if it overruns `LowLevelHooksTimeout` (documented as ~300 ms by third parties, capped at 1000 ms on Win10 1709+), Windows **silently removes the hook with no notification** — the direct analogue of `kCGEventTapDisabledByTimeout`, except macOS gives you an event and Windows does not, so a **watchdog + re-install** is mandatory.
  - Hooks are per-desktop: **not called on the UAC secure desktop or the lock screen**. Plan for a fail-safe release.
  - `ClipCursor` is a *shared global* resource and `DefWindowProc`'s focus handling **clears the clip rectangle on focus changes**, so it must be re-asserted per event, not set once.
  - Calling `SetCursorPos` from inside the hook re-enters the message pipeline and interacts badly with DPI scaling (documented recursion/rounding artifacts at scale ≠ 1.0) — needs careful sub-pixel/coordinate-space handling.
  - High-polling-rate mice (≥1 kHz) are a known weak point of `WH_MOUSE_LL` across *all* Windows KVM software (Deskflow discussion #9301). Raw Input (`WM_INPUT`) has no timeout and cannot be silently removed but **cannot suppress** the local cursor, so it can only be a supplement.
  - UIPI: a non-elevated process has reduced visibility/effect over elevated windows; covering elevated apps generally means running elevated (or `uiAccess`, which requires signing + Program Files install).
- **Effort:** the single hardest part of the port.

### 7.2 Keyboard input and typing — ✅ Feasible, Low–Medium risk

- **Reuse:** the 8-byte 6KRO `KEY_REPORT` frame, 6-key rollover bookkeeping, `HidKeycodes`.
- **Must be different:** the translation table. `CGKeyCode` is macOS-only. On Windows take the **scan code** from `KBDLLHOOKSTRUCT` (`scanCode` + `flags`, `MapVirtualKeyEx`) rather than the VK code, so the Android side receives physical-key positions independent of the Windows layout — mirroring what `macToHidUsage` does.
- **Semantics simplify:** the entire `Cmd→Ctrl` ergonomic layer (`resolveNavigationOrEditingTranslation`, `isCommandToCtrlRemappedUsage`) exists because macOS uses ⌘. On Windows, Ctrl already means Ctrl, so that code path becomes mostly identity. What *does* still need deciding: the Windows key (= Meta on Android), `Alt+Tab`/`Win` chord interception while routing, and `Alt+←/→` (Windows word-jump convention is Ctrl+←/→).
- **Limitations:** hook timeout/silent removal as above; no secure-desktop input; keyboard state can desync if the hook is removed mid-chord (mitigate: send an all-keys-up report on hook loss — the equivalents of `onCursorLeaveOrPanicReset` and `keyTranslator.reset()`).

### 7.3 Smooth scrolling — ⚠️ Feasible with caveats, Medium risk

- **Reuse:** all of it, including the 1/32-notch quantization and Android's `SmoothScrollInjector` (no Android change needed — it consumes 1/32 sub-units regardless of host).
- **Must be different:** the source. macOS gives pixel-precise `scrollingDeltaY` + `hasPreciseScrollingDeltas`. Windows gives `WM_MOUSEWHEEL` / `WM_MOUSEHWHEEL` with `WHEEL_DELTA = 120`, **but** precision touchpads and high-resolution wheels legitimately send **sub-120 deltas** (Microsoft's own precision-touchpad team confirmed high-precision wheel messages). The conversion is `hiRes = delta / 120 * 32`.
- **Caveats:** many Windows apps assume multiples of 120, and naive implementations accumulate to whole notches — the well-documented "dead zone then a jump" failure mode. Sub-notch smoothness is therefore achievable but **not guaranteed identical to macOS**, and trackpad momentum/inertia is generated by the OS *before* your hook sees it, so you inherit Windows' feel rather than macOS'. This needs hands-on experimentation to state a number.
- **Verdict:** we can preserve "smooth scroll" as a feature, but exact Mac-trackpad parity should not be promised.

### 7.4 Clipboard sync, both directions — ✅ Feasible, Low risk

- **Reuse:** the frames (`CLIPBOARD_SET` 0x05 / `CLIPBOARD_EVENT` 0x83, `uint32_le` length + UTF-8, 256 KiB cap), the echo-suppression algorithm, the "flush local clipboard on copy/paste" hook, and the whole Android `IClipboard` implementation.
- **Windows APIs:** `AddClipboardFormatListener` + `WM_CLIPBOARDUPDATE` is the recommended notification path; `GetClipboardSequenceNumber` is the direct analogue of `NSPasteboard.changeCount` (Microsoft explicitly advises against polling it in a tight loop). Text: `OpenClipboard`/`GetClipboardData(CF_UNICODETEXT)` or `System.Windows.Clipboard`. **No OS permission prompt** — unlike macOS TCC.
- **Limitations:** clipboard can be held open by another app → retry with `GetOpenClipboardWindow` diagnostics (this is also a hook-timeout hazard if you touch the clipboard in the hook callback — do it off-thread). Rich text/images are out of scope, same as today. Mirroring the Android side's 300 ms echo window and 55/140 ms post-Copy checks is required for parity.

### 7.5 USB connectivity — ✅ Feasible, but this is where Windows is *worse than macOS* (Medium–High user-facing risk)

- **Reuse: essentially 100% of the protocol path.** USB here is `adb forward tcp:<port> localabstract:seam_uhid` → loopback TCP. `adb.exe` supports `devices -l`, `forward tcp:0`, `push`, `shell`, `mdns services`, `pair`, `connect` identically. Only the socket API (BSD → Winsock) and `adb` binary discovery change. **No new USB driver code is needed — SEAM never talks to USB directly.**
- **The friction is the ADB USB driver.** On macOS, ADB is driverless. On Windows, **ADB requires an ADB-capable USB driver per OEM** (Google's driver for Pixel only; Samsung/Xiaomi/OPPO/Transsion/TECNO etc. supply their own, or Google's `android_winusb.inf` is installed manually as "Android ADB Interface"). `adb devices` shows nothing without it, and "file transfer works" does not imply ADB works (MTP and ADB are different interfaces). This is *the* biggest install-support burden of the port.
- **Mitigations that already exist in this codebase:** (a) bundle `adb.exe` + `AdbWinApi.dll` + `AdbWinUsbApi.dll` (Apache-2.0, redistributable with NOTICE) so Platform-Tools is not a user prerequisite; (b) lean on **Wireless Debugging / pure Wi-Fi**, which bypasses the USB driver entirely — SEAM already supports both; (c) detect "device present but no ADB interface" and surface targeted guidance.
- **Confirmed vs assumed:** driver-per-OEM requirement = **confirmed** (widely documented, Google OEM driver page). Whether Windows 11 auto-installs a working ADB driver via Windows Update for a given TECNO/MediaTek handset = **needs testing on real hardware**.

### 7.6 Wi-Fi connectivity — ✅ Feasible, Low–Medium risk

- **Reuse:** the entire design — TCP control + 42-byte signed UDP fast path on `:27186`, sequence/replay rejection, DSCP EF marking, TCP fallback when a UDP send fails, discovery probe/reply strings.
- **Windows APIs:** `System.Net.Sockets` (`TcpClient` with `NoDelay`, `UdpClient`, `SocketOptionName.TypeOfService` for DSCP, `SIO_UDP_CONNRESET` workaround so UDP `recv` doesn't throw on ICMP port-unreachable), `Stopwatch`/`QueryPerformanceCounter` for the monotonic µs clock.
- **Firewall:** favourable direction — **the Windows host is the client** for TCP and the *sender* for UDP; it listens on no port. Unsolicited inbound is not required; replies to an outbound UDP datagram are permitted statefully. So the classic "Windows Firewall blocks inbound" problem is largely avoided. Residual risks: corporate/managed firewall egress policy, **AP/client isolation** on guest Wi-Fi (blocks phone↔PC entirely — affects macOS equally), and DSCP marking possibly being ignored without policy (harmless).
- **Confirmed vs assumed:** "no inbound port needed" = **confirmed by code** (host opens outbound sockets only). "Windows Firewall will therefore not prompt" = **assumed**, needs a clean-machine test.

### 7.7 Connection discovery and pairing — ✅ Feasible, Low risk

- **Reuse:** `adb devices -l` parsing, `adb mdns services` → `_adb-tls-connect._tcp`, `adb pair` SPAKE2+ flow, the `computeAgentStamp` idempotency check, log scraping for `SEAM_WIFI_ENDPOINT`, and the `SEAM_DISCOVER_V1` → `SEAM_OFFER_V1` UDP broadcast probe. All host-agnostic in substance.
- **Must be different:** (a) replace `netstat -rn -f inet` gateway parsing (`queryMacDefaultGatewayIp`) with `GetAdaptersAddresses`/`GetBestInterface`; (b) broadcast semantics — `255.255.255.255` works but per-interface directed broadcast and VPN/virtual adapters need care; (c) `adbPath` resolution → bundled `adb.exe` then `%LOCALAPPDATA%`/`PATH`; (d) stop assuming the phone is the hotspot gateway (nice-to-have, also true on macOS).
- **Pairing UX is unchanged** — done on the phone's Wireless Debugging screen.

### 7.8 Connection stability and recovery — ✅ Feasible, Medium risk

- **Reuse:** 500 ms heartbeat (`PING`/`PONG` with µs timestamps), `HEARTBEAT_TIMEOUT_MS = 3000` socket timeouts, subject-less `promoteStandbyTransport` failover, Android's "don't reset `/dev/uhid` until the last link dies" invariant, the 1500 ms auto-pilot re-discovery loop.
- **Windows-specific additions:** re-assert the clip rectangle after every focus change; install a **hook watchdog** (Windows will not tell you the hook was removed — poll with a synthetic-input self-check or a heartbeat that requires hook activity); subscribe to network-change notifications (`NotifyIpInterfaceChange`) for faster Wi-Fi recovery; handle display topology changes (`WM_DISPLAYCHANGE`) since `CGDisplayBounds` has no direct per-call equivalent; handle sleep/resume (`WM_POWERBROADCAST`) — a Mac logout/relogin is analogous but Windows sleep happens far more often in practice.
- **Fail-safe requirements:** never leave `ClipCursor` set, never leave the system cursor replaced via `SetSystemCursor` (it survives a crash and the user is left with an invisible pointer), always release on session lock/switch.

### 7.9 Android device compatibility — ✅ Unchanged

- **No Android changes required.** The agent is host-OS-agnostic and the wire format is byte-exact. Same `/dev/uhid` (Android 11–15+, `uid=2000`), same `IClipboard` reflection variants (Android 10–15), same `PointerLinearizer` settings.
- The Windows port **inherits** the existing Android-side fragility (OEM ROMs, HiOS battery management, `mouse_pointer_acceleration_enabled` quirks) — it neither fixes nor worsens it.
- One subtle coupling to respect: cursor *feel* is the **product** of host gain (1.85) × Android `CurveVelocityControl` emulation (up to 3.0× above 3000 px/s). Change the host gain and you change feel on every device. Keep it identical.
- If we ever want to close the plaintext-stream gap, **that is an Android-side + macOS-side change** and must be a separate, versioned protocol effort — not smuggled into a Windows PR.

---

## 8. Recommended Architecture

### Option A — Separate Windows app, reusing only the Android agent
Fast to start, zero risk to macOS. But "reusing only the agent" means re-deriving the protocol from documentation (which is **wrong about encryption**), and it forks maintenance: two hosts, two ideas of edge-crossing maths, no shared acceptance criteria. Acceptable as a *spike*, not as the destination.

### Option B — Cross-platform desktop core (e.g. Rust `seam-core`) + thin platform shells
Superficially attractive; the honest numbers disagree. The genuinely shareable logic is **~1,340 of 4,390 lines (31%)**, and the *hard* 61% (input capture, cursor control, UI) is 100% platform-specific in every implementation that exists. Cost: Swift↔Rust FFI, universal/arm64 static linking, notarization implications, a second toolchain in a repo whose entire build is one `Makefile` and two shell scripts, and a rewrite of working, shipped Mac code. That is precisely the "rewrite because architecture is fun" trap. **Defer.**

### Option C — Dedicated Windows host inside the monorepo, contract unchanged ✅ **Recommended**
Add `windows-host/` alongside `macos-host/`, speaking `SEAM-Wire v1` byte-for-byte, with `android-agent/` untouched and `make` targets added additively (`make build-windows`, `make windows-test`).

Why this wins here:

| Criterion | A | B | **C** |
|---|---|---|---|
| Risk to existing macOS/Android | none | **high** (rewrite in scope) | none (additive) |
| Code reuse now | low | medium | **high** (spec + algorithms + harnesses) |
| Long-term maintenance | **poor** (forked knowledge) | good *if* a 3rd OS arrives | **good** (one repo, one contract, one set of hardware tests) |
| Platform abstraction | none | heavy | **already exists** (`SeamTransport`, `SEAM-Wire v1`) |
| Dependency compatibility | n/a | adds Rust toolchain | **none** — .NET or C++ only |
| Testing complexity | high drift | medium once built | **low** (shared golden vectors) |
| Performance | fine | fine | fine (hooks, not GC-bound, if callbacks stay allocation-light) |
| Future OS (Linux) | fork again | **best** | good — then extract the core *with two working implementations as evidence* |

**Language recommendation: C# on .NET 8 (WinUI 3, or WPF for the tray + flyout).** Rationale: first-class P/Invoke to `user32`/`kernel32`/`iphlpapi`; mature clipboard, tray (`Shell_NotifyIcon`) and per-monitor-DPI support; `System.Net.Sockets` and `System.Security.Cryptography` cover transport and HMAC with zero third-party packages (matching the project's "no unnecessary dependencies" ethos); single-file self-contained publish; straightforward Authenticode signing. Rust + `windows` crate + Tauri is a credible alternative if portability to Linux is a near-term goal; C++/Qt has precedent (Deskflow) but costs the most time. **Do not choose Electron** — it contradicts the project's native-footprint identity and complicates the hook thread.

**Contract-first discipline (the part that actually protects the Mac):** keep the wire format in a language-neutral spec and add **golden test vectors** (fixed key, fixed nonces → expected 38/68/41-byte handshake messages and 42-byte UDP datagrams). Both hosts must pass them. This is cheap, catches nonce-endianness and domain-string drift (the classic failure), and gives the maintainer something verifiable on a machine that is not a Mac — which is exactly what `CONTRIBUTING.md`'s hardware-only process lacks.

---

## 9. Expected Technical Challenges

**Confirmed (verified against documentation/behaviour):**

1. **No cursor-decoupling API on Windows.** Must emulate with clip + warp + hook; `ClipCursor` is global and is cleared by `DefWindowProc` on focus change.
2. **Silent hook removal** after `LowLevelHooksTimeout`; no notification, no error code. A watchdog is mandatory.
3. **Hooks don't cover the secure desktop / lock screen.** Must fail safe (release clip, unhook, send all-keys-up + `CURSOR_LEAVE`).
4. **ADB needs a per-OEM USB driver on Windows; macOS does not.** Wireless Debugging sidesteps it.
5. **High-polling-rate mice are a known `WH_MOUSE_LL` weakness** across Windows KVMs; Raw Input is a partial remedy but cannot suppress the local pointer.
6. **Precision touchpads send sub-120 wheel deltas**, so smooth scroll is achievable — but native macOS pixel-granularity parity is not guaranteed.
7. **UIPI/elevation:** covering elevated windows implies running elevated (or `uiAccess`, which requires a signed binary in Program Files).
8. **Windows has no clipboard permission prompt**, unlike macOS TCC — one class of problem simply disappears.

**Assumptions that require hands-on experiment (do not plan around them):**

- Whether Windows 11 auto-installs a working ADB driver for a specific TECNO/MediaTek handset.
- Actual end-to-end RTT and jitter from a Windows host over USB vs Wi-Fi (macOS claims ≥1.5 ms USB / 2–6 ms Wi-Fi; nothing about Windows is proven).
- Whether `ClipCursor`+`SetCursorPos` re-assertion can be made visually invisible on high-DPI multi-monitor setups (the documented "jitter" complaint in every naive implementation).
- Whether a Defender Firewall prompt appears on a clean machine for the outbound-only UDP fast path.
- Whether Authenticode/EV signing materially reduces SmartScreen friction (it does for reputation, but the timeline for a new cert is not instant).

**Packaging/installation/update (all net-new work):**

- Bundling `adb.exe` + `AdbWinApi.dll` + `AdbWinUsbApi.dll` and the `seam-agent.jar` resource (mirrors `resolveAgentJarPath`).
- Authenticode signing (macOS uses ad-hoc signing + "right-click → Open"; Windows uses SmartScreen, and an unsigned EXE reads as malware to users).
- Installer (MSIX vs. Inno/NSIS vs. MSI), per-user vs. per-machine (per-machine + Program Files is what `uiAccess` needs), auto-update channel, uninstall cleanliness, and "don't leave the cursor clipped on crash".
- Tray-only app with no taskbar presence, and Windows 11 Mica/Acrylic in place of Liquid Glass (there is no `GlassEffectContainer` analogue).

**Project-process challenges:**

- No tests, no CI, no PR template: a Windows PR lands into a repo where the only verification is a Mac + phone in the loop. Adding a conformance test suite is not gold-plating — without it the maintainer cannot review the port at all.
- Repository hygiene (§4) will otherwise become part of every PR review.

---

## 10. Implementation Complexity Estimate

| Workstream | New/changed code | Effort | Risk |
|---|---|---|---|
| Protocol + auth + frames (C#) | ~600 LOC | 3–5 d | Low (spec exists, byte-exact) |
| Transport: USB-ADB, Wi-Fi hybrid, multiplexer | ~700 LOC | 5–7 d | Low–Medium |
| Bootstrap: adb wrapper, mDNS, UDP discovery, stamp, daemon launch | ~700 LOC | 5–8 d | Medium (Windows quoting/paths) |
| **Input capture: hooks, clip/warp, hide, edge state machine, gain/residuals** | **~900–1,200 LOC** | **15–20 d** | **High** |
| Keyboard translation (scan code → HID) + shortcut policy | ~350 LOC | 4–6 d | Low–Medium |
| Scroll quantization | ~150 LOC | 2–3 d | Medium |
| Clipboard both ways | ~200 LOC | 2–3 d | Low |
| Tray + arrangement flyout + auto-pilot | ~600–900 LOC (XAML) | 8–12 d | Medium |
| Golden-vector conformance tests | ~300 LOC | 3–4 d | Low |
| Packaging, signing, updater, docs | — | 5–8 d | Medium |
| **Total** | **≈ 4,500–5,500 LOC** | **≈ 8–12 engineer-weeks** | — |

For scale: the existing macOS host is 4,390 lines. **The Windows host is a peer-sized component, not a small extension** — but it is a *known-shape* one, because the protocol, the edge maths, and the Android side are already solved. A single experienced Windows/systems developer: ~10–12 calendar weeks to parity including hardware validation. Two developers (input/transport split): ~6–7 weeks.

---

## 11. Recommended Development Roadmap

**Phase 0 — Contract proof (1 week). Highest value per day; do this first.**
A throwaway C# console app, no UI, no hooks: `adb` bootstrap → push/launch the existing unchanged `seam-agent.jar` → HMAC handshake over the forwarded socket → parse `DISPLAY_INFO` → stream a scripted figure-eight of `MOUSE_REPORT` dels and a few `KEY_REPORT`s → drive `EDGE_RETURN` and receive `CLIPBOARD_EVENT`. Port `Main.swift`'s `usb-test` harness. **Decision gate: if this works, the entire port is de-risked**, because Android needs no changes and the protocol is proven byte-compatible from Windows.

**Phase 1 — Input capture spike (2 weeks).** `WH_MOUSE_LL` + `WH_KEYBOARD_LL` on a dedicated message-loop thread; `ClipCursor`/`SetCursorPos` pointer pinning; cursor hiding; edge detection reproducing `evaluateBoundaryCrossing` exactly; panic hotkey; hook watchdog + fail-safe release on secure desktop. Test on 1 kHz mouse, 60/120/144 Hz displays, 100%/150%/200% DPI, dual monitor. **This is where the schedule risk lives — front-load it.**

**Phase 2 — Feature parity, wired (2–3 weeks).** Keyboard scan-code→HID table + modifier state machine + all-keys-up on disconnect; 1/32-notch scroll with sub-120 wheel handling; clipboard via `AddClipboardFormatListener` with echo suppression. Verify against the identical Android build the Mac uses.

**Phase 3 — Connectivity parity (1–2 weeks).** UDP discovery probe + saved-IP cache + gateway/interface enumeration; Wireless Debugging `adb pair`/`connect` + mDNS; dual transport with hot standby and mid-glide failover; Wi-Fi→USB upgrade; auto-pilot loop; network/display/power event handling.

**Phase 4 — UX (1–2 weeks).** `Shell_NotifyIcon` tray, status flyout, drag-to-arrange equivalent, first-run guidance (developer options, driver detection, "use Wireless Debugging if USB isn't detected").

**Phase 5 — Distribution and hardening (1–2 weeks).** Bundle Platform-Tools + jar, self-contained publish, Authenticode, installer, update channel, crash-safety audit (clip release, unhook, cursor restore), conformance test suite in CI.

**Phase 6 — Upstream.** Open a scoped PR: `windows-host/` + additive `Makefile` targets + golden vectors + docs, explicitly stating that macOS behaviour is unchanged, with the hardware matrix and RTT numbers `CONTRIBUTING.md` asks for.

---

## 12. Final Recommendation

**Yes — a reliable Windows host is realistic, and it can be built without touching the macOS or Android implementations.**

The technical case is strong for three specific reasons, each verified in source rather than assumed:

1. **The Android agent needs no modification.** It is host-OS-agnostic by construction; `SO_PEERCRED` validates the device-side adbd peer, not the host.
2. **The wire protocol is small, explicit, and byte-exact.** ~1,340 lines of the macOS host — framing, HMAC handshake, UDP datagram, dual-lane transport, failover — translate directly.
3. **Every hard problem has a documented Windows equivalent,** just not an identical one: `WH_MOUSE_LL`/`WH_KEYBOARD_LL` + `ClipCursor`/`SetCursorPos` instead of `CGEventTap` + `CGAssociateMouseAndMouseCursorPosition`; `AddClipboardFormatListener` instead of `NSPasteboard.changeCount`; `Shell_NotifyIcon` instead of `NSStatusItem`; `adb.exe` instead of `adb`.

**Best approach:** **Option C** — a dedicated `windows-host/` in the same monorepo (C#/.NET 8, WinUI 3 or WPF), speaking the unchanged `SEAM-Wire v1` contract, with the Android agent untouched and the macOS host untouched. Keep the wire format in a language-neutral spec with **golden test vectors** that both hosts must pass, instead of prematurely extracting a shared core. Revisit Option B (a Rust `seam-core`) only when a third OS makes the shared surface pay for itself — the hardest 61% of the code will never be shareable.

**Build first:** the Phase 0 contract proof — a console app that takes an untouched `seam-agent.jar`, bootstraps it over ADB from Windows, completes the HMAC handshake, reads `DISPLAY_INFO`, and moves the Android cursor. One week of work that either validates or kills the entire premise before any UI, hook, or packaging effort is spent.

**Honest caveats, not optimism:**

- **Cursor capture, not the protocol, is the risk.** Windows has no decoupling API; hooks are silently removed on timeout, are absent on the secure desktop, are weaker against elevated windows, and are the documented weak point for high-polling-rate mice across *all* Windows KVM software. Expect 2–3× the effort of any other workstream, and expect to need elevation for full coverage.
- **USB gets worse before it gets better.** macOS is driverless; Windows is per-OEM driver. Bundling Platform-Tools and defaulting toward Wireless Debugging is the mitigation, and `adb` must be verified on the actual TECNO/MediaTek target.
- **"Smooth scrolling" is achievable, "identical to macOS" is not promised.** Windows precision touchpads do send sub-120 deltas, but granularity and momentum belong to Windows.
- **Distribution is a real project.** Signing certificates, SmartScreen reputation, installer, updater — none of it exists today, and it is not optional for something that installs a global input hook.
- **Two findings should be raised upstream before/alongside the port, in separate PRs:** the research docs specify Noise/ChaCha20-Poly1305 but the code authenticates without encrypting (so keystrokes and clipboard text cross Wi-Fi in plaintext — a material security gap for *both* platforms), and Windows-style verification cannot be expressed in the current `CONTRIBUTING.md` process, which assumes a Mac.

**Bottom line:** low technical risk on protocol, transport, discovery, clipboard, and Android compatibility — all reusable or trivially portable. Concentrated risk on Windows input capture and on packaging/signing. A defensible plan is 8–12 engineer-weeks to parity, gated by a one-week contract proof that should be run before anything else is committed.

---

### Appendix A — Verified vs. assumed

| Claim | Status | Evidence |
|---|---|---|
| Android agent requires no changes for a Windows host | **Verified** | No host-OS references anywhere in `android-agent/`; `SO_PEERCRED` checks device-side adbd `uid 2000`/`0` (`UsbLocalSocketServer.verifyPeerCredentials`); wire layouts fixed in `SeamProtocol` |
| Protocol/auth fully portable | **Verified** | `SeamWireProtocol.swift` / `SeamAuthenticator.swift` are pure byte math + CryptoKit HMAC; mirrored by `SeamProtocol.java` / `SeamAuthenticator.java` |
| Frames are plaintext after auth (docs claim otherwise) | **Verified** | No AEAD anywhere in either tree; transports write `frame.serialize()` directly |
| Host listens on no inbound port for Wi-Fi | **Verified** | `WifiHybridTransport.connect` opens outbound TCP+UDP only; `:27186` is bound on the *phone* (`WifiTransportServer`) |
| `adb` supports everything SEAM uses on Windows | **Verified** (documented CLI parity) | `devices -l`, `forward tcp:0`, `push`, `shell`, `mdns`, `pair`, `connect` |
| Windows: no API to decouple mouse from pointer | **Verified** (absence of API; workaround is universal) | KVM implementations use clip+warp+hook |
| Windows low-level hooks silently removed on timeout; absent on secure desktop | **Verified** | Microsoft `LowLevelMouseProc` docs; multiple independent reports |
| `ClipCursor` cleared on focus change | **Verified** | `DefWindowProc` focus handling clears the clip rect (Raymond Chen, Old New Thing) |
| Windows needs per-OEM ADB USB driver; macOS driverless | **Verified** | Google OEM USB driver docs; ADB troubleshooting guides |
| Precision touchpads send sub-120 wheel deltas | **Verified** | Microsoft precision-touchpad team statement; multiple OSS implementations |
| Platform-Tools redistributable | **Verified (Apache-2.0)**, with the usual caveat that Google's packaged binaries vs. the SDK ToS warrant a proper `NOTICE` and a legal read before shipping | adb `NOTICE` / Apache-2.0 |
| Windows 11 auto-installs a working ADB driver for *our* TECNO device | **Assumption — test required** | — |
| Windows RTT/jitter parity with macOS figures | **Assumption — measure required** | — |
| Windows Firewall will not prompt for the outbound UDP fast path | **Assumption — clean-machine test** | — |
| `ClipCursor`+`SetCursorPos` can be made visually seamless at 150–200% DPI | **Assumption — spike required** | Known DPI round-trip artifacts in naive implementations |

### Appendix B — Platform mapping reference

| macOS | Windows equivalent | Fidelity |
|---|---|---|
| `CGEventTap` @ `.cghidEventTap` | `SetWindowsHookEx(WH_MOUSE_LL / WH_KEYBOARD_LL)`; optionally Raw Input `WM_INPUT` | Partial — no timeout notice, no secure desktop |
| `CGAssociateMouseAndMouseCursorPosition(0)` | *(none)* `ClipCursor` + `SetCursorPos` + swallow | Emulated |
| `CGWarpMouseCursorPosition` | `SetCursorPos` | Good |
| `CGDisplayHideCursor` / `NSCursor.hide` | blank `SetCursor` on an overlay window; avoid `SetSystemCursor` (survives crash) | Partial |
| private `CGSSetConnectionProperty("SetsCursorInBackground")` | *(none needed)* | n/a |
| `CGDisplayBounds(CGMainDisplayID())` | `GetSystemMetrics` / `EnumDisplayMonitors` / `MonitorFromPoint`, per-monitor DPI V2 | Good |
| `CGKeyCode` | VK / scan code (`MapVirtualKeyEx`) | Different table, same concept |
| `CGEventFlags` | `KBDLLHOOKSTRUCT.flags` + own modifier state | Good |
| `NSPasteboard.changeCount` | `GetClipboardSequenceNumber` | Excellent |
| `NSPasteboard` change monitoring | `AddClipboardFormatListener` + `WM_CLIPBOARDUPDATE` | Better (event-driven) |
| `NSStatusItem` + `NSPopover` | `Shell_NotifyIcon` + flyout window (WinUI 3 / WPF) | Partial (no native popover) |
| Liquid Glass / `GlassEffectContainer` | Windows 11 Mica / Acrylic | Cosmetic only |
| `DispatchSourceTimer` | `PeriodicTimer` / `System.Threading.Timer` | Good |
| `MonotonicClock` (`DispatchTime.uptimeNanoseconds`) | `Stopwatch.GetTimestamp` / QPC | Good |
| BSD `socket/read/write`, `SO_NOSIGPIPE` | Winsock via `System.Net.Sockets`; no SIGPIPE | Good |
| `IP_TOS` 0x10 / DSCP `0x2E` | `SocketOptionName.TypeOfService` | Good, may be ignored without policy |
| `netstat -rn -f inet` gateway | `GetAdaptersAddresses` / `GetBestInterface` | Better |
| `/usr/bin/env adb`, `/opt/homebrew/bin/adb` | bundled `adb.exe` + `%LOCALAPPDATA%` + `PATH` | Better (self-contained) |
| `~/.seam/auth_key`, `last_wifi_ip` | `%APPDATA%\Seam\` | Equivalent |
| ad-hoc `codesign` + TCC | Authenticode + SmartScreen + installer | Harder |
