use seam_linux_host::input::edge::SpatialEdgeEngine;
use seam_linux_host::input::x11::keysym::{
    keysym_to_hid, modifier_bit_for_keysym, KeyStateTracker, HID_USAGE_ESCAPE, MOD_LEFT_ALT,
    MOD_LEFT_CTRL, MOD_LEFT_META, MOD_LEFT_SHIFT,
};
use seam_linux_host::protocol::wire::EdgeSide;
use seam_linux_host::transport::multiplexer::SeamTransportMultiplexer;
use std::sync::Arc;

#[test]
fn test_keysym_to_hid_mappings() {
    // Letters
    assert_eq!(keysym_to_hid(0x0061), 0x04); // 'a'
    assert_eq!(keysym_to_hid(0x007a), 0x1d); // 'z'
    assert_eq!(keysym_to_hid(0x0041), 0x04); // 'A'
    assert_eq!(keysym_to_hid(0x005a), 0x1d); // 'Z'

    // Numbers
    assert_eq!(keysym_to_hid(0x0031), 0x1e); // '1'
    assert_eq!(keysym_to_hid(0x0039), 0x26); // '9'
    assert_eq!(keysym_to_hid(0x0030), 0x27); // '0'

    // Standard controls
    assert_eq!(keysym_to_hid(0xff0d), 0x28); // Return
    assert_eq!(keysym_to_hid(0xff1b), HID_USAGE_ESCAPE); // Escape
    assert_eq!(keysym_to_hid(0xff08), 0x2a); // Backspace
    assert_eq!(keysym_to_hid(0xff09), 0x2b); // Tab
    assert_eq!(keysym_to_hid(0x0020), 0x2c); // Space

    // Navigation
    assert_eq!(keysym_to_hid(0xff51), 0x50); // Left
    assert_eq!(keysym_to_hid(0xff52), 0x52); // Up
    assert_eq!(keysym_to_hid(0xff53), 0x4f); // Right
    assert_eq!(keysym_to_hid(0xff54), 0x51); // Down

    // F-Keys
    assert_eq!(keysym_to_hid(0xffbe), 0x3a); // F1
    assert_eq!(keysym_to_hid(0xffc9), 0x45); // F12
}

#[test]
fn test_modifier_bits() {
    assert_eq!(modifier_bit_for_keysym(0xffe1), MOD_LEFT_SHIFT);
    assert_eq!(modifier_bit_for_keysym(0xffe3), MOD_LEFT_CTRL);
    assert_eq!(modifier_bit_for_keysym(0xffe9), MOD_LEFT_ALT);
    assert_eq!(modifier_bit_for_keysym(0xffeb), MOD_LEFT_META);
}

#[test]
fn test_6kro_state_tracking() {
    let mut tracker = KeyStateTracker::new();

    // Key down 'a'
    tracker.handle_key_down(0x0061);
    assert_eq!(tracker.build_6kro_report(), [0x04, 0, 0, 0, 0, 0]);

    // Key down 'b', 'c', 'd'
    tracker.handle_key_down(0x0062);
    tracker.handle_key_down(0x0063);
    tracker.handle_key_down(0x0064);
    assert_eq!(tracker.build_6kro_report(), [0x04, 0x05, 0x06, 0x07, 0, 0]);

    // Key down Shift_L -> should update modifiers, not 6KRO keys
    tracker.handle_key_down(0xffe1);
    assert_eq!(tracker.current_modifiers(), MOD_LEFT_SHIFT);
    assert_eq!(tracker.build_6kro_report(), [0x04, 0x05, 0x06, 0x07, 0, 0]);

    // Release 'b'
    tracker.handle_key_up(0x0062);
    assert_eq!(tracker.build_6kro_report(), [0x04, 0x06, 0x07, 0, 0, 0]);

    // Release Shift_L
    tracker.handle_key_up(0xffe1);
    assert_eq!(tracker.current_modifiers(), 0);

    // Eviction test: add 5 more keys so total exceeds 6
    tracker.handle_key_down(0x0065); // 'e' -> 0x08
    tracker.handle_key_down(0x0066); // 'f' -> 0x09
    tracker.handle_key_down(0x0067); // 'g' -> 0x0a
    tracker.handle_key_down(0x0068); // 'h' -> 0x0b (should evict oldest 'a' 0x04)

    let report = tracker.build_6kro_report();
    assert_eq!(report.len(), 6);
    assert!(!report.contains(&0x04)); // 'a' was evicted
    assert!(report.contains(&0x0b)); // 'h' is present
}

#[test]
fn test_edge_crossing_logic() {
    let mux = Arc::new(SeamTransportMultiplexer::new());
    let mut engine = SpatialEdgeEngine::new(mux);
    engine.set_active_edge(EdgeSide::Right);

    let width = 1920;
    let height = 1080;

    // Center of screen -> no crossing
    assert!(!engine.evaluate_crossing(960, 540, 10.0, 0.0, width, height));

    // Near right edge (1918), but top panel (y = 10) -> no crossing (safe band check)
    assert!(!engine.evaluate_crossing(1918, 10, 5.0, 0.0, width, height));

    // Near right edge (1918), safe band (y = 500), pushing right (+1.5) -> accumulator starts
    assert!(!engine.evaluate_crossing(1918, 500, 1.5, 0.0, width, height));

    // Push again (+1.0) -> accumulator reaches 2.5 >= 2.0 -> triggers crossing!
    assert!(engine.evaluate_crossing(1918, 500, 1.0, 0.0, width, height));
}

#[test]
fn test_edge_crossing_slow_push_pinned_at_wall() {
    let mux = Arc::new(SeamTransportMultiplexer::new());
    let mut engine = SpatialEdgeEngine::new(mux);
    engine.set_active_edge(EdgeSide::Right);

    let (width, height) = (1920, 1080);

    // A slow 1 px step lands exactly on the last pixel: not enough on its own.
    assert!(!engine.evaluate_crossing(width - 1, 500, 1.0, 0.0, width, height));

    // X11 clamps the pointer at the bezel, so every following poll reports
    // dx = 0. The dwell credit must still carry the accumulator to threshold.
    let mut crossed = false;
    for _ in 0..8 {
        if engine.evaluate_crossing(width - 1, 500, 0.0, 0.0, width, height) {
            crossed = true;
            break;
        }
    }
    assert!(
        crossed,
        "pointer pinned against the right bezel should cross"
    );
}

#[test]
fn test_edge_crossing_inward_motion_resets_push() {
    let mux = Arc::new(SeamTransportMultiplexer::new());
    let mut engine = SpatialEdgeEngine::new(mux);
    engine.set_active_edge(EdgeSide::Right);

    let (width, height) = (1920, 1080);

    // Partial outward push, then retreat toward the centre.
    assert!(!engine.evaluate_crossing(width - 1, 500, 1.0, 0.0, width, height));
    assert!(!engine.evaluate_crossing(width - 4, 500, -3.0, 0.0, width, height));

    // A fresh 1 px push must not inherit the earlier credit.
    assert!(!engine.evaluate_crossing(width - 1, 500, 1.0, 0.0, width, height));
}
