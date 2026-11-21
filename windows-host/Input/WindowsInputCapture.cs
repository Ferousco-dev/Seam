using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using Seam.WindowsHost.Protocol;
using Seam.WindowsHost.Transport;

namespace Seam.WindowsHost.Input
{
    /// <summary>
    /// Windows global input capture + spatial edge crossing.
    ///
    /// This is the counterpart of QuartzEdgeEngine (macOS). It cannot be a direct translation:
    /// Windows has no equivalent of CGAssociateMouseAndMouseCursorPosition, so the pointer is
    /// held in place by re-centring it with SetCursorPos after each swallowed event, with a
    /// small ClipCursor box as a guard. (A clipped cursor never reports deltas, which is why
    /// clipping alone cannot work — this is the same approach used by Deskflow/Input Leap.)
    ///
    /// Verified-by-reading, NOT verified-on-Windows: this file has never executed.
    /// </summary>
    public sealed class WindowsInputCapture : IDisposable
    {
        // ---- Win32 constants -------------------------------------------------------------
        private const int WH_KEYBOARD_LL = 13;
        private const int WH_MOUSE_LL = 14;
        private const uint WM_QUIT = 0x0012;
        private const uint WM_MOUSEMOVE = 0x0200;
        private const uint WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
        private const uint WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
        private const uint WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208;
        private const uint WM_MOUSEWHEEL = 0x020A, WM_MOUSEHWHEEL = 0x020E;
        private const uint WM_XBUTTONDOWN = 0x020B, WM_XBUTTONUP = 0x020C;
        private const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
        private const uint WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
        private const uint LLKHF_EXTENDED = 0x01;
        private const uint VkQ = 0x51;
        private const uint VkControl = 0x11, VkMenu = 0x12;

        // ---- Edge-crossing tuning (ported 1:1 from QuartzEdgeEngine) ---------------------
        private const double EdgeZonePx = 3.0;
        private const double PushThreshold = 2.0;
        private const double DwellPushPerEvent = 0.55;   // macOS polling-tick compensation
        private const double CornerExclusionPx = 16.0;
        private const double PointerVelocityGain = 1.85; // macos-host default; keep for identical feel
        private const double WheelUnitsPerNotch = 32.0;  // Android SmoothScrollInjector HI_RES_UNITS_PER_NOTCH

        // ---- Interop ---------------------------------------------------------------------
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        private delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern bool ClipCursor(ref RECT lpRect);

        [DllImport("user32.dll", EntryPoint = "ClipCursor")]
        private static extern bool ClipCursorReset(IntPtr lpRect);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        // A thread is not a "GUI thread" until it has a message queue; WH_MOUSE_LL/WH_KEYBOARD_LL
        // callbacks are dispatched by that queue. Force its creation first.
        [DllImport("user32.dll")]
        private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin,
            uint wMsgFilterMax, uint wRemoveMsg);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        // ---- State -----------------------------------------------------------------------
        private readonly SeamConnection _connection;
        private readonly byte _edge;
        private readonly bool _forceCapture;
        /// <summary>
        /// Log sink. NEVER called from a hook callback directly: Console.WriteLine can block
        /// indefinitely when the console is in QuickEdit mode with a selection active, which
        /// would blow the ~300 ms LowLevelHooksTimeout and get the hook silently uninstalled.
        /// Hook-thread messages are queued and drained by the caller's idle loop instead.
        /// </summary>
        private readonly Action<string> _log;
        private readonly ConcurrentQueue<string> _pendingLog = new ConcurrentQueue<string>();
        private const int MaxPendingLog = 256;
        private readonly HidKeyState _keys = new HidKeyState();

        private readonly LowLevelProc _mouseProc; // held to prevent GC of the delegate
        private readonly LowLevelProc _keyProc;

        private Thread? _thread;
        private volatile uint _threadId;
        private IntPtr _mouseHook = IntPtr.Zero;
        private IntPtr _keyHook = IntPtr.Zero;

        private volatile bool _routing;
        private volatile bool _disposed;
        private volatile bool _reportedKeyReturn;

        private int _parkX, _parkY;
        private int _lastX, _lastY;
        private bool _haveLast;
        private double _outwardPush;
        private byte _buttons;
        private int _skipInitialDeltas;
        private double _residualDx, _residualDy, _residualV, _residualH;
        private long _lastReturnTicks;
        private long _eventsForwarded;
        private long _unsupportedKeys;

