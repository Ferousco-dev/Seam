#!/usr/bin/env python3
"""
SEAM device-side simulator (TEST FIXTURE — not part of the product).

Transliterates the *shipped* Android agent's server code so a host can be exercised
without a phone:

    android-agent/.../protocol/SeamAuthenticator.java   -> authenticate_server()
    android-agent/.../transport/UsbLocalSocketServer.java -> handle_authenticated_loop()
    android-agent/.../core/SeamInputEngine.java         -> subset: shadow cursor + EDGE_RETURN

What this proves: the host's handshake bytes, framing, heartbeat and telemetry parsing
match the Java implementation's expectations.

What this does NOT prove: anything about /dev/uhid, Android input injection, OEM ROM
behaviour, or real device latency. Those need the real agent on real hardware.

Standalone use (lets you test a Windows host with no Android device):
    python3 tools/agent_sim.py --port 27186 --verbose
    seam-host.exe --connect 127.0.0.1:27186
"""

from __future__ import annotations

import argparse
import hashlib
import hmac
import os
import socket
import struct
import sys
import threading
import time

# ---- Constants (mirrors SeamProtocol.java) ------------------------------------------
MAGIC = b"SEAM"
VERSION = 1
TRANSPORT_USB = 0x01
TRANSPORT_WIFI = 0x02

AUTH_OK = 0x01
AUTH_FAILED = 0xFF

EDGE_LEFT, EDGE_RIGHT, EDGE_TOP, EDGE_BOTTOM = 0, 1, 2, 3

OP_MOUSE_REPORT = 0x01
OP_KEY_REPORT = 0x02
OP_CURSOR_ENTER = 0x03
OP_CURSOR_LEAVE = 0x04
OP_CLIPBOARD_SET = 0x05
OP_PING = 0x06

OP_DISPLAY_INFO = 0x81
OP_EDGE_RETURN = 0x82
OP_CLIPBOARD_EVENT = 0x83
OP_PONG = 0x84

LEN_MOUSE_REPORT = 7
LEN_KEY_REPORT = 8
LEN_CURSOR_ENTER = 6
LEN_CURSOR_LEAVE = 1
LEN_PING_PONG = 8
LEN_DISPLAY_INFO = 10
LEN_EDGE_RETURN = 6
MAX_CLIPBOARD_BYTES = 256 * 1024

HEARTBEAT_TIMEOUT_MS = 3000
ENTRY_GRACE_MS = 160
EDGE_RETURN_PUSH_THRESHOLD_PX = 4

CLIENT_DOMAIN = b"SEAM-CLIENT-AUTH-v1"
SERVER_DOMAIN = b"SEAM-SERVER-AUTH-v1"

FIXED_SESSION_ID = 0x0BADC0DE12345678


def _recv_exact(sock: socket.socket, count: int) -> bytes:
    buf = bytearray()
    while len(buf) < count:
        chunk = sock.recv(count - len(buf))
        if not chunk:
            raise ConnectionError("peer closed")
        buf += chunk
    return bytes(buf)


def _denorm(norm: int, dimension: int) -> int:
    """SeamProtocol.denormalizeCoordinate"""
    if dimension <= 1:
        return 0
    return round((max(0, min(65535, norm)) / 65535.0) * (dimension - 1))


def _norm(pixel: int, dimension: int) -> int:
    """SeamProtocol.normalizeCoordinate"""
    if dimension <= 1:
        return 0
    clamped = max(0, min(dimension - 1, pixel))
    return round((clamped / (dimension - 1)) * 65535.0)


