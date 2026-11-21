#!/usr/bin/env python3
"""
SEAM-Wire v1 conformance checks that can run with no phone, no adb and no .NET.

This is the oracle used while building windows-host/. It verifies, executably:

  1. Golden handshake vectors — the exact 68-byte client response and the 41-byte ack
     verification, derived from the Java server implementation's rules
     (domain strings, nonce ordering, little-endian session id). These are the same
     constants embedded in windows-host/Testing/SelfTest.cs.
  2. Frame layouts — byte-for-byte the same hex the C# serializers must produce.
  3. A real TCP handshake + frame exchange against tools/agent_sim.py, which is a
     transliteration of the shipped Java server code (auth, opcode loop, shadow cursor,
     EDGE_RETURN, heartbeat timeout).

It does NOT execute the C# code. See docs/windows/README.md for exactly what that means.

Usage:
    python3 tools/verify_protocol.py [-v]
"""

from __future__ import annotations

import hashlib
import hmac
import os
import socket
import struct
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from agent_sim import (  # noqa: E402
    AgentSim, MAGIC, VERSION, AUTH_OK,
    OP_MOUSE_REPORT, OP_KEY_REPORT, OP_CURSOR_ENTER, OP_CURSOR_LEAVE, OP_CLIPBOARD_SET, OP_PING,
    OP_DISPLAY_INFO, OP_EDGE_RETURN, OP_PONG,
    CLIENT_DOMAIN, SERVER_DOMAIN,
)

PASSED = 0
FAILED = 0


def check(name: str, condition: bool, detail: str = "") -> None:
    global PASSED, FAILED
    if condition:
        PASSED += 1
        print(f"  [PASS] {name}" + (f"  ({detail})" if detail else ""))
    else:
        FAILED += 1
        print(f"  [FAIL] {name}" + (f"  ({detail})" if detail else ""))


# ---- Vectors: identical constants to windows-host/Testing/SelfTest.cs ----------------
GOLDEN_KEY = bytes.fromhex("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f")
GOLDEN_SERVER_NONCE = bytes.fromhex(
    "a0a1a2a3a4a5a6a7a8a9aaabacadaeafb0b1b2b3b4b5b6b7b8b9babbbcbdbebf")
GOLDEN_CLIENT_NONCE = bytes.fromhex(
    "303132333435363738393a3b3c3d3e3f404142434445464748494a4b4c4d4e4f")
GOLDEN_CLIENT_HMAC = "e9c7aec0a81566c23cdd13fe335c8cda5fc4ce80b69ee6bc9e1f55c3171ec9e7"
GOLDEN_SERVER_HMAC = "c44d92e6dc72f81024bad8eb57dcc2a85b290c9f07c05ef80d2410b4e1657caf"
GOLDEN_SESSION_ID = 0x1122334455667788


def test_golden_handshake() -> None:
    print("\n[1] Golden handshake vectors (client path, Java-derived)")

    client_hmac = hmac.new(
        GOLDEN_KEY, CLIENT_DOMAIN + GOLDEN_SERVER_NONCE + GOLDEN_CLIENT_NONCE, hashlib.sha256
    ).digest()
    check("client HMAC matches Java-derived vector",
          client_hmac.hex() == GOLDEN_CLIENT_HMAC, client_hmac.hex()[:16] + "…")

    session_le = GOLDEN_SESSION_ID.to_bytes(8, "little")
    check("session id is little-endian on the wire", session_le.hex() == "8877665544332211")

    server_hmac = hmac.new(
        GOLDEN_KEY, SERVER_DOMAIN + GOLDEN_CLIENT_NONCE + GOLDEN_SERVER_NONCE + session_le,
        hashlib.sha256
    ).digest()
    check("server HMAC matches Java-derived vector",
          server_hmac.hex() == GOLDEN_SERVER_HMAC, server_hmac.hex()[:16] + "…")

    challenge = MAGIC + bytes([VERSION, 0x01]) + GOLDEN_SERVER_NONCE
    check("38-byte challenge layout",
          len(challenge) == 38 and challenge[:6].hex() == "5345414d0101")

    response = MAGIC + GOLDEN_CLIENT_NONCE + client_hmac
    check("68-byte client response layout",
          len(response) == 68 and response[:4] == MAGIC
          and response[4:36] == GOLDEN_CLIENT_NONCE and response[36:] == client_hmac)

    ack = bytes([AUTH_OK]) + session_le + server_hmac
    check("41-byte server ack layout", len(ack) == 41 and ack[0] == AUTH_OK)


