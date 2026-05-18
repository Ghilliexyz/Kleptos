# Kleptos

![Downloads](https://img.shields.io/github/downloads/Ghilliexyz/Kleptos/total?style=for-the-badge&color=4ecdc4&logo=github&logoColor=white&labelColor=191919)
![Version](https://img.shields.io/github/v/release/Ghilliexyz/Kleptos?style=for-the-badge&color=4ecdc4&logo=semanticrelease&logoColor=white&labelColor=191919)
![Platform](https://img.shields.io/badge/platform-windows-4ecdc4?style=for-the-badge&logo=windows&logoColor=white&labelColor=191919)
![.NET](https://img.shields.io/badge/.NET-8.0-4ecdc4?style=for-the-badge&logo=dotnet&logoColor=white&labelColor=191919)
![Last commit](https://img.shields.io/github/last-commit/Ghilliexyz/Kleptos?style=for-the-badge&color=4ecdc4&logo=git&logoColor=white&labelColor=191919)

A Windows desktop wrapper around yt-dlp and ffmpeg. Paste a URL, pick a format, hit download. That's it.

Built because I got tired of remembering yt-dlp flags every time I wanted to grab a video.

## Features

- Single and bulk URL downloads
- Format and quality presets (mp4, mp3, webm, and friends)
- Cookie support for authenticated downloads, with a stale-cookie warning so you know when to re-export
- Auto-updates via Velopack
- Bundled yt-dlp that updates itself from GitHub releases
- Dark theme because of course

## Install

Grab the latest installer from the [releases page](https://github.com/Ghilliexyz/Kleptos/releases). Windows x64 only.

## Building from source

Requires .NET 8 SDK.

```bash
dotnet build Kleptos.csproj
```

For a release build:

```bash
dotnet publish Kleptos.csproj -c Release --self-contained -r win-x64 -o .\publish
```

The `build.bat` script wraps this and the Velopack packaging step if you want the full installer flow.

## Notes

You'll need `yt-dlp.exe` and `ffmpeg.exe` in the app directory. The installer ships with them; if you're running from source, drop them in `bin\Debug\net8.0-windows\` yourself.

## License

See LICENSE.
