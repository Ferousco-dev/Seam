use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::Receiver;
use std::sync::Arc;
use std::thread;
use std::time::Duration;

use crate::input::x11::cursor::X11CursorController;
use crate::input::x11::display::X11DisplaySession;
use crate::input::x11::grab::X11Grabber;
use crate::protocol::frames::{EdgeReturnRecord, InboundFrame, OutboundFrame};
use crate::protocol::wire::{from_normalized_u16, to_normalized_u16, EdgeSide};
use crate::transport::multiplexer::SeamTransportMultiplexer;
use crate::transport::{now_monotonic_us, SendReliability};

/// Cumulative outward pointer movement (in pixels) required before a pointer
/// that reached a screen edge is considered to have crossed onto Android.
const EDGE_PUSH_THRESHOLD_PX: f64 = 2.0;

/// Outward pushes older than this are discarded so slowly drifting into an edge
/// while doing something else does not accumulate into an accidental crossing.
const EDGE_PUSH_WINDOW_US: u64 = 200_000;

/// Outward movement credited per poll while the pointer is pinned against the
/// screen boundary. X11 clamps the pointer at the bezel, so the physical push
/// past the edge arrives as a zero delta; without this the final "lean" into
/// the edge would never reach the threshold.
const EDGE_DWELL_CREDIT_PX: f64 = 0.55;

/// Cooldown after returning from Android, preventing the same gesture from
/// immediately re-entering Android.
const RETURN_COOLDOWN_US: u64 = 180_000;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CursorTarget {
    Linux,
    Android,
}

pub struct SpatialEdgeEngine {
    multiplexer: Arc<SeamTransportMultiplexer>,
    active_edge: EdgeSide,
    velocity_gain: f64,
    current_target: CursorTarget,
    outward_push: f64,
    last_outward_push_us: u64,
    last_return_us: u64,
    running: Arc<AtomicBool>,
}

impl SpatialEdgeEngine {
    pub fn new(multiplexer: Arc<SeamTransportMultiplexer>) -> Self {
        Self {
            multiplexer,
            active_edge: EdgeSide::Right,
            velocity_gain: 1.85,
            current_target: CursorTarget::Linux,
            outward_push: 0.0,
            last_outward_push_us: 0,
            last_return_us: 0,
            running: Arc::new(AtomicBool::new(false)),
        }
    }

    pub fn set_active_edge(&mut self, edge: EdgeSide) {
        self.active_edge = edge;
    }

    pub fn set_velocity_gain(&mut self, gain: f64) {
        self.velocity_gain = gain.clamp(0.5, 5.0);
    }

    pub fn current_target(&self) -> CursorTarget {
        self.current_target
    }

    pub fn stop(&self) {
        self.running.store(false, Ordering::SeqCst);
    }

    /// Maps a Linux edge crossing to the Android edge and normalized entry
    /// point. The cursor enters the Android screen on the side facing the
    /// Linux monitor, and `activeReturnEdge` on Android is set to that same
    /// edge so pushing back out returns the cursor to Linux.
    pub fn android_entry_point(
        host_edge: EdgeSide,
        x: i32,
        y: i32,
        width: i32,
        height: i32,
    ) -> (EdgeSide, u16, u16) {
        let norm_x = to_normalized_u16(x, width);
        let norm_y = to_normalized_u16(y, height);
        match host_edge {
            EdgeSide::Right => (EdgeSide::Left, 0, norm_y),
            EdgeSide::Left => (EdgeSide::Right, u16::MAX, norm_y),
            EdgeSide::Top => (EdgeSide::Bottom, norm_x, u16::MAX),
            EdgeSide::Bottom => (EdgeSide::Top, norm_x, 0),
        }
    }

