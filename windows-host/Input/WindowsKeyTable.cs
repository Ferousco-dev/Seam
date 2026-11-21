using System;
using Seam.WindowsHost.Protocol;

namespace Seam.WindowsHost.Input
{
    /// <summary>
    /// Windows Set-1 hardware scan code -> USB HID Usage Page 0x07 translation.
    ///
    /// This is the Windows counterpart of MacKeycodeTranslator.macToHidUsage. The macOS table
    /// is keyed on `CGKeyCode`; Windows supplies a hardware scan code in
    /// KBDLLHOOKSTRUCT.scanCode, which is layout-independent and therefore the safer source
    /// (matching the macOS host's intent: physical key position, not character produced).
    ///
    /// Ergonomic note: macOS needs a Cmd->Ctrl remap layer because ⌘ does not mean Ctrl on
    /// Android. On Windows, Ctrl already means Ctrl, so no equivalent translation is needed;
    /// this is a simplification, not a missing feature.
    /// </summary>
    public static class WindowsKeyTable
    {
        // HID keyboard modifier bits (report byte 0)
        public const byte ModLeftCtrl = 0x01;
        public const byte ModLeftShift = 0x02;
        public const byte ModLeftAlt = 0x04;
        public const byte ModLeftGui = 0x08;
        public const byte ModRightCtrl = 0x10;
        public const byte ModRightShift = 0x20;
        public const byte ModRightAlt = 0x40;
        public const byte ModRightGui = 0x80;

        private const uint VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12;
        private const uint VkLShift = 0xA0, VkRShift = 0xA1;
        private const uint VkLControl = 0xA2, VkRControl = 0xA3;
        private const uint VkLMenu = 0xA4, VkRMenu = 0xA5;
        private const uint VkLWin = 0x5B, VkRWin = 0x5C;

        /// <summary>
        /// Maps a modifier key to its HID modifier bit. Returns 0 for non-modifier keys.
        /// </summary>
        public static byte ModifierBit(uint vkCode, uint scanCode, bool extended)
        {
            switch (vkCode)
            {
                case VkShift:
                    return scanCode == 0x36 ? ModRightShift : ModLeftShift;
                case VkLShift: return ModLeftShift;
                case VkRShift: return ModRightShift;
                case VkControl:
                    return extended ? ModRightCtrl : ModLeftCtrl;
                case VkLControl: return ModLeftCtrl;
                case VkRControl: return ModRightCtrl;
                case VkMenu:
                    return extended ? ModRightAlt : ModLeftAlt;
                case VkLMenu: return ModLeftAlt;
                case VkRMenu: return ModRightAlt;
                case VkLWin: return ModLeftGui;
                case VkRWin: return ModRightGui;
                default: return 0;
            }
        }

