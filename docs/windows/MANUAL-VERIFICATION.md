# Manual verification on real Windows hardware

The CI workflow proves everything that can be proven without a phone: it compiles, runs the
offline self-test, publishes the app folder, resolves resources from a path with spaces,
builds the ZIP and the installer, **silently installs the app, runs it from the install
folder, reinstalls over it, and uninstalls it again**. It also **launches the application with
no arguments on a real interactive desktop, waits for the main window, proves the WinForms
message loop is running, sends `WM_CLOSE`, and asserts a clean exit with no leftover
process**. It cannot prove anything about a physical phone, a USB driver, real
mouse/keyboard capture, or how the window looks and behaves for a human.

Run this list on the machine you reported problems from. Every step states the exact command
and the result that counts as a pass. Nothing below has been executed by the agent — this is
the list to execute.

**Current status, stated plainly.**

| Area | Status |
|---|---|
| USB session through the desktop window | not yet run on hardware (the console build was) |
| Wireless discovery, pairing, automatic reconnect, Disconnect (§2.7, §2.8b) | **not run at all** — implemented and covered by 17 offline self-test checks only |
| Window layout at 100% / 125% DPI | 100% exercised by CI at 96 dpi; **125% not verified** |
| Installer, upgrade, uninstall | CI-verified on a clean Windows runner |
| Windows 10 (as opposed to the Windows Server image CI uses) | not verified |

Nothing in this repository claims a physically verified Wi-Fi session. The Galaxy A05 report
that prompted the guidance work was a USB authorisation/message problem, not evidence that the
wireless implementation is broken — and neither is evidence that it works.

---

## 0. Record what you are testing

```
seam-host.exe --diagnose
```

Paste the whole output into the issue. It contains no key material: the pairing key is
printed as `fingerprint: <16 hex>` only.

Pass: ends with `RESULT: all bundled resources found.` and exit code 0
(`echo $LASTEXITCODE` in PowerShell).

---

## 1. Installer, clean machine

1. No SEAM installed, no `%USERPROFILE%\.seam` folder.
2. Double-click `SEAM-Setup-x64.exe` → SmartScreen → **More info → Run anyway**.
3. Install to the default `C:\Program Files\SEAM`, tick the desktop shortcut.

Pass:
- Start Menu contains `SEAM`, `SEAM diagnostics`, `SEAM quick start`, `Uninstall SEAM`.
- **Settings → Apps → Installed apps** lists `SEAM 0.10.0` with an uninstall entry.
- `C:\Program Files\SEAM\platform-tools\adb.exe` and `C:\Program Files\SEAM\seam-agent.jar`
  exist.

## 2. Desktop application (GUI) — launch, interaction, resize, close

This is the section CI can only half-cover: it proves the window is created and the message
loop runs, not how the application behaves for a person. Do this **before** connecting a
phone, so that any failure is unambiguous.

### 2.1 Normal launch, every route a user has

With no phone attached, launch SEAM four ways and confirm each one **opens the window**:

1. **Start Menu → SEAM.**
2. **Desktop shortcut → SEAM** (only exists if you ticked the box during setup).
3. **File Explorer → `C:\Program Files\SEAM\seam-host.exe`** (double-click).
4. **Portable:** extract `SEAM-Windows-x64.zip` to `C:\Users\<you>\Desktop\SEAM test (v2)`
   and double-click `seam-host.exe` in that folder.

Pass, for all four:

- The window appears within ~3 seconds: title `SEAM`, SEAM icon in the title bar and on the
  taskbar (not a generic white icon), left rail with **Home** selected, and the status pill
  reading **Not connected**.
- **No console window flashes** and no black window is left behind.
- **No UAC prompt** for the application itself (the installer is different: it elevates).
- No crash dialog and no error banner just because there is no phone.
- Task Manager → Details shows exactly one `seam-host.exe`. No `conhost.exe` is attached to it.

Then confirm resource resolution from the same session, from an unrelated directory:

```
cd C:\
& "C:\Program Files\SEAM\seam-host.exe" --diagnose
```

Pass: `app dir` is the install folder (or the portable folder), `source   : bundled with SEAM`,
`RESULT: all bundled resources found.`, exit code 0, and no `%TEMP%` path anywhere.

### 2.2 The window is live, not a static picture

1. Leave the window open for 30 s with no phone.
2. Watch the activity log: it should show the ADB/device polling lines rather than freezing.
3. Move the window, minimise and restore it, and click in it.

Pass: the window repaints, the controls stay enabled/disabled consistently, and the process
stays responsive (Windows does not report "Not Responding", no spinning cursor on hover).

### 2.3 Interaction — every control

Do each of these and note anything that does nothing, throws, or looks broken:

- **Settings:** open the Settings page in the left rail, change the edge (right → left), toggle
  *auto-connect*, change the activity length, **Save settings**, close the window, reopen SEAM.
  Pass: the values are still there
  (`%APPDATA%\SEAM\ui-settings.json`), and the hint text in the window names the new edge.
