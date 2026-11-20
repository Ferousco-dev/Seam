use crate::protocol::wire::EdgeSide;
use std::fs;
use std::io;
use std::path::PathBuf;

#[derive(Debug, Clone)]
pub struct HostConfig {
    pub active_edge: EdgeSide,
    pub velocity_gain: f64,
    pub auto_reconnect: bool,
    pub wireless_host: Option<String>,
}

impl HostConfig {
    pub fn default_config() -> Self {
        Self {
            active_edge: EdgeSide::Right,
            velocity_gain: 1.85,
            auto_reconnect: true,
            wireless_host: None,
        }
    }

    pub fn config_path() -> PathBuf {
        let candidate_dirs = [
            std::env::var("SEAM_CONFIG_DIR").ok().map(PathBuf::from),
            std::env::var("XDG_CONFIG_HOME")
                .ok()
                .map(|x| PathBuf::from(x).join("seam")),
            std::env::var("HOME")
                .ok()
                .map(|h| PathBuf::from(h).join(".seam")),
            Some(PathBuf::from("/tmp/.seam")),
        ];
        for dir in candidate_dirs.iter().flatten() {
            let p = dir.join("config.ini");
            if p.is_file() {
                return p;
            }
        }
        let home = std::env::var("HOME")
            .map(PathBuf::from)
            .unwrap_or_else(|_| PathBuf::from("/tmp"));
        home.join(".seam").join("config.ini")
    }

    pub fn load() -> Self {
        let mut cfg = Self::default_config();
        let path = Self::config_path();
        if let Ok(content) = fs::read_to_string(&path) {
            for line in content.lines() {
                let trimmed = line.trim();
                if trimmed.is_empty() || trimmed.starts_with('#') {
                    continue;
                }
                if let Some((k, v)) = trimmed.split_once('=') {
                    match k.trim() {
                        "edge" => match v.trim().to_lowercase().as_str() {
                            "left" => cfg.active_edge = EdgeSide::Left,
                            "right" => cfg.active_edge = EdgeSide::Right,
                            "top" => cfg.active_edge = EdgeSide::Top,
                            "bottom" => cfg.active_edge = EdgeSide::Bottom,
                            _ => {}
                        },
                        "velocity_gain" => {
                            if let Ok(val) = v.trim().parse::<f64>() {
                                cfg.velocity_gain = val.clamp(0.5, 5.0);
                            }
                        }
                        "auto_reconnect" => {
                            cfg.auto_reconnect = v.trim() == "true" || v.trim() == "1";
                        }
                        "wireless_host" => {
                            let s = v.trim();
                            if !s.is_empty() {
                                cfg.wireless_host = Some(s.to_string());
                            }
                        }
                        _ => {}
                    }
                }
            }
        }
        cfg
    }

    pub fn save(&self) -> io::Result<()> {
        let edge_str = match self.active_edge {
            EdgeSide::Left => "left",
            EdgeSide::Right => "right",
            EdgeSide::Top => "top",
            EdgeSide::Bottom => "bottom",
        };
        let host_str = self.wireless_host.as_deref().unwrap_or("");
        let content = format!(
            "# SEAM Linux Host Configuration\nedge={}\nvelocity_gain={:.2}\nauto_reconnect={}\nwireless_host={}\n",
            edge_str, self.velocity_gain, self.auto_reconnect, host_str
        );

        let candidate_dirs = [
            std::env::var("SEAM_CONFIG_DIR").ok().map(PathBuf::from),
            std::env::var("XDG_CONFIG_HOME")
                .ok()
                .map(|x| PathBuf::from(x).join("seam")),
            std::env::var("HOME")
                .ok()
                .map(|h| PathBuf::from(h).join(".seam")),
            Some(PathBuf::from("/tmp/.seam")),
        ];
        for dir in candidate_dirs.iter().flatten() {
            if fs::create_dir_all(dir).is_ok() {
                let path = dir.join("config.ini");
                if fs::write(&path, &content).is_ok() {
                    return Ok(());
                }
            }
        }
        Ok(())
    }
}
