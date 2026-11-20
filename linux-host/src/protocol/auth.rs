use crate::protocol::crypto::{compute_hmac_sha256, constant_time_eq};
use crate::protocol::wire::{AUTH_STATUS_OK, MAGIC_BYTES, VERSION_BYTE};
use std::fs;
use std::io::{self, Read, Write};
use std::path::PathBuf;

pub const NONCE_LEN: usize = 32;
pub const UDP_TAG_LEN: usize = 16;
pub const UDP_DATAGRAM_LEN: usize = 42;
pub const UDP_MAGIC: u8 = 0x55;

const CLIENT_DOMAIN: &[u8] = b"SEAM-CLIENT-AUTH-v1";
const SERVER_DOMAIN: &[u8] = b"SEAM-SERVER-AUTH-v1";
const UDP_DOMAIN: &[u8] = b"SEAM-UDP-AUTH-v1";

#[derive(Clone)]
pub struct Authenticator {
    key: [u8; 32],
    hex: String,
}

impl Authenticator {
    pub fn from_bytes(key: [u8; 32]) -> Self {
        let hex = key.iter().map(|b| format!("{:02x}", b)).collect();
        Self { key, hex }
    }

    pub fn from_hex(hex_str: &str) -> Option<Self> {
        let trimmed = hex_str.trim();
        if trimmed.len() != 64 {
            return None;
        }
        let mut key = [0u8; 32];
        for i in 0..32 {
            let chunk = &trimmed[i * 2..i * 2 + 2];
            key[i] = u8::from_str_radix(chunk, 16).ok()?;
        }
        Some(Self {
            key,
            hex: trimmed.to_lowercase(),
        })
    }

    pub fn hex(&self) -> &str {
        &self.hex
    }

    pub fn key_bytes(&self) -> &[u8; 32] {
        &self.key
    }

    pub fn load_or_create() -> io::Result<Self> {
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

        // 1. Try to read existing key
        for dir in candidate_dirs.iter().flatten() {
            let key_file = dir.join("auth_key");
            if let Ok(content) = fs::read_to_string(&key_file) {
                if let Some(auth) = Self::from_hex(&content) {
                    return Ok(auth);
                }
            }
        }

        // 2. Generate new 32-byte cryptographically secure random key
        let mut random_key = [0u8; 32];
        let mut f = fs::File::open("/dev/urandom")?;
        f.read_exact(&mut random_key)?;
        let auth = Self::from_bytes(random_key);

        // 3. Try to save to first writable directory
        let mut saved = false;
        for dir in candidate_dirs.iter().flatten() {
            if fs::create_dir_all(dir).is_ok() {
                let key_file = dir.join("auth_key");
                if fs::write(&key_file, auth.hex()).is_ok() {
                    saved = true;
                    break;
                }
            }
        }

        if !saved {
            eprintln!("[Auth] Warning: Could not persist authentication key to disk (memory-only key will be used)");
        }

        Ok(auth)
    }

    pub fn authenticate_client_stream<R: Read, W: Write>(
        &self,
        reader: &mut R,
        writer: &mut W,
    ) -> io::Result<u64> {
        // 1. Read 38-byte Server Challenge: MAGIC(4B) || version(1B) || transport(1B) || server_nonce(32B)
        let mut challenge = [0u8; 38];
        reader.read_exact(&mut challenge)?;

        if &challenge[0..4] != MAGIC_BYTES {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "Invalid protocol magic in server challenge",
            ));
        }
        if challenge[4] != VERSION_BYTE {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                format!("Unsupported protocol version: {}", challenge[4]),
            ));
        }
        let server_nonce = &challenge[6..38];

        // 2. Generate 32-byte Client Nonce and Client HMAC-SHA256
        let mut client_nonce = [0u8; NONCE_LEN];
        let mut urandom = fs::File::open("/dev/urandom")?;
        urandom.read_exact(&mut client_nonce)?;

        let client_hmac =
            compute_hmac_sha256(&self.key, &[CLIENT_DOMAIN, server_nonce, &client_nonce]);

        // Send 68-byte Client Response: MAGIC(4B) || client_nonce(32B) || client_hmac(32B)
        let mut response = Vec::with_capacity(68);
        response.extend_from_slice(MAGIC_BYTES);
        response.extend_from_slice(&client_nonce);
        response.extend_from_slice(&client_hmac);
        writer.write_all(&response)?;
        writer.flush()?;

        // 3. Read 41-byte Server Ack: status(1B) || session_id(8B, le) || server_hmac(32B)
        let mut ack = [0u8; 41];
        reader.read_exact(&mut ack)?;

        let status = ack[0];
        if status != AUTH_STATUS_OK {
            return Err(io::Error::new(
                io::ErrorKind::PermissionDenied,
                format!(
                    "Server rejected authentication with status 0x{:02X}",
                    status
                ),
            ));
        }

        let session_id_bytes = &ack[1..9];
        let server_hmac = &ack[9..41];

        // Mutual authentication verification
        let expected_server_hmac = compute_hmac_sha256(
            &self.key,
            &[SERVER_DOMAIN, &client_nonce, server_nonce, session_id_bytes],
        );

        if !constant_time_eq(server_hmac, &expected_server_hmac) {
            return Err(io::Error::new(
                io::ErrorKind::PermissionDenied,
                "Server HMAC verification failed (peer is untrusted or key mismatch)",
            ));
        }

        let session_id = u64::from_le_bytes([
            session_id_bytes[0],
            session_id_bytes[1],
            session_id_bytes[2],
            session_id_bytes[3],
            session_id_bytes[4],
            session_id_bytes[5],
            session_id_bytes[6],
            session_id_bytes[7],
        ]);

        Ok(session_id)
    }

    pub fn build_signed_udp(
        &self,
        session_id: u64,
        sequence: u64,
        opcode: u8,
        payload8: [u8; 8],
    ) -> [u8; UDP_DATAGRAM_LEN] {
        let mut pkt = [0u8; UDP_DATAGRAM_LEN];
        pkt[0] = UDP_MAGIC;
        pkt[1..9].copy_from_slice(&session_id.to_le_bytes());
        pkt[9..17].copy_from_slice(&sequence.to_le_bytes());
        pkt[17] = opcode;
        pkt[18..26].copy_from_slice(&payload8);

        let digest = compute_hmac_sha256(&self.key, &[UDP_DOMAIN, &pkt[0..26]]);
        pkt[26..42].copy_from_slice(&digest[0..UDP_TAG_LEN]);
        pkt
    }

    pub fn verify_udp(&self, packet: &[u8]) -> bool {
        if packet.len() != UDP_DATAGRAM_LEN || packet[0] != UDP_MAGIC {
            return false;
        }
        let digest = compute_hmac_sha256(&self.key, &[UDP_DOMAIN, &packet[0..26]]);
        constant_time_eq(&packet[26..42], &digest[0..UDP_TAG_LEN])
    }
}