    /// Maps an Android `EDGE_RETURN` back to the matching Linux screen edge.
    pub fn linux_return_point(
        android_edge: EdgeSide,
        norm_x: u16,
        norm_y: u16,
        width: i32,
        height: i32,
    ) -> (i32, i32) {
        let x = from_normalized_u16(norm_x, width);
        let y = from_normalized_u16(norm_y, height);
        let max_x = (width - 1).max(0);
        let max_y = (height - 1).max(0);
        match android_edge {
            // Cursor left the phone on its left edge, so it re-enters Linux
            // from the right edge.
            EdgeSide::Left => (max_x, y.clamp(0, max_y)),
            EdgeSide::Right => (0, y.clamp(0, max_y)),
            EdgeSide::Top => (x.clamp(0, max_x), max_y),
            EdgeSide::Bottom => (x.clamp(0, max_x), 0),
        }
    }

    /// Evaluates whether a cursor location at `(x, y)` should transition into
    /// Android. `dx`/`dy` are the deltas since the previous poll.
    pub fn evaluate_crossing(
        &mut self,
        x: i32,
        y: i32,
        dx: f64,
        dy: f64,
        width: i32,
        height: i32,
    ) -> bool {
        if self.current_target != CursorTarget::Linux {
            return false;
        }

        let now = now_monotonic_us();
        if self.last_return_us > 0 && now.saturating_sub(self.last_return_us) < RETURN_COOLDOWN_US {
            self.outward_push = 0.0;
            return false;
        }

        let in_safe_vertical = y > 24 && y < height - 16;
        let in_safe_horizontal = x > 16 && x < width - 16;

        let (in_zone, in_band, outward_delta) = match self.active_edge {
            EdgeSide::Right => (
                width - x <= 3,
                in_safe_vertical,
                if dx > 0.0 { dx } else { 0.0 },
            ),
            EdgeSide::Left => (x <= 3, in_safe_vertical, if dx < 0.0 { -dx } else { 0.0 }),
            EdgeSide::Bottom => (
                height - y <= 3,
                in_safe_horizontal,
                if dy > 0.0 { dy } else { 0.0 },
            ),
            EdgeSide::Top => (y <= 3, in_safe_horizontal, if dy < 0.0 { -dy } else { 0.0 }),
        };

        if !(in_zone && in_band) {
            self.outward_push = 0.0;
            return false;
        }

        let at_wall = match self.active_edge {
            EdgeSide::Right => x >= width - 1,
            EdgeSide::Left => x <= 0,
            EdgeSide::Bottom => y >= height - 1,
            EdgeSide::Top => y <= 0,
        };

        if outward_delta <= 0.0 {
            // Pinned against the bezel: X11 clamps the pointer, so there is no
            // further delta even though the user is still pushing outward.
            // Credit a small dwell amount so a slow push that lands exactly on
            // the wall still crosses. Any inward motion (or a pause away from
            // the wall) resets the accumulator.
            if at_wall {
                if now.saturating_sub(self.last_outward_push_us) > EDGE_PUSH_WINDOW_US {
                    self.outward_push = 0.0;
                }
                self.last_outward_push_us = now;
                self.outward_push += EDGE_DWELL_CREDIT_PX;
                if self.outward_push >= EDGE_PUSH_THRESHOLD_PX {
                    self.outward_push = 0.0;
                    return true;
                }
                return false;
            }
            // Moving back toward the screen centre invalidates any partial
            // outward push.
            let moving_inward = match self.active_edge {
                EdgeSide::Right => dx < 0.0,
                EdgeSide::Left => dx > 0.0,
                EdgeSide::Bottom => dy < 0.0,
                EdgeSide::Top => dy > 0.0,
            };
            if moving_inward {
                self.outward_push = 0.0;
                self.last_outward_push_us = 0;
            } else if now.saturating_sub(self.last_outward_push_us) > EDGE_PUSH_WINDOW_US {
                self.outward_push = 0.0;
            }
            return false;
        }

        if now.saturating_sub(self.last_outward_push_us) > EDGE_PUSH_WINDOW_US {
            self.outward_push = 0.0;
        }
        self.last_outward_push_us = now;
        self.outward_push += outward_delta;

        if self.outward_push >= EDGE_PUSH_THRESHOLD_PX {
            self.outward_push = 0.0;
            return true;
        }
        false
    }

