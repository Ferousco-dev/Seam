use crate::input::x11::display::X11DisplaySession;
use crate::protocol::auth::Authenticator;
use crate::protocol::crypto::compute_sha256;
use crate::transport::adb::{list_devices, locate_adb};
use std::process::Command;

pub struct DiagnosticReport {
    pub adb_available: bool,
    pub adb_version: String,
    pub device_count: usize,
    pub x11_available: bool,
    pub screen_dimensions: Option<(i32, i32)>,
    pub auth_key_configured: bool,
    pub clipboard_tool: Option<&'static str>,
    pub crypto_ok: bool,
}

pub fn run_self_test() -> DiagnosticReport {
    let adb_path = locate_adb();
    let (adb_available, adb_version) = match Command::new(&adb_path).arg("version").output() {
        Ok(out) => {
            let ver = String::from_utf8_lossy(&out.stdout)
                .lines()
                .next()
                .unwrap_or("")
                .to_string();
            (out.status.success(), ver)
        }
        Err(_) => (false, "Not found".to_string()),
    };

    let device_count = list_devices(&adb_path).map(|d| d.len()).unwrap_or(0);

    let (x11_available, screen_dimensions) = match X11DisplaySession::open() {
        Some(s) => (true, Some(s.dimensions())),
        None => (false, None),
    };

    let auth_key_configured = Authenticator::load_or_create().is_ok();

    let clipboard_tool = if Command::new("xclip").arg("-version").output().is_ok() {
        Some("xclip")
    } else if Command::new("xsel").arg("--version").output().is_ok() {
        Some("xsel")
    } else {
        None
    };

    let sha_test = compute_sha256(b"seam");
    let crypto_ok = sha_test != [0u8; 32];

    DiagnosticReport {
        adb_available,
        adb_version,
        device_count,
        x11_available,
        screen_dimensions,
        auth_key_configured,
        clipboard_tool,
        crypto_ok,
    }
}

pub fn print_diagnostics() {
    println!("============================================================");
    println!(" SEAM Linux Host — System Diagnostics");
    println!("============================================================");
    println!("Operating System     : Linux Mint / Cinnamon (X11)");

    let report = run_self_test();
    println!(
        "ADB Status           : {}",
        if report.adb_available {
            "Available"
        } else {
            "Missing"
        }
    );
    if report.adb_available {
        println!("  Version            : {}", report.adb_version);
        println!("  Connected Devices  : {}", report.device_count);
    }

    println!(
        "X11 Subsystem        : {}",
        if report.x11_available {
            "Connected"
        } else {
            "Unavailable (Check $DISPLAY)"
        }
    );
    if let Some((w, h)) = report.screen_dimensions {
        println!("  Display Resolution : {}x{} px", w, h);
    }

    println!(
        "Auth Key Config      : {}",
        if report.auth_key_configured {
            "Loaded (~/.seam/auth_key)"
        } else {
            "Failed"
        }
    );
    println!(
        "Clipboard Tool       : {}",
        report
            .clipboard_tool
            .unwrap_or("None (Install xclip or xsel)")
    );
    println!(
        "Crypto Acceleration  : {}",
        if report.crypto_ok {
            "Hardware (libcrypto)"
        } else {
            "Error"
        }
    );
    println!("============================================================");
}