        /// <summary>Hook-thread-safe logging: enqueue only, no I/O.</summary>
        private void LogAsync(string message)
        {
            if (_pendingLog.Count < MaxPendingLog)
            {
                _pendingLog.Enqueue(message);
            }
        }

        /// <summary>Writes queued hook-thread messages. Call from a non-hook thread (idle loop).</summary>
        public void DrainLog()
        {
            while (_pendingLog.TryDequeue(out string? line))
            {
                if (line != null) _log(line);
            }
        }

        public bool IsRoutingToAndroid => _routing;
        public long EventsForwarded => Interlocked.Read(ref _eventsForwarded);
        public long UnsupportedKeys => Interlocked.Read(ref _unsupportedKeys);

        public WindowsInputCapture(SeamConnection connection, byte edge, bool forceCapture, Action<string> log)
        {
            _connection = connection;
            _edge = edge;
            _forceCapture = forceCapture;
            _log = log;
            _mouseProc = MouseHook;
            _keyProc = KeyboardHook;
        }

        private static int ScreenWidth => GetSystemMetrics(0);   // SM_CXSCREEN
        private static int ScreenHeight => GetSystemMetrics(1);  // SM_CYSCREEN

        /// <summary>Declares per-monitor-v2 DPI awareness so hook coordinates are physical pixels.</summary>
        public static void EnableDpiAwareness()
        {
            try
            {
                if (!SetProcessDpiAwarenessContext(new IntPtr(-4))) // PER_MONITOR_AWARE_V2
                {
                    SetProcessDPIAware();
                }
            }
            catch
            {
                try { SetProcessDPIAware(); } catch { }
            }
        }

        /// <summary>Installs both low-level hooks on a dedicated message-pumping thread.</summary>
        public void Start()
        {
            _thread = new Thread(MessageLoop)
            {
                IsBackground = true,
                Name = "seam-hooks"
            };
            _thread.Start();
        }

