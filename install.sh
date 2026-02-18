#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────
#  LMP – Libuntu Music Player  |  install.sh
#  Supports: Arch Linux / Arch-based (EndeavourOS, Manjaro …)
#  Run:  chmod +x install.sh && ./install.sh
# ─────────────────────────────────────────────────────────────
set -e

INSTALL_DIR="$HOME/.local/share/LMP"
DESKTOP_DIR="$HOME/.local/share/applications"
BINARY="$INSTALL_DIR/MP3Player"

echo "════════════════════════════════════════"
echo "  LMP – Libuntu Music Player  Installer"
echo "════════════════════════════════════════"

# ── 1. Check / install system dependencies ───────────────────
echo ""
echo "▶ Checking system dependencies…"

MISSING_PKGS=()

command -v dotnet &>/dev/null || MISSING_PKGS+=(dotnet-sdk)
pacman -Q mpg123     &>/dev/null || MISSING_PKGS+=(mpg123)
pacman -Q libpulse   &>/dev/null || MISSING_PKGS+=(libpulse)
pacman -Q gtk3       &>/dev/null || MISSING_PKGS+=(gtk3)
pacman -Q yt-dlp     &>/dev/null || MISSING_PKGS+=(yt-dlp)
pacman -Q ffmpeg     &>/dev/null || MISSING_PKGS+=(ffmpeg)

if [ ${#MISSING_PKGS[@]} -gt 0 ]; then
    echo "  Installing missing packages: ${MISSING_PKGS[*]}"
    sudo pacman -S --needed --noconfirm "${MISSING_PKGS[@]}"
else
    echo "  All dependencies already installed ✓"
fi

# ── 2. Build & publish ────────────────────────────────────────
echo ""
echo "▶ Building LMP…"
dotnet publish MP3Player.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -o "$INSTALL_DIR"

chmod +x "$BINARY"
echo "  Published to $INSTALL_DIR ✓"

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