    /// Runs the interactive input loop on the calling thread. Inbound frames
    /// (notably `EDGE_RETURN`) are drained from `inbound` on every iteration.
    pub fn run_session(&mut self, inbound: &Receiver<InboundFrame>) {
        let stop = AtomicBool::new(false);
        let panic = AtomicBool::new(false);
        self.run_session_with(inbound, &stop, &panic);
    }

    /// Runs the interactive input loop, observing external `stop` and `panic`
    /// flags so a UI can terminate the session or force a return to Linux.
    /// `panic` is consumed (reset) when handled.
    pub fn run_session_with(
        &mut self,
        inbound: &Receiver<InboundFrame>,
        stop: &AtomicBool,
        panic: &AtomicBool,
    ) {
        let display_session = match X11DisplaySession::open() {
            Some(s) => s,
            None => {
                eprintln!("[EdgeEngine] Could not open X11 display (is DISPLAY set?)");
                return;
            }
        };

        let (width, height) = display_session.dimensions();
        let cursor_ctrl = unsafe {
            X11CursorController::new(display_session.display_ptr(), display_session.root_window())
        };
        let mut grabber = X11Grabber::new(
            display_session.display_ptr(),
            display_session.root_window(),
            cursor_ctrl.blank_cursor(),
        );

        self.running.store(true, Ordering::SeqCst);
        let mut prev_loc = display_session
            .query_pointer()
            .unwrap_or((width / 2, height / 2));

        while self.running.load(Ordering::SeqCst) && !stop.load(Ordering::SeqCst) {
            if panic.swap(false, Ordering::SeqCst) && self.current_target == CursorTarget::Android {
                println!("[EdgeEngine] Panic return requested; returning to Linux");
                self.force_return_to_linux(&mut grabber, &cursor_ctrl, width, height);
                prev_loc = (width / 2, height / 2);
                continue;
            }

            self.drain_inbound(
                inbound,
                &mut grabber,
                &cursor_ctrl,
                width,
                height,
                &mut prev_loc,
            );

            match self.current_target {
                CursorTarget::Linux => {
                    if let Some((cur_x, cur_y)) = display_session.query_pointer() {
                        let dx = (cur_x - prev_loc.0) as f64;
                        let dy = (cur_y - prev_loc.1) as f64;
                        prev_loc = (cur_x, cur_y);

                        if self.evaluate_crossing(cur_x, cur_y, dx, dy, width, height) {
                            let (entry_edge, norm_x, norm_y) = Self::android_entry_point(
                                self.active_edge,
                                cur_x,
                                cur_y,
                                width,
                                height,
                            );
                            let enter_frame = OutboundFrame::CursorEnter {
                                edge: entry_edge,
                                norm_x,
                                norm_y,
                                flags: 0,
                            };
                            if let Err(err) = self
                                .multiplexer
                                .send(&enter_frame, SendReliability::ReliableOrdered)
                            {
                                eprintln!("[EdgeEngine] Could not send CURSOR_ENTER: {err}");
                                continue;
                            }

                            // Park at the centre so relative motion has room in
                            // every direction and the pointer never hits an X11
                            // screen boundary while captured.
                            let park = (width / 2, height / 2);
                            cursor_ctrl.warp(park.0, park.1);
                            if grabber.grab(park.0, park.1) {
                                cursor_ctrl.hide();
                                self.current_target = CursorTarget::Android;
                                prev_loc = park;
                                println!("[EdgeEngine] Switched input target -> Android (Remote)");
                            } else {
                                eprintln!(
                                    "[EdgeEngine] Could not grab X11 pointer/keyboard; staying on Linux"
                                );
                                // Leave the pointer where the user left it
                                // instead of stranding it at the park point.
                                cursor_ctrl.warp(cur_x, cur_y);
                                prev_loc = (cur_x, cur_y);
                                let _ = self.multiplexer.send(
                                    &OutboundFrame::CursorLeave { reason: 2 },
                                    SendReliability::ReliableOrdered,
                                );
                            }
                        }
                    }
                    thread::sleep(Duration::from_millis(8)); // ~120 Hz edge poll
                }
                CursorTarget::Android => {
                    let (frames, panic_escape) = grabber.process_events();

                    if panic_escape {
                        println!("[EdgeEngine] Panic escape chord pressed; returning to Linux");
                        self.force_return_to_linux(&mut grabber, &cursor_ctrl, width, height);
                        prev_loc = (width / 2, height / 2);
                        continue;
                    }

                    for frame in frames {
                        match frame {
                            OutboundFrame::MouseReport {
                                buttons,
                                dx,
                                dy,
                                v_wheel,
                                h_wheel,
                            } => {
                                let scaled_dx = ((dx as f64) * self.velocity_gain).round() as i16;
                                let scaled_dy = ((dy as f64) * self.velocity_gain).round() as i16;
                                let report = OutboundFrame::MouseReport {
                                    buttons,
                                    dx: scaled_dx,
                                    dy: scaled_dy,
                                    v_wheel,
                                    h_wheel,
                                };
                                let rel = if buttons == 0 && v_wheel == 0 && h_wheel == 0 {
                                    SendReliability::UnreliableSequenced
                                } else {
                                    SendReliability::ReliableOrdered
                                };
                                if let Err(err) = self.multiplexer.send(&report, rel) {
                                    eprintln!("[EdgeEngine] Mouse send failed: {err}");
                                }
                            }
                            other => {
                                if let Err(err) = self
                                    .multiplexer
                                    .send(&other, SendReliability::ReliableOrdered)
                                {
                                    eprintln!("[EdgeEngine] Input send failed: {err}");
                                }
                            }
                        }
                    }
                    thread::sleep(Duration::from_millis(2)); // high-rate input pump
                }
            }
        }

        if self.current_target == CursorTarget::Android {
            let _ = self.multiplexer.send(
                &OutboundFrame::CursorLeave { reason: 1 },
                SendReliability::ReliableOrdered,
            );
        }
        grabber.ungrab();
        cursor_ctrl.show();
    }

