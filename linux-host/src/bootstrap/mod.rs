use std::fs;
use std::io;
use std::net::{Ipv4Addr, SocketAddrV4, UdpSocket};
use std::path::PathBuf;
use std::thread;
use std::time::{Duration, Instant};

use crate::protocol::auth::Authenticator;
use crate::protocol::crypto::compute_sha256;
use crate::transport::adb::{
    forward_socket, list_devices, locate_adb, remove_forward, run_adb_command, AdbDevice,
};
use crate::transport::SeamEndpoint;

pub const REMOTE_JAR_PATH: &str = "/data/local/tmp/seam-agent.jar";
pub const REMOTE_LOG_PATH: &str = "/data/local/tmp/seam-agent.log";
pub const REMOTE_STAMP_PATH: &str = "/data/local/tmp/seam-agent.stamp";

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DiscoveredWifiEndpoint {
    pub interface_name: String,
    pub ip: String,
    pub port: u16,
}

impl DiscoveredWifiEndpoint {
    pub fn to_seam_endpoint(&self) -> SeamEndpoint {
        SeamEndpoint::Wifi {
            host: self.ip.clone(),
            tcp_port: self.port,
            udp_port: self.port,
        }
    }
}

pub struct Bootstrapper {
    adb_bin: String,
    agent_jar_path: PathBuf,
}

impl Bootstrapper {
    pub fn new(agent_jar_path: PathBuf) -> Self {
        Self {
            adb_bin: locate_adb(),
            agent_jar_path,
        }
    }

    pub fn with_adb_path(adb_bin: String, agent_jar_path: PathBuf) -> Self {
        Self {
            adb_bin,
            agent_jar_path,
        }
    }

    pub fn list_devices(&self) -> io::Result<Vec<AdbDevice>> {
        list_devices(&self.adb_bin)
    }

    pub fn bootstrap_session(
        &self,
        preferred_serial: Option<&str>,
    ) -> io::Result<(
        AdbDevice,
        SeamEndpoint,
        Vec<DiscoveredWifiEndpoint>,
        Authenticator,
    )> {
        let auth = Authenticator::load_or_create()?;
        let devices = self.list_devices()?;
        if devices.is_empty() {
            return Err(io::Error::new(
                io::ErrorKind::NotFound,
                "No Android devices detected via ADB. Connect your device via USB or Wireless Debugging.",
            ));
        }

        let device = if let Some(serial) = preferred_serial {
            devices
                .into_iter()
                .find(|d| d.serial == serial)
                .ok_or_else(|| {
                    io::Error::new(
                        io::ErrorKind::NotFound,
                        format!("Device with serial '{}' not found", serial),
                    )
                })?
        } else {
            devices.into_iter().next().unwrap()
        };

        if device.is_unauthorized() {
            return Err(io::Error::new(
                io::ErrorKind::PermissionDenied,
                format!(
                    "Device {} ({}) is unauthorized. Tap 'Allow USB debugging' on the Android screen.",
                    device.serial, device.model
                ),
            ));
        }
        if !device.is_ready() {
            return Err(io::Error::other(format!(
                "Device {} is in state '{}'",
                device.serial, device.state
            )));
        }

        if !self.agent_jar_path.exists() {
            return Err(io::Error::new(
                io::ErrorKind::NotFound,
                format!(
                    "SEAM agent archive not found at {:?}. Please ensure dist/seam-agent.jar is built.",
                    self.agent_jar_path
                ),
            ));
        }

        // 1. Ensure on-screen touch keyboard (Gboard) stays active with hard keyboard attached
        let _ = run_adb_command(
            &self.adb_bin,
            &[
                "-s",
                &device.serial,
                "shell",
                "settings put secure show_ime_with_hard_keyboard 1",
            ],
        );

        // 2. Compute stamp
        let expected_stamp = self.compute_stamp(auth.hex())?;

        // 3. Check if daemon is already running with exact matching stamp.
        // `pgrep -f seam_agent` is unusable here: the probe shell's own command
        // line contains the pattern, so it always matches and a stale stamp
        // makes the host skip launching a dead agent.
        let probe_cmd = format!(
            "(pidof seam_agent >/dev/null 2>&1 && cat {} 2>/dev/null) || true",
            REMOTE_STAMP_PATH
        );
        let running_stamp =
            run_adb_command(&self.adb_bin, &["-s", &device.serial, "shell", &probe_cmd])
                .unwrap_or_default()
                .trim()
                .to_string();

        let wifi_endpoints = if !expected_stamp.is_empty() && running_stamp == expected_stamp {
            if let Ok(endpoints) =
                self.wait_for_log_ready(&device.serial, Duration::from_millis(500))
            {
                endpoints
            } else {
                self.deploy_and_launch(&device.serial, auth.hex(), &expected_stamp)?
            }
        } else {
            self.deploy_and_launch(&device.serial, auth.hex(), &expected_stamp)?
        };

        // 4. Setup ADB forward: tcp:0 -> localabstract:seam_uhid
        let forwarded_port = forward_socket(
            &self.adb_bin,
            &device.serial,
            crate::protocol::wire::LOCAL_SOCKET,
        )?;

        let usb_endpoint = SeamEndpoint::UsbLocalhost {
            port: forwarded_port,
            serial: device.serial.clone(),
        };

        Ok((device, usb_endpoint, wifi_endpoints, auth))
    }

