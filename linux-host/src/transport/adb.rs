use std::io;
use std::process::Command;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AdbDevice {
    pub serial: String,
    pub state: String,
    pub model: String,
    pub transport_id: String,
}

impl AdbDevice {
    pub fn is_ready(&self) -> bool {
        self.state == "device"
    }

    pub fn is_unauthorized(&self) -> bool {
        self.state == "unauthorized"
    }

    pub fn is_wireless(&self) -> bool {
        self.serial.contains(':') || self.serial.contains("._adb-tls-connect._tcp")
    }
}

pub fn locate_adb() -> String {
    if let Ok(path) = std::env::var("ADB") {
        if !path.is_empty() {
            return path;
        }
    }
    for candidate in &[
        "/usr/bin/adb",
        "/usr/local/bin/adb",
        "/opt/android-sdk/platform-tools/adb",
    ] {
        if std::path::Path::new(candidate).is_file() {
            return candidate.to_string();
        }
    }
    "adb".to_string()
}

pub fn run_adb_command(adb_bin: &str, args: &[&str]) -> io::Result<String> {
    let mut cmd = Command::new(adb_bin);
    cmd.args(args);

    // If $HOME is read-only (e.g. sandbox or restricted environment), provide a writable fallback
    let is_home_writable = std::env::var("HOME")
        .ok()
        .map(|h| std::fs::create_dir_all(format!("{}/.android", h)).is_ok())
        .unwrap_or(false);

    if !is_home_writable {
        cmd.env("HOME", "/tmp");
        cmd.env("ANDROID_USER_HOME", "/tmp/.android");
    }

    let output = cmd.output()?;
    if !output.status.success() {
        let err = String::from_utf8_lossy(&output.stderr);
        return Err(io::Error::other(format!(
            "ADB command failed (status={}): {}",
            output.status, err
        )));
    }
    Ok(String::from_utf8_lossy(&output.stdout).to_string())
}

pub fn list_devices(adb_bin: &str) -> io::Result<Vec<AdbDevice>> {
    let output = run_adb_command(adb_bin, &["devices", "-l"])?;
    let mut devices = Vec::new();

    for raw_line in output.lines() {
        let line = raw_line.trim();
        if line.is_empty() || line.starts_with("List of devices") || line.starts_with('*') {
            continue;
        }
        let tokens: Vec<&str> = line.split_whitespace().collect();
        if tokens.len() < 2 {
            continue;
        }
        let serial = tokens[0].to_string();
        let state = tokens[1].to_string();
        let mut model = "Android".to_string();
        let mut transport_id = "0".to_string();

        for token in &tokens[2..] {
            if let Some(m) = token.strip_prefix("model:") {
                model = m.to_string();
            } else if let Some(tid) = token.strip_prefix("transport_id:") {
                transport_id = tid.to_string();
            }
        }

        devices.push(AdbDevice {
            serial,
            state,
            model,
            transport_id,
        });
    }
    Ok(devices)
}

pub fn forward_socket(adb_bin: &str, serial: &str, abstract_socket_name: &str) -> io::Result<u16> {
    let target_abstract = format!("localabstract:{}", abstract_socket_name);
    let output = run_adb_command(
        adb_bin,
        &["-s", serial, "forward", "tcp:0", &target_abstract],
    )?;
    let port_str = output.trim();
    port_str.parse::<u16>().map_err(|e| {
        io::Error::new(
            io::ErrorKind::InvalidData,
            format!("Failed to parse forwarded port '{}': {}", port_str, e),
        )
    })
}

pub fn remove_forward(adb_bin: &str, serial: &str) -> io::Result<()> {
    let _ = run_adb_command(adb_bin, &["-s", serial, "forward", "--remove-all"]);
    Ok(())
}

pub fn query_mdns_services(adb_bin: &str) -> io::Result<Vec<String>> {
    let output = match run_adb_command(adb_bin, &["mdns", "services"]) {
        Ok(out) => out,
        Err(_) => return Ok(Vec::new()),
    };
    let mut addrs = Vec::new();
    for line in output.lines() {
        let trimmed = line.trim();
        if trimmed.contains("_adb-tls-connect._tcp") {
            let tokens: Vec<&str> = trimmed.split_whitespace().collect();
            if let Some(&addr) = tokens.last() {
                if addr.contains(':') && addr != "_adb-tls-connect._tcp" {
                    addrs.push(addr.to_string());
                }
            }
        }
    }
    Ok(addrs)
}
