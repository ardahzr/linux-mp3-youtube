#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────
#  LMP – Libuntu Music Player  |  install.sh
#  Supports: Arch Linux / Arch-based (EndeavourOS, Manjaro …)
#  Does NOT require .NET SDK — uses pre-built binary.
#  Run:  chmod +x install.sh && ./install.sh
# ─────────────────────────────────────────────────────────────
set -e

INSTALL_DIR="$HOME/.local/share/LMP"
DESKTOP_DIR="$HOME/.local/share/applications"
BINARY="$INSTALL_DIR/MP3Player"

# GitHub Releases URL – update this after uploading a new release
RELEASE_URL="https://github.com/ardahzr/linux-mp3-youtube/releases/latest/download/MP3Player-linux-x64"

echo "════════════════════════════════════════"
echo "  LMP – Libuntu Music Player  Installer"
echo "════════════════════════════════════════"

# ── 1. Check / install system dependencies (no dotnet needed) ─
echo ""
echo "▶ Checking system dependencies…"

MISSING_PKGS=()
pacman -Q mpg123   &>/dev/null || MISSING_PKGS+=(mpg123)
pacman -Q libpulse &>/dev/null || MISSING_PKGS+=(libpulse)
pacman -Q gtk3     &>/dev/null || MISSING_PKGS+=(gtk3)
pacman -Q yt-dlp   &>/dev/null || MISSING_PKGS+=(yt-dlp)
pacman -Q ffmpeg   &>/dev/null || MISSING_PKGS+=(ffmpeg)

if [ ${#MISSING_PKGS[@]} -gt 0 ]; then
    echo "  Installing: ${MISSING_PKGS[*]}"
    sudo pacman -S --needed --noconfirm "${MISSING_PKGS[@]}"
else
    echo "  All dependencies already installed ✓"
fi

# ── 2. Download or copy binary ────────────────────────────────
echo ""
mkdir -p "$INSTALL_DIR"

# If running from a cloned repo that already has the binary, just copy it.
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
if [ -f "$SCRIPT_DIR/MP3Player-linux-x64" ]; then
    echo "▶ Copying pre-built binary from repo…"
    cp "$SCRIPT_DIR/MP3Player-linux-x64" "$BINARY"
else
    echo "▶ Downloading pre-built binary from GitHub Releases…"
    if command -v curl &>/dev/null; then
        curl -L "$RELEASE_URL" -o "$BINARY"
    elif command -v wget &>/dev/null; then
        wget -q --show-progress "$RELEASE_URL" -O "$BINARY"
    else
        echo "❌ curl or wget not found. Install one: sudo pacman -S curl"
        exit 1
    fi
fi

chmod +x "$BINARY"
echo "  Binary ready at $BINARY ✓"

# ── 3. Desktop entry ──────────────────────────────────────────
echo ""
echo "▶ Creating desktop shortcut…"
mkdir -p "$DESKTOP_DIR"

cat > "$DESKTOP_DIR/lmp.desktop" <<EOF
[Desktop Entry]
Version=1.0
Type=Application
Name=LMP – Libuntu Music Player
GenericName=Music Player
Comment=Lightweight GTK music player with YouTube download
Exec=$BINARY
Icon=audio-x-generic
Terminal=false
Categories=AudioVideo;Audio;Player;
Keywords=music;mp3;player;youtube;
StartupNotify=true
EOF

chmod +x "$DESKTOP_DIR/lmp.desktop"
update-desktop-database "$DESKTOP_DIR" 2>/dev/null || true
echo "  Desktop entry created ✓"

# ── 4. Done ───────────────────────────────────────────────────
echo ""
echo "════════════════════════════════════════"
echo "  ✅  LMP installed successfully!"
echo "  Launch: search 'LMP' in your app menu"
echo "  Or run: $BINARY"
echo "════════════════════════════════════════"