        private void MessageLoop()
        {
            _threadId = GetCurrentThreadId();
            const uint PM_NOREMOVE = 0x0000;
            PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE); // create this thread's message queue
            IntPtr module = GetModuleHandle(null);

            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
            _keyHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyProc, module, 0);

            if (_mouseHook == IntPtr.Zero || _keyHook == IntPtr.Zero)
            {
                LogAsync($"[ERROR] SetWindowsHookEx failed (mouse=0x{_mouseHook:X}, kb=0x{_keyHook:X}, " +
                     $"err={Marshal.GetLastWin32Error()}). Try running elevated.");
                return;
            }

            LogAsync("[Input] Low-level mouse + keyboard hooks installed.");

            while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
            if (_keyHook != IntPtr.Zero) UnhookWindowsHookEx(_keyHook);
            _mouseHook = _keyHook = IntPtr.Zero;
        }

        // ---- Mouse -----------------------------------------------------------------------

        private IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0)
            {
                return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            }

            uint message = (uint)wParam.ToInt64();
            MSLLHOOKSTRUCT data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

            if (!_routing)
            {
                if (message == WM_MOUSEMOVE)
                {
                    double rawDx = _haveLast ? data.pt.X - _lastX : 0;
                    double rawDy = _haveLast ? data.pt.Y - _lastY : 0;
                    _lastX = data.pt.X;
                    _lastY = data.pt.Y;
                    _haveLast = true;

                    if (_forceCapture || EvaluateBoundaryCrossing(data.pt, rawDx, rawDy))
                    {
                        BeginAndroid(data.pt);
                        return (IntPtr)1;
                    }
                }
                return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            }

            switch (message)
            {
                case WM_MOUSEMOVE:
                {
                    // The pointer is pinned at the park point, so the delta is measured from it,
                    // then the pointer is put back before returning.
                    double dx = data.pt.X - _parkX;
                    double dy = data.pt.Y - _parkY;
                    RecentreCursor();
                    if (dx != 0 || dy != 0)
                    {
                        ForwardDelta(dx, dy);
                    }
                    return (IntPtr)1;
                }
                case WM_LBUTTONDOWN: _buttons |= 0x01; SendButtons(); return (IntPtr)1;
                case WM_LBUTTONUP: _buttons = (byte)(_buttons & ~0x01); SendButtons(); return (IntPtr)1;
                case WM_RBUTTONDOWN: _buttons |= 0x02; SendButtons(); return (IntPtr)1;
                case WM_RBUTTONUP: _buttons = (byte)(_buttons & ~0x02); SendButtons(); return (IntPtr)1;
                case WM_MBUTTONDOWN: _buttons |= 0x04; SendButtons(); return (IntPtr)1;
                case WM_MBUTTONUP: _buttons = (byte)(_buttons & ~0x04); SendButtons(); return (IntPtr)1;
                case WM_XBUTTONDOWN:
                {
                    int button = (int)((data.mouseData >> 16) & 0xFFFF);
                    _buttons |= (byte)(button == 1 ? 0x08 : 0x10);
                    SendButtons();
                    return (IntPtr)1;
                }
                case WM_XBUTTONUP:
                {
                    int button = (int)((data.mouseData >> 16) & 0xFFFF);
                    _buttons = (byte)(_buttons & ~(button == 1 ? 0x08 : 0x10));
                    SendButtons();
                    return (IntPtr)1;
                }
                case WM_MOUSEWHEEL:
                {
                    short delta = (short)((data.mouseData >> 16) & 0xFFFF);
                    ForwardScroll(delta, 0);
                    return (IntPtr)1;
                }
                case WM_MOUSEHWHEEL:
                {
                    short delta = (short)((data.mouseData >> 16) & 0xFFFF);
                    ForwardScroll(0, delta);
                    return (IntPtr)1;
                }
                default:
                    return (IntPtr)1; // swallow everything else while routed
            }
        }

        /// <summary>
        /// Port of QuartzEdgeEngine.evaluateBoundaryCrossing. Windows clamps the pointer at the
        /// screen edge, so a genuine outward delta only appears on the approach; sustained
        /// edge dwell is accumulated per event instead (same idea as the macOS 120 Hz poll tick).
        /// </summary>
        private bool EvaluateBoundaryCrossing(POINT pt, double rawDx, double rawDy)
        {
            if (MonotonicMs() - _lastReturnTicks < 180)
            {
                _outwardPush = 0;
                return false;
            }

            int w = ScreenWidth;
            int h = ScreenHeight;
            double distance, outward = 0;
            bool inSafeBand;

            switch (_edge)
            {
                case SeamWireProtocol.EdgeRight:
                    distance = (w - 1) - pt.X;
                    inSafeBand = pt.Y > CornerExclusionPx && pt.Y < h - CornerExclusionPx;
                    if (rawDx > 0) outward = rawDx;
                    break;
                case SeamWireProtocol.EdgeLeft:
                    distance = pt.X;
                    inSafeBand = pt.Y > CornerExclusionPx && pt.Y < h - CornerExclusionPx;
                    if (rawDx < 0) outward = -rawDx;
                    break;
                case SeamWireProtocol.EdgeBottom:
                    distance = (h - 1) - pt.Y;
                    inSafeBand = pt.X > CornerExclusionPx && pt.X < w - CornerExclusionPx;
                    if (rawDy > 0) outward = rawDy;
                    break;
                default: // Top
                    distance = pt.Y;
                    inSafeBand = pt.X > CornerExclusionPx && pt.X < w - CornerExclusionPx;
                    if (rawDy < 0) outward = -rawDy;
                    break;
            }

            // NOTE: macOS gates the top edge on "below the menu bar"; Windows has no top menu bar,
            // so the top edge is governed purely by distance + the corner exclusion band.
            if (distance <= EdgeZonePx && inSafeBand)
            {
                _outwardPush += outward > 0 ? outward : DwellPushPerEvent;
                if (_outwardPush >= PushThreshold)
                {
                    _outwardPush = 0;
                    return true;
                }
            }
            else if (distance > 10.0 || !inSafeBand)
            {
                _outwardPush = 0;
            }

            return false;
        }

        private void BeginAndroid(POINT exitPoint)
        {
            int w = ScreenWidth;
            int h = ScreenHeight;

            double ratioX = Math.Clamp((double)exitPoint.X / Math.Max(1, w - 1), 0.0, 1.0);
            double ratioY = Math.Clamp((double)exitPoint.Y / Math.Max(1, h - 1), 0.0, 1.0);

            byte androidEntryEdge;
            ushort entryNormX, entryNormY;

            // Entry insets (360 / 65175) are copied verbatim from QuartzEdgeEngine.transitionToAndroid
            // so entry behaviour matches the macOS host exactly.
            switch (_edge)
            {
                case SeamWireProtocol.EdgeRight:
                    androidEntryEdge = SeamWireProtocol.EdgeLeft;
                    entryNormX = 360;
                    entryNormY = (ushort)Math.Round(ratioY * 65535.0);
                    break;
                case SeamWireProtocol.EdgeLeft:
                    androidEntryEdge = SeamWireProtocol.EdgeRight;
                    entryNormX = 65175;
                    entryNormY = (ushort)Math.Round(ratioY * 65535.0);
                    break;
                case SeamWireProtocol.EdgeBottom:
                    androidEntryEdge = SeamWireProtocol.EdgeTop;
                    entryNormX = (ushort)Math.Round(ratioX * 65535.0);
                    entryNormY = 360;
                    break;
                default:
                    androidEntryEdge = SeamWireProtocol.EdgeBottom;
                    entryNormX = (ushort)Math.Round(ratioX * 65535.0);
                    entryNormY = 65175;
                    break;
            }

            try
            {
                _connection.Send(SeamFrames.CursorEnter(androidEntryEdge, entryNormX, entryNormY, 0));
            }
            catch (Exception ex)
            {
                LogAsync($"[ERROR] CURSOR_ENTER failed: {ex.Message}");
                return;
            }

            _parkX = exitPoint.X;
            _parkY = exitPoint.Y;
            _residualDx = _residualDy = _residualV = _residualH = 0;
            _skipInitialDeltas = 2;
            _outwardPush = 0;
            _keys.Reset();
            _routing = true;

            SetCursorPos(_parkX, _parkY);
            ReassertClip();
            LogAsync($"[Routing] -> Android (entry edge={androidEntryEdge}, normX={entryNormX}, normY={entryNormY}). " +
                 "Ctrl+Alt+Q returns to Windows.");
        }

        private void RecentreCursor()
        {
            SetCursorPos(_parkX, _parkY);
            ReassertClip(); // Windows clears the clip rectangle on focus changes, so re-assert often
        }

        private void ReassertClip()
        {
            // A 3x3 guard box: small enough to hold the pointer still, large enough that the
            // tiny intra-box movement still surfaces as a delta we can read.
            RECT box = new RECT
            {
                Left = _parkX - 1,
                Top = _parkY - 1,
                Right = _parkX + 2,
                Bottom = _parkY + 2
            };
            ClipCursor(ref box);
        }

        private void EndAndroid(ushort normX, ushort normY)
        {
            _routing = false;
            _lastReturnTicks = MonotonicMs();
            _buttons = 0;
            _outwardPush = 0;
            _haveLast = false;

            byte[] reset = _keys.Reset();
            try { _connection.Send(reset); } catch { }

            ClipCursorReset(IntPtr.Zero);

            int w = ScreenWidth;
            int h = ScreenHeight;
            double ratioX = normX / 65535.0;
            double ratioY = normY / 65535.0;

            int x, y;
            switch (_edge)
            {
                case SeamWireProtocol.EdgeRight:
                    x = w - 6;
                    y = (int)(ratioY * h);
                    break;
                case SeamWireProtocol.EdgeLeft:
                    x = 6;
                    y = (int)(ratioY * h);
                    break;
                case SeamWireProtocol.EdgeBottom:
                    x = (int)(ratioX * w);
                    y = h - 6;
                    break;
                default:
                    x = (int)(ratioX * w);
                    y = 26;
                    break;
            }

            SetCursorPos(x, y);
            _lastX = x;
            _lastY = y;
            LogAsync("[Routing] -> Windows (cursor restored).");
        }

        /// <summary>Handles an EDGE_RETURN frame from the agent.</summary>
        public void HandleEdgeReturn(AndroidEdgeReturnEvent evt)
        {
            if (!_routing) return;
            EndAndroid(evt.NormX, evt.NormY);
        }

        /// <summary>Panic exit (Ctrl+Alt+Q or console quit).</summary>
        public void ForceReturn()
        {
            if (!_routing)
            {
                return;
            }
            try
            {
                _connection.Send(SeamFrames.CursorLeave(1));
            }
            catch { }
            EndAndroid(32768, 32768);
        }

        private void ForwardDelta(double rawDx, double rawDy)
        {
            if (_skipInitialDeltas > 0)
            {
                _skipInitialDeltas--;
                if (Math.Abs(rawDx) > 80 || Math.Abs(rawDy) > 80) return;
            }

            double scaledX = rawDx * PointerVelocityGain + _residualDx;
            double scaledY = rawDy * PointerVelocityGain + _residualDy;
            int outDx = (int)Math.Truncate(scaledX);
            int outDy = (int)Math.Truncate(scaledY);
            _residualDx = scaledX - outDx;
            _residualDy = scaledY - outDy;

            if (outDx == 0 && outDy == 0) return;

            try
            {
                _connection.Send(SeamFrames.MouseReport(
                    _buttons,
                    (short)Math.Clamp(outDx, short.MinValue, short.MaxValue),
                    (short)Math.Clamp(outDy, short.MinValue, short.MaxValue),
                    0, 0));
                Interlocked.Increment(ref _eventsForwarded);
            }
            catch (Exception ex)
            {
                LogAsync($"[ERROR] Mouse forward failed: {ex.Message}");
                ForceReturn();
            }
        }

        private void SendButtons()
        {
            try
            {
                _connection.Send(SeamFrames.MouseReport(_buttons, 0, 0, 0, 0));
                Interlocked.Increment(ref _eventsForwarded);
            }
            catch (Exception ex)
            {
                LogAsync($"[ERROR] Button forward failed: {ex.Message}");
                ForceReturn();
            }
        }

        /// <summary>
        /// Windows wheel delta (multiples of 120, but precision touchpads may send sub-120)
        /// -> 1/32-notch units consumed by Android's SmoothScrollInjector.
        /// </summary>
        private void ForwardScroll(short vDelta, short hDelta)
        {
            _residualV += (vDelta / 120.0) * WheelUnitsPerNotch;
            _residualH += (hDelta / 120.0) * WheelUnitsPerNotch;

            int vTick = (int)Math.Truncate(_residualV);
            int hTick = (int)Math.Truncate(_residualH);
            _residualV -= vTick;
            _residualH -= hTick;

            if (vTick == 0 && hTick == 0) return;

            try
            {
                _connection.Send(SeamFrames.MouseReport(
                    _buttons, 0, 0,
                    (sbyte)Math.Clamp(vTick, sbyte.MinValue, sbyte.MaxValue),
                    (sbyte)Math.Clamp(hTick, sbyte.MinValue, sbyte.MaxValue)));
                Interlocked.Increment(ref _eventsForwarded);
            }
            catch (Exception ex)
            {
                LogAsync($"[ERROR] Scroll forward failed: {ex.Message}");
                ForceReturn();
            }
        }

        // ---- Keyboard --------------------------------------------------------------------

        private IntPtr KeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0)
            {
                return CallNextHookEx(_keyHook, nCode, wParam, lParam);
            }
            if (!_routing)
            {
                return CallNextHookEx(_keyHook, nCode, wParam, lParam);
            }

            uint message = (uint)wParam.ToInt64();
            bool isDown = message == WM_KEYDOWN || message == WM_SYSKEYDOWN;
            KBDLLHOOKSTRUCT data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool extended = (data.flags & LLKHF_EXTENDED) != 0;

            // Panic hotkey: Ctrl + Alt + Q
            if (isDown && data.vkCode == VkQ && IsDown(VkControl) && IsDown(VkMenu))
            {
                ForceReturn();
                return (IntPtr)1;
            }

            byte modifierBit = WindowsKeyTable.ModifierBit(data.vkCode, data.scanCode, extended);
            if (modifierBit != 0)
            {
                _keys.SetModifier(modifierBit, isDown);
                SendKeyReport();
                return (IntPtr)1;
            }

            byte usage = WindowsKeyTable.HidUsage(data.scanCode, extended);
            if (usage == 0)
            {
                if (!_reportedKeyReturn)
                {
                    _reportedKeyReturn = true;
                    Interlocked.Increment(ref _unsupportedKeys);
                    LogAsync($"[WARN] Unmapped key scan=0x{data.scanCode:X2} vk=0x{data.vkCode:X2} — not forwarded.");
                }
                return (IntPtr)1;
            }

            _reportedKeyReturn = false;
            if (isDown) _keys.AddUsage(usage);
            else _keys.RemoveUsage(usage);
            SendKeyReport();
            return (IntPtr)1;
        }

        private void SendKeyReport()
        {
            try
            {
                _connection.Send(_keys.BuildReport());
                Interlocked.Increment(ref _eventsForwarded);
            }
            catch (Exception ex)
            {
                LogAsync($"[ERROR] Key forward failed: {ex.Message}");
                ForceReturn();
            }
        }

        [DllImport("user32.dll")]
        private static extern short GetKeyState(int nVirtKey);

        private static bool IsDown(uint vk) => (GetKeyState((int)vk) & 0x8000) != 0;

        private static long MonotonicMs() => Environment.TickCount64;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            ForceReturn();
            ClipCursorReset(IntPtr.Zero);

            if (_threadId != 0)
            {
                PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }
        }
    }
}