def test_frame_layouts() -> None:
    print("\n[2] Frame layouts (must equal windows-host/Protocol/SeamFrames.cs output)")

    mouse = bytes([OP_MOUSE_REPORT, 0x01]) + struct.pack("<hh", 5, -3) + bytes([0, 0])
    check("MOUSE_REPORT is 8 bytes", len(mouse) == 8)
    check("MOUSE_REPORT hex", mouse.hex() == "01010500fdff0000", mouse.hex())

    key = bytes([OP_KEY_REPORT, 0x02, 0x00, 0x04, 0, 0, 0, 0, 0])
    check("KEY_REPORT is 9 bytes", len(key) == 9)
    check("KEY_REPORT hex", key.hex() == "020200040000000000", key.hex())

    enter = bytes([OP_CURSOR_ENTER, 0x00]) + struct.pack("<HH", 360, 32768) + bytes([0])
    check("CURSOR_ENTER is 7 bytes", len(enter) == 7)
    check("CURSOR_ENTER hex (LE normX/normY)", enter.hex() == "03006801008000", enter.hex())

    clip = bytes([OP_CLIPBOARD_SET]) + struct.pack("<I", 2) + b"hi"
    check("CLIPBOARD_SET is 5+len", len(clip) == 7)
    check("CLIPBOARD_SET hex", clip.hex() == "05020000006869", clip.hex())

    ping = bytes([OP_PING]) + struct.pack("<Q", 0x0102030405060708)
    check("PING is 9 bytes, LE uint64", len(ping) == 9)
    check("PING hex", ping.hex() == "060807060504030201", ping.hex())

    payload = struct.pack("<HHHBBH", 1080, 2400, 420, 0, 120, 0)
    check("DISPLAY_INFO payload is 10 bytes", len(payload) == 10)
    check("DISPLAY_INFO hex", payload.hex() == "38046009a40100780000", payload.hex())

    w, h, dpi, rot, hz, flags = struct.unpack("<HHHBBH", payload)
    check("DISPLAY_INFO decodes to 1080x2400 @420dpi 120Hz",
          (w, h, dpi, rot, hz, flags) == (1080, 2400, 420, 0, 120, 0))


def _recv_exact(sock: socket.socket, count: int) -> bytes:
    buf = bytearray()
    while len(buf) < count:
        chunk = sock.recv(count - len(buf))
        if not chunk:
            raise ConnectionError("peer closed")
        buf += chunk
    return bytes(buf)


