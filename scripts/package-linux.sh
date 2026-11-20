#!/usr/bin/env bash
# ==============================================================================
# SEAM Linux Release Packager
#
# Produces, under build/release/:
#   seam-<version>-linux-<arch>.tar.gz   portable rootless install
#   seam_<version>_<deb_arch>.deb        Debian/Mint/Ubuntu package
#   SHA256SUMS                           artifact checksums
#
# Usage:
#   scripts/package-linux.sh [version]
# ==============================================================================
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="${1:-${SEAM_VERSION:-1.1.0}}"

case "$(uname -m)" in
    x86_64|amd64) TAR_ARCH="x86_64"; DEB_ARCH="amd64" ;;
    aarch64|arm64) TAR_ARCH="arm64"; DEB_ARCH="arm64" ;;
    *) echo "ERROR: unsupported architecture: $(uname -m)" >&2; exit 1 ;;
esac

OUT_DIR="$ROOT_DIR/build/release"
STAGE="$OUT_DIR/seam-$VERSION-linux-$TAR_ARCH"
DEB_ROOT="$OUT_DIR/deb/seam_${VERSION}_${DEB_ARCH}"

echo "==> [1/6] Building release binary..."
cd "$ROOT_DIR/linux-host"
cargo build --release

if [[ ! -f "$ROOT_DIR/dist/seam-agent.jar" ]]; then
    echo "ERROR: dist/seam-agent.jar is missing." >&2
    exit 1
fi

echo "==> [2/6] Staging portable tree..."
rm -rf "$OUT_DIR"
mkdir -p \
    "$STAGE/bin" \
    "$STAGE/share/seam" \
    "$STAGE/share/applications" \
    "$STAGE/share/icons/hicolor/256x256/apps" \
    "$STAGE/share/pixmaps"

install -m 0755 "$ROOT_DIR/linux-host/target/release/seam" "$STAGE/bin/seam"
install -m 0644 "$ROOT_DIR/dist/seam-agent.jar" "$STAGE/share/seam/seam-agent.jar"
install -m 0644 "$ROOT_DIR/assets/app-icon.png" \
    "$STAGE/share/icons/hicolor/256x256/apps/seam.png"
install -m 0644 "$ROOT_DIR/assets/app-icon.png" "$STAGE/share/pixmaps/seam.png"

cat > "$STAGE/install.sh" <<'INSTALL_EOF'
#!/usr/bin/env bash
# SEAM Linux portable installer.
# Usage: ./install.sh [prefix]     (default: $HOME/.local)
#        ./install.sh --uninstall [prefix]
set -euo pipefail

SRC="$(cd "$(dirname "$0")" && pwd)"

if [[ "${1:-}" == "--uninstall" ]]; then
    PREFIX="${2:-$HOME/.local}"
    rm -f "$PREFIX/bin/seam"
    rm -f "$PREFIX/share/seam/seam-agent.jar"
    rm -f "$PREFIX/share/applications/seam.desktop"
    rm -f "$PREFIX/share/icons/hicolor/256x256/apps/seam.png"
    rm -f "$PREFIX/share/pixmaps/seam.png"
    echo "Removed SEAM from $PREFIX"
    exit 0
fi

PREFIX="${1:-$HOME/.local}"
mkdir -p \
    "$PREFIX/bin" \
    "$PREFIX/share/seam" \
    "$PREFIX/share/applications" \
    "$PREFIX/share/icons/hicolor/256x256/apps" \
    "$PREFIX/share/pixmaps"

install -m 0755 "$SRC/bin/seam" "$PREFIX/bin/seam"
install -m 0644 "$SRC/share/seam/seam-agent.jar" "$PREFIX/share/seam/seam-agent.jar"
install -m 0644 "$SRC/share/icons/hicolor/256x256/apps/seam.png" \
    "$PREFIX/share/icons/hicolor/256x256/apps/seam.png"
install -m 0644 "$SRC/share/pixmaps/seam.png" "$PREFIX/share/pixmaps/seam.png"

cat > "$PREFIX/share/applications/seam.desktop" <<DESKTOP_EOF
[Desktop Entry]
Type=Application
Name=SEAM Control Center
GenericName=Universal Control
Comment=Cross your cursor between Linux and Android
Exec="$PREFIX/bin/seam" app
Icon=seam
Terminal=false
Categories=Utility;
Keywords=android;usb;wifi;cursor;input;seam;
StartupNotify=true
DESKTOP_EOF
chmod 0644 "$PREFIX/share/applications/seam.desktop"

