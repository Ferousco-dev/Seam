use crate::input::x11::ffi::*;
use std::ffi::c_int;
use std::ptr;

pub struct X11DisplaySession {
    display: *mut Display,
    screen: c_int,
    root: Window,
    width: i32,
    height: i32,
}

impl X11DisplaySession {
    pub fn open() -> Option<Self> {
        unsafe {
            let display = XOpenDisplay(ptr::null());
            if display.is_null() {
                return None;
            }
            let screen = XDefaultScreen(display);
            let root = XRootWindow(display, screen);
            let width = XDisplayWidth(display, screen) as i32;
            let height = XDisplayHeight(display, screen) as i32;

            Some(Self {
                display,
                screen,
                root,
                width,
                height,
            })
        }
    }

    pub fn display_ptr(&self) -> *mut Display {
        self.display
    }

    pub fn screen_num(&self) -> c_int {
        self.screen
    }

    pub fn root_window(&self) -> Window {
        self.root
    }

    pub fn dimensions(&self) -> (i32, i32) {
        (self.width, self.height)
    }

    pub fn query_pointer(&self) -> Option<(i32, i32)> {
        unsafe {
            let mut root_ret: Window = 0;
            let mut child_ret: Window = 0;
            let mut root_x: c_int = 0;
            let mut root_y: c_int = 0;
            let mut win_x: c_int = 0;
            let mut win_y: c_int = 0;
            let mut mask: u32 = 0;

            let res = XQueryPointer(
                self.display,
                self.root,
                &mut root_ret,
                &mut child_ret,
                &mut root_x,
                &mut root_y,
                &mut win_x,
                &mut win_y,
                &mut mask,
            );
            if res != 0 {
                Some((root_x, root_y))
            } else {
                None
            }
        }
    }
}

impl Drop for X11DisplaySession {
    fn drop(&mut self) {
        if !self.display.is_null() {
            unsafe {
                XCloseDisplay(self.display);
            }
        }
    }
}