    pub fn teardown_session(&self, serial: &str) {
        let _ = remove_forward(&self.adb_bin, serial);
    }

    fn compute_stamp(&self, auth_key_hex: &str) -> io::Result<String> {
        let jar_bytes = fs::read(&self.agent_jar_path)?;
        let jar_hash = compute_sha256(&jar_bytes);
        let key_hash = compute_sha256(auth_key_hex.as_bytes());

        let jar_hex: String = jar_hash[..4].iter().map(|b| format!("{:02x}", b)).collect();
        let key_hex: String = key_hash[..4].iter().map(|b| format!("{:02x}", b)).collect();
        Ok(format!("{}:{}", jar_hex, key_hex))
    }

    fn deploy_and_launch(
        &self,
        serial: &str,
        auth_key_hex: &str,
        stamp: &str,
    ) -> io::Result<Vec<DiscoveredWifiEndpoint>> {
        let _ = run_adb_command(
            &self.adb_bin,
            &["-s", serial, "shell", "pkill -x seam_agent || true"],
        );
        run_adb_command(
            &self.adb_bin,
            &[
                "-s",
                serial,
                "push",
                self.agent_jar_path.to_str().unwrap(),
                REMOTE_JAR_PATH,
            ],
        )?;

        let launch_script = format!(
            "rm -f {log}; printf '%s' '{stamp}' > {stamp_path}; nohup sh -c 'ANDROID_NO_USE_FWMARK_CLIENT=1 CLASSPATH={jar} app_process /system/bin --nice-name=seam_agent com.seam.agent.Main --server --detach --auth-key={key}' >{log} 2>&1 </dev/null &",
            log = REMOTE_LOG_PATH,
            stamp = stamp,
            stamp_path = REMOTE_STAMP_PATH,
            jar = REMOTE_JAR_PATH,
            key = auth_key_hex,
        );

        run_adb_command(&self.adb_bin, &["-s", serial, "shell", &launch_script])?;

        self.wait_for_log_ready(serial, Duration::from_secs(4))
    }

    fn wait_for_log_ready(
        &self,
        serial: &str,
        timeout: Duration,
    ) -> io::Result<Vec<DiscoveredWifiEndpoint>> {
        let deadline = Instant::now() + timeout;
        while Instant::now() < deadline {
            let cat_cmd = format!("cat {} 2>/dev/null || true", REMOTE_LOG_PATH);
            if let Ok(log) = run_adb_command(&self.adb_bin, &["-s", serial, "shell", &cat_cmd]) {
                if log.contains("SEAM_AGENT_READY") {
                    let mut endpoints = Vec::new();
                    for line in log.lines() {
                        let trimmed = line.trim();
                        if let Some(rest) = trimmed.strip_prefix("SEAM_WIFI_ENDPOINT:") {
                            let parts: Vec<&str> = rest.split(':').collect();
                            if parts.len() == 3 {
                                if let Ok(port) = parts[2].parse::<u16>() {
                                    endpoints.push(DiscoveredWifiEndpoint {
                                        interface_name: parts[0].to_string(),
                                        ip: parts[1].to_string(),
                                        port,
                                    });
                                }
                            }
                        }
                    }
                    return Ok(endpoints);
                }
            }
            thread::sleep(Duration::from_millis(60));
        }

        Err(io::Error::new(
            io::ErrorKind::TimedOut,
            format!(
                "Timed out waiting for SEAM agent daemon to start on {}",
                serial
            ),
        ))
    }

    /// Discovers active Wi-Fi / Hotspot instances directly over UDP broadcast
    pub fn discover_wireless_agent(&self) -> Vec<DiscoveredWifiEndpoint> {
        let socket = match UdpSocket::bind("0.0.0.0:0") {
            Ok(s) => s,
            Err(_) => return Vec::new(),
        };
        let _ = socket.set_broadcast(true);
        let _ = socket.set_read_timeout(Some(Duration::from_millis(400)));

        let probe = b"SEAM_DISCOVER_V1";
        let target = SocketAddrV4::new(
            Ipv4Addr::new(255, 255, 255, 255),
            crate::protocol::wire::DEFAULT_PORT,
        );
        let _ = socket.send_to(probe, target);

        let mut discovered = Vec::new();
        let mut buf = [0u8; 128];
        if let Ok((n, src)) = socket.recv_from(&mut buf) {
            if let Ok(msg) = std::str::from_utf8(&buf[..n]) {
                if msg.starts_with("SEAM_OFFER_V1") {
                    discovered.push(DiscoveredWifiEndpoint {
                        interface_name: "wifi".to_string(),
                        ip: src.ip().to_string(),
                        port: crate::protocol::wire::DEFAULT_PORT,
                    });
                }
            }
        }
        discovered
    }
}