- **Diagnostics:** open the Diagnostics page in the left rail. Pass: a report within a few
  seconds, ending with
  `RESULT: all bundled resources found.`, containing a short `fingerprint:` and **no long key**.
- **Activity:** open it, watch a line arrive with a session running, tick/untick **Follow latest**.
  Pass: lines carry a timestamp, severity colours are only green/amber/red for real events, the
  list stops auto-scrolling when follow is off, and returning to Home does not lose lines.
- **Copy log:** on the Activity page, click **Copy log**, paste into Notepad. Pass: the visible
  log text is on the clipboard.
- **Clear log:** click it. Pass: the log empties and the placeholder text returns.
- **Refresh:** click it. Pass: it does not error with no phone attached.
- **Connect / Disconnect / Enable input sharing:** with no phone these must be disabled or
  explain why, never crash. (Their real behaviour is tested in §2.6.)
- **Banner:** if an error banner appears, **Dismiss** removes it and the layout closes up.

### 2.4 Resize and display scaling

1. Drag the window as small as it goes, then maximise, then restore.
2. Repeat at Windows display scaling **100 %**, **125 %** and **150 %**
   (Settings → System → Display → Scale; sign out and back in if Windows asks).
3. On a laptop, also try with an external monitor if one is available.

Pass at every scale: no clipped labels, no overlapping buttons, the status card and the log
keep their proportions, the window never grows beyond the usable screen area, and the
minimum size still shows every control.

### 2.5 Close cleanly, nothing left behind

1. Close the window with the **X** while disconnected.
2. Close it again while **connected** (repeat after §2.6).
3. After closing, check Task Manager → Details.

Pass:

- `seam-host.exe` disappears within a couple of seconds; no error dialog on exit.
- Your mouse and keyboard behave exactly as before (the low-level hooks are removed).
- `%APPDATA%\SEAM\ui-settings.json` was just updated (the close path saves your settings).
- `adb.exe` may still be running: that is the ADB server daemon, which is normal and shared
  with any other Android tooling. **`seam-host.exe` must not be.**
- Relaunching immediately works, with or without that `adb.exe` still running.

### 2.6 USB session through the window

1. Phone connected by USB with **USB debugging** on but **not yet authorised**.
2. Launch SEAM.

Pass: the phone is listed as **unauthorized** and the window says to accept the prompt on the
phone (no console needed). Accept it on the phone → within a few seconds the device becomes
authorised.

3. Press **Connect**.

Pass: the status pill turns green, the log shows the agent push/start, `mutual HMAC-SHA256 OK`
and `DISPLAY_INFO`; no console window appears at any point.

4. Move the pointer to the named screen edge → the phone takes the pointer; type on the PC
   keyboard → characters land on the phone; **Ctrl + Alt + Q** returns control to Windows.
5. Copy text on either side and paste on the other (clipboard both directions).
6. Press **Disconnect**, then unplug and re-plug the cable.

Pass: control returns to Windows, the app notices the unplug/replug (Connect becomes
Reconnect) and can connect again **without** restarting SEAM.

### 2.7 Wireless debugging through the window

1. Phone: Developer options → **Wireless debugging** → *Pair device with pairing code*.
2. On the PC, pair once with the bundled ADB (this is the only step that needs a terminal):

```
"C:\Program Files\SEAM\platform-tools\adb.exe" pair <phone-ip>:<pairing-port>
"C:\Program Files\SEAM\platform-tools\adb.exe" connect <phone-ip>:<debug-port>
```

3. In the SEAM window press **Refresh**, then **Connect**.

Pass: the phone appears without a USB cable, the session behaves exactly as in §2.6 steps 3–6,
and nothing requires an OEM USB driver.

### 2.8 If something does not work

Record: which of 2.1–2.7 failed, the exact window message, and the output of
`seam-host.exe --diagnose` (it contains no key material). Screenshots of the window at the
problem moment are the most useful evidence, because everything above is about what a person
sees rather than what a log says.

### 2.8b Wireless discovery and automatic reconnection

Ported from the macOS autopilot: every poll lists `adb devices`, and every third poll runs
`adb mdns services` and `adb connect`s an already-paired `_adb-tls-connect._tcp` endpoint, then
starts the session over that transport exactly as it does over USB.

1. Settings -> tick **Connect automatically, including phones on Wi-Fi**.
2. Phone: Developer options -> Wireless debugging on, phone and PC on the same network.
3. First time only: phone -> *Pair device with pairing code*, then pair once with the bundled adb
   (the commands are in §2.7). Android requires that code to be typed on the PC side; SEAM shows
   **Waiting for the first wireless pairing** until it has been done, and never pretends to bypass it.
4. After pairing, unplug USB. The status pill goes to Connecting then Connected over Wi-Fi with no
   command prompt involved.
5. Interrupt Wi-Fi (airplane mode / leave the network) and restore it: the session reconnects by
   itself within about 30 s, because the dropped adb transport is re-dialled on the next mDNS tick.
6. Press **Disconnect**: the autopilot must stay quiet until **Connect** is pressed again.

