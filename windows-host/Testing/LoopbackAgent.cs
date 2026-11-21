using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Seam.WindowsHost.Protocol;

namespace Seam.WindowsHost.Testing
{
    /// <summary>
    /// In-process stand-in for the Android agent's server side, used ONLY as a test fixture.
    ///
    /// This is a line-by-line transliteration of the shipped Java:
    ///   android-agent/.../protocol/SeamAuthenticator.java  (authenticateServerSocket)
    ///   android-agent/.../transport/UsbLocalSocketServer.java (handleAuthenticatedLoop)
    ///
    /// It is NOT a substitute for the real agent and proves nothing about /dev/uhid or
    /// Android input injection. It exists so the host's handshake and framing can be
    /// exercised end-to-end on a machine with no phone attached.
    /// </summary>
    public sealed class LoopbackAgent : IDisposable
    {
        private static readonly byte[] ClientDomain = Encoding.ASCII.GetBytes("SEAM-CLIENT-AUTH-v1");
        private static readonly byte[] ServerDomain = Encoding.ASCII.GetBytes("SEAM-SERVER-AUTH-v1");

        private readonly byte[] _key;
        private readonly TcpListener _listener;
        private Thread? _thread;
        private volatile bool _running;
        private TcpClient? _client;

        public int Port { get; }

        // Observable state for assertions
        public volatile int MouseFrames;
        public volatile int KeyFrames;
        public volatile int PingFrames;
        public volatile string? LastClipboardText;
        public volatile bool AuthSucceeded;
        public volatile string? AuthFailure;
        public int EdgeReturnNormalY = 32768;

        public LoopbackAgent(byte[] key)
        {
            _key = key;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }

        public void Start()
        {
            _running = true;
            _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "loopback-agent" };
            _thread.Start();
        }

        private void AcceptLoop()
        {
            try
            {
                _client = _listener.AcceptTcpClient();
                _client.NoDelay = true;
                NetworkStream stream = _client.GetStream();

                byte[] serverNonce = RandomNumberGenerator.GetBytes(32);

                // 1. Server challenge (38 bytes) — SeamAuthenticator.authenticateServerSocket
                byte[] challenge = new byte[38];
                Array.Copy(SeamWireProtocol.Magic, 0, challenge, 0, 4);
                challenge[4] = SeamWireProtocol.ProtocolVersion;
                challenge[5] = SeamWireProtocol.TransportUsb;
                Array.Copy(serverNonce, 0, challenge, 6, 32);
                stream.Write(challenge, 0, challenge.Length);
                stream.Flush();

                // 2. Client response (68 bytes)
                byte[] response = ReadExact(stream, 68);
                if (response[0] != 'S' || response[1] != 'E' || response[2] != 'A' || response[3] != 'M')
                {
                    AuthFailure = "bad client magic";
                    Fail(stream);
                    return;
                }

                byte[] clientNonce = new byte[32];
                Array.Copy(response, 4, clientNonce, 0, 32);
                byte[] clientHmac = new byte[32];
                Array.Copy(response, 36, clientHmac, 0, 32);

                byte[] expected = Hmac(ClientDomain, serverNonce, clientNonce, null);
                if (!CryptographicOperations.FixedTimeEquals(clientHmac, expected))
                {
                    AuthFailure = "client HMAC mismatch";
                    Fail(stream);
                    return;
                }

                // 3. Server ack (41 bytes)
                ulong sessionId = 0x0BADC0DE12345678UL;
                byte[] sessionIdLe = new byte[8];
                for (int i = 0; i < 8; i++) sessionIdLe[i] = (byte)((sessionId >> (i * 8)) & 0xFF);

                byte[] serverHmac = Hmac(ServerDomain, clientNonce, serverNonce, sessionIdLe);

                byte[] ack = new byte[41];
                ack[0] = SeamWireProtocol.AuthStatusOk;
                Array.Copy(sessionIdLe, 0, ack, 1, 8);
                Array.Copy(serverHmac, 0, ack, 9, 32);
                stream.Write(ack, 0, ack.Length);
                stream.Flush();

                AuthSucceeded = true;

                // UsbLocalSocketServer sends DISPLAY_INFO immediately after auth.
                SendDisplayInfo(stream, 1080, 2400, 420, 0, 120);

                // Frame loop
                while (_running)
                {
                    int opcode = ReadExact(stream, 1)[0];
                    switch (opcode)
                    {
                        case SeamWireProtocol.OpMouseReport:
                        {
                            byte[] p = ReadExact(stream, SeamWireProtocol.LenMouseReport);
                            MouseFrames++;
                            short dx = (short)(p[1] | (p[2] << 8));
                            // Simulate the agent's EDGE_RETURN when the cursor is pushed far left.
                            if (dx < 0 && MouseFrames % 40 == 0)
                            {
                                SendEdgeReturn(stream, SeamWireProtocol.EdgeLeft, 0, (ushort)EdgeReturnNormalY, 6);
                            }
                            break;
                        }
                        case SeamWireProtocol.OpKeyReport:
                            ReadExact(stream, SeamWireProtocol.LenKeyReport);
                            KeyFrames++;
                            break;
                        case SeamWireProtocol.OpCursorEnter:
                            ReadExact(stream, SeamWireProtocol.LenCursorEnter);
                            break;
                        case SeamWireProtocol.OpCursorLeave:
                            ReadExact(stream, SeamWireProtocol.LenCursorLeave);
                            break;
                        case SeamWireProtocol.OpClipboardSet:
                        {
                            uint len = BitConverter.ToUInt32(ReadExact(stream, 4), 0);
                            byte[] payload = len > 0 ? ReadExact(stream, (int)len) : Array.Empty<byte>();
                            LastClipboardText = Encoding.UTF8.GetString(payload);
                            break;
                        }
                        case SeamWireProtocol.OpPing:
                        {
                            byte[] ts = ReadExact(stream, SeamWireProtocol.LenPingPong);
                            PingFrames++;
                            byte[] pong = new byte[9];
                            pong[0] = SeamWireProtocol.OpPong;
                            Array.Copy(ts, 0, pong, 1, 8);
                            stream.Write(pong, 0, pong.Length);
                            stream.Flush();
                            break;
                        }
                        default:
                            return; // unknown opcode ends the session, like the Java server
                    }
                }
            }
            catch (Exception ex)
            {
                if (_running && AuthFailure == null && !AuthSucceeded)
                {
                    AuthFailure = ex.Message;
                }
            }
        }

