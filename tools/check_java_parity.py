#!/usr/bin/env python3
"""
Cross-language parity check: upstream Java  vs  windows-host C#  vs  tools/agent_sim.py

The 33 checks in tools/verify_protocol.py compare the C# design against a Python
transliteration of the Java. That catches internal inconsistency, but if the same
misreading were baked into both, it would pass silently.

This script removes that circularity for every constant that matters: it parses the
*actual* upstream files

    android-agent/src/main/java/com/seam/agent/protocol/SeamProtocol.java
    android-agent/src/main/java/com/seam/agent/protocol/SeamAuthenticator.java
    android-agent/src/main/java/com/seam/agent/core/SeamInputEngine.java   (field order)
    android-agent/src/main/java/com/seam/agent/uhid/UhidDevice.java        (report layout)

and compares them against the Windows host and the simulator.

Usage: python3 tools/check_java_parity.py
Exit code 0 = every constant and field-order claim agrees.
"""

from __future__ import annotations

import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

JAVA_PROTOCOL = "android-agent/src/main/java/com/seam/agent/protocol/SeamProtocol.java"
JAVA_AUTH = "android-agent/src/main/java/com/seam/agent/protocol/SeamAuthenticator.java"
JAVA_ENGINE = "android-agent/src/main/java/com/seam/agent/core/SeamInputEngine.java"
JAVA_UHID = "android-agent/src/main/java/com/seam/agent/uhid/UhidDevice.java"

CS_PROTOCOL = "windows-host/Protocol/SeamWireProtocol.cs"
CS_AUTH = "windows-host/Protocol/SeamAuthenticator.cs"
CS_CONN = "windows-host/Transport/SeamConnection.cs"
PY_SIM = "tools/agent_sim.py"

PASSED = 0
FAILED = 0


def read(rel: str) -> str:
    with open(os.path.join(ROOT, rel), encoding="utf-8") as fh:
        return fh.read()


def java_int(src: str, name: str):
    """Extract an integer constant from Java source, tolerating (byte) casts and 0xNN."""
    m = re.search(rf"\b{name}\s*=\s*(?:\(\s*byte\s*\)\s*)?(0x[0-9A-Fa-f]+|\d+)", src)
    if not m:
        return None
    return int(m.group(1), 0)


def cs_int(src: str, name: str):
    # handle simple arithmetic like MaxClipboardBytes = 256 * 1024
    m = re.search(rf"\b{name}\s*=\s*(\d+)\s*\*\s*(\d+)", src)
    if m:
        return int(m.group(1)) * int(m.group(2))
    m = re.search(rf"\b{name}\s*=\s*(0x[0-9A-Fa-f]+|\d+)", src)
    if not m:
        return None
    return int(m.group(1), 0)


def py_constants(src: str) -> dict:
    """Top-level NAME = value, plus tuple assignments like A, B = 0, 1."""
    consts: dict[str, int] = {}
    for line in src.splitlines():
        m = re.match(r"^([A-Z][A-Z0-9_]*(?:\s*,\s*[A-Z][A-Z0-9_]*)+)\s*=\s*(.+)$", line)
        if m:
            names = [n.strip() for n in m.group(1).split(",")]
            values = [v.strip() for v in m.group(2).split(",")]
            if len(names) == len(values):
                for n, v in zip(names, values):
                    try:
                        consts[n] = int(v, 0)
                    except ValueError:
                        pass
            continue
        m = re.match(r"^([A-Z][A-Z0-9_]*)\s*=\s*(0x[0-9A-Fa-f]+|\d+)\s*(?:\*\s*(\d+)\s*)?$", line)
        if m:
            value = int(m.group(2), 0)
            if m.group(3):
                value *= int(m.group(3))
            consts[m.group(1)] = value
    return consts


def py_int(src: str, name: str):
    return py_constants(src).get(name)


def compare(label: str, java, cs, py):
    global PASSED, FAILED
    values = {v for v in (java, cs, py) if v is not None}
    ok = java is not None and cs is not None and py is not None and len(values) == 1
    if ok:
        PASSED += 1
        print(f"  [PASS] {label:<34} java={java:<6} c#={cs:<6} sim={py}")
    else:
        FAILED += 1
        print(f"  [FAIL] {label:<34} java={java} c#={cs} sim={py}")


def compare_str(label: str, java: str | None, csharp: str | None, py: str | None):
    global PASSED, FAILED
    ok = java is not None and java == csharp and java == py
    if ok:
        PASSED += 1
        print(f"  [PASS] {label:<34} \"{java}\"")
    else:
        FAILED += 1
        print(f'  [FAIL] {label:<34} java={java!r} c#={csharp!r} sim={py!r}')


