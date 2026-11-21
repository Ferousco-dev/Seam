#!/usr/bin/env bash
# Independent re-derivation of the SEAM-Wire v1 handshake golden vectors.
#
# Why this exists: the vectors pinned in tools/verify_protocol.py and
# windows-host/Testing/SelfTest.cs were produced by tools/agent_sim.py, a Python
# transliteration of the Java agent. Checking C# against vectors that a second
# hand-written implementation produced only pins regression, not fidelity.
# This script rebuilds the HMAC inputs byte-for-byte from the Java's own
# composition rule (SeamAuthenticator.java lines 110 and 127:
#     clientHmac = HMAC-SHA256(key, CLIENT_DOMAIN || serverNonce || clientNonce)
#     serverHmac = HMAC-SHA256(key, SERVER_DOMAIN || clientNonce || serverNonce || sessionId_le)
# and hashes them with OpenSSL, which shares no code with either implementation.
#
# Usage: bash tools/verify_vectors_openssl.sh
# Requires: openssl, od, sed (anything on Windows: Git-Bash / WSL is easiest).

set -u

KEY=000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f
SERVER_NONCE=a0a1a2a3a4a5a6a7a8a9aaabacadaeafb0b1b2b3b4b5b6b7b8b9babbbcbdbebf
CLIENT_NONCE=303132333435363738393a3b3c3d3e3f404142434445464748494a4b4c4d4e4f
SESSION_ID_LE=8877665544332211                       # 0x1122334455667788 little-endian

EXPECTED_CLIENT=e9c7aec0a81566c23cdd13fe335c8cda5fc4ce80b69ee6bc9e1f55c3171ec9e7
EXPECTED_SERVER=c44d92e6dc72f81024bad8eb57dcc2a85b290c9f07c05ef80d2410b4e1657caf

if ! command -v openssl >/dev/null 2>&1; then
  echo "openssl not found — cannot run the independent check." >&2
  exit 2
fi

tmp=$(mktemp -d) || exit 2
trap 'rm -rf "$tmp"' EXIT

# "a0a1..." -> "\xa0\xa1..." so bash's printf emits the raw bytes
esc() { printf '%s' "$1" | sed 's/../\\x&/g'; }

# clientHmac input
{
  printf 'SEAM-CLIENT-AUTH-v1'
  printf "$(esc "$SERVER_NONCE")"
  printf "$(esc "$CLIENT_NONCE")"
} > "$tmp/client.bin"

# serverHmac input
{
  printf 'SEAM-SERVER-AUTH-v1'
  printf "$(esc "$CLIENT_NONCE")"
  printf "$(esc "$SERVER_NONCE")"
  printf "$(esc "$SESSION_ID_LE")"
} > "$tmp/server.bin"

# sanity: 19 + 32 + 32 = 83 bytes, 19 + 32 + 32 + 8 = 91 bytes
csize=$(wc -c < "$tmp/client.bin")
ssize=$(wc -c < "$tmp/server.bin")
if [ "$csize" -ne 83 ] || [ "$ssize" -ne 91 ]; then
  echo "input assembly wrong: client=$csize (want 83) server=$ssize (want 91)" >&2
  exit 2
fi

hmac_hex() {
  openssl dgst -sha256 -mac HMAC -macopt "hexkey:$KEY" -binary "$1" \
    | od -An -v -tx1 | tr -d ' \n'
}

got_client=$(hmac_hex "$tmp/client.bin")
got_server=$(hmac_hex "$tmp/server.bin")

fail=0
check() {
  if [ "$2" = "$3" ]; then
    echo "  [PASS] $1  $2"
  else
    echo "  [FAIL] $1"
    echo "         pinned  =$3"
    echo "         openssl =$2"
    fail=1
  fi
}

echo "=== Independent (OpenSSL) re-derivation of golden vectors ==="
check "clientHmac" "$got_client" "$EXPECTED_CLIENT"
check "serverHmac" "$got_server" "$EXPECTED_SERVER"

if [ "$fail" -eq 0 ]; then
  echo "=== 2 passed, 0 failed — vectors confirmed by an implementation that shares"
  echo "    no code with the Java agent, the Python simulator, or the C# host ==="
else
  echo "=== FAILURES: a pinned vector does not follow from the Java composition rule ==="
fi
exit "$fail"
