using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Seam.WindowsHost.Protocol
{
    /// <summary>
    /// Mutual HMAC-SHA256 challenge/response authenticator — the client (host) side only.
    ///
    /// Exact mirror of the client path in:
    ///   macos-host/Sources/SeamHost/Protocol/SeamAuthenticator.swift  (authenticateClientStream)
    /// and the server path in:
    ///   android-agent/src/main/java/com/seam/agent/protocol/SeamAuthenticator.java
    ///
    ///  1. Server -> Client, 38 bytes: "SEAM"(4) | version(1) | transport(1) | serverNonce(32)
    ///  2. Client -> Server, 68 bytes: "SEAM"(4) | clientNonce(32) | clientHmac(32)
    ///       clientHmac = HMAC-SHA256(key, "SEAM-CLIENT-AUTH-v1" | serverNonce | clientNonce)
    ///  3. Server -> Client, 41 bytes: status(1) | sessionId(8, LE) | serverHmac(32)
    ///       serverHmac = HMAC-SHA256(key, "SEAM-SERVER-AUTH-v1" | clientNonce | serverNonce | sessionIdLe)
    ///
    /// Note: after this handshake the stream is NOT encrypted. That is the current
    /// behaviour of the shipped macOS host and Android agent; this host preserves it
    /// rather than inventing a second, incompatible security layer.
    /// </summary>
    public sealed class SeamAuthenticator
    {
        public const int NonceSize = 32;
        public const int HmacSize = 32;

        private static readonly byte[] ClientDomain = Encoding.ASCII.GetBytes("SEAM-CLIENT-AUTH-v1");
        private static readonly byte[] ServerDomain = Encoding.ASCII.GetBytes("SEAM-SERVER-AUTH-v1");

        public byte[] Key { get; }

        public string KeyHex => Convert.ToHexString(Key).ToLowerInvariant();

        public SeamAuthenticator(byte[] key)
        {
            if (key == null || key.Length < 16)
            {
                throw new ArgumentException("Authentication key must be at least 16 bytes", nameof(key));
            }
            Key = key;
        }

        /// <summary>The on-disk pairing key location (%USERPROFILE%\.seam\auth_key).</summary>
        public static string KeyStorePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".seam", "auth_key");

        /// <summary>
        /// Non-reversible public identifier for the pairing key: the first 16 hex characters of
        /// SHA-256(key). Safe to log and to compare between two hosts; the key itself never is.
        /// </summary>
        public string KeyFingerprint =>
            Convert.ToHexString(SHA256.HashData(Key)).Substring(0, 16).ToLowerInvariant();

        /// <summary>
        /// Loads or creates the persistent 256-bit pairing key.
        /// macOS stores ~/.seam/auth_key; we mirror that layout at %USERPROFILE%\.seam\auth_key
        /// so the file name and format match the other host implementation.
        /// </summary>
        public static SeamAuthenticator LoadOrCreatePersistentKey()
        {
            string keyFile = KeyStorePath;
            string dir = Path.GetDirectoryName(keyFile) ?? ".";

            try
            {
                if (File.Exists(keyFile))
                {
                    string hex = File.ReadAllText(keyFile).Trim();
                    byte[]? parsed = ParseHexKey(hex);
                    if (parsed != null && parsed.Length == 32)
                    {
                        return new SeamAuthenticator(parsed);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WARN] Could not read {keyFile}: {ex.Message}");
            }

            byte[] generated = RandomNumberGenerator.GetBytes(32);
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(keyFile, Convert.ToHexString(generated).ToLowerInvariant());
                Console.WriteLine($"[Auth] Generated new pairing key at {keyFile}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WARN] Could not persist pairing key: {ex.Message}");
            }
            return new SeamAuthenticator(generated);
        }

        public static byte[]? ParseHexKey(string hex)
        {
            string clean = hex.Trim();
            if (clean.Length != 64) return null;
            try { return Convert.FromHexString(clean); }
            catch (FormatException) { return null; }
        }

        /// <summary>
        /// Runs the client side of the handshake over a blocking byte stream and returns the
        /// 64-bit session id assigned by the agent.
        /// </summary>
        /// <param name="clientNonceOverride">
        /// Test-only hook: supplies a fixed client nonce so the exact 68-byte response can be
        /// asserted against vectors derived independently from the Java server implementation.
        /// Production callers must pass null.
        /// </param>
        public ulong AuthenticateClientStream(
            Func<int, byte[]> readExact,
            Action<byte[]> writeAll,
            byte[]? clientNonceOverride = null)
        {
            // 1. Server challenge
            byte[] challenge = readExact(38);
            if (challenge[0] != SeamWireProtocol.Magic[0] || challenge[1] != SeamWireProtocol.Magic[1] ||
                challenge[2] != SeamWireProtocol.Magic[2] || challenge[3] != SeamWireProtocol.Magic[3])
            {
                throw new InvalidOperationException("Invalid server protocol magic");
            }
            if (challenge[4] != SeamWireProtocol.ProtocolVersion)
            {
                throw new InvalidOperationException($"Unsupported protocol version: {challenge[4]}");
            }
            byte[] serverNonce = new byte[NonceSize];
            Array.Copy(challenge, 6, serverNonce, 0, NonceSize); // byte 5 = transport id

            // 2. Client response
            byte[] clientNonce = clientNonceOverride ?? RandomNumberGenerator.GetBytes(NonceSize);
            if (clientNonce.Length != NonceSize)
            {
                throw new ArgumentException("Client nonce override must be 32 bytes", nameof(clientNonceOverride));
            }
            byte[] clientHmac = ComputeHmac(ClientDomain, serverNonce, clientNonce, null);

            byte[] response = new byte[68];
            Array.Copy(SeamWireProtocol.Magic, 0, response, 0, 4);
            Array.Copy(clientNonce, 0, response, 4, NonceSize);
            Array.Copy(clientHmac, 0, response, 36, HmacSize);
            writeAll(response);

            // 3. Server acknowledgement
            byte[] ack = readExact(41);
            if (ack[0] != SeamWireProtocol.AuthStatusOk)
            {
                throw new InvalidOperationException(
                    $"Server rejected client HMAC (status=0x{ack[0]:X2})");
            }

            byte[] sessionIdLe = new byte[8];
            Array.Copy(ack, 1, sessionIdLe, 0, 8);
            byte[] serverHmac = new byte[HmacSize];
            Array.Copy(ack, 9, serverHmac, 0, HmacSize);

            byte[] expected = ComputeHmac(ServerDomain, clientNonce, serverNonce, sessionIdLe);
            if (!ConstantTimeEquals(serverHmac, expected))
            {
                throw new InvalidOperationException("Server HMAC verification failed");
            }

            ulong sessionId = 0;
            for (int i = 0; i < 8; i++)
            {
                sessionId |= (ulong)sessionIdLe[i] << (i * 8);
            }
            return sessionId;
        }

        private byte[] ComputeHmac(byte[] domain, byte[] firstNonce, byte[] secondNonce, byte[]? extra)
        {
            using var hmac = new HMACSHA256(Key);
            hmac.TransformBlock(domain, 0, domain.Length, null, 0);
            hmac.TransformBlock(firstNonce, 0, firstNonce.Length, null, 0);
            hmac.TransformBlock(secondNonce, 0, secondNonce.Length, null, 0);
            if (extra != null)
            {
                hmac.TransformBlock(extra, 0, extra.Length, null, 0);
            }
            hmac.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return hmac.Hash!;
        }

        private static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
            {
                diff |= a[i] ^ b[i];
            }
            return diff == 0;
        }
    }
}
