use crate::protocol::wire::{from_normalized_u16, read_prefixed_utf8, EdgeSide};
use std::io::{self, Read};

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum OutboundFrame {
    MouseReport {
        buttons: u8,
        dx: i16,
        dy: i16,
        v_wheel: i8,
        h_wheel: i8,
    },
    KeyReport {
        modifiers: u8,
        keys: [u8; 6],
    },
    CursorEnter {
        edge: EdgeSide,
        norm_x: u16,
        norm_y: u16,
        flags: u8,
    },
    CursorLeave {
        reason: u8,
    },
    ClipboardSet {
        text: String,
    },
    Ping {
        timestamp_us: u64,
    },
}

impl OutboundFrame {
    pub fn serialize(&self) -> Vec<u8> {
        match self {
            OutboundFrame::MouseReport {
                buttons,
                dx,
                dy,
                v_wheel,
                h_wheel,
            } => {
                let dx_bytes = dx.to_le_bytes();
                let dy_bytes = dy.to_le_bytes();
                vec![
                    0x01,
                    buttons & 0x1F,
                    dx_bytes[0],
                    dx_bytes[1],
                    dy_bytes[0],
                    dy_bytes[1],
                    *v_wheel as u8,
                    *h_wheel as u8,
                ]
            }
            OutboundFrame::KeyReport { modifiers, keys } => {
                vec![
                    0x02, *modifiers, 0x00, keys[0], keys[1], keys[2], keys[3], keys[4], keys[5],
                ]
            }
            OutboundFrame::CursorEnter {
                edge,
                norm_x,
                norm_y,
                flags,
            } => {
                let x_bytes = norm_x.to_le_bytes();
                let y_bytes = norm_y.to_le_bytes();
                vec![
                    0x03,
                    *edge as u8,
                    x_bytes[0],
                    x_bytes[1],
                    y_bytes[0],
                    y_bytes[1],
                    *flags,
                ]
            }
            OutboundFrame::CursorLeave { reason } => {
                vec![0x04, *reason]
            }
            OutboundFrame::ClipboardSet { text } => {
                let bytes = truncate_utf8(text, crate::protocol::wire::MAX_CLIP_LEN);
                let len = (bytes.len() as u32).to_le_bytes();
                let mut out = Vec::with_capacity(5 + bytes.len());
                out.push(0x05);
                out.extend_from_slice(&len);
                out.extend_from_slice(bytes);
                out
            }
            OutboundFrame::Ping { timestamp_us } => {
                let mut out = Vec::with_capacity(9);
                out.push(0x06);
                out.extend_from_slice(&timestamp_us.to_le_bytes());
                out
            }
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DisplayInfoRecord {
    pub width: u16,
    pub height: u16,
    pub density_dpi: u16,
    pub rotation: u8,
    pub refresh_rate: u8,
    pub flags: u16,
}

impl DisplayInfoRecord {
    pub fn rotation_degrees(&self) -> u32 {
        match self.rotation {
            1 => 90,
            2 => 180,
            3 => 270,
            _ => 0,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EdgeReturnRecord {
    pub edge: EdgeSide,
    pub norm_x: u16,
    pub norm_y: u16,
    pub velocity: u8,
}

impl EdgeReturnRecord {
    pub fn to_pixel_coords(&self, width: i32, height: i32) -> (i32, i32) {
        (
            from_normalized_u16(self.norm_x, width),
            from_normalized_u16(self.norm_y, height),
        )
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum InboundFrame {
    DisplayInfo(DisplayInfoRecord),
    EdgeReturn(EdgeReturnRecord),
    ClipboardEvent(String),
    Pong { timestamp_us: u64 },
}

impl InboundFrame {
    pub fn read_from<R: Read>(reader: &mut R) -> io::Result<Self> {
        let mut opcode_buf = [0u8; 1];
        reader.read_exact(&mut opcode_buf)?;
        match opcode_buf[0] {
            0x81 => {
                let mut p = [0u8; 10];
                reader.read_exact(&mut p)?;
                let width = u16::from_le_bytes([p[0], p[1]]);
                let height = u16::from_le_bytes([p[2], p[3]]);
                let density_dpi = u16::from_le_bytes([p[4], p[5]]);
                let rotation = p[6];
                let refresh_rate = p[7];
                let flags = u16::from_le_bytes([p[8], p[9]]);
                Ok(InboundFrame::DisplayInfo(DisplayInfoRecord {
                    width,
                    height,
                    density_dpi,
                    rotation,
                    refresh_rate,
                    flags,
                }))
            }
            0x82 => {
                let mut p = [0u8; 6];
                reader.read_exact(&mut p)?;
                let edge = EdgeSide::decode(p[0]).unwrap_or(EdgeSide::Left);
                let norm_x = u16::from_le_bytes([p[1], p[2]]);
                let norm_y = u16::from_le_bytes([p[3], p[4]]);
                let velocity = p[5];
                Ok(InboundFrame::EdgeReturn(EdgeReturnRecord {
                    edge,
                    norm_x,
                    norm_y,
                    velocity,
                }))
            }
            0x83 => {
                let text = read_prefixed_utf8(reader)?;
                Ok(InboundFrame::ClipboardEvent(text))
            }
            0x84 => {
                let mut p = [0u8; 8];
                reader.read_exact(&mut p)?;
                let timestamp_us = u64::from_le_bytes(p);
                Ok(InboundFrame::Pong { timestamp_us })
            }
            other => Err(io::Error::new(
                io::ErrorKind::InvalidData,
                format!("Unrecognized opcode 0x{:02X}", other),
            )),
        }
    }
}

/// Truncates `text` to at most `max_bytes` UTF-8 bytes without splitting a
/// multi-byte code point. Mirrors the macOS and Android implementations, which
/// walk backwards from the limit while the byte is a UTF-8 continuation byte.
pub fn truncate_utf8(text: &str, max_bytes: usize) -> &[u8] {
    let bytes = text.as_bytes();
    if bytes.len() <= max_bytes {
        return bytes;
    }
    let mut end = max_bytes;
    while end > 0 && (bytes[end] & 0xC0) == 0x80 {
        end -= 1;
    }
    &bytes[..end]
}