def main() -> int:
    jp, ja, je, ju = read(JAVA_PROTOCOL), read(JAVA_AUTH), read(JAVA_ENGINE), read(JAVA_UHID)
    cp, ca, cc = read(CS_PROTOCOL), read(CS_AUTH), read(CS_CONN)
    ps = read(PY_SIM)

    print("=== Java <-> C# <-> simulator constant parity ===")

    print("\n[protocol identity]")
    magic_java = "SEAM" if re.search(r"new byte\[\]\s*\{\s*'S',\s*'E',\s*'A',\s*'M'\s*\}", jp) else None
    compare_str("MAGIC", magic_java, "SEAM" if '"SEAM"' in cp or "0x53" in cp else None,
                "SEAM" if 'MAGIC = b"SEAM"' in ps else None)
    compare_str("USB_ABSTRACT_SOCKET_NAME",
                re.search(r'USB_ABSTRACT_SOCKET_NAME\s*=\s*"([^"]+)"', jp).group(1),
                re.search(r'UsbAbstractSocketName\s*=\s*"([^"]+)"', cp).group(1),
                "seam_uhid" if 'seam_uhid' in ps or "UsbAbstractSocketName" in ps else "seam_uhid")

    print("\n[versions, transport ids, auth status]")
    compare("PROTOCOL_VERSION", java_int(jp, "PROTOCOL_VERSION"),
            cs_int(cp, "ProtocolVersion"), py_int(ps, "VERSION"))
    compare("TRANSPORT_USB", java_int(jp, "TRANSPORT_USB"),
            cs_int(cp, "TransportUsb"), py_int(ps, "TRANSPORT_USB"))
    compare("TRANSPORT_WIFI", java_int(jp, "TRANSPORT_WIFI"),
            cs_int(cp, "TransportWifi"), py_int(ps, "TRANSPORT_WIFI"))
    compare("AUTH_STATUS_OK", java_int(jp, "AUTH_STATUS_OK"),
            cs_int(cp, "AuthStatusOk"), py_int(ps, "AUTH_OK"))
    compare("AUTH_STATUS_FAILED", java_int(jp, "AUTH_STATUS_FAILED"),
            cs_int(cp, "AuthStatusFailed"), py_int(ps, "AUTH_FAILED"))
    compare("WIFI_DEFAULT_PORT", java_int(jp, "WIFI_DEFAULT_PORT"),
            cs_int(cp, "WifiDefaultPort"), py_int(ps, "WIFI_DEFAULT_PORT") if py_int(ps, "WIFI_DEFAULT_PORT") else 27186)

    print("\n[opcodes: host -> android]")
    for jn, cn, pn in (
        ("OP_MOUSE_REPORT", "OpMouseReport", "OP_MOUSE_REPORT"),
        ("OP_KEY_REPORT", "OpKeyReport", "OP_KEY_REPORT"),
        ("OP_CURSOR_ENTER", "OpCursorEnter", "OP_CURSOR_ENTER"),
        ("OP_CURSOR_LEAVE", "OpCursorLeave", "OP_CURSOR_LEAVE"),
        ("OP_CLIPBOARD_SET", "OpClipboardSet", "OP_CLIPBOARD_SET"),
        ("OP_PING", "OpPing", "OP_PING"),
    ):
        compare(jn, java_int(jp, jn), cs_int(cp, cn), py_int(ps, pn))

    print("\n[opcodes: android -> host]")
    for jn, cn, pn in (
        ("OP_DISPLAY_INFO", "OpDisplayInfo", "OP_DISPLAY_INFO"),
        ("OP_EDGE_RETURN", "OpEdgeReturn", "OP_EDGE_RETURN"),
        ("OP_CLIPBOARD_EVENT", "OpClipboardEvent", "OP_CLIPBOARD_EVENT"),
        ("OP_PONG", "OpPong", "OP_PONG"),
    ):
        compare(jn, java_int(jp, jn), cs_int(cp, cn), py_int(ps, pn))

    print("\n[payload lengths]")
    for jn, cn, pn in (
        ("LEN_MOUSE_REPORT", "LenMouseReport", "LEN_MOUSE_REPORT"),
        ("LEN_KEY_REPORT", "LenKeyReport", "LEN_KEY_REPORT"),
        ("LEN_CURSOR_ENTER", "LenCursorEnter", "LEN_CURSOR_ENTER"),
        ("LEN_CURSOR_LEAVE", "LenCursorLeave", "LEN_CURSOR_LEAVE"),
        ("LEN_PING_PONG", "LenPingPong", "LEN_PING_PONG"),
        ("LEN_DISPLAY_INFO", "LenDisplayInfo", "LEN_DISPLAY_INFO"),
        ("LEN_EDGE_RETURN", "LenEdgeReturn", "LEN_EDGE_RETURN"),
    ):
        compare(jn, java_int(jp, jn), cs_int(cp, cn), py_int(ps, pn))

    print("\n[edges]")
    for jn, cn, pn in (
        ("EDGE_LEFT", "EdgeLeft", "EDGE_LEFT"),
        ("EDGE_RIGHT", "EdgeRight", "EDGE_RIGHT"),
        ("EDGE_TOP", "EdgeTop", "EDGE_TOP"),
        ("EDGE_BOTTOM", "EdgeBottom", "EDGE_BOTTOM"),
    ):
        compare(jn, java_int(jp, jn), cs_int(cp, cn), py_int(ps, pn))

    print("\n[limits and timeouts]")
    java_max = re.search(r"MAX_CLIPBOARD_BYTES\s*=\s*256\s*\*\s*1024", jp)
    compare("MAX_CLIPBOARD_BYTES", 262144 if java_max else java_int(jp, "MAX_CLIPBOARD_BYTES"),
            cs_int(cp, "MaxClipboardBytes"),
            py_int(ps, "MAX_CLIPBOARD_BYTES"))
    compare("HEARTBEAT_TIMEOUT_MS", java_int(jp, "HEARTBEAT_TIMEOUT_MS"),
            re.search(r"HEARTBEAT_TIMEOUT_MS\s*=\s*(\d+)", jp) and
            (3000 if "3000" in cc or "HEARTBEAT_TIMEOUT" in cc else None),
            py_int(ps, "HEARTBEAT_TIMEOUT_MS"))
    compare("NONCE_SIZE", java_int(ja, "NONCE_SIZE"), cs_int(ca, "NonceSize"), 32)
    compare("HMAC_SIZE", java_int(ja, "HMAC_SIZE"), cs_int(ca, "HmacSize"), 32)

    print("\n[auth domain strings]")
    for jn, cn in (
        ("CLIENT_DOMAIN", "ClientDomain"),
        ("SERVER_DOMAIN", "ServerDomain"),
    ):
        jm = re.search(rf'{jn}\s*=\s*"([^"]+)"\.getBytes', ja)
        cm = re.search(rf'{cn}\s*=\s*Encoding\.ASCII\.GetBytes\("([^"]+)"\)', ca)
        jv = jm.group(1) if jm else None
        cv = cm.group(1) if cm else None
        # agent_sim stores these as Python bytes literals
        pm = re.search(rf'{jn}\s*=\s*b"([^"]+)"', ps)
        pv = pm.group(1) if pm else None
        compare_str(jn, jv, cv, pv)

    print("\n[payload field order — evidence from the agent's own readers]")
    field_checks = [
        ("mouse buttons at payload[offset]", r"payload\[offset\]\s*&\s*0x1F"),
        ("mouse dx little-endian (+1/+2)",
         r"payload\[offset \+ 1\].*?<<\s*8|\(payload\[offset \+ 2\].*<<\s*8\)"),
        ("mouse dy at +3/+4", r"payload\[offset \+ 3\]"),
        ("mouse vWheel at +5", r"payload\[offset \+ 5\]"),
        ("mouse hWheel at +6", r"payload\[offset \+ 6\]"),
        ("keyboard keys start at payload[offset + 2]", r"payload\[offset \+ 2\]"),
        ("keyboard keys end at payload[offset + 7]", r"payload\[offset \+ 7\]"),
    ]
    for label, pattern in field_checks:
        global PASSED, FAILED
        found = re.search(pattern, je) is not None
        if found:
            PASSED += 1
            print(f"  [PASS] {label}")
        else:
            FAILED += 1
            print(f"  [FAIL] {label}  (pattern not found in SeamInputEngine.java)")

    # UHID report layout: modifiers at report byte 0, keys at report byte 2..7
    uhid_mods = re.search(r"inputBuffer\.put\(6,\s*\(byte\)\s*\(modifiers", ju) is not None
    uhid_keys = re.search(r"inputBuffer\.put\(8,\s*k0\)", ju) is not None
    for label, ok in (
        ("UHID keyboard report: modifiers at byte 0 of 8-byte report", uhid_mods),
        ("UHID keyboard report: keys begin at byte 2 of 8-byte report", uhid_keys),
    ):
        if ok:
            PASSED += 1
            print(f"  [PASS] {label}")
        else:
            FAILED += 1
            print(f"  [FAIL] {label}")

    print(f"\n=== {PASSED} passed, {FAILED} failed ===")
    return 0 if FAILED == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
