using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Kleptos.Services;

public enum OutputEventKind
{
    None,
    Progress,          // structured or legacy progress line
    Destination,       // [download] Destination: <path>
    Merging,           // [Merger] Merging formats into "<path>"
    PlaylistItem,      // [download] Downloading item N of M
    AlreadyDownloaded, // file already present, or thumbnail already present
    InArchive,         // skipped because it is recorded in the download archive
    ThumbnailWritten,
    Error,
}

/// <summary>One interesting thing yt-dlp printed. Fields are filled per kind.</summary>
public sealed record OutputEvent(OutputEventKind Kind)
{
    public static readonly OutputEvent Nothing = new(OutputEventKind.None);

    public string Status { get; init; } = string.Empty;  // "downloading" / "finished"
    public double Percent { get; init; } = -1;
    public long? DownloadedBytes { get; init; }
    public long? TotalBytes { get; init; }
    public double? SpeedBytesPerSec { get; init; }
    public double? EtaSeconds { get; init; }
    public int PlaylistIndex { get; init; }
    public int PlaylistCount { get; init; }
    public string Path { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public bool AuthRequired { get; init; }

    /// <summary>True when this line is one of our structured progress lines (hidden from the console).</summary>
    public bool IsStructured { get; init; }

    public double SizeMB => (TotalBytes ?? DownloadedBytes ?? 0) / (1024.0 * 1024.0);
    public double SpeedMBps => (SpeedBytesPerSec ?? 0) / (1024.0 * 1024.0);
}

/// <summary>
/// Parses yt-dlp output. Progress comes from a machine-readable template
/// (see <see cref="ProgressTemplate"/>), so it does not depend on yt-dlp's human-facing
/// wording; the regexes below are kept for everything else and as a fallback.
/// </summary>
public static class YtDlpOutputParser
{
    public const string Marker = "[KLEPTOS]";

    /// <summary>Passed as --progress-template "download:...". Fields are '|' separated; the filename is last because it may contain '|'.</summary>
    public const string ProgressTemplate =
        Marker + " %(progress.status)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|" +
        "%(progress.speed)s|%(progress.eta)s|%(info.playlist_index)s|%(info.n_entries)s|%(progress.filename)s";

