//! SHA-256 and HMAC-SHA256 primitives used by the SEAM wire protocol.
//!
//! Implemented with the pure-Rust `sha2` / `hmac` crates so the Linux host has
//! no native OpenSSL dependency and builds identically across distributions.

use hmac::{Hmac, Mac};
use sha2::{Digest, Sha256};

type HmacSha256 = Hmac<Sha256>;

pub fn compute_sha256(data: &[u8]) -> [u8; 32] {
    let digest = Sha256::digest(data);
    let mut out = [0u8; 32];
    out.copy_from_slice(&digest);
    out
}

pub fn compute_hmac_sha256(key: &[u8], parts: &[&[u8]]) -> [u8; 32] {
    // `new_from_slice` only fails for keys larger than the block size limit
    // accepted by the implementation; HMAC accepts keys of any length, so this
    // cannot fail for a 32-byte SEAM key.
    let mut mac = HmacSha256::new_from_slice(key).expect("HMAC accepts keys of any length");
    for part in parts {
        mac.update(part);
    }
    let digest = mac.finalize().into_bytes();
    let mut out = [0u8; 32];
    out.copy_from_slice(&digest);
    out
}

pub fn constant_time_eq(a: &[u8], b: &[u8]) -> bool {
    if a.len() != b.len() {
        return false;
    }
    let mut diff = 0u8;
    for (x, y) in a.iter().zip(b.iter()) {
        diff |= x ^ y;
    }
    diff == 0
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn sha256_matches_known_vectors() {
        assert_eq!(
            hex(&compute_sha256(b"abc")),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        );
        assert_eq!(
            hex(&compute_sha256(b"")),
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
        );
    }

    #[test]
    fn hmac_sha256_matches_rfc4231_case_2() {
        // RFC 4231 test case 2: key = "Jefe", data = "what do ya want for nothing?"
        assert_eq!(
            hex(&compute_hmac_sha256(
                b"Jefe",
                &[b"what do ya want for nothing?"]
            )),
            "5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843"
        );
    }

    #[test]
    fn hmac_is_order_sensitive_for_domains() {
        let key = [0x11u8; 32];
        let a = compute_hmac_sha256(&key, &[b"SEAM-CLIENT-AUTH-v1", b"nonce"]);
        let b = compute_hmac_sha256(&key, &[b"nonce", b"SEAM-CLIENT-AUTH-v1"]);
        assert_ne!(a, b);
    }

    fn hex(bytes: &[u8]) -> String {
        bytes.iter().map(|b| format!("{b:02x}")).collect()
    }
}
