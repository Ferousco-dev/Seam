use std::io::{self, Write};
use std::net::{SocketAddr, TcpStream, UdpSocket};
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

pub struct WifiHybridTransport {
    state: Arc<Mutex<TransportState>>,
    last_rtt_us: Arc<AtomicU64>,
    last_udp_rtt_us: Arc<AtomicU64>,
    tcp_stream: Arc<Mutex<Option<TcpStream>>>,
    udp_socket: Arc<Mutex<Option<UdpSocket>>>,
    authenticator: Arc<Mutex<Option<Authenticator>>>,
    session_id: Arc<AtomicU64>,
    udp_sequence: Arc<AtomicU64>,
    running: Arc<AtomicBool>,
    delegate: Option<Arc<dyn TransportDelegate>>,
    heartbeat_handle: Option<thread::JoinHandle<()>>,
    tcp_reader_handle: Option<thread::JoinHandle<()>>,
    udp_reader_handle: Option<thread::JoinHandle<()>>,
}

impl WifiHybridTransport {
    pub fn new() -> Self {
        Self {
            state: Arc::new(Mutex::new(TransportState::Idle)),
            last_rtt_us: Arc::new(AtomicU64::new(0)),
            last_udp_rtt_us: Arc::new(AtomicU64::new(0)),
            tcp_stream: Arc::new(Mutex::new(None)),
            udp_socket: Arc::new(Mutex::new(None)),
            authenticator: Arc::new(Mutex::new(None)),
            session_id: Arc::new(AtomicU64::new(0)),
            udp_sequence: Arc::new(AtomicU64::new(0)),
            running: Arc::new(AtomicBool::new(false)),
            delegate: None,
            heartbeat_handle: None,
            tcp_reader_handle: None,
            udp_reader_handle: None,
        }
    }

    pub fn last_udp_rtt_us(&self) -> u64 {
        self.last_udp_rtt_us.load(Ordering::Relaxed)
    }

    fn update_state(&self, new_state: TransportState) {
        if let Ok(mut lock) = self.state.lock() {
            *lock = new_state.clone();
        }
        if let Some(ref d) = self.delegate {
            d.on_state_changed(TransportKind::WifiHybrid, new_state);
        }
    }
}

impl Default for WifiHybridTransport {
    fn default() -> Self {
        Self::new()
    }
}

