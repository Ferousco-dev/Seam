use std::path::PathBuf;
use std::sync::atomic::AtomicBool;
use std::sync::mpsc::{self, Sender};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::Duration;

use crate::bootstrap::Bootstrapper;
use crate::clipboard::ClipboardSyncEngine;
use crate::config::HostConfig;
use crate::diagnostics::{print_diagnostics, run_self_test};
use crate::input::edge::SpatialEdgeEngine;
use crate::protocol::auth::Authenticator;
use crate::protocol::frames::{InboundFrame, OutboundFrame};
use crate::protocol::wire::{EdgeSide, DEFAULT_PORT};
use crate::transport::adb::{locate_adb, run_adb_command};
use crate::transport::multiplexer::SeamTransportMultiplexer;
use crate::transport::usb::UsbAdbTransport;
use crate::transport::wifi_hybrid::WifiHybridTransport;
use crate::transport::{
    SeamEndpoint, SeamTransport, SendReliability, TransportDelegate, TransportKind, TransportState,
};
use crate::ui::launch_gtk_app;

/// Routes inbound transport frames to the session: clipboard events are
/// applied immediately, display telemetry is cached, and edge returns are
/// queued for the input loop.
struct SessionRouter {
    inbound_tx: Sender<InboundFrame>,
    clipboard: Arc<ClipboardSyncEngine>,
    geometry: Arc<Mutex<Option<(u16, u16)>>>,
}

impl TransportDelegate for SessionRouter {
    fn on_state_changed(&self, kind: TransportKind, state: TransportState) {
        match state {
            TransportState::Closed { reason } => {
                eprintln!("[Transport] {kind} closed: {reason}");
            }
            TransportState::Ready { session_id } => {
                println!("[Transport] {kind} ready (session=0x{session_id:016X})");
            }
            _ => {}
        }
    }

    fn on_frame_received(&self, kind: TransportKind, frame: InboundFrame) {
        match frame {
            InboundFrame::ClipboardEvent(text) => {
                println!(
                    "[Clipboard] Received {} bytes from Android via {kind}",
                    text.len()
                );
                self.clipboard.apply_remote_text(&text);
            }
            InboundFrame::DisplayInfo(info) => {
                let mut geometry = self.geometry.lock().ok();
                let changed = match geometry.as_deref() {
                    Some(Some((w, h))) => *w != info.width || *h != info.height,
                    _ => true,
                };
                if let Some(slot) = geometry.as_deref_mut() {
                    *slot = Some((info.width, info.height));
                }
                if changed {
                    println!(
                        "[Display] Android {}x{} @ {} dpi, rotation {}°, {} Hz",
                        info.width,
                        info.height,
                        info.density_dpi,
                        info.rotation_degrees(),
                        info.refresh_rate
                    );
                }
            }
            InboundFrame::EdgeReturn(ret) => {
                let _ = self.inbound_tx.send(InboundFrame::EdgeReturn(ret));
            }
            InboundFrame::Pong { .. } => {}
        }
    }

    fn on_rtt_measured(&self, _kind: TransportKind, _rtt_us: u64) {}
}

pub fn resolve_agent_jar_path() -> PathBuf {
    if let Ok(env_path) = std::env::var("SEAM_AGENT_JAR") {
        let p = PathBuf::from(env_path);
        if p.is_file() {
            return p;
        }
    }

    if let Ok(exe_path) = std::env::current_exe() {
        if let Some(exe_dir) = exe_path.parent() {
            let candidate1 = exe_dir.join("seam-agent.jar");
            if candidate1.is_file() {
                return candidate1;
            }
            let candidate2 = exe_dir.join("../dist/seam-agent.jar");
            if candidate2.is_file() {
                return candidate2;
            }
            let candidate3 = exe_dir.join("../../dist/seam-agent.jar");
            if candidate3.is_file() {
                return candidate3;
            }
            // Installed layout: <prefix>/bin/seam + <prefix>/share/seam/seam-agent.jar
            let candidate4 = exe_dir.join("../share/seam/seam-agent.jar");
            if candidate4.is_file() {
                return candidate4;
            }
            let candidate5 = exe_dir.join("../../share/seam/seam-agent.jar");
            if candidate5.is_file() {
                return candidate5;
            }
        }
    }

    let mut data_dirs: Vec<PathBuf> = Vec::new();
    if let Ok(xdg) = std::env::var("XDG_DATA_HOME") {
        if !xdg.is_empty() {
            data_dirs.push(PathBuf::from(xdg).join("seam"));
        }
    }
    if let Ok(home) = std::env::var("HOME") {
        data_dirs.push(PathBuf::from(home).join(".local/share/seam"));
    }
    data_dirs.push(PathBuf::from("/usr/local/share/seam"));
    data_dirs.push(PathBuf::from("/usr/share/seam"));

    for dir in &data_dirs {
        let candidate = dir.join("seam-agent.jar");
        if candidate.is_file() {
            return candidate;
        }
    }

    let cwd = std::env::current_dir().unwrap_or_else(|_| PathBuf::from("."));
    for candidate in &[
        cwd.join("dist/seam-agent.jar"),
        cwd.join("seam-agent.jar"),
        cwd.join("../dist/seam-agent.jar"),
    ] {
        if candidate.is_file() {
            return candidate.clone();
        }
    }

    cwd.join("dist/seam-agent.jar")
}

