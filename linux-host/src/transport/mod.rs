use crate::protocol::auth::Authenticator;
use crate::protocol::frames::{InboundFrame, OutboundFrame};
use std::io;
use std::sync::Arc;
use std::time::Instant;

pub mod adb;
pub mod multiplexer;
pub mod usb;
pub mod wifi_hybrid;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
#[repr(u8)]
pub enum TransportKind {
    UsbAdb = 0x01,
    WifiHybrid = 0x02,
}

impl TransportKind {
    pub fn as_str(&self) -> &'static str {
        match self {
            Self::UsbAdb => "USB-ADB",
            Self::WifiHybrid => "Wi-Fi-Hybrid",
        }
    }
}

impl std::fmt::Display for TransportKind {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}", self.as_str())
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
pub enum SendReliability {
    ReliableOrdered = 0x01,
    UnreliableSequenced = 0x02,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum TransportState {
    Idle,
    Connecting,
    Authenticating,
    Ready { session_id: u64 },
    Closed { reason: String },
}

impl std::fmt::Display for TransportState {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Idle => write!(f, "Idle"),
            Self::Connecting => write!(f, "Connecting"),
            Self::Authenticating => write!(f, "Authenticating"),
            Self::Ready { session_id } => write!(f, "Ready(session=0x{:016X})", session_id),
            Self::Closed { reason } => write!(f, "Closed({})", reason),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SeamEndpoint {
    UsbLocalhost {
        port: u16,
        serial: String,
    },
    Wifi {
        host: String,
        tcp_port: u16,
        udp_port: u16,
    },
}

pub trait TransportDelegate: Send + Sync {
    fn on_state_changed(&self, kind: TransportKind, state: TransportState);
    fn on_frame_received(&self, kind: TransportKind, frame: InboundFrame);
    fn on_rtt_measured(&self, kind: TransportKind, rtt_us: u64);
}

pub trait SeamTransport: Send + Sync {
    fn kind(&self) -> TransportKind;
    fn state(&self) -> TransportState;
    fn last_rtt_us(&self) -> u64;
    /// Installs the delegate that receives state changes, inbound frames, and
    /// RTT measurements. The multiplexer calls this before `connect`.
    fn set_delegate(&mut self, delegate: Arc<dyn TransportDelegate>);
    fn connect(&mut self, endpoint: &SeamEndpoint, authenticator: &Authenticator)
        -> io::Result<()>;
    fn send(&self, frame: &OutboundFrame, reliability: SendReliability) -> io::Result<()>;
    fn send_ping(&self) -> io::Result<()>;
    fn disconnect(&mut self, reason: &str);
}

/// Helper returning monotonic microseconds since process start
static PROCESS_START: std::sync::OnceLock<Instant> = std::sync::OnceLock::new();

pub fn now_monotonic_us() -> u64 {
    let start = PROCESS_START.get_or_init(Instant::now);
    start.elapsed().as_micros() as u64
}
