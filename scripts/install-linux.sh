#!/usr/bin/env bash
# ==============================================================================
# SEAM Linux Host Installer (per-user, rootless)
#
# Installs:
#   <prefix>/bin/seam                         CLI + GTK control center
#   <prefix>/share/seam/seam-agent.jar        Android agent
#   <prefix>/share/applications/seam.desktop  Menu entry
#   <prefix>/share/icons/hicolor/.../seam.png Application icon
#
# Default prefix is $HOME/.local (no sudo required).
# Override with SEAM_PREFIX=/usr/local (writes to system paths).
#
# Usage:
#   scripts/install-linux.sh [--uninstall]
# ==============================================================================
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PREFIX="${SEAM_PREFIX:-$HOME/.local}"
BIN_DIR="$PREFIX/bin"
SHARE_DIR="$PREFIX/share/seam"
APPS_DIR="$PREFIX/share/applications"
ICON_DIR="$PREFIX/share/icons/hicolor/256x256/apps"
PIXMAP_DIR="$PREFIX/share/pixmaps"

if [[ "${1:-}" == "--uninstall" ]]; then
    echo "==> Removing SEAM from $PREFIX..."
    rm -f "$BIN_DIR/seam"
    rm -f "$SHARE_DIR/seam-agent.jar"
    rm -f "$APPS_DIR/seam.desktop"
    rm -f "$ICON_DIR/seam.png"
    rm -f "$PIXMAP_DIR/seam.png"
    rmdir "$SHARE_DIR" 2>/dev/null || true
    command -v update-desktop-database >/dev/null 2>&1 && \
        update-desktop-database "$APPS_DIR" >/dev/null 2>&1 || true
    command -v gtk-update-icon-cache >/dev/null 2>&1 && \
        gtk-update-icon-cache -f -t "$PREFIX/share/icons/hicolor" >/dev/null 2>&1 || true
    echo "    Done."
    exit 0
fi

echo "==> [1/5] Building release binary..."
cd "$ROOT_DIR/linux-host"
cargo build --release

if [[ ! -f "$ROOT_DIR/dist/seam-agent.jar" ]]; then
    echo "ERROR: dist/seam-agent.jar is missing. Build it first:" >&2
    echo "       ./scripts/build-agent.sh   (macOS host checkout)" >&2
    echo "       or copy an existing seam-agent.jar into dist/." >&2
    exit 1
fi

echo "==> [2/5] Installing directories under $PREFIX..."
mkdir -p "$BIN_DIR" "$SHARE_DIR" "$APPS_DIR" "$ICON_DIR" "$PIXMAP_DIR"

echo "==> [3/5] Installing binary and Android agent..."
install -m 0755 "$ROOT_DIR/linux-host/target/release/seam" "$BIN_DIR/seam"
install -m 0644 "$ROOT_DIR/dist/seam-agent.jar" "$SHARE_DIR/seam-agent.jar"

echo "==> [4/5] Installing icon..."
install -m 0644 "$ROOT_DIR/assets/app-icon.png" "$ICON_DIR/seam.png"
install -m 0644 "$ROOT_DIR/assets/app-icon.png" "$PIXMAP_DIR/seam.png"

echo "==> [5/5] Installing desktop entry..."
DESKTOP_FILE="$APPS_DIR/seam.desktop"
{
    echo "[Desktop Entry]"
    echo "Type=Application"
    echo "Name=SEAM Control Center"
    echo "GenericName=Universal Control"
    echo "Comment=Cross your cursor between Linux and Android"
    echo "Exec=$BIN_DIR/seam app"
    echo "Icon=seam"
    echo "Terminal=false"
    echo "Categories=Utility;"
    echo "Keywords=android;usb;wifi;cursor;input;seam;"
    echo "StartupNotify=true"
} > "$DESKTOP_FILE"
chmod 0644 "$DESKTOP_FILE"

command -v update-desktop-database >/dev/null 2>&1 && \
    update-desktop-database "$APPS_DIR" >/dev/null 2>&1 || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && \
    gtk-update-icon-cache -f -t "$PREFIX/share/icons/hicolor" >/dev/null 2>&1 || true

echo
echo "SEAM installed:"
echo "  binary : $BIN_DIR/seam"
echo "  agent  : $SHARE_DIR/seam-agent.jar"
echo "  menu   : SEAM Control Center"
echo
echo "Launch the control center from your application menu, or run:"
echo "  seam app"
echo "Headless session:"
echo "  seam connect"