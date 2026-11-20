use seam_linux_host::protocol::auth::{Authenticator, UDP_DATAGRAM_LEN};
use seam_linux_host::protocol::crypto::{compute_hmac_sha256, compute_sha256};
use seam_linux_host::protocol::frames::{InboundFrame, OutboundFrame};
use seam_linux_host::protocol::wire::{
    denormalize_coordinate, normalize_coordinate, EdgeSide, AUTH_STATUS_OK, MAGIC_BYTES,
    VERSION_BYTE,
};
use std::io::Cursor;

#[test]
fn test_coordinate_normalization() {
    assert_eq!(normalize_coordinate(0, 1080), 0);
    assert_eq!(normalize_coordinate(1079, 1080), 65535);
    assert_eq!(normalize_coordinate(540, 1081), 32768);

    assert_eq!(denormalize_coordinate(0, 1080), 0);
    assert_eq!(denormalize_coordinate(65535, 1080), 1079);

    // Roundtrip test across entire 1080p axis
    for px in 0..1080 {
        let norm = normalize_coordinate(px, 1080);
        let back = denormalize_coordinate(norm, 1080);
        assert!((back - px).abs() <= 1);
    }
}

#[test]
fn test_mouse_frame_serialization() {
    let frame = OutboundFrame::MouseReport {
        buttons: 0x01 | 0x02, // Left + Right
        dx: -150,
        dy: 300,
        v_wheel: -2,
        h_wheel: 4,
    };
    let bytes = frame.serialize();
    assert_eq!(bytes.len(), 8);
    assert_eq!(bytes[0], 0x01); // opMouseReport
    assert_eq!(bytes[1], 0x03); // buttons
    assert_eq!(i16::from_le_bytes([bytes[2], bytes[3]]), -150);
    assert_eq!(i16::from_le_bytes([bytes[4], bytes[5]]), 300);
    assert_eq!(bytes[6] as i8, -2);
    assert_eq!(bytes[7] as i8, 4);
}

#[test]
fn test_key_frame_serialization() {
    let frame = OutboundFrame::KeyReport {
        modifiers: 0x01 | 0x08,                     // LCtrl + LMeta
        keys: [0x04, 0x05, 0x06, 0x00, 0x00, 0x00], // 'A', 'B', 'C'
    };
    let bytes = frame.serialize();
    assert_eq!(bytes.len(), 9);
    assert_eq!(bytes[0], 0x02); // opKeyReport
    assert_eq!(bytes[1], 0x09);
    assert_eq!(bytes[2], 0x00); // reserved
    assert_eq!(&bytes[3..9], &[0x04, 0x05, 0x06, 0x00, 0x00, 0x00]);
}

#[test]
fn test_cursor_enter_serialization() {
    let frame = OutboundFrame::CursorEnter {
        edge: EdgeSide::Right,
        norm_x: 65000,
        norm_y: 32000,
        flags: 0x01,
    };
    let bytes = frame.serialize();
    assert_eq!(bytes.len(), 7);
    assert_eq!(bytes[0], 0x03);
    assert_eq!(bytes[1], 0x01); // Right
    assert_eq!(u16::from_le_bytes([bytes[2], bytes[3]]), 65000);
    assert_eq!(u16::from_le_bytes([bytes[4], bytes[5]]), 32000);
    assert_eq!(bytes[6], 0x01);
}

#[test]
fn test_clipboard_set_serialization() {
    let text = "Hello from Linux Host!";
    let frame = OutboundFrame::ClipboardSet {
        text: text.to_string(),
    };
    let bytes = frame.serialize();
    assert_eq!(bytes[0], 0x05);
    let len = u32::from_le_bytes([bytes[1], bytes[2], bytes[3], bytes[4]]) as usize;
    assert_eq!(len, text.len());
    assert_eq!(&bytes[5..5 + len], text.as_bytes());
}

#[test]
fn test_inbound_frames_parsing() {
    // 1. DisplayInfo
    let mut disp_bytes = vec![0x81];
    disp_bytes.extend_from_slice(&1080u16.to_le_bytes());
    disp_bytes.extend_from_slice(&2400u16.to_le_bytes());
    disp_bytes.extend_from_slice(&480u16.to_le_bytes());
    disp_bytes.push(1); // 90 deg rotation
    disp_bytes.push(120); // 120 Hz
    disp_bytes.extend_from_slice(&0u16.to_le_bytes());

    let mut cursor = Cursor::new(disp_bytes);
    let frame = InboundFrame::read_from(&mut cursor).unwrap();
    match frame {
        InboundFrame::DisplayInfo(info) => {
            assert_eq!(info.width, 1080);
            assert_eq!(info.height, 2400);
            assert_eq!(info.density_dpi, 480);
            assert_eq!(info.rotation_degrees(), 90);
            assert_eq!(info.refresh_rate, 120);
        }
        _ => panic!("Expected DisplayInfo"),
    }

    // 2. EdgeReturn
    let mut edge_bytes = vec![0x82, 0x00]; // Left
    edge_bytes.extend_from_slice(&100u16.to_le_bytes());
    edge_bytes.extend_from_slice(&32768u16.to_le_bytes());
    edge_bytes.push(15); // velocity

    let mut cursor = Cursor::new(edge_bytes);
    let frame = InboundFrame::read_from(&mut cursor).unwrap();
    match frame {
        InboundFrame::EdgeReturn(ret) => {
            assert_eq!(ret.edge, EdgeSide::Left);
            assert_eq!(ret.norm_x, 100);
            assert_eq!(ret.norm_y, 32768);
            assert_eq!(ret.velocity, 15);
        }
        _ => panic!("Expected EdgeReturn"),
    }
}