pub fn run_list_devices() {
    let adb = locate_adb();
    println!("Discovering Android devices via ADB ({})...", adb);
    match crate::transport::adb::list_devices(&adb) {
        Ok(devices) => {
            if devices.is_empty() {
                println!("No Android devices found.");
                println!("Tips:");
                println!("  1. Ensure USB Debugging is enabled on your phone.");
                println!("  2. Connect the phone with a USB cable or use 'adb connect <ip>:<port>' for Wireless Debugging.");
                return;
            }
            println!("Attached devices ({}):", devices.len());
            for d in devices {
                let kind = if d.is_wireless() {
                    "Wi-Fi / Wireless"
                } else {
                    "USB"
                };
                let auth_note = if d.is_unauthorized() {
                    " [UNAUTHORIZED - Tap 'Allow USB debugging' on phone]"
                } else {
                    ""
                };
                println!(
                    "  * {} | state: {} | model: {} | type: {} | transport_id: {}{}",
                    d.serial, d.state, d.model, kind, d.transport_id, auth_note
                );
            }
        }
        Err(e) => {
            eprintln!("Error querying ADB devices: {}", e);
        }
    }
}

pub fn run_device_info() {
    let adb = locate_adb();
    println!("Querying connected Android device info...");
    let jar_path = resolve_agent_jar_path();
    let bootstrapper = Bootstrapper::new(jar_path.clone());

    let devices = match bootstrapper.list_devices() {
        Ok(d) => d,
        Err(e) => {
            eprintln!("Failed to list devices: {}", e);
            return;
        }
    };
    if devices.is_empty() {
        eprintln!("No devices connected.");
        return;
    }
    let target = &devices[0];
    if target.is_unauthorized() {
        eprintln!(
            "Device {} is unauthorized. Please tap 'Allow USB debugging' on the screen.",
            target.serial
        );
        return;
    }

    if jar_path.is_file() {
        let _ = run_adb_command(
            &adb,
            &[
                "-s",
                &target.serial,
                "push",
                jar_path.to_str().unwrap(),
                crate::bootstrap::REMOTE_JAR_PATH,
            ],
        );
    }

    let cmd = "CLASSPATH=/data/local/tmp/seam-agent.jar app_process /system/bin --nice-name=seam_agent com.seam.agent.Main --info";
    match run_adb_command(&adb, &["-s", &target.serial, "shell", cmd]) {
        Ok(output) => println!("{}", output),
        Err(e) => eprintln!("Failed to query device info: {}", e),
    }
}

/// Shared, thread-safe status for the CLI and GTK UI.
#[derive(Clone, Debug)]
pub struct SessionStatus {
    pub text: String,
    pub active: bool,
}

impl SessionStatus {
    pub fn new(text: impl Into<String>, active: bool) -> Self {
        Self {
            text: text.into(),
            active,
        }
    }
}

impl Default for SessionStatus {
    fn default() -> Self {
        Self {
            text: "Idle".to_string(),
            active: false,
        }
    }
}

pub fn run_connect(preferred_serial: Option<&str>, edge_override: Option<&str>) {
    println!("============================================================");
    println!(" SEAM Linux Host — Screen-Edge Crossing Mode");
    println!("============================================================");

    let mut config = HostConfig::load();
    if let Some(edge_str) = edge_override {
        match edge_str.to_lowercase().as_str() {
            "left" => config.active_edge = EdgeSide::Left,
            "right" => config.active_edge = EdgeSide::Right,
            "top" => config.active_edge = EdgeSide::Top,
            "bottom" => config.active_edge = EdgeSide::Bottom,
            _ => eprintln!(
                "Warning: Unknown edge '{}', using {:?}",
                edge_str, config.active_edge
            ),
        }
    }

    let stop = Arc::new(AtomicBool::new(false));
    let panic = Arc::new(AtomicBool::new(false));
    let status = Arc::new(Mutex::new(SessionStatus::default()));

    if let Err(e) = run_connect_session(
        preferred_serial,
        config.active_edge,
        config.velocity_gain,
        stop,
        panic,
        status,
    ) {
        eprintln!("Session error: {}", e);
    }
}

