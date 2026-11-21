using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using Seam.WindowsHost.Protocol;

namespace Seam.WindowsHost.Transport
{
    /// <summary>
    /// A live, authenticated `SEAM-Wire v1` session to the Android agent over a TCP byte stream.
    ///
    /// Both existing transports reduce to this on the host side:
    ///   - USB : `adb forward tcp:&lt;port&gt; localabstract:seam_uhid`  -> connect to 127.0.0.1:&lt;port&gt;
    ///   - Wi-Fi: connect straight to the phone's TCP control port (27186)
    ///
    /// IMPORTANT: <see cref="Connect"/> completes the handshake but does NOT start reading.
    /// The caller must attach event handlers and then call <see cref="Start"/>, because the
    /// agent sends `DISPLAY_INFO` immediately after authentication — starting the reader
    /// before the handlers exist loses that frame (a real race found in review).
    /// </summary>
    public sealed class SeamConnection : IDisposable
    {
        /// <summary>
        /// Sync write budget. Must stay below the Windows low-level-hook timeout
        /// (LowLevelHooksTimeout, ~300 ms by default) because mouse/keyboard frames are
        /// written from inside the WH_MOUSE_LL / WH_KEYBOARD_LL callbacks. A blocked write
        /// longer than that gets the hook silently removed by Windows.
        /// Trade-off: a >250 ms stalled peer drops frames and faults the session (safe).
        /// </summary>
        private const int WriteTimeoutMs = 250;

        /// <summary>Read budget: a silent peer (no PONG for 10 s) means the link is dead.</summary>
        private const int ReadTimeoutMs = 10_000;

        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private Thread? _readerThread;
        private Timer? _heartbeatTimer;
        private readonly object _writeLock = new object();
        private int _closeSignalled;
        private volatile bool _running;
        private long _framesSent;
        private long _framesReceived;

        public ulong SessionId { get; }
        public string Endpoint { get; }
        public long FramesSent => Interlocked.Read(ref _framesSent);
        public long FramesReceived => Interlocked.Read(ref _framesReceived);
        public bool IsRunning => _running;

        public event Action<AndroidDisplayInfo>? DisplayInfoReceived;
        public event Action<AndroidEdgeReturnEvent>? EdgeReturnReceived;
        public event Action<string>? ClipboardReceived;
        public event Action<ulong>? PongReceived;
        public event Action<string>? Closed;

        private SeamConnection(TcpClient client, ulong sessionId, string endpoint)
        {
            _client = client;
            _stream = client.GetStream();
            _stream.ReadTimeout = ReadTimeoutMs;
            _stream.WriteTimeout = WriteTimeoutMs;
            SessionId = sessionId;
            Endpoint = endpoint;
        }

        /// <summary>Connects and runs the mutual HMAC-SHA256 handshake. Throws on failure.</summary>
        public static SeamConnection Connect(string host, int port, SeamAuthenticator authenticator)
        {
            var tcp = new TcpClient
            {
                NoDelay = true // TCP_NODELAY, as in both reference transports
            };

            try
            {
                tcp.Connect(host, port);
            }
            catch (Exception ex)
            {
                tcp.Dispose();
                throw new IOException($"connect({host}:{port}) failed: {ex.Message}", ex);
            }

            NetworkStream stream = tcp.GetStream();
            ulong sessionId;
            try
            {
                // The handshake itself is a well-bounded exchange; give it generous timeouts.
                stream.ReadTimeout = 5000;
                stream.WriteTimeout = 5000;
                sessionId = authenticator.AuthenticateClientStream(
                    n => ReadExact(stream, n),
                    b => WriteAll(stream, b));
            }
            catch
            {
                tcp.Dispose();
                throw;
            }

            return new SeamConnection(tcp, sessionId, $"{host}:{port}");
        }

        /// <summary>Starts the frame reader and the 500 ms heartbeat. Call after wiring handlers.</summary>
        public void Start()
        {
            if (_running) return;
            _running = true;

            _readerThread = new Thread(ReaderLoop)
            {
                IsBackground = true,
                Name = "seam-reader"
            };
            _readerThread.Start();

            // The agent sets SO_TIMEOUT = 3000 ms on its side (SeamProtocol.HEARTBEAT_TIMEOUT_MS),
            // so the host must keep traffic flowing or the session is torn down. The macOS host
            // pings every 500 ms; match that.
            _heartbeatTimer = new Timer(_ =>
            {
                try { Send(SeamFrames.Ping(MonotonicMicroseconds())); }
                catch { /* the reader/fault path reports the failure */ }
            }, null, 500, 500);
        }

