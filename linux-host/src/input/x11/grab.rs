use crate::input::x11::ffi::*;
use crate::input::x11::keysym::KeyStateTracker;
use crate::protocol::frames::OutboundFrame;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;

pub struct X11Grabber {
    display: *mut Display,
    root: Window,
    blank_cursor: Cursor,
    is_grabbed: Arc<AtomicBool>,
    key_tracker: KeyStateTracker,
    held_buttons: u8,
    park_x: i32,
    park_y: i32,
    last_key_report: Option<(u8, [u8; 6])>,
}

impl X11Grabber {
    pub fn new(display: *mut Display, root: Window, blank_cursor: Cursor) -> Self {
        Self {
            display,
            root,
            blank_cursor,
            is_grabbed: Arc::new(AtomicBool::new(false)),
            key_tracker: KeyStateTracker::new(),
            held_buttons: 0,
            park_x: 0,
            park_y: 0,
            last_key_report: None,
        }
    }

    pub fn is_grabbed(&self) -> bool {
        self.is_grabbed.load(Ordering::Relaxed)
    }

    pub fn grab(&mut self, park_x: i32, park_y: i32) -> bool {
        self.park_x = park_x;
        self.park_y = park_y;
        self.key_tracker.reset();
        self.held_buttons = 0;
        self.last_key_report = None;

        let mask = (POINTER_MOTION_MASK | BUTTON_PRESS_MASK | BUTTON_RELEASE_MASK) as c_uint;

        unsafe {
            // Put the pointer at the parking point before grabbing so the
            // first motion event has a well-defined origin.
            XWarpPointer(
                self.display,
                0,
                self.root,
                0,
                0,
                0,
                0,
                park_x,
                park_y,
            );
            XSync(self.display, FALSE);

            let p_res = XGrabPointer(
                self.display,
                self.root,
                FALSE,
                mask,
                GRAB_MODE_ASYNC,
                GRAB_MODE_ASYNC,
                self.root,
                self.blank_cursor,
                CURRENT_TIME,
            );
            if p_res != GRAB_SUCCESS {
                return false;
            }

            let k_res = XGrabKeyboard(
                self.display,
                self.root,
                FALSE,
                GRAB_MODE_ASYNC,
                GRAB_MODE_ASYNC,
                CURRENT_TIME,
            );
            if k_res != GRAB_SUCCESS {
                XUngrabPointer(self.display, CURRENT_TIME);
                return false;
            }

            XFlush(self.display);
        }

        self.is_grabbed.store(true, Ordering::SeqCst);
        true
    }

    pub fn ungrab(&mut self) {
        if self.is_grabbed.swap(false, Ordering::SeqCst) {
            unsafe {
                XUngrabPointer(self.display, CURRENT_TIME);
                XUngrabKeyboard(self.display, CURRENT_TIME);
                XFlush(self.display);
            }
            self.key_tracker.reset();
            self.held_buttons = 0;
            self.last_key_report = None;
        }
    }

