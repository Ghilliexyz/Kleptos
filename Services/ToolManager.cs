using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kleptos.Models;

namespace Kleptos.Services;

/// <summary>
/// Keeps the external binaries current: yt-dlp (stable or nightly channel, all OSes) and
/// ffmpeg/ffprobe (downloaded on demand on macOS/Linux, or on Windows if the bundled copy is missing).
/// </summary>
public sealed class ToolManager
{
    private readonly HttpClient _http;

    public ToolManager(HttpClient http) => _http = http;

    public static string YtDlpPath => Path.Combine(AppContext.BaseDirectory, PlatformHelper.YtDlpBinaryName);

    public static string ReleasesApi(YtDlpChannel channel) => channel == YtDlpChannel.Nightly
        ? "https://api.github.com/repos/yt-dlp/yt-dlp-nightly-builds/releases/latest"
        : "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";

    public static string AssetUrl(YtDlpChannel channel, string tag) => channel == YtDlpChannel.Nightly
        ? $"https://github.com/yt-dlp/yt-dlp-nightly-builds/releases/download/{tag}/{PlatformHelper.YtDlpAssetName}"
        : $"https://github.com/yt-dlp/yt-dlp/releases/download/{tag}/{PlatformHelper.YtDlpAssetName}";

    public async Task<string?> GetLatestYtDlpVersionAsync(YtDlpChannel channel, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(ReleasesApi(channel), ct);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return doc.RootElement.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// The GitHub tag and `yt-dlp --version` print the same string on both channels
    /// (e.g. "2026.10.01" stable, "2026.10.29.233024" nightly), so switching channel
    /// always shows up as a difference and triggers a download.
    /// </summary>
    public static bool SameVersion(string? installed, string? latest)
    {
        if (string.IsNullOrWhiteSpace(installed) || string.IsNullOrWhiteSpace(latest)) return false;
        installed = installed.Trim();
        latest = latest.Trim();
        return string.Equals(installed, latest, StringComparison.OrdinalIgnoreCase);
    }

    public async Task DownloadYtDlpAsync(YtDlpChannel channel, string tag, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        string target = YtDlpPath;
        string tempPath = Path.Combine(Path.GetTempPath(), $"kleptos_yt-dlp_{Guid.NewGuid():N}.new");
        string backupPath = target + ".old";

        await DownloadFileAsync(AssetUrl(channel, tag), tempPath, progress, ct);

        if (!PlatformHelper.IsValidExecutable(tempPath, minBytes: 1_000_000))
        {
            TryDelete(tempPath);
            throw new InvalidOperationException($"Downloaded {PlatformHelper.YtDlpAssetName} looks truncated or is not a valid executable.");
        }

        if (File.Exists(target))
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    File.Replace(tempPath, target, backupPath, ignoreMetadataErrors: true);
                    TryDelete(backupPath);
                }
                catch
                {
                    // File.Replace can be flaky (locked dest, cross-fs); fall back to a plain move.
                    try { File.Move(tempPath, target, overwrite: true); }
                    catch { TryDelete(tempPath); throw; }
                }
            }
            else
            {
                File.Move(tempPath, target, overwrite: true);
            }
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(tempPath, target);
        }

        PlatformHelper.MakeExecutable(target);
    }

    // ── ffmpeg ──

    /// <summary>Where Kleptos puts a downloaded ffmpeg.</summary>
    public static string ManagedFfmpegPath => Path.Combine(AppPaths.ToolsDir, PlatformHelper.FfmpegBinaryName);

    /// <summary>Download URLs for ffmpeg and ffprobe on this OS/arch, or null when unsupported.</summary>
    public static (string Ffmpeg, string? Ffprobe)? FfmpegSources()
    {
        bool arm = RuntimeInformation.OSArchitecture == Architecture.Arm64;
        if (OperatingSystem.IsMacOS())
        {
            // Martin Riedl's static builds (arm64 + amd64), stable redirect URLs.
            string arch = arm ? "arm64" : "amd64";
            return ($"https://ffmpeg.martin-riedl.de/redirect/latest/macos/{arch}/release/ffmpeg.zip",
                    $"https://ffmpeg.martin-riedl.de/redirect/latest/macos/{arch}/release/ffprobe.zip");
        }
        if (OperatingSystem.IsLinux())
        {
            string arch = arm ? "arm64" : "amd64";
            return ($"https://ffmpeg.martin-riedl.de/redirect/latest/linux/{arch}/release/ffmpeg.zip",
                    $"https://ffmpeg.martin-riedl.de/redirect/latest/linux/{arch}/release/ffprobe.zip");
        }
        if (OperatingSystem.IsWindows())
        {
            // yt-dlp's own patched builds; one zip holds bin/ffmpeg.exe and bin/ffprobe.exe.
            return (arm
                ? "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-winarm64-gpl.zip"
                : "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip", null);
        }
        return null;
    }

    /// <summary>Fallback for Intel Macs if the primary source stops publishing amd64 builds.</summary>
    private static (string Ffmpeg, string Ffprobe) MacIntelFallback() =>
        ("https://evermeet.cx/ffmpeg/getrelease/zip", "https://evermeet.cx/ffmpeg/getrelease/ffprobe/zip");

    /// <summary>Downloads ffmpeg (and ffprobe) into <see cref="AppPaths.ToolsDir"/>. Returns the ffmpeg path.</summary>
    public async Task<string> InstallFfmpegAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var sources = FfmpegSources() ?? throw new PlatformNotSupportedException("No ffmpeg download is available for this platform.");
        Directory.CreateDirectory(AppPaths.ToolsDir);

        var workDir = Path.Combine(Path.GetTempPath(), $"kleptos_ffmpeg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            var ffmpegZip = Path.Combine(workDir, "ffmpeg.zip");
            try
            {
                await DownloadFileAsync(sources.Ffmpeg, ffmpegZip, progress, ct);
            }
            catch when (OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture != Architecture.Arm64)
            {
                sources = (MacIntelFallback().Ffmpeg, MacIntelFallback().Ffprobe);
                await DownloadFileAsync(sources.Ffmpeg, ffmpegZip, progress, ct);
            }

            ExtractBinary(ffmpegZip, PlatformHelper.FfmpegBinaryName, workDir);
            ExtractBinary(ffmpegZip, PlatformHelper.FfprobeBinaryName, workDir, optional: true);

            if (sources.Ffprobe is string probeUrl)
            {
                try
                {
                    var probeZip = Path.Combine(workDir, "ffprobe.zip");
                    await DownloadFileAsync(probeUrl, probeZip, null, ct);
                    ExtractBinary(probeZip, PlatformHelper.FfprobeBinaryName, workDir, optional: true);
                }
                catch (OperationCanceledException) { throw; }
                catch { /* ffprobe is nice to have (duration probing); ffmpeg alone covers merging */ }
            }

            foreach (var name in new[] { PlatformHelper.FfmpegBinaryName, PlatformHelper.FfprobeBinaryName })
            {
                var src = Path.Combine(workDir, name);
                if (!File.Exists(src)) continue;
                if (!PlatformHelper.IsValidExecutable(src, minBytes: 1_000_000))
                    throw new InvalidOperationException($"Downloaded {name} is not a valid executable for this OS.");
                var dest = Path.Combine(AppPaths.ToolsDir, name);
                File.Move(src, dest, overwrite: true);
                PlatformHelper.MakeExecutable(dest);
            }

            if (!File.Exists(ManagedFfmpegPath))
                throw new InvalidOperationException("ffmpeg was not found in the downloaded archive.");
            return ManagedFfmpegPath;
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    private static void ExtractBinary(string zipPath, string binaryName, string destDir, bool optional = false)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.Entries
            .Where(e => string.Equals(e.Name, binaryName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName.Length)
            .FirstOrDefault();
        if (entry == null)
        {
            if (optional) return;
            throw new InvalidOperationException($"{binaryName} not found in {Path.GetFileName(zipPath)}.");
        }
        entry.ExtractToFile(Path.Combine(destDir, binaryName), overwrite: true);
    }

    private async Task DownloadFileAsync(string url, string path, IProgress<double>? progress, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long? total = resp.Content.Headers.ContentLength;

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(path);
        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (total is long t && t > 0) progress?.Report(read * 100.0 / t);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