Pass: steps 4-6 behave as described, and no second session is started while one is connecting or
connected (Activity shows one `[Link] connecting to ...` line per attempt).

### 2.9 What the window says when Connect cannot proceed

Each row is one state the code can detect. The headline is what the Home page shows; the
second line is the advice under it. No row may mention a driver unless the row says so.

| Situation | Headline | Must mention |
|---|---|---|
| adb.exe missing from the app folder | SEAM cannot find adb | reinstall, `--diagnose` |
| no phone visible to adb | No phone detected | USB debugging, the ALLOW prompt |
| phone plugged in, not authorised | The phone has not authorised this PC | ALLOW on the phone |
| adb says `offline` | The phone stopped responding | unplug, re-plug, Connect |
| a saved Wi-Fi address stopped answering | The phone is not answering over Wi-Fi | `adb connect` or the cable |
| Connect pressed, agent never came up | The SEAM agent did not start on the phone | Diagnostics page for its log |
| Connect pressed, socket closed immediately | The phone closed the connection | press Connect again |
| pair code changed since the last session | The pairing keys do not match | Connect re-installs the agent |

Pass: the row that matches the real situation is the one shown, the sentence underneath says
what to do, and nothing tells a USB user to install an OEM driver unless Windows itself is the
problem (phone flagged in Device Manager).

## 3. Phone authorisation flow

1. Phone connected by USB, USB debugging on, **not yet authorised**.
2. Launch SEAM.

Pass: SEAM reports the device as `unauthorized` and tells you to accept the prompt on the
phone. Accept it, re-run, and the device appears as authorised.

## 4. Agent push and start

1. Phone authorised, no agent running (`adb shell ps | findstr seam` returns nothing).
2. `seam-host.exe --trajectory`

Pass: host pushes `seam-agent.jar` to `/data/local/tmp/seam-agent.jar`, starts it, the
handshake prints `mutual HMAC-SHA256 OK`, `DISPLAY_INFO` is printed, and the phone's cursor
traces a figure-eight. No input hooks are installed in this mode, so your PC mouse keeps
working normally.

Then:

```
adb -s <serial> shell cat /data/local/tmp/seam-agent.log
```

Pass: the log contains `SEAM_AGENT_READY` and **no** `--auth-key` value.

## 5. Full capture mode

`seam-host.exe --capture` — move the mouse right off the screen edge onto the phone.

Pass: the phone cursor moves; `Ctrl+Alt+Q` returns control to Windows; `q` in the console
exits. Confirm the PC keyboard types on the phone and comes back after `Ctrl+Alt+Q`.

Known behaviour, not a bug: input pauses while a UAC prompt or the lock screen is up
(low-level hooks cannot see them).

## 6. Upgrade preserves pairing

1. Note the fingerprint from `--diagnose` (`PAIRING KEY / fingerprint:`).
2. Run `SEAM-Setup-x64.exe` again (same version is fine).

Pass: install completes without an error, `--diagnose` still prints the **same**
fingerprint, `%USERPROFILE%\.seam\auth_key` still exists, and `--trajectory` reconnects
without re-pairing the phone.

## 7. Uninstall keeps user data by default

**Settings → Apps → SEAM → Uninstall.**

Pass: the wizard asks about the pairing key with **No** preselected. Answer **No** →
`C:\Program Files\SEAM` is gone, the Start Menu entries are gone, *Installed apps* no longer
lists SEAM, and `%USERPROFILE%\.seam\auth_key` still exists.

Repeat the install/uninstall once choosing **Yes** → the key file is deleted.

## 8. Portable ZIP

1. Extract `SEAM-Windows-x64.zip` to a folder whose path has spaces, e.g.
   `C:\Users\<you>\Desktop\SEAM test (v2)`.
2. Run `seam-host.exe --self-test`, `--diagnose`, then `--trajectory` from that folder.

Pass: all three work; `app dir` is the spaced folder; `source : bundled with SEAM`.

3. Rename the folder and run again.

Pass: still works — nothing is stored in the folder that would break a move.

Pass: **no** two-nested-folders situation after Explorer's *Extract All*.

## 9. No-tooling check

On a Windows machine with **no** .NET, **no** JDK, **no** Android SDK, and a `PATH` that
contains no adb:

- Installer path and portable path both run `--self-test` and `--diagnose` successfully.
- `where adb` returns "not found", yet `--diagnose` still shows `source : bundled with
  SEAM` and a working `adb version` line.

## 10. macOS and Android untouched

- `macos-host/` build and `scripts/build-agent.sh` behaviour are unchanged by this work
  (no files under `macos-host/` or `android-agent/` were modified).
- The Android agent's wire protocol is version `0x01`, unchanged; the phone does not need a
  new agent build for this Windows release.

---

## Reporting

Open an issue at <https://github.com/CalebPiusC/Seam/issues> with:

1. `--diagnose` output (safe: no key material).
2. The `RESULT:` line and exit code.
3. Phone model, Android version, connection type (USB / Wireless Debugging).
4. Which numbered step above failed and what you saw instead.
