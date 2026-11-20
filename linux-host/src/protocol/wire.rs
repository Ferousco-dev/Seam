use std::io::{self, Read};

pub const MAGIC_BYTES: &[u8; 4] = b"SEAM";
pub const VERSION_BYTE: u8 = 1;
pub const LOCAL_SOCKET: &str = "seam_uhid";
pub const DEFAULT_PORT: u16 = 27186;
pub const MAX_CLIP_LEN: usize = 256 * 1024;
pub const AUTH_STATUS_OK: u8 = 0x01;
pub const AUTH_STATUS_FAILED: u8 = 0xFF;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
pub enum EdgeSide {
    Left = 0,
    Right = 1,
    Top = 2,
    Bottom = 3,
}

impl EdgeSide {
    pub fn decode(val: u8) -> Option<Self> {
        match val {
            0 => Some(Self::Left),
            1 => Some(Self::Right),
            2 => Some(Self::Top),
            3 => Some(Self::Bottom),
            _ => None,
        }
    }
}

pub fn to_normalized_u16(coord: i32, max_extent: i32) -> u16 {
    if max_extent <= 1 {
        return 0;
    }
    let bounded = coord.clamp(0, max_extent - 1) as f64;
    ((bounded / (max_extent - 1) as f64) * 65535.0).round() as u16
}

pub fn from_normalized_u16(val: u16, max_extent: i32) -> i32 {
    if max_extent <= 1 {
        return 0;
    }
    ((val as f64 / 65535.0) * (max_extent - 1) as f64).round() as i32
}

pub use from_normalized_u16 as denormalize_coordinate;
pub use to_normalized_u16 as normalize_coordinate;

pub fn read_prefixed_utf8<R: Read>(stream: &mut R) -> io::Result<String> {
    let mut len_bytes = [0u8; 4];
    stream.read_exact(&mut len_bytes)?;
    let count = u32::from_le_bytes(len_bytes) as usize;
    if count > MAX_CLIP_LEN {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "Payload too large",
        ));
    }
    if count == 0 {
        return Ok(String::new());
    }
    let mut buf = vec![0u8; count];
    stream.read_exact(&mut buf)?;
    // Match the macOS and Android hosts, which decode clipboard payloads
    // lossily rather than dropping the whole connection on invalid UTF-8.
    Ok(String::from_utf8_lossy(&buf).into_owned())
}