def test_end_to_end(verbose: bool) -> None:
    print("\n[3] End-to-end against the device simulator (real TCP, real HMAC)")

    with AgentSim(GOLDEN_KEY, verbose=verbose) as sim:
        sock = socket.create_connection(("127.0.0.1", sim.port), timeout=3)
        sock.settimeout(3)

        # --- handshake (client side, same steps as SeamAuthenticator.cs) ---
        challenge = _recv_exact(sock, 38)
        check("server challenge magic + version",
              challenge[:4] == MAGIC and challenge[4] == VERSION,
              challenge[:6].hex())
        server_nonce = challenge[6:38]
        client_nonce = os.urandom(32)
        client_hmac = hmac.new(
            GOLDEN_KEY, CLIENT_DOMAIN + server_nonce + client_nonce, hashlib.sha256).digest()
        sock.sendall(MAGIC + client_nonce + client_hmac)

        ack = _recv_exact(sock, 41)
        check("mutual auth accepted by simulator", ack[0] == AUTH_OK, f"status=0x{ack[0]:02X}")
        session_id = int.from_bytes(ack[1:9], "little")
        check("session id received", session_id == sim.session_id, f"0x{session_id:016X}")

        expected_server_hmac = hmac.new(
            GOLDEN_KEY, SERVER_DOMAIN + client_nonce + server_nonce + ack[1:9],
            hashlib.sha256).digest()
        check("server HMAC verified (mutual auth complete)",
              hmac.compare_digest(ack[9:41], expected_server_hmac))

        # --- DISPLAY_INFO (sent immediately after auth, like UsbLocalSocketServer) ---
        opcode = _recv_exact(sock, 1)[0]
        check("first inbound frame is DISPLAY_INFO", opcode == OP_DISPLAY_INFO, f"0x{opcode:02X}")
        info = _recv_exact(sock, 10)
        w, h, dpi, rot, hz, _flags = struct.unpack("<HHHBBH", info)
        check("DISPLAY_INFO parsed: 1080x2400 @420 120Hz",
              (w, h, dpi, rot, hz) == (1080, 2400, 420, 0, 120), f"{w}x{h}@{dpi}")

        # --- mouse ---
        for _ in range(50):
            sock.sendall(bytes([OP_MOUSE_REPORT, 0x00]) + struct.pack("<hh", 4, 2) + bytes([0, 0]))
        time.sleep(0.25)
        check("50 MOUSE_REPORT frames reached the agent", sim.mouse_frames >= 50,
              f"agent saw {sim.mouse_frames}")

        # --- keyboard: Ctrl+C then all-up ---
        sock.sendall(bytes([OP_KEY_REPORT, 0x01, 0x00, 0x06, 0, 0, 0, 0, 0]))
        sock.sendall(bytes([OP_KEY_REPORT, 0x00, 0x00, 0x00, 0, 0, 0, 0, 0]))
        time.sleep(0.15)
        check("KEY_REPORT frames reached the agent", sim.key_frames >= 2,
              f"agent saw {sim.key_frames}")

        # --- clipboard (host -> Android) ---
        text = "seam-from-windows"
        sock.sendall(bytes([OP_CLIPBOARD_SET]) + struct.pack("<I", len(text)) + text.encode())
        time.sleep(0.15)
        check("CLIPBOARD_SET decoded by agent", sim.clipboard_texts[-1:] == [text],
              f"{sim.clipboard_texts}")

        # --- heartbeat (the agent tears the session down after 3 s of silence) ---
        sent_us = int(time.monotonic() * 1_000_000)
        sock.sendall(bytes([OP_PING]) + struct.pack("<Q", sent_us))
        pong_op = _recv_exact(sock, 1)[0]
        pong_payload = _recv_exact(sock, 8)
        check("PING answered with PONG", pong_op == OP_PONG)
        check("PONG echoes the microsecond timestamp",
              int.from_bytes(pong_payload, "little") == sent_us)

        # --- edge return (Android -> host) ---
        sock.sendall(bytes([OP_CURSOR_ENTER, 0x00]) + struct.pack("<HH", 360, 32768) + bytes([0]))
        time.sleep(0.20)  # > ENTRY_GRACE_MS
        for _ in range(60):
            sock.sendall(bytes([OP_MOUSE_REPORT, 0x00]) + struct.pack("<hh", -8, 0) + bytes([0, 0]))
            time.sleep(0.004)

        sock.settimeout(2)
        got_edge_return = False
        norm_y = -1
        deadline = time.time() + 1.5
        while time.time() < deadline:
            op = _recv_exact(sock, 1)[0]
            if op == OP_EDGE_RETURN:
                payload = _recv_exact(sock, 6)
                _edge, norm_x, norm_y, _vel = struct.unpack("<BHHB", payload)
                got_edge_return = True
                break
        check("EDGE_RETURN received when pushed past the return bezel", got_edge_return,
              f"normY={norm_y}")
        check("simulator counted the edge return", sim.edge_returns_sent >= 1,
              f"{sim.edge_returns_sent}")

        sock.sendall(bytes([OP_CURSOR_LEAVE, 0x01]))
        sock.close()

    check("agent observed a clean mutually-authenticated session", sim.auth_ok and not sim.auth_error)


def main() -> int:
    verbose = "-v" in sys.argv or "--verbose" in sys.argv
    print("=== SEAM-Wire v1 conformance verification ===")
    test_golden_handshake()
    test_frame_layouts()
    test_end_to_end(verbose)
    print(f"\n=== {PASSED} passed, {FAILED} failed ===")
    return 0 if FAILED == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
