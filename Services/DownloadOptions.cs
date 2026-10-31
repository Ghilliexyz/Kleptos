using System.Collections.Generic;
using Kleptos.Models;

namespace Kleptos.Services;

/// <summary>
/// Snapshot of every user choice that affects a yt-dlp run. Built from the UI once per
/// run, then handed to <see cref="YtDlpArgsBuilder"/>, which has no UI dependencies.
/// </summary>
public sealed record DownloadOptions
{
    public string OutputFolder { get; init; } = AppPaths.DefaultDownloadsFolder;

    /// <summary>Target extension, or "ext" for "let yt-dlp decide".</summary>
    public string FileExtension { get; init; } = "ext";

    /// <summary>0 = best (no height cap).</summary>
    public int QualityHeight { get; init; }

    public FormatPreset Preset { get; init; } = FormatPreset.Normal;

    /// <summary>A format spec picked in the preview card (e.g. "137+bestaudio/137"). Overrides Format/Quality/Preset stream selection.</summary>
    public string? ExactFormat { get; init; }

    public bool ThumbnailOnly { get; init; }
    public bool Playlist { get; init; }
    public string PlaylistItems { get; init; } = string.Empty;

    public bool Subtitles { get; init; }
    public string SubLangs { get; init; } = "en";
    public bool SponsorBlock { get; init; }

    public CookieSource CookieSource { get; init; } = CookieSource.None;
    public string CookiesFile { get; init; } = string.Empty;
    public string BrowserProfile { get; init; } = string.Empty;

    public bool UseArchive { get; init; }
    public string ArchivePath { get; init; } = string.Empty;

    public string RateLimit { get; init; } = string.Empty;
    public int ConcurrentFragments { get; init; } = 1;
    public int Retries { get; init; } = 10;
    public string Proxy { get; init; } = string.Empty;

    /// <summary>Template for auto-named files, relative to OutputFolder. May contain '/' for sub-folders.</summary>
    public string FilenameTemplate { get; init; } = "%(title)s";

    public string? FfmpegLocation { get; init; }

    public List<SegmentInfo>? Segments { get; init; }

    /// <summary>When false the structured progress template is not requested (used by tests to compare legacy output).</summary>
    public bool StructuredProgress { get; init; } = true;
}