        public void SendPing() => Send(SeamFrames.Ping(MonotonicMicroseconds()));

        public void Send(byte[] frame)
        {
            if (!_running) throw new IOException("Connection is not running");

            lock (_writeLock)
            {
                if (!_running) throw new IOException("Connection is not running");
                try
                {
                    WriteAll(_stream, frame);
                    Interlocked.Increment(ref _framesSent);
                }
                catch (Exception ex)
                {
                    // A failed/partial write leaves the stream unusable: fault the session
                    // rather than let later frames corrupt the stream framing.
                    _running = false;
                    SignalClosed($"write failed: {ex.Message}");
                    throw;
                }
            }
        }

        private void ReaderLoop()
        {
            string reason = "Peer closed the connection";
            try
            {
                while (_running)
                {
                    byte opcode = ReadExact(_stream, 1)[0];

                    switch (opcode)
                    {
                        case SeamWireProtocol.OpDisplayInfo:
                        {
                            byte[] payload = ReadExact(_stream, SeamWireProtocol.LenDisplayInfo);
                            Interlocked.Increment(ref _framesReceived);
                            DisplayInfoReceived?.Invoke(AndroidDisplayInfo.Parse(payload));
                            break;
                        }
                        case SeamWireProtocol.OpEdgeReturn:
                        {
                            byte[] payload = ReadExact(_stream, SeamWireProtocol.LenEdgeReturn);
                            Interlocked.Increment(ref _framesReceived);
                            EdgeReturnReceived?.Invoke(AndroidEdgeReturnEvent.Parse(payload));
                            break;
                        }
                        case SeamWireProtocol.OpClipboardEvent:
                        {
                            string text = SeamWireProtocol.ReadClipboardText(n => ReadExact(_stream, n));
                            Interlocked.Increment(ref _framesReceived);
                            ClipboardReceived?.Invoke(text);
                            break;
                        }
                        case SeamWireProtocol.OpPong:
                        {
                            byte[] payload = ReadExact(_stream, SeamWireProtocol.LenPingPong);
                            Interlocked.Increment(ref _framesReceived);
                            ulong sentUs = 0;
                            for (int i = 0; i < 8; i++)
                            {
                                sentUs |= (ulong)payload[i] << (i * 8);
                            }
                            PongReceived?.Invoke(sentUs);
                            break;
                        }
                        default:
                            throw new IOException($"Unknown inbound opcode 0x{opcode:X2}");
                    }
                }
            }
            catch (Exception ex)
            {
                if (_running) reason = ex.Message;
            }
            finally
            {
                bool wasRunning = _running;
                _running = false;
                if (wasRunning)
                {
                    SignalClosed(reason);
                }
            }
        }

        /// <summary>Idempotent teardown + notification, safe from any thread.</summary>
        private void SignalClosed(string reason)
        {
            if (Interlocked.Exchange(ref _closeSignalled, 1) != 0) return;

            try { _heartbeatTimer?.Dispose(); } catch { }
            try { _stream.Dispose(); } catch { }
            try { _client.Close(); } catch { }
            Closed?.Invoke(reason);
        }

        private static byte[] ReadExact(NetworkStream stream, int count)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read <= 0) throw new IOException("Remote peer closed the connection (EOF)");
                offset += read;
            }
            return buffer;
        }

        private static void WriteAll(NetworkStream stream, byte[] bytes)
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        /// <summary>Monotonic microsecond clock (mirror of MonotonicClock.nowMicroseconds()).</summary>
        public static ulong MonotonicMicroseconds()
        {
            return (ulong)(Stopwatch.GetTimestamp() * (1_000_000.0 / Stopwatch.Frequency));
        }

        public void Dispose()
        {
            bool wasRunning = _running;
            _running = false;
            if (wasRunning)
            {
                // Deliberate shutdown: close resources but do not raise Closed as a failure.
                Interlocked.Exchange(ref _closeSignalled, 1);
                try { _heartbeatTimer?.Dispose(); } catch { }
                try { _stream.Dispose(); } catch { }
                try { _client.Close(); } catch { }
            }
            else
            {
                SignalClosed("disposed");
            }
        }
    }
}