    private static readonly Regex LegacyProgressRegex =
        new(@"\[download\]\s+([\d.]+)%\s+of\s+~?\s*([\d.]+)([\w/]+)(?:\s+at\s+([\d.]+)([\w/]+))?(?:\s+ETA\s+(\S+))?", RegexOptions.Compiled);
    private static readonly Regex DestinationRegex =
        new(@"\[download\]\s+Destination:\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex MergerRegex =
        new(@"\[Merger\]\s+Merging formats into\s+""(.+)""", RegexOptions.Compiled);
    private static readonly Regex ExtractAudioRegex =
        new(@"\[ExtractAudio\]\s+Destination:\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex PlaylistProgressRegex =
        new(@"\[download\]\s+Downloading item\s+(\d+)\s+of\s+(\d+)", RegexOptions.Compiled);
    private static readonly Regex AlreadyDownloadedRegex =
        new(@"\[download\]\s+(.+?)\s+has already been downloaded", RegexOptions.Compiled);
    private static readonly Regex InArchiveRegex =
        new(@"(?:\[download\]\s+)?(\S+?):?\s+has already been recorded in the archive", RegexOptions.Compiled);
    // Thumbnail-only runs (--skip-download --write-thumbnail) never emit a progress or
    // merge line, so success has to be detected from the thumbnail writer output instead.
    private static readonly Regex ThumbnailWrittenRegex =
        new(@"Writing\s+.*\bthumbnail\b.*\bto:\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ThumbnailAlreadyRegex =
        new(@"\bthumbnail\b.*\bis already present\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ErrorRegex =
        new(@"ERROR:\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex AuthRequiredRegex =
        new(@"Sign in to confirm|Please log in|This video is private|--cookies-from-browser|unable to download webpage|members-only|login required",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CookieDbRegex =
        new(@"Could not copy .* cookie database|failed to decrypt|cookies? database", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>True for yt-dlp errors about reading a browser's cookie store (browser open / locked / unsupported).</summary>
    public static bool IsCookieDatabaseError(string line) => CookieDbRegex.IsMatch(line);

    public static OutputEvent Parse(string? line)
    {
        if (string.IsNullOrEmpty(line)) return OutputEvent.Nothing;

        int markerAt = line.IndexOf(Marker, StringComparison.Ordinal);
        if (markerAt >= 0)
            return ParseStructured(line.Substring(markerAt + Marker.Length).Trim());

        Match m;

        m = DestinationRegex.Match(line);
        if (m.Success) return new OutputEvent(OutputEventKind.Destination) { Path = m.Groups[1].Value.Trim() };

        m = ExtractAudioRegex.Match(line);
        if (m.Success) return new OutputEvent(OutputEventKind.Destination) { Path = m.Groups[1].Value.Trim() };

        m = MergerRegex.Match(line);
        if (m.Success) return new OutputEvent(OutputEventKind.Merging) { Path = m.Groups[1].Value.Trim() };

        m = PlaylistProgressRegex.Match(line);
        if (m.Success && int.TryParse(m.Groups[1].Value, out int cur) && int.TryParse(m.Groups[2].Value, out int tot))
            return new OutputEvent(OutputEventKind.PlaylistItem) { PlaylistIndex = cur, PlaylistCount = tot };

        m = InArchiveRegex.Match(line);
        if (m.Success)
            return new OutputEvent(OutputEventKind.InArchive) { Message = m.Groups[1].Value };

        m = AlreadyDownloadedRegex.Match(line);
        if (m.Success) return new OutputEvent(OutputEventKind.AlreadyDownloaded) { Path = m.Groups[1].Value.Trim() };
        if (ThumbnailAlreadyRegex.IsMatch(line)) return new OutputEvent(OutputEventKind.AlreadyDownloaded);

        m = ThumbnailWrittenRegex.Match(line);
        if (m.Success) return new OutputEvent(OutputEventKind.ThumbnailWritten) { Path = m.Groups[1].Value.Trim() };

        m = ErrorRegex.Match(line);
        if (m.Success)
            return new OutputEvent(OutputEventKind.Error)
            {
                Message = m.Groups[1].Value.Trim(),
                AuthRequired = AuthRequiredRegex.IsMatch(line),
            };

        m = LegacyProgressRegex.Match(line);
        if (m.Success)
        {
            double percent = ParseDouble(m.Groups[1].Value) ?? -1;
            double? sizeMB = ParseDouble(m.Groups[2].Value) is double s ? ConvertToMB(s, m.Groups[3].Value) : null;
            double? speedMB = m.Groups[4].Success && ParseDouble(m.Groups[4].Value) is double sp ? ConvertToMB(sp, m.Groups[5].Value) : null;
            return new OutputEvent(OutputEventKind.Progress)
            {
                Status = percent >= 100 ? "finished" : "downloading",
                Percent = percent,
                TotalBytes = sizeMB is double mb ? (long)(mb * 1024 * 1024) : null,
                SpeedBytesPerSec = speedMB is double smb ? smb * 1024 * 1024 : null,
                EtaSeconds = m.Groups[6].Success ? ParseEta(m.Groups[6].Value) : null,
            };
        }

        if (AuthRequiredRegex.IsMatch(line))
            return new OutputEvent(OutputEventKind.None) { AuthRequired = true };

        return OutputEvent.Nothing;
    }

    private static OutputEvent ParseStructured(string payload)
    {
        // status|downloaded|total|total_estimate|speed|eta|playlist_index|n_entries|filename
        var f = payload.Split('|', 9);
        if (f.Length < 6) return OutputEvent.Nothing;

        long? downloaded = ParseLong(f[1]);
        long? total = ParseLong(f[2]) ?? ParseLong(f[3]);
        double percent = -1;
        if (downloaded is long d && total is long t && t > 0)
            percent = Math.Clamp(d * 100.0 / t, 0, 100);
        string status = f[0].Trim();
        if (status == "finished") percent = 100;

        return new OutputEvent(OutputEventKind.Progress)
        {
            IsStructured = true,
            Status = status,
            Percent = percent,
            DownloadedBytes = downloaded,
            TotalBytes = total,
            SpeedBytesPerSec = ParseDouble(f[4]),
            EtaSeconds = ParseDouble(f[5]),
            PlaylistIndex = f.Length > 6 ? (int)(ParseLong(f[6]) ?? 0) : 0,
            PlaylistCount = f.Length > 7 ? (int)(ParseLong(f[7]) ?? 0) : 0,
            Path = f.Length > 8 && f[8] != "NA" ? f[8].Trim() : string.Empty,
        };
    }

    private static long? ParseLong(string s)
    {
        s = s.Trim();
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var dv) && !double.IsNaN(dv)) return (long)dv;
        return null;
    }

    private static double? ParseDouble(string s) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && !double.IsNaN(v) ? v : null;

    private static double? ParseEta(string s)
    {
        var secs = Formatting.ParseClock(s);
        return secs >= 0 ? secs : null;
    }

    public static double ConvertToMB(double value, string unit)
    {
        var u = unit.Trim().TrimEnd('/', 's').ToLowerInvariant();
        return u switch
        {
            "b" => value / (1024.0 * 1024.0),
            "kib" => value / 1024.0,
            "mib" => value,
            "gib" => value * 1024.0,
            "kb" => value / 1000.0,
            "mb" => value,
            "gb" => value * 1000.0,
            _ => value,
        };
    }

    /// <summary>A short, readable console line for a structured progress event, or null to print nothing.</summary>
    public static string? ConsoleLine(OutputEvent e)
    {
        if (e.Kind != OutputEventKind.Progress || !e.IsStructured) return null;
        if (e.Status != "finished") return null;
        var name = string.IsNullOrEmpty(e.Path) ? string.Empty : " " + System.IO.Path.GetFileName(e.Path);
        return $"[download] 100% of {Formatting.Size(e.SizeMB)}{name}";
    }
}
