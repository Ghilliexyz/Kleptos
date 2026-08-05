#!/usr/bin/env bash
# Creates the macOS/Linux release archives for Kleptos.
# Called from build-release.bat via Git Bash.
#
# Notes:
# - Windows-only binaries (yt-dlp.exe, ffmpeg.exe) are excluded; on macOS/Linux
#   yt-dlp downloads itself on first launch and ffmpeg comes from the OS.
# - NTFS has no Unix exec bit and MSYS chmod is a no-op here, so the tar is
#   built in two passes: the Kleptos launcher goes in first with --mode=755,
#   then the rest is appended with default modes, then the tar is gzipped.
set -u
cd "$(dirname "$0")"

mkdir -p ./Releases

make_archive() {
    local src="$1" out="$2"
    for attempt in 1 2 3; do
        rm -f "${out%.gz}" "$out"
        tar -cf "${out%.gz}" --mode=755 -C "$src" ./Kleptos 2>/dev/null \
            && tar -rf "${out%.gz}" -C "$src" \
                   --exclude='./Kleptos' --exclude='./yt-dlp.exe' --exclude='./ffmpeg.exe' . 2>/dev/null \
            && gzip -f "${out%.gz}"
        local count
        count=$(tar -tzf "$out" 2>/dev/null | wc -l)
        # A complete publish output has ~220 files; anything far less means
        # the archive was truncated (e.g. transient AV/indexer interference).
        if [ "$count" -ge 200 ]; then
            echo "OK: $out ($count files)"
            return 0
        fi
        echo "Archive $out looks truncated ($count files), retrying (attempt $attempt)..."
        sleep 2
    done
    echo "ERROR: failed to create $out after 3 attempts"
    return 1
}

make_archive "./publish/osx-arm64" "./Releases/Kleptos-macos-arm64.tar.gz" || exit 1
make_archive "./publish/osx-x64"   "./Releases/Kleptos-macos-x64.tar.gz"   || exit 1
make_archive "./publish/linux-x64" "./Releases/Kleptos-linux-x64.tar.gz"   || exit 1
