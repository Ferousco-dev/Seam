use crate::input::x11::ffi::KeySym;

pub const MOD_LEFT_CTRL: u8 = 1 << 0;
pub const MOD_LEFT_SHIFT: u8 = 1 << 1;
pub const MOD_LEFT_ALT: u8 = 1 << 2;
pub const MOD_LEFT_META: u8 = 1 << 3;
pub const MOD_RIGHT_CTRL: u8 = 1 << 4;
pub const MOD_RIGHT_SHIFT: u8 = 1 << 5;
pub const MOD_RIGHT_ALT: u8 = 1 << 6;
pub const MOD_RIGHT_META: u8 = 1 << 7;

pub const HID_USAGE_NONE: u8 = 0x00;
pub const HID_USAGE_ESCAPE: u8 = 0x29;

pub fn is_modifier_keysym(keysym: KeySym) -> bool {
    matches!(
        keysym,
        0xffe1 | 0xffe2 | // Shift_L, Shift_R
        0xffe3 | 0xffe4 | // Control_L, Control_R
        0xffe9 | 0xffea | // Alt_L, Alt_R
        0xffeb | 0xffec // Super_L, Super_R
    )
}

pub fn modifier_bit_for_keysym(keysym: KeySym) -> u8 {
    match keysym {
        0xffe1 => MOD_LEFT_SHIFT,
        0xffe2 => MOD_RIGHT_SHIFT,
        0xffe3 => MOD_LEFT_CTRL,
        0xffe4 => MOD_RIGHT_CTRL,
        0xffe9 => MOD_LEFT_ALT,
        0xffea => MOD_RIGHT_ALT,
        0xffeb => MOD_LEFT_META,
        0xffec => MOD_RIGHT_META,
        _ => 0,
    }
}

pub fn keysym_to_hid(keysym: KeySym) -> u8 {
    // Standard Latin letters
    if (0x0061..=0x007a).contains(&keysym) {
        // 'a' .. 'z'
        return 0x04 + (keysym - 0x0061) as u8;
    }
    if (0x0041..=0x005a).contains(&keysym) {
        // 'A' .. 'Z'
        return 0x04 + (keysym - 0x0041) as u8;
    }

    // Numbers
    if (0x0031..=0x0039).contains(&keysym) {
        // '1' .. '9'
        return 0x1e + (keysym - 0x0031) as u8;
    }
    if keysym == 0x0030 {
        // '0'
        return 0x27;
    }

    match keysym {
        // Whitespace and editing
        0x0020 => 0x2c, // space
        0xff0d => 0x28, // Return
        0xff8d => 0x58, // Keypad Enter
        0xff08 => 0x2a, // BackSpace
        0xff09 => 0x2b, // Tab
        0xff1b => 0x29, // Escape
        0xffff => 0x4c, // Delete
        0xff63 => 0x49, // Insert
        0xff50 => 0x4a, // Home
        0xff57 => 0x4d, // End
        0xff55 => 0x4b, // Page_Up
        0xff56 => 0x4e, // Page_Down

        // Arrow navigation
        0xff51 => 0x50, // Left
        0xff52 => 0x52, // Up
        0xff53 => 0x4f, // Right
        0xff54 => 0x51, // Down

        // Function keys F1 - F12
        0xffbe => 0x3a,
        0xffbf => 0x3b,
        0xffc0 => 0x3c,
        0xffc1 => 0x3d,
        0xffc2 => 0x3e,
        0xffc3 => 0x3f,
        0xffc4 => 0x40,
        0xffc5 => 0x41,
        0xffc6 => 0x42,
        0xffc7 => 0x43,
        0xffc8 => 0x44,
        0xffc9 => 0x45,

        // Symbols and punctuation
        0x002d | 0x005f => 0x2d, // Minus, Underscore
        0x003d | 0x002b => 0x2e, // Equal, Plus
        0x005b | 0x007b => 0x2f, // BracketLeft, BraceLeft
        0x005d | 0x007d => 0x30, // BracketRight, BraceRight
        0x005c | 0x007c => 0x31, // Backslash, Bar
        0x003b | 0x003a => 0x33, // Semicolon, Colon
        0x0027 | 0x0022 => 0x34, // Apostrophe, Quote
        0x0060 | 0x007e => 0x35, // Grave, Tilde
        0x002c | 0x003c => 0x36, // Comma, Less
        0x002e | 0x003e => 0x37, // Period, Greater
        0x002f | 0x003f => 0x38, // Slash, Question

        // Lock keys
        0xffe5 => 0x39, // Caps_Lock
        0xff14 => 0x47, // Scroll_Lock
        0xff7f => 0x53, // Num_Lock

        _ => HID_USAGE_NONE,
    }
}

pub struct KeyStateTracker {
    pressed_keys: Vec<u8>,
    modifiers: u8,
}

impl KeyStateTracker {
    pub fn new() -> Self {
        Self {
            pressed_keys: Vec::with_capacity(6),
            modifiers: 0,
        }
    }

    pub fn reset(&mut self) {
        self.pressed_keys.clear();
        self.modifiers = 0;
    }

    pub fn handle_key_down(&mut self, keysym: KeySym) -> bool {
        if is_modifier_keysym(keysym) {
            let bit = modifier_bit_for_keysym(keysym);
            self.modifiers |= bit;
            return false;
        }

        let hid = keysym_to_hid(keysym);
        if hid == HID_USAGE_NONE {
            return false;
        }

        if !self.pressed_keys.contains(&hid) {
            if self.pressed_keys.len() >= 6 {
                self.pressed_keys.remove(0); // Evict oldest key for 6KRO
            }
            self.pressed_keys.push(hid);
        }
        true
    }

    pub fn handle_key_up(&mut self, keysym: KeySym) {
        if is_modifier_keysym(keysym) {
            let bit = modifier_bit_for_keysym(keysym);
            self.modifiers &= !bit;
            return;
        }

        let hid = keysym_to_hid(keysym);
        self.pressed_keys.retain(|&k| k != hid);
    }

    pub fn is_panic_escape(&self, keysym: KeySym) -> bool {
        let ctrl = self.modifiers & (MOD_LEFT_CTRL | MOD_RIGHT_CTRL) != 0;
        let alt = self.modifiers & (MOD_LEFT_ALT | MOD_RIGHT_ALT) != 0;
        let shift = self.modifiers & (MOD_LEFT_SHIFT | MOD_RIGHT_SHIFT) != 0;

        // Match the macOS host's deliberate escape chords instead of hijacking
        // a bare Escape key, which must remain usable on Android.
        let escape_chord = keysym == 0xff1b && ctrl && alt && shift;
        let q_chord = (keysym == 0x0071 || keysym == 0x0051) && ctrl && alt;
        escape_chord || q_chord
    }

    pub fn current_modifiers(&self) -> u8 {
        self.modifiers
    }

    pub fn build_6kro_report(&self) -> [u8; 6] {
        let mut report = [0u8; 6];
        for (i, &key) in self.pressed_keys.iter().take(6).enumerate() {
            report[i] = key;
        }
        report
    }
}

impl Default for KeyStateTracker {
    fn default() -> Self {
        Self::new()
    }
}