impl SeamTransport for WifiHybridTransport {
    fn kind(&self) -> TransportKind {
        TransportKind::WifiHybrid
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
        let (host, tcp_port, udp_port) = match endpoint {
            SeamEndpoint::Wifi {
                host,
                tcp_port,
                udp_port,
            } => (host.as_str(), *tcp_port, *udp_port),
            _ => {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidInput,
                    "WifiHybridTransport requires a Wifi endpoint",
                ))
            }
        };

        self.update_state(TransportState::Connecting);

        // 1. Connect TCP control socket with 1.5s timeout
        let tcp_addr: SocketAddr = format!("{}:{}", host, tcp_port).parse().map_err(|e| {
            io::Error::new(
                io::ErrorKind::InvalidInput,
                format!("Invalid TCP address: {}", e),
            )
        })?;

        let stream = TcpStream::connect_timeout(&tcp_addr, Duration::from_millis(1500))?;
        stream.set_nodelay(true)?;

        // 2. Connect UDP fast-path socket
        let udp_addr: SocketAddr = format!("{}:{}", host, udp_port).parse().map_err(|e| {
            io::Error::new(
                io::ErrorKind::InvalidInput,
                format!("Invalid UDP address: {}", e),
            )
        })?;

        let udp = UdpSocket::bind("0.0.0.0:0")?;
        let _ = udp.connect(udp_addr);
        let _ = udp.set_read_timeout(Some(Duration::from_millis(500)));

        self.update_state(TransportState::Authenticating);

        let mut read_half = stream.try_clone()?;
        let mut write_half = stream.try_clone()?;

        let session_id =
            match authenticator.authenticate_client_stream(&mut read_half, &mut write_half) {
                Ok(sid) => sid,
                Err(e) => {
                    self.update_state(TransportState::Closed {
                        reason: format!("Wi-Fi Authentication failed: {}", e),
                    });
                    return Err(e);
                }
            };

        self.session_id.store(session_id, Ordering::SeqCst);
        self.udp_sequence.store(0, Ordering::SeqCst);
        self.running.store(true, Ordering::SeqCst);

        if let Ok(mut lock) = self.authenticator.lock() {
            *lock = Some(authenticator.clone());
        }
        if let Ok(mut lock) = self.tcp_stream.lock() {
            *lock = Some(stream);
        }
        if let Ok(mut lock) = self.udp_socket.lock() {
            *lock = Some(udp.try_clone()?);
        }

        self.update_state(TransportState::Ready { session_id });

        // Spawn TCP reader thread
        let running_tcp = Arc::clone(&self.running);
        let state_tcp = Arc::clone(&self.state);
        let rtt_tcp = Arc::clone(&self.last_rtt_us);
        let delegate_tcp = self.delegate.clone();

        let tcp_reader = thread::Builder::new()
            .name("seam-wifi-tcp".to_string())
            .spawn(move || {
                while running_tcp.load(Ordering::Relaxed) {
                    match InboundFrame::read_from(&mut read_half) {
                        Ok(frame) => {
                            if let InboundFrame::Pong { timestamp_us } = frame {
                                let now = now_monotonic_us();
                                if now >= timestamp_us {
                                    let rtt = now - timestamp_us;
                                    rtt_tcp.store(rtt, Ordering::Relaxed);
                                    if let Some(ref d) = delegate_tcp {
                                        d.on_rtt_measured(TransportKind::WifiHybrid, rtt);
                                    }
                                }
                            }
                            if let Some(ref d) = delegate_tcp {
                                d.on_frame_received(TransportKind::WifiHybrid, frame);
                            }
                        }
                        Err(e) => {
                            if running_tcp.load(Ordering::Relaxed) {
                                running_tcp.store(false, Ordering::SeqCst);
                                let close_state = TransportState::Closed {
                                    reason: format!("Wi-Fi TCP read failed: {}", e),
                                };
                                if let Ok(mut lock) = state_tcp.lock() {
                                    *lock = close_state.clone();
                                }
                                if let Some(ref d) = delegate_tcp {
                                    d.on_state_changed(TransportKind::WifiHybrid, close_state);
                                }
                            }
                            break;
                        }
                    }
                }
            })?;
        self.tcp_reader_handle = Some(tcp_reader);

        // Spawn UDP reader thread for pong packets
        let running_udp = Arc::clone(&self.running);
        let udp_sock_recv = udp;
        let auth_udp = Arc::clone(&self.authenticator);
        let udp_rtt_clone = Arc::clone(&self.last_udp_rtt_us);
        let delegate_udp = self.delegate.clone();

        let udp_reader = thread::Builder::new()
            .name("seam-wifi-udp".to_string())
            .spawn(move || {
                let mut buf = [0u8; 64];
                while running_udp.load(Ordering::Relaxed) {
                    if let Ok(n) = udp_sock_recv.recv(&mut buf) {
                        if n == 42 {
                            let is_valid = {
                                if let Ok(lock) = auth_udp.lock() {
                                    if let Some(ref auth) = *lock {
                                        auth.verify_udp(&buf[..42])
                                    } else {
                                        false
                                    }
                                } else {
                                    false
                                }
                            };
                            if is_valid && buf[17] == 0x84 {
                                // OP_PONG over UDP
                                let sent_us = u64::from_le_bytes([
                                    buf[18], buf[19], buf[20], buf[21], buf[22], buf[23], buf[24],
                                    buf[25],
                                ]);
                                let now = now_monotonic_us();
                                if now >= sent_us {
                                    let rtt = now - sent_us;
                                    udp_rtt_clone.store(rtt, Ordering::Relaxed);
                                    if let Some(ref d) = delegate_udp {
                                        d.on_rtt_measured(TransportKind::WifiHybrid, rtt);
                                    }
                                }
                            }
                        }
                    }
                }
            })?;
        self.udp_reader_handle = Some(udp_reader);

        // Spawn heartbeat ping thread (every 500 ms)
        let running_hb = Arc::clone(&self.running);
        let stream_hb = Arc::clone(&self.tcp_stream);

        let heartbeat = thread::Builder::new()
            .name("seam-wifi-ping".to_string())
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

    fn send(&self, frame: &OutboundFrame, reliability: SendReliability) -> io::Result<()> {
        if !self.running.load(Ordering::Relaxed) {
            return Err(io::Error::new(
                io::ErrorKind::NotConnected,
                "Wi-Fi transport is not connected",
            ));
        }

        // Fast-path: route pure mouse motion deltas over HMAC-signed UDP datagrams
        if reliability == SendReliability::UnreliableSequenced {
            if let OutboundFrame::MouseReport {
                buttons,
                dx,
                dy,
                v_wheel,
                h_wheel,
            } = frame
            {
                let seq = self.udp_sequence.fetch_add(1, Ordering::Relaxed) + 1;
                let sid = self.session_id.load(Ordering::Relaxed);

                let auth_opt = self.authenticator.lock().ok().and_then(|l| l.clone());
                let udp_opt = self
                    .udp_socket
                    .lock()
                    .ok()
                    .and_then(|l| l.as_ref().and_then(|s| s.try_clone().ok()));

                if let (Some(auth), Some(udp)) = (auth_opt, udp_opt) {
                    let dx_b = dx.to_le_bytes();
                    let dy_b = dy.to_le_bytes();
                    let payload8 = [
                        buttons & 0x1F,
                        dx_b[0],
                        dx_b[1],
                        dy_b[0],
                        dy_b[1],
                        *v_wheel as u8,
                        *h_wheel as u8,
                        0x00,
                    ];
                    let datagram = auth.build_signed_udp(sid, seq, 0x01, payload8);
                    if let Ok(sent) = udp.send(&datagram) {
                        if sent == datagram.len() {
                            return Ok(());
                        }
                    }
                    // If UDP send fails, transparently fall through to TCP stream
                }
            }
        }

        let bytes = frame.serialize();
        if let Ok(mut lock) = self.tcp_stream.lock() {
            if let Some(ref mut s) = *lock {
                s.write_all(&bytes)?;
                return Ok(());
            }
        }
        Err(io::Error::new(
            io::ErrorKind::NotConnected,
            "Wi-Fi TCP stream is closed",
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
        if let Ok(mut lock) = self.tcp_stream.lock() {
            if let Some(s) = lock.take() {
                let _ = s.shutdown(std::net::Shutdown::Both);
            }
        }
        if let Ok(mut lock) = self.udp_socket.lock() {
            let _ = lock.take();
        }
        if was_running {
            self.update_state(TransportState::Closed {
                reason: reason.to_string(),
            });
        }
    }
}