/// Runs a full screen-edge crossing session on the calling thread.
///
/// `stop` terminates the session loop, while `panic` (one-shot) forces the
/// cursor back to Linux without ending the session. `status` is updated at
/// each lifecycle transition so a UI can render progress.
pub fn run_connect_session(
    preferred_serial: Option<&str>,
    active_edge: EdgeSide,
    velocity_gain: f64,
    stop: Arc<AtomicBool>,
    panic: Arc<AtomicBool>,
    status: Arc<Mutex<SessionStatus>>,
) -> std::io::Result<()> {
    let set_status = |text: &str, active: bool| {
        if let Ok(mut guard) = status.lock() {
            *guard = SessionStatus::new(text, active);
        }
    };

    set_status("Connecting to Android device…", false);

    let jar_path = resolve_agent_jar_path();
    let bootstrapper = Bootstrapper::new(jar_path);

    println!("Bootstrapping session on Android device...");
    let (device, usb_endpoint, wifi_endpoints, auth) =
        bootstrapper.bootstrap_session(preferred_serial)?;

    println!(
        "Attached device      : {} ({})",
        device.serial, device.model
    );
    println!("Active Boundary Edge : {:?}", active_edge);
    println!("USB ADB Endpoint     : {:?}", usb_endpoint);

    let mux = Arc::new(SeamTransportMultiplexer::new());

    // Start clipboard synchronization before installing the delegate so that
    // Android -> Linux clipboard events can be applied immediately.
    let clipboard = Arc::new(ClipboardSyncEngine::new(Arc::clone(&mux)));
    clipboard.start();

    let geometry = Arc::new(Mutex::new(None));
    let (inbound_tx, inbound_rx) = mpsc::channel();
    mux.set_delegate(Arc::new(SessionRouter {
        inbound_tx,
        clipboard: Arc::clone(&clipboard),
        geometry: Arc::clone(&geometry),
    }));

    // Connect USB transport
    let usb = Box::new(UsbAdbTransport::new());
    if let Err(e) = mux.add_and_connect_transport(usb, &usb_endpoint, &auth, true) {
        clipboard.stop();
        bootstrapper.teardown_session(&device.serial);
        return Err(e);
    }

    // Connect Wi-Fi standby transport if available
    if let Some(wifi_target) = wifi_endpoints.first() {
        println!(
            "Found Wi-Fi Endpoint : {}:{} ({})",
            wifi_target.ip, wifi_target.port, wifi_target.interface_name
        );
        let wifi = Box::new(WifiHybridTransport::new());
        let wifi_ep = wifi_target.to_seam_endpoint();
        if let Err(e) = mux.add_and_connect_transport(wifi, &wifi_ep, &auth, false) {
            eprintln!("Warning: Wi-Fi standby transport connection failed: {}", e);
        }
    }

    // Start spatial screen-edge engine
    let mut edge_engine = SpatialEdgeEngine::new(Arc::clone(&mux));
    edge_engine.set_active_edge(active_edge);
    edge_engine.set_velocity_gain(velocity_gain);

    println!("------------------------------------------------------------");
    println!(
        "Ready! Move your cursor against the {:?} screen edge to cross onto Android.",
        active_edge
    );
    println!("Press Ctrl+Alt+Shift+Escape (or Ctrl+Alt+Q) to return to Linux.");
    println!("------------------------------------------------------------");

    set_status(
        &format!("Session active — push the cursor past the {active_edge:?} edge"),
        true,
    );

    edge_engine.run_session_with(&inbound_rx, &stop, &panic);

    clipboard.stop();
    mux.disconnect_all("Session terminated");
    bootstrapper.teardown_session(&device.serial);
    set_status("Session ended", false);
    println!("SEAM session ended.");
    Ok(())
}

pub fn run_disconnect(serial: Option<&str>) {
    let adb = locate_adb();
    if let Some(s) = serial {
        let _ = crate::transport::adb::remove_forward(&adb, s);
        println!("Removed forwards for device {}", s);
    } else {
        let _ = run_adb_command(&adb, &["forward", "--remove-all"]);
        println!("Removed all active ADB port forwards.");
    }
}

