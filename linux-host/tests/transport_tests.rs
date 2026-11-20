use seam_linux_host::protocol::auth::Authenticator;
use seam_linux_host::protocol::frames::OutboundFrame;
use seam_linux_host::transport::adb::AdbDevice;
use seam_linux_host::transport::multiplexer::SeamTransportMultiplexer;
use seam_linux_host::transport::{
    now_monotonic_us, SeamEndpoint, SeamTransport, SendReliability, TransportDelegate,
    TransportKind, TransportState,
};
use std::io;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;

struct MockTransport {
    kind: TransportKind,
    state: TransportState,
    should_fail_send: Arc<AtomicBool>,
    sent_frames: Arc<std::sync::Mutex<Vec<OutboundFrame>>>,
}

impl MockTransport {
    fn new(kind: TransportKind) -> Self {
        Self {
            kind,
            state: TransportState::Ready { session_id: 12345 },
            should_fail_send: Arc::new(AtomicBool::new(false)),
            sent_frames: Arc::new(std::sync::Mutex::new(Vec::new())),
        }
    }
}

impl SeamTransport for MockTransport {
    fn kind(&self) -> TransportKind {
        self.kind
    }

    fn state(&self) -> TransportState {
        self.state.clone()
    }

    fn last_rtt_us(&self) -> u64 {
        1500
    }

    fn connect(&mut self, _endpoint: &SeamEndpoint, _auth: &Authenticator) -> io::Result<()> {
        self.state = TransportState::Ready { session_id: 12345 };
        Ok(())
    }

    fn set_delegate(&mut self, _delegate: Arc<dyn TransportDelegate>) {
        // no-op mock
    }

    fn send(&self, frame: &OutboundFrame, _rel: SendReliability) -> io::Result<()> {
        if self.should_fail_send.load(Ordering::Relaxed) {
            return Err(io::Error::new(
                io::ErrorKind::BrokenPipe,
                "Mock transport failure",
            ));
        }
        self.sent_frames.lock().unwrap().push(frame.clone());
        Ok(())
    }

    fn send_ping(&self) -> io::Result<()> {
        Ok(())
    }

    fn disconnect(&mut self, reason: &str) {
        self.state = TransportState::Closed {
            reason: reason.to_string(),
        };
    }
}

#[test]
fn test_monotonic_clock() {
    let t1 = now_monotonic_us();
    std::thread::sleep(std::time::Duration::from_millis(5));
    let t2 = now_monotonic_us();
    assert!(t2 >= t1 + 4000); // at least 4 ms passed
}

#[test]
fn test_adb_device_parsing() {
    let dev = AdbDevice {
        serial: "11275373CP003502".to_string(),
        state: "device".to_string(),
        model: "TECNO_KM5".to_string(),
        transport_id: "1".to_string(),
    };
    assert!(dev.is_ready());
    assert!(!dev.is_unauthorized());
    assert!(!dev.is_wireless());

    let unauth = AdbDevice {
        serial: "11275373CP003502".to_string(),
        state: "unauthorized".to_string(),
        model: "Android".to_string(),
        transport_id: "1".to_string(),
    };
    assert!(!unauth.is_ready());
    assert!(unauth.is_unauthorized());

    let wifi_dev = AdbDevice {
        serial: "192.168.1.155:5555".to_string(),
        state: "device".to_string(),
        model: "Pixel_8".to_string(),
        transport_id: "2".to_string(),
    };
    assert!(wifi_dev.is_ready());
    assert!(wifi_dev.is_wireless());
}

#[test]
fn test_multiplexer_switching_and_failover() {
    let mux = SeamTransportMultiplexer::new();
    let auth = Authenticator::from_bytes([0xAA; 32]);

    let usb_transport = Box::new(MockTransport::new(TransportKind::UsbAdb));
    let usb_sent = Arc::clone(&usb_transport.sent_frames);
    let usb_fail = Arc::clone(&usb_transport.should_fail_send);

    let wifi_transport = Box::new(MockTransport::new(TransportKind::WifiHybrid));
    let wifi_sent = Arc::clone(&wifi_transport.sent_frames);

    let usb_endpoint = SeamEndpoint::UsbLocalhost {
        port: 1234,
        serial: "TEST_SERIAL".to_string(),
    };
    let wifi_endpoint = SeamEndpoint::Wifi {
        host: "192.168.1.100".to_string(),
        tcp_port: 27186,
        udp_port: 27186,
    };

    // Add USB as primary
    mux.add_and_connect_transport(usb_transport, &usb_endpoint, &auth, true)
        .unwrap();
    assert_eq!(mux.active_primary_kind(), Some(TransportKind::UsbAdb));

    // Add Wi-Fi as standby
    mux.add_and_connect_transport(wifi_transport, &wifi_endpoint, &auth, false)
        .unwrap();
    assert_eq!(mux.active_primary_kind(), Some(TransportKind::UsbAdb));

    // Send frame -> should go to USB
    let frame1 = OutboundFrame::Ping { timestamp_us: 100 };
    mux.send(&frame1, SendReliability::ReliableOrdered).unwrap();
    assert_eq!(usb_sent.lock().unwrap().len(), 1);
    assert_eq!(wifi_sent.lock().unwrap().len(), 0);

    // Manual switch to Wi-Fi
    mux.switch_active_transport(TransportKind::WifiHybrid)
        .unwrap();
    assert_eq!(mux.active_primary_kind(), Some(TransportKind::WifiHybrid));

    // Send frame -> should go to Wi-Fi
    let frame2 = OutboundFrame::Ping { timestamp_us: 200 };
    mux.send(&frame2, SendReliability::ReliableOrdered).unwrap();
    assert_eq!(wifi_sent.lock().unwrap().len(), 1);

    // Switch back to USB
    mux.switch_active_transport(TransportKind::UsbAdb).unwrap();
    assert_eq!(mux.active_primary_kind(), Some(TransportKind::UsbAdb));

    // Simulate primary USB cable failure mid-send -> should hot-failover to Wi-Fi!
    usb_fail.store(true, Ordering::SeqCst);
    let frame3 = OutboundFrame::Ping { timestamp_us: 300 };
    mux.send(&frame3, SendReliability::ReliableOrdered).unwrap();

    // Verify active primary was automatically promoted to Wi-Fi and frame was delivered!
    assert_eq!(mux.active_primary_kind(), Some(TransportKind::WifiHybrid));
    assert_eq!(wifi_sent.lock().unwrap().len(), 2);
}