class AgentSim:
    """A single-session TCP stand-in for the Android agent's USB/Wi-Fi servers."""

    def __init__(self, key: bytes, port: int = 0, display=(1080, 2400, 420, 0, 120), verbose=True):
        if len(key) < 16:
            raise ValueError("key must be >= 16 bytes")
        self.key = key
        self.display = display
        self.verbose = verbose

        self._sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self._sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self._sock.bind(("127.0.0.1", port))
        self._sock.listen(1)
        self.port = self._sock.getsockname()[1]

        self._thread: threading.Thread | None = None
        self._stop = threading.Event()

        # Observable state for assertions
        self.auth_ok = False
        self.auth_error: str | None = None
        self.session_id = 0
        self.mouse_frames = 0
        self.key_frames = 0
        self.ping_frames = 0
        self.clipboard_texts: list[str] = []
        self.edge_returns_sent = 0
        self.last_mouse = (0, 0, 0)
        self.disconnected = threading.Event()

    # -- lifecycle ---------------------------------------------------------------------
    def start(self):
        self._thread = threading.Thread(target=self._run, name="agent-sim", daemon=True)
        self._thread.start()
        return self

    def stop(self):
        self._stop.set()
        try:
            self._sock.close()
        except OSError:
            pass

    def __enter__(self):
        return self.start()

    def __exit__(self, *_exc):
        self.stop()

    def _log(self, msg: str):
        if self.verbose:
            print(f"[agent-sim] {msg}", flush=True)

    # -- server ------------------------------------------------------------------------
    def _run(self):
        try:
            client, addr = self._sock.accept()
        except OSError:
            return
        self._log(f"connection from {addr}")
        try:
            client.settimeout(HEARTBEAT_TIMEOUT_MS / 1000.0)  # Java: setSoTimeout(3000)
            self._serve(client)
        except socket.timeout:
            self._log("heartbeat timeout — session closed (host stopped pinging)")
        except (ConnectionError, OSError) as exc:
            if not self._stop.is_set():
                self._log(f"session ended: {exc}")
        finally:
            try:
                client.close()
            except OSError:
                pass
            self.disconnected.set()

    def _hmac(self, domain: bytes, first: bytes, second: bytes, extra: bytes | None = None) -> bytes:
        mac = hmac.new(self.key, domain, hashlib.sha256)
        mac.update(first)
        mac.update(second)
        if extra is not None:
            mac.update(extra)
        return mac.digest()

    def _serve(self, client: socket.socket):
        # 1. 38-byte server challenge
        server_nonce = os.urandom(32)
        client.sendall(MAGIC + bytes([VERSION, TRANSPORT_USB]) + server_nonce)

        # 2. 68-byte client response
        response = _recv_exact(client, 68)
        if response[0:4] != MAGIC:
            self.auth_error = "invalid client magic"
            client.sendall(bytes([AUTH_FAILED]) + b"\x00" * 40)
            return

        client_nonce = response[4:36]
        client_hmac = response[36:68]
        expected = self._hmac(CLIENT_DOMAIN, server_nonce, client_nonce)
        if not hmac.compare_digest(client_hmac, expected):
            self.auth_error = "client HMAC mismatch"
            client.sendall(bytes([AUTH_FAILED]) + b"\x00" * 40)
            return

        # 3. 41-byte server ack
        session_id = FIXED_SESSION_ID
        session_le = session_id.to_bytes(8, "little")
        server_hmac = self._hmac(SERVER_DOMAIN, client_nonce, server_nonce, session_le)
        client.sendall(bytes([AUTH_OK]) + session_le + server_hmac)

        self.auth_ok = True
        self.session_id = session_id
        self._log(f"mutual auth OK (sessionId=0x{session_id:016X})")

        # UsbLocalSocketServer sends DISPLAY_INFO immediately after auth.
        w, h, dpi, rot, hz = self.display
        client.sendall(
            bytes([OP_DISPLAY_INFO]) + struct.pack("<HHHBBH", w, h, dpi, rot, hz, 0)
        )
        self._log(f"DISPLAY_INFO sent: {w}x{h} @{dpi}dpi {hz}Hz")

        # Input-engine shadow state (subset of SeamInputEngine)
        shadow_x = shadow_y = 0
        active_edge = EDGE_LEFT
        cursor_active = False
        cursor_enter_ms = 0.0
        push_accum = 0

        while not self._stop.is_set():
            opcode = _recv_exact(client, 1)[0]

            if opcode == OP_MOUSE_REPORT:
                payload = _recv_exact(client, LEN_MOUSE_REPORT)
                buttons, dx, dy, vwheel, hwheel = struct.unpack("<Bhhbb", payload)
                self.mouse_frames += 1
                self.last_mouse = (buttons, dx, dy)

                now_ms = time.monotonic() * 1000.0
                shadow_x = max(0, min(w - 1, shadow_x + dx))
                shadow_y = max(0, min(h - 1, shadow_y + dy))

                at_edge = (
                    cursor_active
                    and buttons == 0
                    and (now_ms - cursor_enter_ms) >= ENTRY_GRACE_MS
                    and (
                        (active_edge == EDGE_LEFT and shadow_x == 0 and dx < 0)
                        or (active_edge == EDGE_RIGHT and shadow_x == w - 1 and dx > 0)
                        or (active_edge == EDGE_TOP and shadow_y == 0 and dy < 0)
                        or (active_edge == EDGE_BOTTOM and shadow_y == h - 1 and dy > 0)
                    )
                )
                if at_edge:
                    outward = -dx if active_edge == EDGE_LEFT else dx
                    push_accum += abs(outward)
                    if push_accum >= EDGE_RETURN_PUSH_THRESHOLD_PX:
                        cursor_active = False
                        push_accum = 0
                        self.edge_returns_sent += 1
                        client.sendall(
                            bytes([OP_EDGE_RETURN, active_edge])
                            + struct.pack("<HHB", _norm(shadow_x, w), _norm(shadow_y, h), 4)
                        )
                        self._log("EDGE_RETURN sent")
                else:
                    push_accum = 0

            elif opcode == OP_KEY_REPORT:
                _recv_exact(client, LEN_KEY_REPORT)
                self.key_frames += 1

            elif opcode == OP_CURSOR_ENTER:
                payload = _recv_exact(client, LEN_CURSOR_ENTER)
                edge = payload[0]
                norm_x, norm_y = struct.unpack("<HH", payload[1:5])
                active_edge = edge
                shadow_x = _denorm(norm_x, w)
                shadow_y = _denorm(norm_y, h)
                cursor_active = True
                cursor_enter_ms = time.monotonic() * 1000.0
                push_accum = 0
                self._log(f"CURSOR_ENTER edge={edge} -> ({shadow_x},{shadow_y})")

            elif opcode == OP_CURSOR_LEAVE:
                _recv_exact(client, LEN_CURSOR_LEAVE)
                cursor_active = False
                self._log("CURSOR_LEAVE")

            elif opcode == OP_CLIPBOARD_SET:
                (length,) = struct.unpack("<I", _recv_exact(client, 4))
                if length > MAX_CLIPBOARD_BYTES:
                    raise ConnectionError(f"invalid clipboard length {length}")
                text = _recv_exact(client, length).decode("utf-8") if length else ""
                self.clipboard_texts.append(text)
                self._log(f"CLIPBOARD_SET ({length} bytes)")

            elif opcode == OP_PING:
                payload = _recv_exact(client, LEN_PING_PONG)
                self.ping_frames += 1
                client.sendall(bytes([OP_PONG]) + payload)

            else:
                raise ConnectionError(f"unknown opcode 0x{opcode:02X}")


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description="SEAM device-side simulator (test fixture)")
    parser.add_argument("--port", type=int, default=0, help="TCP port (0 = ephemeral)")
    parser.add_argument("--key", help="64-char hex pairing key (default: ~/.seam/auth_key or random)")
    parser.add_argument("--key-file", default=os.path.expanduser("~/.seam/auth_key"))
    parser.add_argument("--verbose", action="store_true")
    args = parser.parse_args(argv)

    if args.key:
        key = bytes.fromhex(args.key)
    elif os.path.exists(args.key_file):
        key = bytes.fromhex(open(args.key_file).read().strip())
    else:
        key = os.urandom(32)
        os.makedirs(os.path.dirname(args.key_file), exist_ok=True)
        with open(args.key_file, "w") as fh:
            fh.write(key.hex())
        print(f"[agent-sim] generated key at {args.key_file}")

    sim = AgentSim(key, port=args.port, verbose=True)
    print(f"[agent-sim] listening on 127.0.0.1:{sim.port}")
    print(f"[agent-sim] connect with: seam-host.exe --connect 127.0.0.1:{sim.port}")
    sim.start()
    try:
        while True:
            time.sleep(0.2)
            if sim.disconnected.is_set():
                print("[agent-sim] session closed (one session per run).")
                break
    except KeyboardInterrupt:
        print()
    finally:
        sim.stop()
        print("[agent-sim] stopped")
    return 0


if __name__ == "__main__":
    sys.exit(main())