    /// Pumps pending X11 events while grabbed. Returns generated outbound frames or panic return signal.
    pub fn process_events(&mut self) -> (Vec<OutboundFrame>, bool) {
        let mut frames = Vec::new();
        let mut should_panic_return = false;

        unsafe {
            while XPending(self.display) > 0 {
                let mut event: XEvent = std::mem::zeroed();
                XNextEvent(self.display, &mut event);

                match event.type_ {
                    MOTION_NOTIFY => {
                        let motion = event.xmotion;
                        let dx = (motion.x_root - self.park_x) as i16;
                        let dy = (motion.y_root - self.park_y) as i16;

                        if dx != 0 || dy != 0 {
                            // Warp back to park point to prevent cursor reaching edge
                            XWarpPointer(
                                self.display,
                                0,
                                self.root,
                                0,
                                0,
                                0,
                                0,
                                self.park_x,
                                self.park_y,
                            );
                            XFlush(self.display);

                            frames.push(OutboundFrame::MouseReport {
                                buttons: self.held_buttons,
                                dx,
                                dy,
                                v_wheel: 0,
                                h_wheel: 0,
                            });
                        }
                    }
                    BUTTON_PRESS => {
                        let button = event.xbutton.button;
                        match button {
                            1 => self.held_buttons |= 0x01, // Left
                            2 => self.held_buttons |= 0x04, // Middle
                            3 => self.held_buttons |= 0x02, // Right (Android Back)
                            4 => {
                                // Vertical wheel UP
                                frames.push(OutboundFrame::MouseReport {
                                    buttons: self.held_buttons,
                                    dx: 0,
                                    dy: 0,
                                    v_wheel: 1,
                                    h_wheel: 0,
                                });
                                continue;
                            }
                            5 => {
                                // Vertical wheel DOWN
                                frames.push(OutboundFrame::MouseReport {
                                    buttons: self.held_buttons,
                                    dx: 0,
                                    dy: 0,
                                    v_wheel: -1,
                                    h_wheel: 0,
                                });
                                continue;
                            }
                            6 => {
                                // Horizontal pan LEFT
                                frames.push(OutboundFrame::MouseReport {
                                    buttons: self.held_buttons,
                                    dx: 0,
                                    dy: 0,
                                    v_wheel: 0,
                                    h_wheel: -1,
                                });
                                continue;
                            }
                            7 => {
                                // Horizontal pan RIGHT
                                frames.push(OutboundFrame::MouseReport {
                                    buttons: self.held_buttons,
                                    dx: 0,
                                    dy: 0,
                                    v_wheel: 0,
                                    h_wheel: 1,
                                });
                                continue;
                            }
                            8 => self.held_buttons |= 0x08, // Back button
                            9 => self.held_buttons |= 0x10, // Forward button
                            _ => {}
                        }
                        frames.push(OutboundFrame::MouseReport {
                            buttons: self.held_buttons,
                            dx: 0,
                            dy: 0,
                            v_wheel: 0,
                            h_wheel: 0,
                        });
                    }
                    BUTTON_RELEASE => {
                        let button = event.xbutton.button;
                        match button {
                            1 => self.held_buttons &= !0x01,
                            2 => self.held_buttons &= !0x04,
                            3 => self.held_buttons &= !0x02,
                            8 => self.held_buttons &= !0x08,
                            9 => self.held_buttons &= !0x10,
                            _ => {}
                        }
                        frames.push(OutboundFrame::MouseReport {
                            buttons: self.held_buttons,
                            dx: 0,
                            dy: 0,
                            v_wheel: 0,
                            h_wheel: 0,
                        });
                    }
                    KEY_PRESS => {
                        let keycode = event.xkey.keycode as KeyCode;
                        let keysym = XKeycodeToKeysym(self.display, keycode, 0);

                        // A deliberate chord returns control to Linux. A bare
                        // Escape is forwarded to Android like any other key.
                        if self.key_tracker.is_panic_escape(keysym) {
                            should_panic_return = true;
                            break;
                        }

                        self.key_tracker.handle_key_down(keysym);
                        self.push_key_report(&mut frames);
                    }
                    KEY_RELEASE => {
                        let keycode = event.xkey.keycode as KeyCode;
                        let keysym = XKeycodeToKeysym(self.display, keycode, 0);

                        self.key_tracker.handle_key_up(keysym);
                        self.push_key_report(&mut frames);
                    }
                    _ => {}
                }
            }
        }

        (frames, should_panic_return)
    }
    /// Emits a key report only when the modifier/key state actually changed.
    /// X11 auto-repeat delivers repeated `KEY_PRESS` events without matching
    /// releases; Android's input stack performs its own repeat, so forwarding
    /// duplicate reports is both wasteful and can confuse repeat handling.
    fn push_key_report(&mut self, frames: &mut Vec<OutboundFrame>) {
        let report = (
            self.key_tracker.current_modifiers(),
            self.key_tracker.build_6kro_report(),
        );
        if self.last_key_report.as_ref() == Some(&report) {
            return;
        }
        self.last_key_report = Some(report);
        frames.push(OutboundFrame::KeyReport {
            modifiers: report.0,
            keys: report.1,
        });
    }
}

impl Drop for X11Grabber {
    fn drop(&mut self) {
        self.ungrab();
    }
}
