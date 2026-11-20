pub use std::ffi::{c_char, c_int, c_uint, c_ulong, c_void};

pub type Display = c_void;
pub type Window = c_ulong;
pub type Drawable = c_ulong;
pub type Pixmap = c_ulong;
pub type Cursor = c_ulong;
pub type Colormap = c_ulong;
pub type Time = c_ulong;
pub type KeySym = c_ulong;
pub type KeyCode = u8;
pub type Atom = c_ulong;
pub type Bool = c_int;

pub const TRUE: Bool = 1;
pub const FALSE: Bool = 0;
pub const CURRENT_TIME: Time = 0;

pub const GRAB_SUCCESS: c_int = 0;
pub const GRAB_MODE_SYNC: c_int = 0;
pub const GRAB_MODE_ASYNC: c_int = 1;

pub const KEY_PRESS_MASK: c_ulong = 1 << 0;
pub const KEY_RELEASE_MASK: c_ulong = 1 << 1;
pub const BUTTON_PRESS_MASK: c_ulong = 1 << 2;
pub const BUTTON_RELEASE_MASK: c_ulong = 1 << 3;
pub const POINTER_MOTION_MASK: c_ulong = 1 << 6;

pub const KEY_PRESS: c_int = 2;
pub const KEY_RELEASE: c_int = 3;
pub const BUTTON_PRESS: c_int = 4;
pub const BUTTON_RELEASE: c_int = 5;
pub const MOTION_NOTIFY: c_int = 6;

#[repr(C)]
#[derive(Debug, Clone, Copy, Default)]
pub struct XColor {
    pub pixel: c_ulong,
    pub red: u16,
    pub green: u16,
    pub blue: u16,
    pub flags: c_char,
    pub pad: c_char,
}

#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct XKeyEvent {
    pub type_: c_int,
    pub serial: c_ulong,
    pub send_event: Bool,
    pub display: *mut Display,
    pub window: Window,
    pub root: Window,
    pub subwindow: Window,
    pub time: Time,
    pub x: c_int,
    pub y: c_int,
    pub x_root: c_int,
    pub y_root: c_int,
    pub state: c_uint,
    pub keycode: c_uint,
    pub same_screen: Bool,
}

#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct XButtonEvent {
    pub type_: c_int,
    pub serial: c_ulong,
    pub send_event: Bool,
    pub display: *mut Display,
    pub window: Window,
    pub root: Window,
    pub subwindow: Window,
    pub time: Time,
    pub x: c_int,
    pub y: c_int,
    pub x_root: c_int,
    pub y_root: c_int,
    pub state: c_uint,
    pub button: c_uint,
    pub same_screen: Bool,
}

#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct XMotionEvent {
    pub type_: c_int,
    pub serial: c_ulong,
    pub send_event: Bool,
    pub display: *mut Display,
    pub window: Window,
    pub root: Window,
    pub subwindow: Window,
    pub time: Time,
    pub x: c_int,
    pub y: c_int,
    pub x_root: c_int,
    pub y_root: c_int,
    pub state: c_uint,
    pub is_hint: c_char,
    pub same_screen: Bool,
}

#[repr(C)]
pub union XEvent {
    pub type_: c_int,
    pub xkey: XKeyEvent,
    pub xbutton: XButtonEvent,
    pub xmotion: XMotionEvent,
    pub pad: [c_long; 24],
}

#[allow(non_camel_case_types)]
pub type c_long = i64;

unsafe extern "C" {
    pub fn XOpenDisplay(display_name: *const c_char) -> *mut Display;
    pub fn XCloseDisplay(display: *mut Display) -> c_int;
    pub fn XDefaultScreen(display: *mut Display) -> c_int;
    pub fn XRootWindow(display: *mut Display, screen_number: c_int) -> Window;
    pub fn XDisplayWidth(display: *mut Display, screen_number: c_int) -> c_int;
    pub fn XDisplayHeight(display: *mut Display, screen_number: c_int) -> c_int;
    pub fn XFlush(display: *mut Display) -> c_int;
    pub fn XSync(display: *mut Display, discard: Bool) -> c_int;

    pub fn XQueryPointer(
        display: *mut Display,
        w: Window,
        root_return: *mut Window,
        child_return: *mut Window,
        root_x_return: *mut c_int,
        root_y_return: *mut c_int,
        win_x_return: *mut c_int,
        win_y_return: *mut c_int,
        mask_return: *mut c_uint,
    ) -> Bool;

    pub fn XWarpPointer(
        display: *mut Display,
        src_w: Window,
        dest_w: Window,
        src_x: c_int,
        src_y: c_int,
        src_width: c_uint,
        src_height: c_uint,
        dest_x: c_int,
        dest_y: c_int,
    ) -> c_int;

    pub fn XGrabPointer(
        display: *mut Display,
        grab_window: Window,
        owner_events: Bool,
        event_mask: c_uint,
        pointer_mode: c_int,
        keyboard_mode: c_int,
        confine_to: Window,
        cursor: Cursor,
        time: Time,
    ) -> c_int;

    pub fn XUngrabPointer(display: *mut Display, time: Time) -> c_int;

    pub fn XGrabKeyboard(
        display: *mut Display,
        grab_window: Window,
        owner_events: Bool,
        pointer_mode: c_int,
        keyboard_mode: c_int,
        time: Time,
    ) -> c_int;

    pub fn XUngrabKeyboard(display: *mut Display, time: Time) -> c_int;

    pub fn XCreateBitmapFromData(
        display: *mut Display,
        d: Drawable,
        data: *const c_char,
        width: c_uint,
        height: c_uint,
    ) -> Pixmap;

    pub fn XCreatePixmapCursor(
        display: *mut Display,
        source: Pixmap,
        mask: Pixmap,
        foreground_color: *mut XColor,
        background_color: *mut XColor,
        x: c_uint,
        y: c_uint,
    ) -> Cursor;

    pub fn XFreePixmap(display: *mut Display, pixmap: Pixmap) -> c_int;
    pub fn XFreeCursor(display: *mut Display, cursor: Cursor) -> c_int;

    pub fn XNextEvent(display: *mut Display, event_return: *mut XEvent) -> c_int;
    pub fn XPending(display: *mut Display) -> c_int;

    pub fn XKeycodeToKeysym(display: *mut Display, keycode: KeyCode, index: c_int) -> KeySym;

    pub fn XFixesHideCursor(display: *mut Display, window: Window);
    pub fn XFixesShowCursor(display: *mut Display, window: Window);
}
