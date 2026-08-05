# Kleptos

![Downloads](https://img.shields.io/github/downloads/Ghilliexyz/Kleptos/total?style=for-the-badge&color=ff3c3c&logo=github&logoColor=white&labelColor=191919)
![Version](https://img.shields.io/github/v/release/Ghilliexyz/Kleptos?style=for-the-badge&color=ff3c3c&logo=semanticrelease&logoColor=white&labelColor=191919)
![Platform](https://img.shields.io/badge/platform-windows%20%7C%20macos%20%7C%20linux-ff3c3c?style=for-the-badge&logo=windows&logoColor=white&labelColor=191919)
![.NET](https://img.shields.io/badge/.NET-8.0-ff3c3c?style=for-the-badge&logo=dotnet&logoColor=white&labelColor=191919)
![Last commit](https://img.shields.io/github/last-commit/Ghilliexyz/Kleptos?style=for-the-badge&color=ff3c3c&logo=git&logoColor=white&labelColor=191919)

A desktop wrapper around yt-dlp and ffmpeg for Windows, macOS, and Linux. Paste a URL, pick a format, hit download. That's it.

Built because I got tired of remembering yt-dlp flags every time I wanted to grab a video.

## Features

- Single and bulk URL downloads (multi-download: one URL per line, optional custom file names)
- Download queue with per-item status, cancel, retry, and clear-finished
- Download history with quick re-download and open-folder shortcuts
- Format and quality presets (mp4, mp3, webm, and friends)
- Playlist mode toggle, with an inline hint when a URL looks like a playlist
- Subtitle downloads in 27 languages, plus SponsorBlock removal
- Trim clips on an interactive timeline in the Segments tab
- Clipboard auto-paste and drag-and-drop URLs
- Live speed and ETA while downloading, plus a post-run stats summary
- Raw yt-dlp console with a copy button for bug reports
- Cookie support for authenticated downloads, with a stale-cookie warning so you know when to re-export
- Built-in Help page that walks you through the cookies setup
- Minimizes to the tray while downloading
- Auto-updates via Velopack
- Bundled yt-dlp that updates itself from GitHub releases
- Dark theme because of course

## Install

Grab the latest build for your OS from the [releases page](https://github.com/Ghilliexyz/Kleptos/releases).

## Building from source

Requires .NET 8 SDK. Built with [Avalonia UI](https://avaloniaui.net/).

```bash
dotnet build Kleptos.csproj
```

For a release build, pick your runtime identifier (`win-x64`, `osx-arm64`, `osx-x64`, `linux-x64`):

```bash
dotnet publish Kleptos.csproj -c Release --self-contained -r win-x64 -o .\publish
```

The `build-release.bat` script is the one-step release flow: it publishes all four platforms, runs Velopack packaging (Windows installer + update channel), and creates the macOS/Linux archives via `build-archives.sh` - everything lands in `Releases\` ready to upload to a GitHub release.

## Notes

- **Windows:** `yt-dlp.exe` and `ffmpeg.exe` sit next to the app; the installer ships them. If you're running from source, drop them in the build output directory - or just launch once and Kleptos will download yt-dlp itself.
- **macOS / Linux:** yt-dlp is downloaded automatically (per-platform binary) on first launch. Install ffmpeg separately (`brew install ffmpeg` / `sudo apt install ffmpeg`) - it's needed for merging formats, audio extraction, and thumbnail conversion.

## License

See LICENSE.
