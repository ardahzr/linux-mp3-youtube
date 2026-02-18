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
- .NET SDK 9+
- `mpg123`, `libpulse`, `gtk3`, `yt-dlp`, `ffmpeg`

## Install (one command)

```bash
git clone https://github.com/YOUR_USERNAME/lmp.git
cd lmp
chmod +x install.sh
./install.sh
```

After install, search **"LMP"** in your application menu.

## Update

```bash
cd lmp
git pull
./install.sh
```

## Manual run (without installing)

```bash
dotnet run --project MP3Player.csproj
```

## Uninstall

```bash
rm -rf ~/.local/share/LMP
rm ~/.local/share/applications/lmp.desktop
```