#[test]
fn test_sha256_known_vector() {
    let digest = compute_sha256(b"abc");
    let hex: String = digest.iter().map(|b| format!("{:02x}", b)).collect();
    assert_eq!(
        hex,
        "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
    );
}

#[test]
fn test_mutual_authentication_handshake_simulation() {
    use std::sync::mpsc;
    use std::thread;

    let key = [0x42u8; 32];
    let client_auth = Authenticator::from_bytes(key);

    let (c2s_tx, c2s_rx) = mpsc::channel::<Vec<u8>>();
    let (s2c_tx, s2c_rx) = mpsc::channel::<Vec<u8>>();

    let assigned_session_id = 0x1122334455667788u64;

    // Simulated Server thread
    let server_handle = thread::spawn(move || {
        let server_nonce = [0x77u8; 32];
        // 1. Send Server Challenge
        let mut challenge = Vec::new();
        challenge.extend_from_slice(MAGIC_BYTES);
        challenge.push(VERSION_BYTE);
        challenge.push(0x01); // USB
        challenge.extend_from_slice(&server_nonce);
        s2c_tx.send(challenge).unwrap();

        // 2. Read Client Response (68 bytes)
        let resp = c2s_rx.recv().unwrap();
        assert_eq!(resp.len(), 68);
        assert_eq!(&resp[0..4], MAGIC_BYTES);
        let client_nonce = &resp[4..36];
        let client_hmac = &resp[36..68];

        let expected_client_hmac =
            compute_hmac_sha256(&key, &[b"SEAM-CLIENT-AUTH-v1", &server_nonce, client_nonce]);
        assert_eq!(client_hmac, &expected_client_hmac);

        // 3. Send Server Ack
        let session_id_bytes = assigned_session_id.to_le_bytes();
        let server_hmac = compute_hmac_sha256(
            &key,
            &[
                b"SEAM-SERVER-AUTH-v1",
                client_nonce,
                &server_nonce,
                &session_id_bytes,
            ],
        );
        let mut ack = vec![AUTH_STATUS_OK];
        ack.extend_from_slice(&session_id_bytes);
        ack.extend_from_slice(&server_hmac);
        s2c_tx.send(ack).unwrap();
    });

    // Custom stream adapters for client
    struct PipeReader {
        rx: mpsc::Receiver<Vec<u8>>,
        buf: Vec<u8>,
        pos: usize,
    }
    impl std::io::Read for PipeReader {
        fn read(&mut self, buf: &mut [u8]) -> std::io::Result<usize> {
            while self.pos >= self.buf.len() {
                self.buf = self
                    .rx
                    .recv()
                    .map_err(|e| std::io::Error::new(std::io::ErrorKind::UnexpectedEof, e))?;
                self.pos = 0;
            }
            let avail = self.buf.len() - self.pos;
            let to_copy = avail.min(buf.len());
            buf[..to_copy].copy_from_slice(&self.buf[self.pos..self.pos + to_copy]);
            self.pos += to_copy;
            Ok(to_copy)
        }
    }

    struct PipeWriter {
        tx: mpsc::Sender<Vec<u8>>,
        buf: Vec<u8>,
    }
    impl std::io::Write for PipeWriter {
        fn write(&mut self, buf: &[u8]) -> std::io::Result<usize> {
            self.buf.extend_from_slice(buf);
            Ok(buf.len())
        }
        fn flush(&mut self) -> std::io::Result<()> {
            if !self.buf.is_empty() {
                self.tx
                    .send(std::mem::take(&mut self.buf))
                    .map_err(|e| std::io::Error::new(std::io::ErrorKind::BrokenPipe, e))?;
            }
            Ok(())
        }
    }

    let mut reader = PipeReader {
        rx: s2c_rx,
        buf: Vec::new(),
        pos: 0,
    };
    let mut writer = PipeWriter {
        tx: c2s_tx,
        buf: Vec::new(),
    };

    let session_id = client_auth
        .authenticate_client_stream(&mut reader, &mut writer)
        .unwrap();
    assert_eq!(session_id, assigned_session_id);

    server_handle.join().unwrap();
}

#[test]
fn test_udp_fast_path_signing_and_verification() {
    let key = [0x99u8; 32];
    let auth = Authenticator::from_bytes(key);

    let session_id = 0xDEADBEEFCAFE1234u64;
    let sequence = 42u64;
    let opcode = 0x01; // mouse report
    let payload = [0x01, 0x10, 0x00, 0x20, 0x00, 0x00, 0x00, 0x00];

    let datagram = auth.build_signed_udp(session_id, sequence, opcode, payload);
    assert_eq!(datagram.len(), UDP_DATAGRAM_LEN);
    assert_eq!(datagram[0], 0x55);

    // Verify valid packet
    assert!(auth.verify_udp(&datagram));

    // Corrupted packet should fail verification
    let mut corrupted = datagram;
    corrupted[18] ^= 0xFF; // Flip payload bit
    assert!(!auth.verify_udp(&corrupted));

    // Wrong key should fail verification
    let wrong_auth = Authenticator::from_bytes([0x88u8; 32]);
    assert!(!wrong_auth.verify_udp(&datagram));
}
