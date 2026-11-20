use crate::input::x11::ffi::*;
use std::sync::atomic::{AtomicBool, Ordering};

pub struct X11CursorController {
    display: *mut Display,
    root: Window,
    blank_cursor: Cursor,
    is_hidden: AtomicBool,
}

impl X11CursorController {
    /// Creates a cursor controller for an open X11 display.
    ///
    /// # Safety
    ///
    /// `display` must be a valid pointer returned by `XOpenDisplay` that remains
    /// open for the lifetime of this controller, and `root` must be a window on
    /// that display.
    pub unsafe fn new(display: *mut Display, root: Window) -> Self {
        let blank_cursor = Self::create_blank_cursor(display, root);
        Self {
            display,
            root,
            blank_cursor,
            is_hidden: AtomicBool::new(false),
        }
    }

    pub fn blank_cursor(&self) -> Cursor {
        self.blank_cursor
    }

    pub fn hide(&self) {
        if !self.is_hidden.swap(true, Ordering::SeqCst) {
            unsafe {
                XFixesHideCursor(self.display, self.root);
                XFlush(self.display);
            }
        }
    }

    pub fn show(&self) {
        if self.is_hidden.swap(false, Ordering::SeqCst) {
            unsafe {
                XFixesShowCursor(self.display, self.root);
                XFlush(self.display);
            }
        }
    }

    pub fn warp(&self, x: i32, y: i32) {
        unsafe {
            XWarpPointer(
                self.display,
                0, // None
                self.root,
                0,
                0,
                0,
                0,
                x,
                y,
            );
            XFlush(self.display);
        }
    }

    unsafe fn create_blank_cursor(display: *mut Display, root: Window) -> Cursor {
        let data: [i8; 1] = [0];
        let pixmap = XCreateBitmapFromData(display, root, data.as_ptr(), 1, 1);
        let mut color = XColor::default();
        let cursor = XCreatePixmapCursor(display, pixmap, pixmap, &mut color, &mut color, 0, 0);
        XFreePixmap(display, pixmap);
        cursor
    }
}

impl Drop for X11CursorController {
    fn drop(&mut self) {
        self.show();
        if self.blank_cursor != 0 {
            unsafe {
                XFreeCursor(self.display, self.blank_cursor);
            }
        }
    }
}