        private static void Fail(NetworkStream stream)
        {
            byte[] fail = new byte[41];
            fail[0] = SeamWireProtocol.AuthStatusFailed;
            try { stream.Write(fail, 0, fail.Length); stream.Flush(); } catch { }
        }

        private static void SendDisplayInfo(NetworkStream s, int w, int h, int dpi, int rot, int hz)
        {
            byte[] f = new byte[11];
            f[0] = SeamWireProtocol.OpDisplayInfo;
            f[1] = (byte)(w & 0xFF); f[2] = (byte)((w >> 8) & 0xFF);
            f[3] = (byte)(h & 0xFF); f[4] = (byte)((h >> 8) & 0xFF);
            f[5] = (byte)(dpi & 0xFF); f[6] = (byte)((dpi >> 8) & 0xFF);
            f[7] = (byte)rot;
            f[8] = (byte)hz;
            f[9] = 0; f[10] = 0;
            s.Write(f, 0, f.Length);
            s.Flush();
        }

        public static void SendEdgeReturn(NetworkStream s, byte edge, ushort normX, ushort normY, byte velocity)
        {
            byte[] f = new byte[7];
            f[0] = SeamWireProtocol.OpEdgeReturn;
            f[1] = edge;
            f[2] = (byte)(normX & 0xFF); f[3] = (byte)((normX >> 8) & 0xFF);
            f[4] = (byte)(normY & 0xFF); f[5] = (byte)((normY >> 8) & 0xFF);
            f[6] = velocity;
            s.Write(f, 0, f.Length);
            s.Flush();
        }

        private byte[] Hmac(byte[] domain, byte[] a, byte[] b, byte[]? extra)
        {
            using var hmac = new HMACSHA256(_key);
            hmac.TransformBlock(domain, 0, domain.Length, null, 0);
            hmac.TransformBlock(a, 0, a.Length, null, 0);
            hmac.TransformBlock(b, 0, b.Length, null, 0);
            if (extra != null) hmac.TransformBlock(extra, 0, extra.Length, null, 0);
            hmac.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return hmac.Hash!;
        }

        private static byte[] ReadExact(NetworkStream stream, int count)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read <= 0) throw new IOException("client closed");
                offset += read;
            }
            return buffer;
        }

        public void Dispose()
        {
            _running = false;
            try { _client?.Close(); } catch { }
            try { _listener.Stop(); } catch { }
        }
    }
}
