using System;
using System.Buffers.Binary;
using System.Text;

namespace Seam.WindowsHost.Protocol
{
    /// <summary>
    /// `SEAM-Wire v1` constants and helpers.
    ///
    /// Byte-for-byte mirror of:
    ///   macos-host/Sources/SeamHost/Protocol/SeamWireProtocol.swift
    ///   android-agent/src/main/java/com/seam/agent/protocol/SeamProtocol.java
    ///
    /// Every multi-byte field is little-endian. Do not "improve" any value here:
    /// the Android agent is unchanged and will reject anything that drifts.
    /// </summary>
    public static class SeamWireProtocol
    {
        public static readonly byte[] Magic = { 0x53, 0x45, 0x41, 0x4D }; // "SEAM"
        public const byte ProtocolVersion = 0x01;

        public const string UsbAbstractSocketName = "seam_uhid";
        public const int WifiDefaultPort = 27186;
        public const int MaxClipboardBytes = 256 * 1024;

        // Transport identifiers (used in the 38-byte server challenge)
        public const byte TransportUsb = 0x01;
        public const byte TransportWifi = 0x02;

        // Handshake status byte
        public const byte AuthStatusOk = 0x01;
        public const byte AuthStatusFailed = 0xFF;

        // Screen boundary edges
        public const byte EdgeLeft = 0x00;
        public const byte EdgeRight = 0x01;
        public const byte EdgeTop = 0x02;
        public const byte EdgeBottom = 0x03;

        // Host -> Android opcodes
        public const byte OpMouseReport = 0x01;
        public const byte OpKeyReport = 0x02;
        public const byte OpCursorEnter = 0x03;
        public const byte OpCursorLeave = 0x04;
        public const byte OpClipboardSet = 0x05;
        public const byte OpPing = 0x06;

        // Android -> Host opcodes
        public const byte OpDisplayInfo = 0x81;
        public const byte OpEdgeReturn = 0x82;
        public const byte OpClipboardEvent = 0x83;
        public const byte OpPong = 0x84;

        // Fixed payload lengths (payload only; the opcode byte is separate)
        public const int LenMouseReport = 7;
        public const int LenKeyReport = 8;
        public const int LenCursorEnter = 6;
        public const int LenCursorLeave = 1;
        public const int LenPingPong = 8;
        public const int LenDisplayInfo = 10;
        public const int LenEdgeReturn = 6;

        /// <summary>Mirror of SeamProtocol.normalizeCoordinate (pixel -> 0..65535).</summary>
        public static ushort NormalizeCoordinate(int pixel, int dimensionPx)
        {
            if (dimensionPx <= 1) return 0;
            int clamped = Math.Max(0, Math.Min(dimensionPx - 1, pixel));
            double scaled = ((double)clamped / (dimensionPx - 1)) * 65535.0;
            return (ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(scaled)));
        }

        /// <summary>Mirror of SeamProtocol.denormalizeCoordinate (0..65535 -> pixel).</summary>
        public static int DenormalizeCoordinate(ushort norm, int dimensionPx)
        {
            if (dimensionPx <= 1) return 0;
            return (int)Math.Round(((double)norm / 65535.0) * (dimensionPx - 1));
        }

        /// <summary>
        /// Reads a length-prefixed UTF-8 clipboard payload: uint32_le length + utf8 bytes.
        /// Mirror of SeamWireProtocol.readClipboardText / SeamProtocol.readClipboardSetText.
        /// </summary>
        public static string ReadClipboardText(Func<int, byte[]> readExact)
        {
            byte[] lenBytes = readExact(4);
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(lenBytes);
            if (length > MaxClipboardBytes)
            {
                throw new InvalidOperationException($"Invalid clipboard payload length: {length}");
            }
            if (length == 0) return string.Empty;
            byte[] payload = readExact((int)length);
            return Encoding.UTF8.GetString(payload);
        }
    }

    /// <summary>Parsed 10-byte DISPLAY_INFO (0x81) telemetry frame.</summary>
    public sealed class AndroidDisplayInfo
    {
        public int WidthPx { get; init; }
        public int HeightPx { get; init; }
        public int DensityDpi { get; init; }
        public int Rotation { get; init; }
        public int RefreshRateHz { get; init; }
        public ushort Flags { get; init; }

        public int RotationDegrees => Rotation switch { 1 => 90, 2 => 180, 3 => 270, _ => 0 };

        /// <summary>Parses the 10 payload bytes exactly as UsbAdbTransport.readerLoop does.</summary>
        public static AndroidDisplayInfo Parse(byte[] p)
        {
            if (p.Length != SeamWireProtocol.LenDisplayInfo)
            {
                throw new ArgumentException($"DISPLAY_INFO payload must be 10 bytes, got {p.Length}");
            }
            return new AndroidDisplayInfo
            {
                WidthPx = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(0, 2)),
                HeightPx = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(2, 2)),
                DensityDpi = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(4, 2)),
                Rotation = p[6],
                RefreshRateHz = p[7],
                Flags = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(8, 2))
            };
        }

        public override string ToString() =>
            $"{WidthPx}x{HeightPx} px @ {DensityDpi} dpi, rotation={RotationDegrees}°, {RefreshRateHz} Hz, flags=0x{Flags:X4}";
    }

    /// <summary>Parsed 6-byte EDGE_RETURN (0x82) telemetry frame.</summary>
    public sealed class AndroidEdgeReturnEvent
    {
        public byte Edge { get; init; }
        public ushort NormX { get; init; }
        public ushort NormY { get; init; }
        public byte Velocity { get; init; }

        public double NormalizedYRatio => NormY / 65535.0;

        public static AndroidEdgeReturnEvent Parse(byte[] p)
        {
            if (p.Length != SeamWireProtocol.LenEdgeReturn)
            {
                throw new ArgumentException($"EDGE_RETURN payload must be 6 bytes, got {p.Length}");
            }
            return new AndroidEdgeReturnEvent
            {
                Edge = p[0],
                NormX = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(1, 2)),
                NormY = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(3, 2)),
                Velocity = p[5]
            };
        }

        public override string ToString() =>
            $"EdgeReturn[edge={Edge}, normX={NormX}, normY={NormY} ({NormalizedYRatio * 100.0:F1}%), velocity={Velocity}]";
    }
}
