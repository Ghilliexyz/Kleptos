using System;
using System.IO;

namespace Kleptos;

/// <summary>
/// Per-OS helpers for the external binaries Kleptos depends on (yt-dlp, ffmpeg).
/// </summary>
public static class PlatformHelper
{
    /// <summary>File name of the yt-dlp binary on the current OS.</summary>
    public static string YtDlpBinaryName => OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp";

    /// <summary>Asset name on the yt-dlp GitHub release page for the current OS.</summary>
    public static string YtDlpAssetName =>
        OperatingSystem.IsWindows() ? "yt-dlp.exe" :
        OperatingSystem.IsMacOS() ? "yt-dlp_macos" :
        "yt-dlp_linux";

    /// <summary>chmod +x on non-Windows; no-op on Windows. Never throws.</summary>
    public static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Verifies the file is a plausible yt-dlp binary for the current OS:
    /// at least 1 MB and the right executable magic (PE / Mach-O / ELF).
    /// </summary>
    public static bool IsValidYtDlpBinary(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 1_000_000) // yt-dlp is well over 1 MB
                return false;

            Span<byte> header = stackalloc byte[4];
            using (var fs = File.OpenRead(path))
            {
                if (fs.Read(header) != 4) return false;
            }

            if (OperatingSystem.IsWindows())
            {
                // PE executable: "MZ"
                return header[0] == (byte)'M' && header[1] == (byte)'Z';
            }

            if (OperatingSystem.IsMacOS())
            {
                // Mach-O 32/64-bit (both endians) or fat/universal binary.
                return
                    (header[0] == 0xFE && header[1] == 0xED && header[2] == 0xFA && header[3] == 0xCE) ||
                    (header[0] == 0xCE && header[1] == 0xFA && header[2] == 0xED && header[3] == 0xFE) ||
                    (header[0] == 0xFE && header[1] == 0xED && header[2] == 0xFA && header[3] == 0xCF) ||
                    (header[0] == 0xCF && header[1] == 0xFA && header[2] == 0xED && header[3] == 0xFE) ||
                    (header[0] == 0xCA && header[1] == 0xFE && header[2] == 0xBA && header[3] == 0xBE);
            }

            // Linux (and anything else): ELF
            return header[0] == 0x7F && header[1] == 0x45 && header[2] == 0x4C && header[3] == 0x46;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Locates ffmpeg: first next to the app, then on PATH. Returns null when not found
    /// (ffmpeg is only needed for merging/recode/thumbnail conversion, so this is a warning,
    /// not a fatal error).
    /// </summary>
    public static string? FindFfmpeg()
    {
        var fileName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

        var nextToApp = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(nextToApp)) return nextToApp;

        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathVar))
        {
            foreach (var dir in pathVar.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    var candidate = Path.Combine(dir.Trim(), fileName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { /* ignore malformed PATH entries */ }
            }
        }

        return null;
    }
}