command -v update-desktop-database >/dev/null 2>&1 && \
    update-desktop-database "$PREFIX/share/applications" >/dev/null 2>&1 || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && \
    gtk-update-icon-cache -f -t "$PREFIX/share/icons/hicolor" >/dev/null 2>&1 || true

echo "Installed SEAM to $PREFIX"
echo "Launch:  seam app"
echo "Headless: seam connect"
INSTALL_EOF
chmod 0755 "$STAGE/install.sh"

cat > "$STAGE/README.txt" <<README_EOF
SEAM Universal Control — Linux Host $VERSION ($TAR_ARCH)

Install (rootless, no sudo):
  ./install.sh                 # installs to \$HOME/.local
  ./install.sh /opt/seam       # custom prefix

Uninstall:
  ./install.sh --uninstall

Requirements:
  - Linux with X11 (XWayland is supported for the session)
  - GTK 3 runtime (libgtk-3-0)
  - adb (platform-tools) on PATH
  - Android 11+ device with USB debugging or wireless debugging

Usage:
  seam app                     # GTK control center
  seam connect [--edge right]  # headless screen-edge session
  seam devices                 # list attached devices
  seam info                    # device telemetry
README_EOF

echo "==> [3/6] Creating tarball..."
tar -C "$OUT_DIR" -czf "$OUT_DIR/seam-$VERSION-linux-$TAR_ARCH.tar.gz" \
    "seam-$VERSION-linux-$TAR_ARCH"

echo "==> [4/6] Staging Debian package..."
rm -rf "$DEB_ROOT"
mkdir -p \
    "$DEB_ROOT/DEBIAN" \
    "$DEB_ROOT/usr/bin" \
    "$DEB_ROOT/usr/share/seam" \
    "$DEB_ROOT/usr/share/applications" \
    "$DEB_ROOT/usr/share/icons/hicolor/256x256/apps" \
    "$DEB_ROOT/usr/share/pixmaps"

install -m 0755 "$ROOT_DIR/linux-host/target/release/seam" "$DEB_ROOT/usr/bin/seam"
install -m 0644 "$ROOT_DIR/dist/seam-agent.jar" "$DEB_ROOT/usr/share/seam/seam-agent.jar"
install -m 0644 "$ROOT_DIR/assets/app-icon.png" \
    "$DEB_ROOT/usr/share/icons/hicolor/256x256/apps/seam.png"
install -m 0644 "$ROOT_DIR/assets/app-icon.png" \
    "$DEB_ROOT/usr/share/pixmaps/seam.png"

cat > "$DEB_ROOT/usr/share/applications/seam.desktop" <<'DESKTOP_EOF'
[Desktop Entry]
Type=Application
Name=SEAM Control Center
GenericName=Universal Control
Comment=Cross your cursor between Linux and Android
Exec=seam app
Icon=seam
Terminal=false
Categories=Utility;
Keywords=android;usb;wifi;cursor;input;seam;
StartupNotify=true
DESKTOP_EOF
chmod 0644 "$DEB_ROOT/usr/share/applications/seam.desktop"

cat > "$DEB_ROOT/DEBIAN/control" <<CONTROL_EOF
Package: seam
Version: $VERSION
Section: utils
Priority: optional
Architecture: $DEB_ARCH
Depends: libc6 (>= 2.31), libx11-6, libxfixes3, libgtk-3-0
Recommends: adb
Maintainer: SEAM Contributors <https://github.com/Ferousco-dev/Seam>
Homepage: https://github.com/Ferousco-dev/Seam
Description: SEAM Universal Control (Linux host)
 Cross your cursor between your Linux desktop and Android phone over USB
 or Wi-Fi, with real /dev/uhid input, clipboard sync, and a GTK control
 center. Requires an Android 11+ device with USB or wireless debugging.
CONTROL_EOF

echo "==> [5/6] Building .deb..."
dpkg-deb --build --root-owner-group "$DEB_ROOT" \
    "$OUT_DIR/seam_${VERSION}_${DEB_ARCH}.deb"

echo "==> [6/6] Writing checksums..."
(
    cd "$OUT_DIR"
    sha256sum ./*.tar.gz ./*.deb > SHA256SUMS
)

echo
echo "Artifacts in $OUT_DIR:"
ls -lh "$OUT_DIR" | grep -E "tar.gz|\.deb|SHA256"