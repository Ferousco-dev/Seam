use std::io::{self, Write};
use std::net::TcpStream;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::Duration;

use crate::protocol::auth::Authenticator;
use crate::protocol::frames::{InboundFrame, OutboundFrame};
use crate::transport::{
    now_monotonic_us, SeamEndpoint, SeamTransport, SendReliability, TransportDelegate,
    TransportKind, TransportState,
};

pub struct UsbAdbTransport {
    state: Arc<Mutex<TransportState>>,
    last_rtt_us: Arc<AtomicU64>,
    stream: Arc<Mutex<Option<TcpStream>>>,
    running: Arc<AtomicBool>,
    delegate: Option<Arc<dyn TransportDelegate>>,
    heartbeat_handle: Option<thread::JoinHandle<()>>,
    reader_handle: Option<thread::JoinHandle<()>>,
}

impl UsbAdbTransport {
    pub fn new() -> Self {
        Self {
            state: Arc::new(Mutex::new(TransportState::Idle)),
            last_rtt_us: Arc::new(AtomicU64::new(0)),
            stream: Arc::new(Mutex::new(None)),
            running: Arc::new(AtomicBool::new(false)),
            delegate: None,
            heartbeat_handle: None,
            reader_handle: None,
        }
    }

    pub fn set_delegate(&mut self, delegate: Arc<dyn TransportDelegate>) {
        self.delegate = Some(delegate);
    }

    fn update_state(&self, new_state: TransportState) {
        if let Ok(mut lock) = self.state.lock() {
            *lock = new_state.clone();
        }
        if let Some(ref d) = self.delegate {
            d.on_state_changed(TransportKind::UsbAdb, new_state);
        }
    }
}

impl Default for UsbAdbTransport {
    fn default() -> Self {
        Self::new()
    }
}

impl SeamTransport for UsbAdbTransport {
    fn kind(&self) -> TransportKind {
        TransportKind::UsbAdb
    }

    fn state(&self) -> TransportState {
        self.state
            .lock()
            .map(|s| s.clone())
            .unwrap_or(TransportState::Idle)
    }

    fn last_rtt_us(&self) -> u64 {
        self.last_rtt_us.load(Ordering::Relaxed)
    }

    fn set_delegate(&mut self, delegate: Arc<dyn TransportDelegate>) {
        self.delegate = Some(delegate);
    }

    fn connect(
        &mut self,
        endpoint: &SeamEndpoint,
        authenticator: &Authenticator,
    ) -> io::Result<()> {
        let port = match endpoint {
            SeamEndpoint::UsbLocalhost { port, .. } => *port,
            _ => {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidInput,
                    "UsbAdbTransport requires a UsbLocalhost endpoint",
                ))
            }
        };

        self.update_state(TransportState::Connecting);

        let stream = TcpStream::connect(("127.0.0.1", port))?;
        stream.set_nodelay(true)?;

        self.update_state(TransportState::Authenticating);

        let mut read_half = stream.try_clone()?;
        let mut write_half = stream.try_clone()?;

        let session_id =
            match authenticator.authenticate_client_stream(&mut read_half, &mut write_half) {
                Ok(sid) => sid,
                Err(e) => {
                    self.update_state(TransportState::Closed {
                        reason: format!("Authentication failed: {}", e),
                    });
                    return Err(e);
                }
            };

        self.running.store(true, Ordering::SeqCst);
        if let Ok(mut lock) = self.stream.lock() {
            *lock = Some(stream);
        }

        self.update_state(TransportState::Ready { session_id });

        // Spawn background reader thread
        let running_clone = Arc::clone(&self.running);
        let state_clone = Arc::clone(&self.state);
        let rtt_clone = Arc::clone(&self.last_rtt_us);
        let delegate_clone = self.delegate.clone();

        let reader = thread::Builder::new()
            .name("seam-usb-reader".to_string())
            .spawn(move || {
                while running_clone.load(Ordering::Relaxed) {
                    match InboundFrame::read_from(&mut read_half) {
                        Ok(frame) => {
                            if let InboundFrame::Pong { timestamp_us } = frame {
                                let now = now_monotonic_us();
                                if now >= timestamp_us {
                                    let rtt = now - timestamp_us;
                                    rtt_clone.store(rtt, Ordering::Relaxed);
                                    if let Some(ref d) = delegate_clone {
                                        d.on_rtt_measured(TransportKind::UsbAdb, rtt);
                                    }
                                }
                            }
                            if let Some(ref d) = delegate_clone {
                                d.on_frame_received(TransportKind::UsbAdb, frame);
                            }
                        }
                        Err(e) => {
                            if running_clone.load(Ordering::Relaxed) {
                                running_clone.store(false, Ordering::SeqCst);
                                let close_state = TransportState::Closed {
                                    reason: format!("USB read failed: {}", e),
                                };
                                if let Ok(mut lock) = state_clone.lock() {
                                    *lock = close_state.clone();
                                }
                                if let Some(ref d) = delegate_clone {
                                    d.on_state_changed(TransportKind::UsbAdb, close_state);
                                }
                            }
                            break;
                        }
                    }
                }
            })?;
        self.reader_handle = Some(reader);

        // Spawn background heartbeat ping thread (every 500 ms)
        let running_hb = Arc::clone(&self.running);
        let stream_hb = Arc::clone(&self.stream);

        let heartbeat = thread::Builder::new()
            .name("seam-usb-ping".to_string())
            .spawn(move || {
                while running_hb.load(Ordering::Relaxed) {
                    thread::sleep(Duration::from_millis(500));
                    if !running_hb.load(Ordering::Relaxed) {
                        break;
                    }
                    let now = now_monotonic_us();
                    let ping = OutboundFrame::Ping { timestamp_us: now };
                    let bytes = ping.serialize();
                    if let Ok(mut lock) = stream_hb.lock() {
                        if let Some(ref mut s) = *lock {
                            let _ = s.write_all(&bytes);
                        }
                    }
                }
            })?;
        self.heartbeat_handle = Some(heartbeat);

        Ok(())
    }

    fn send(&self, frame: &OutboundFrame, _reliability: SendReliability) -> io::Result<()> {
        if !self.running.load(Ordering::Relaxed) {
            return Err(io::Error::new(
                io::ErrorKind::NotConnected,
                "USB transport is not connected",
            ));
        }
        let bytes = frame.serialize();
        if let Ok(mut lock) = self.stream.lock() {
            if let Some(ref mut s) = *lock {
                s.write_all(&bytes)?;
                return Ok(());
            }
        }
        Err(io::Error::new(
            io::ErrorKind::NotConnected,
            "USB transport socket is closed",
        ))
    }

    fn send_ping(&self) -> io::Result<()> {
        let now = now_monotonic_us();
        self.send(
            &OutboundFrame::Ping { timestamp_us: now },
            SendReliability::ReliableOrdered,
        )
    }

    fn disconnect(&mut self, reason: &str) {
        let was_running = self.running.swap(false, Ordering::SeqCst);
        if let Ok(mut lock) = self.stream.lock() {
            if let Some(s) = lock.take() {
                let _ = s.shutdown(std::net::Shutdown::Both);
            }
        }
        if was_running {
            self.update_state(TransportState::Closed {
                reason: reason.to_string(),
            });
        }
    }
}