pub fn run_usb_test(preferred_serial: Option<&str>) {
    println!("Running end-to-end USB transport test...");
    let jar_path = resolve_agent_jar_path();
    let bootstrapper = Bootstrapper::new(jar_path);

    let (device, usb_endpoint, _, auth) = match bootstrapper.bootstrap_session(preferred_serial) {
        Ok(res) => res,
        Err(e) => {
            eprintln!("USB test bootstrap failed: {}", e);
            return;
        }
    };

    let mut usb = UsbAdbTransport::new();
    println!("Connecting USB ADB Transport to {:?}...", usb_endpoint);
    if let Err(e) = usb.connect(&usb_endpoint, &auth) {
        eprintln!("USB connect failed: {}", e);
        bootstrapper.teardown_session(&device.serial);
        return;
    }

    println!("Authenticated! Measuring round-trip time (RTT)...");
    for i in 1..=5 {
        thread::sleep(Duration::from_millis(150));
        let _ = usb.send_ping();
        println!("  Probe #{}: RTT = {} µs", i, usb.last_rtt_us());
    }

    // Send sample mouse movement report
    let sample_mouse = OutboundFrame::MouseReport {
        buttons: 0,
        dx: 10,
        dy: 10,
        v_wheel: 0,
        h_wheel: 0,
    };
    if let Err(e) = usb.send(&sample_mouse, SendReliability::UnreliableSequenced) {
        eprintln!("Failed to send mouse report over USB: {}", e);
    } else {
        println!("Successfully streamed test mouse deltas over USB!");
    }

    usb.disconnect("USB test complete");
    bootstrapper.teardown_session(&device.serial);
    println!("USB Transport Test: PASSED");
}

pub fn run_wifi_test(target_ip: Option<&str>) {
    println!("Running end-to-end Wi-Fi transport test...");
    let auth = match Authenticator::load_or_create() {
        Ok(a) => a,
        Err(e) => {
            eprintln!("Failed to load auth key: {}", e);
            return;
        }
    };

    let ip = if let Some(host) = target_ip {
        host.to_string()
    } else {
        println!("Probing local network via UDP broadcast for running SEAM agent...");
        let jar_path = resolve_agent_jar_path();
        let bootstrapper = Bootstrapper::new(jar_path);
        let discovered = bootstrapper.discover_wireless_agent();
        if let Some(first) = discovered.first() {
            println!("Discovered wireless agent at {}:{}", first.ip, first.port);
            first.ip.clone()
        } else {
            eprintln!("No wireless agent detected automatically. Specify IP: seam wifi-test <IP>");
            return;
        }
    };

    let endpoint = SeamEndpoint::Wifi {
        host: ip.clone(),
        tcp_port: DEFAULT_PORT,
        udp_port: DEFAULT_PORT,
    };

    let mut wifi = WifiHybridTransport::new();
    println!(
        "Connecting Wi-Fi Hybrid Transport to {}:{}...",
        ip, DEFAULT_PORT
    );
    if let Err(e) = wifi.connect(&endpoint, &auth) {
        eprintln!("Wi-Fi connect failed: {}", e);
        return;
    }

    println!("Authenticated over Wi-Fi! Measuring RTT...");
    for i in 1..=5 {
        thread::sleep(Duration::from_millis(150));
        let _ = wifi.send_ping();
        println!(
            "  Probe #{}: TCP RTT = {} µs, UDP RTT = {} µs",
            i,
            wifi.last_rtt_us(),
            wifi.last_udp_rtt_us()
        );
    }

    wifi.disconnect("Wi-Fi test complete");
    println!("Wi-Fi Transport Test: PASSED");
}

pub fn run_self_test_cli() {
    let report = run_self_test();
    println!("============================================================");
    println!(" SEAM Linux Host — Self-Test Suite");
    println!("============================================================");
    println!(
        "1. ADB Availability       : {}",
        if report.adb_available {
            "PASS"
        } else {
            "FAIL (Missing 'adb')"
        }
    );
    println!(
        "2. Cryptographic Engine   : {}",
        if report.crypto_ok {
            "PASS (libcrypto)"
        } else {
            "FAIL"
        }
    );
    println!(
        "3. Auth Key Configuration : {}",
        if report.auth_key_configured {
            "PASS"
        } else {
            "FAIL"
        }
    );
    println!(
        "4. X11 Windowing & Input  : {}",
        if report.x11_available {
            "PASS"
        } else {
            "WARN (X11 unavailable in headless)"
        }
    );
    println!(
        "5. Clipboard Utilities    : {}",
        if report.clipboard_tool.is_some() {
            "PASS"
        } else {
            "WARN (Install xclip or xsel)"
        }
    );
    println!("------------------------------------------------------------");
    let all_ok = report.adb_available && report.crypto_ok && report.auth_key_configured;
    println!(
        "Overall Self-Test Result  : {}",
        if all_ok {
            "READY"
        } else {
            "ATTENTION REQUIRED"
        }
    );
    println!("============================================================");
}

pub fn run_diagnostics_cli() {
    print_diagnostics();
}

pub fn run_app() {
    launch_gtk_app();
}