    fn drain_inbound(
        &mut self,
        inbound: &Receiver<InboundFrame>,
        grabber: &mut X11Grabber,
        cursor_ctrl: &X11CursorController,
        width: i32,
        height: i32,
        prev_loc: &mut (i32, i32),
    ) {
        while let Ok(frame) = inbound.try_recv() {
            if let InboundFrame::EdgeReturn(ret) = frame {
                if self.current_target == CursorTarget::Android {
                    self.return_to_linux(ret, grabber, cursor_ctrl, width, height, prev_loc);
                }
            }
            // DISPLAY_INFO and clipboard events are handled by the session
            // delegate; PONG only feeds RTT telemetry.
        }
    }

    fn return_to_linux(
        &mut self,
        ret: EdgeReturnRecord,
        grabber: &mut X11Grabber,
        cursor_ctrl: &X11CursorController,
        width: i32,
        height: i32,
        prev_loc: &mut (i32, i32),
    ) {
        let (x, y) = Self::linux_return_point(ret.edge, ret.norm_x, ret.norm_y, width, height);
        grabber.ungrab();
        cursor_ctrl.warp(x, y);
        cursor_ctrl.show();
        *prev_loc = (x, y);
        self.current_target = CursorTarget::Linux;
        self.last_return_us = now_monotonic_us();
        self.outward_push = 0.0;
        println!(
            "[EdgeEngine] Switched input target -> Linux (Local) at ({x}, {y}) [edge={:?}, velocity={}]",
            ret.edge, ret.velocity
        );
    }

    fn force_return_to_linux(
        &mut self,
        grabber: &mut X11Grabber,
        cursor_ctrl: &X11CursorController,
        width: i32,
        height: i32,
    ) {
        let _ = self.multiplexer.send(
            &OutboundFrame::CursorLeave { reason: 1 },
            SendReliability::ReliableOrdered,
        );
        grabber.ungrab();
        cursor_ctrl.warp(width / 2, height / 2);
        cursor_ctrl.show();
        self.current_target = CursorTarget::Linux;
        self.last_return_us = now_monotonic_us();
        self.outward_push = 0.0;
    }
}
