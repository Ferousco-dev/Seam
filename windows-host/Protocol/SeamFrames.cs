using System;
using System.Buffers.Binary;
using System.Text;

namespace Seam.WindowsHost.Protocol
{
    /// <summary>
    /// Outbound `SEAM-Wire v1` frame serializers.
    /// Mirror of SeamOutboundFrame.serialize() in SeamWireProtocol.swift.
    ///
    /// Payload layouts are byte-identical to the fixed payload lengths declared by the
    /// Android agent, so the agent can copy mouse/key payloads straight into a
    /// UHID_INPUT2 buffer with no re-serialization.
    /// </summary>
    public static class SeamFrames
    {
        /// <summary>OP_MOUSE_REPORT (0x01): buttons(1) dx(2) dy(2) vWheel(1) hWheel(1) => 8 bytes.</summary>
        public static byte[] MouseReport(byte buttons, short dx, short dy, sbyte vWheel, sbyte hWheel)
        {
            byte[] f = new byte[1 + SeamWireProtocol.LenMouseReport];
            f[0] = SeamWireProtocol.OpMouseReport;
            f[1] = (byte)(buttons & 0x1F);
            BinaryPrimitives.WriteInt16LittleEndian(f.AsSpan(2, 2), dx);
            BinaryPrimitives.WriteInt16LittleEndian(f.AsSpan(4, 2), dy);
            f[6] = (byte)vWheel;
            f[7] = (byte)hWheel;
            return f;
        }

        /// <summary>OP_KEY_REPORT (0x02): modifiers(1) reserved(1) keys[6] => 9 bytes.</summary>
        public static byte[] KeyReport(byte modifiers, byte k0, byte k1, byte k2, byte k3, byte k4, byte k5)
        {
            return new byte[]
            {
                SeamWireProtocol.OpKeyReport,
                modifiers,
                0x00,
                k0, k1, k2, k3, k4, k5
            };
        }

        /// <summary>OP_CURSOR_ENTER (0x03): edge(1) normX(2) normY(2) flags(1) => 7 bytes.</summary>
        public static byte[] CursorEnter(byte edge, ushort normX, ushort normY, byte flags)
        {
            byte[] f = new byte[1 + SeamWireProtocol.LenCursorEnter];
            f[0] = SeamWireProtocol.OpCursorEnter;
            f[1] = edge;
            BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(2, 2), normX);
            BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(4, 2), normY);
            f[6] = flags;
            return f;
        }

        /// <summary>OP_CURSOR_LEAVE (0x04): reason(1) => 2 bytes.</summary>
        public static byte[] CursorLeave(byte reason)
        {
            return new byte[] { SeamWireProtocol.OpCursorLeave, reason };
        }

        /// <summary>OP_CLIPBOARD_SET (0x05): uint32_le length + UTF-8 => 5 + N bytes.</summary>
        public static byte[] ClipboardSet(string text)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(text);
            if (utf8.Length > SeamWireProtocol.MaxClipboardBytes)
            {
                // Trim on a UTF-8 boundary, exactly as the Swift host does.
                int end = SeamWireProtocol.MaxClipboardBytes;
                while (end > 0 && (utf8[end] & 0xC0) == 0x80) end--;
                Array.Resize(ref utf8, end);
            }

            byte[] f = new byte[5 + utf8.Length];
            f[0] = SeamWireProtocol.OpClipboardSet;
            BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(1, 4), (uint)utf8.Length);
            Array.Copy(utf8, 0, f, 5, utf8.Length);
            return f;
        }

        /// <summary>OP_PING (0x06): uint64_le timestamp_us => 9 bytes.</summary>
        public static byte[] Ping(ulong timestampUs)
        {
            byte[] f = new byte[1 + SeamWireProtocol.LenPingPong];
            f[0] = SeamWireProtocol.OpPing;
            BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(1, 8), timestampUs);
            return f;
        }
    }
}