        /// <summary>
        /// Maps a Set-1 scan code to a HID usage. Returns 0 if unsupported (safe: the key is
        /// simply not forwarded, and the console reports it once).
        /// </summary>
        public static byte HidUsage(uint scanCode, bool extended)
        {
            if (extended)
            {
                switch (scanCode)
                {
                    case 0x1C: return 0x58; // Numpad Enter
                    case 0x35: return 0x54; // Numpad /
                    case 0x47: return 0x4A; // Home
                    case 0x48: return 0x52; // Arrow Up
                    case 0x49: return 0x4B; // Page Up
                    case 0x4B: return 0x50; // Arrow Left
                    case 0x4D: return 0x4F; // Arrow Right
                    case 0x4F: return 0x4D; // End
                    case 0x50: return 0x51; // Arrow Down
                    case 0x51: return 0x4E; // Page Down
                    case 0x52: return 0x49; // Insert
                    case 0x53: return 0x4C; // Delete
                    case 0x5D: return 0x65; // Application / Menu
                    default: return 0;
                }
            }

            switch (scanCode)
            {
                case 0x01: return 0x29; // Escape
                case 0x02: return 0x1E; // 1
                case 0x03: return 0x1F; // 2
                case 0x04: return 0x20; // 3
                case 0x05: return 0x21; // 4
                case 0x06: return 0x22; // 5
                case 0x07: return 0x23; // 6
                case 0x08: return 0x24; // 7
                case 0x09: return 0x25; // 8
                case 0x0A: return 0x26; // 9
                case 0x0B: return 0x27; // 0
                case 0x0C: return 0x2D; // -
                case 0x0D: return 0x2E; // =
                case 0x0E: return 0x2A; // Backspace
                case 0x0F: return 0x2B; // Tab
                case 0x10: return 0x14; // Q
                case 0x11: return 0x1A; // W
                case 0x12: return 0x08; // E
                case 0x13: return 0x15; // R
                case 0x14: return 0x17; // T
                case 0x15: return 0x1C; // Y
                case 0x16: return 0x18; // U
                case 0x17: return 0x0C; // I
                case 0x18: return 0x12; // O
                case 0x19: return 0x13; // P
                case 0x1A: return 0x2F; // [
                case 0x1B: return 0x30; // ]
                case 0x1C: return 0x28; // Enter
                case 0x1E: return 0x04; // A
                case 0x1F: return 0x16; // S
                case 0x20: return 0x07; // D
                case 0x21: return 0x09; // F
                case 0x22: return 0x0A; // G
                case 0x23: return 0x0B; // H
                case 0x24: return 0x0D; // J
                case 0x25: return 0x0E; // K
                case 0x26: return 0x0F; // L
                case 0x27: return 0x33; // ;
                case 0x28: return 0x34; // '
                case 0x29: return 0x35; // `
                case 0x2A: return 0x00; // Left Shift  (modifier)
                case 0x2B: return 0x31; // backslash
                case 0x2C: return 0x1D; // Z
                case 0x2D: return 0x1B; // X
                case 0x2E: return 0x06; // C
                case 0x2F: return 0x19; // V
                case 0x30: return 0x05; // B
                case 0x31: return 0x11; // N
                case 0x32: return 0x10; // M
                case 0x33: return 0x36; // ,
                case 0x34: return 0x37; // .
                case 0x35: return 0x38; // /
                case 0x36: return 0x00; // Right Shift (modifier)
                case 0x37: return 0x55; // Numpad *
                case 0x38: return 0x00; // Left Alt    (modifier)
                case 0x39: return 0x2C; // Space
                case 0x3A: return 0x39; // Caps Lock
                case 0x3B: return 0x3A; // F1
                case 0x3C: return 0x3B; // F2
                case 0x3D: return 0x3C; // F3
                case 0x3E: return 0x3D; // F4
                case 0x3F: return 0x3E; // F5
                case 0x40: return 0x3F; // F6
                case 0x41: return 0x40; // F7
                case 0x42: return 0x41; // F8
                case 0x43: return 0x42; // F9
                case 0x44: return 0x43; // F10
                case 0x45: return 0x53; // Num Lock
                case 0x46: return 0x47; // Scroll Lock
                case 0x47: return 0x5F; // Numpad 7
                case 0x48: return 0x60; // Numpad 8
                case 0x49: return 0x61; // Numpad 9
                case 0x4A: return 0x56; // Numpad -
                case 0x4B: return 0x5C; // Numpad 4
                case 0x4C: return 0x5D; // Numpad 5
                case 0x4D: return 0x5E; // Numpad 6
                case 0x4E: return 0x57; // Numpad +
                case 0x4F: return 0x59; // Numpad 1
                case 0x50: return 0x5A; // Numpad 2
                case 0x51: return 0x5B; // Numpad 3
                case 0x52: return 0x62; // Numpad 0
                case 0x53: return 0x63; // Numpad .
                case 0x57: return 0x44; // F11
                case 0x58: return 0x45; // F12
                default: return 0;
            }
        }
    }

    /// <summary>
    /// 6-key rollover keyboard state, mirroring MacKeycodeTranslator's pressedHidKeys bookkeeping
    /// and the 8-byte HID report layout consumed by the Android agent.
    /// </summary>
    public sealed class HidKeyState
    {
        private readonly object _gate = new object();
        private readonly byte[] _pressed = new byte[6];
        private byte _modifiers;

        public byte Modifiers => _modifiers;
        /// <summary>Number of non-zero usages currently held (NOT the array capacity).</summary>
        public int PressedCount
        {
            get
            {
                lock (_gate)
                {
                    int count = 0;
                    for (int i = 0; i < _pressed.Length; i++)
                    {
                        if (_pressed[i] != 0) count++;
                    }
                    return count;
                }
            }
        }

        public void SetModifier(byte bit, bool down)
        {
            lock (_gate)
            {
                if (down) _modifiers |= bit;
                else _modifiers = (byte)(_modifiers & ~bit);
            }
        }

        /// <summary>Adds a usage; evicts the oldest when 6 keys are held (same policy as macOS).</summary>
        public void AddUsage(byte usage)
        {
            if (usage == 0) return;
            lock (_gate)
            {
                for (int i = 0; i < _pressed.Length; i++)
                {
                    if (_pressed[i] == usage) return;
                }
                for (int i = 0; i < _pressed.Length; i++)
                {
                    if (_pressed[i] == 0)
                    {
                        _pressed[i] = usage;
                        return;
                    }
                }
                // Full: shift left and drop the oldest.
                for (int i = 0; i < _pressed.Length - 1; i++)
                {
                    _pressed[i] = _pressed[i + 1];
                }
                _pressed[^1] = usage;
            }
        }

        public void RemoveUsage(byte usage)
        {
            if (usage == 0) return;
            lock (_gate)
            {
                for (int i = 0; i < _pressed.Length; i++)
                {
                    if (_pressed[i] == usage) _pressed[i] = 0;
                }
            }
        }

        public byte[] BuildReport()
        {
            lock (_gate)
            {
                return SeamFrames.KeyReport(
                    _modifiers,
                    _pressed[0], _pressed[1], _pressed[2], _pressed[3], _pressed[4], _pressed[5]);
            }
        }

        /// <summary>Releases everything — must be called on panic return so Android never sees a stuck key.</summary>
        public byte[] Reset()
        {
            lock (_gate)
            {
                Array.Clear(_pressed, 0, _pressed.Length);
                _modifiers = 0;
                return SeamFrames.KeyReport(0, 0, 0, 0, 0, 0, 0);
            }
        }
    }
}
