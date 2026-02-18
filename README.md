# 🎵 LMP – Libuntu Music Player

A Spotify-inspired dark-theme music player for Linux, built with .NET + GTK3.

## Features
- 🎶 MP3, FLAC, OGG, WAV, M4A, AAC, OPUS playback
- 📁 Persistent music library (`~/Documents/MP3Player/`)
- 🎛️ Multi-playlist support with JSON persistence
- 🎨 Spotify-style dark theme with album art thumbnails
- 📥 YouTube → MP3 download via `yt-dlp` (highest quality)

## Requirements
- Arch Linux (or Arch-based: EndeavourOS, Manjaro …)
- `mpg123`, `libpulse`, `gtk3`, `yt-dlp`, `ffmpeg`
- **.NET SDK is NOT required** — pre-built binary is included

## Install (3 commands, no dotnet needed)

```bash
git clone https://github.com/ardahzr/linux-mp3-youtube.git
cd linux-mp3-youtube
chmod +x install.sh && ./install.sh
```

The script will:
1. Install missing system packages via `pacman` (needs `sudo`)
2. Copy the pre-built binary to `~/.local/share/LMP/`
3. Add **LMP** to your application menu

## Update

```bash
cd linux-mp3-youtube
git pull
chmod +x install.sh && ./install.sh
```

## Manual run (without installing)

```bash
./MP3Player-linux-x64
```

## Uninstall

```bash
rm -rf ~/.local/share/LMP
rm ~/.local/share/applications/lmp.desktop
```
