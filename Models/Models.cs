using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;

namespace Kleptos.Models;

/// <summary>One file produced by a run (a playlist run produces several).</summary>
public class DownloadItemStats
{
    public string Url { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    /// <summary>Name to show before yt-dlp reports a file name (the queue entry's title).</summary>
    public string Label { get; set; } = string.Empty;
    public bool Succeeded { get; set; }
    public bool AlreadyDownloaded { get; set; }
    public bool Cancelled { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public double FileSizeMB { get; set; }
    public double SpeedMBps { get; set; }
    public TimeSpan Duration { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }

    public bool IsFinished => EndTime != default;
}

/// <summary>Aggregate of one press of the Download button (or a retry).</summary>
public class SessionStats
{
    public List<DownloadItemStats> Items { get; } = new();
    public int TotalItems { get; set; }
    public DateTime SessionStart { get; set; } = DateTime.Now;

    /// <summary>Aggregate download speed samples (MB/s), one per second, for the sparkline.</summary>
    public List<double> SpeedSamples { get; } = new();
    /// <summary>Seconds between speed samples (doubles each time the history is down-sampled).</summary>
    public double SampleIntervalSeconds { get; set; } = 0.5;
    public double PeakSpeedMBps => SpeedSamples.Count > 0 ? SpeedSamples.Max() : 0;

    private bool _frozen;
    private TimeSpan _frozenDuration;

    public void Freeze()
    {
        if (_frozen) return;
        _frozenDuration = DateTime.Now - SessionStart;
        _frozen = true;
    }

    public bool IsFrozen => _frozen;

    public int CompletedItems => Items.Count(i => i.IsFinished);
    public int SuccessCount => Items.Count(i => i.Succeeded && !i.AlreadyDownloaded);
    public int FailCount => Items.Count(i => !i.Succeeded && !i.AlreadyDownloaded && !i.Cancelled && i.IsFinished);
    public int CancelledCount => Items.Count(i => i.Cancelled);
    public int AlreadyDownloadedCount => Items.Count(i => i.AlreadyDownloaded);
    public double TotalSizeMB => Items.Sum(i => i.FileSizeMB);

    public double AverageSpeedMBps
    {
        get
        {
            var withSpeed = Items.Where(i => i.SpeedMBps > 0).ToList();
            return withSpeed.Count > 0 ? withSpeed.Average(i => i.SpeedMBps) : 0;
        }
    }

    public TimeSpan TotalDuration => _frozen ? _frozenDuration : DateTime.Now - SessionStart;

    public TimeSpan AverageItemDuration
    {
        get
        {
            var withDuration = Items.Where(i => i.Duration > TimeSpan.Zero).ToList();
            return withDuration.Count > 0 ? TimeSpan.FromTicks((long)withDuration.Average(i => i.Duration.Ticks)) : TimeSpan.Zero;
        }
    }
}

public class SegmentInfo
{
    public string StartTime { get; set; } = string.Empty;
    public string EndTime { get; set; } = string.Empty;
}

public enum QueueStatus { Pending, Downloading, Succeeded, Failed, Skipped, Canceled }

public class QueueEntry
{
    public string Url = "";
    /// <summary>Display name (multi-line title, preview title, or the URL).</summary>
    public string Title = "";
    /// <summary>Output base name for multi-download (may contain %(title)s).</summary>
    public string BaseName = "";
    public bool SingleNaming;
    public QueueStatus Status = QueueStatus.Pending;
    public string ErrorMessage = "";

    // Live progress, only meaningful while Status == Downloading.
    public double Percent;
    public double SpeedMBps;
    public double? EtaSeconds;
    public int PlaylistIndex;
    public int PlaylistCount;

    /// <summary>Overall 0-100 progress of this entry, playlist aware.</summary>
    public double OverallPercent => Status switch
    {
        QueueStatus.Succeeded or QueueStatus.Skipped => 100,
        QueueStatus.Downloading when PlaylistCount > 1 =>
            Math.Clamp(((Math.Max(PlaylistIndex, 1) - 1) * 100.0 + Percent) / PlaylistCount, 0, 100),
        QueueStatus.Downloading => Math.Clamp(Percent, 0, 100),
        _ => 0,
    };

    [JsonIgnore] public CancellationTokenSource? ItemCts;
}

public class HistoryEntry
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime When { get; set; }
    public bool Succeeded { get; set; }
    public double FileSizeMB { get; set; }
    public string Folder { get; set; } = "";
}

public enum FormatPreset
{
    Normal,
    YouTube4K,
    MusicLibrary,
    Podcast,
}

/// <summary>Where yt-dlp gets cookies from.</summary>
public enum CookieSource
{
    None,
    File,
    Chrome,
    Firefox,
    Edge,
    Brave,
    Opera,
    Vivaldi,
    Chromium,
    Safari,
}

public enum YtDlpChannel { Stable, Nightly }

public class KleptosSettings
{
    // Per-download options (right-hand panel)
    public string OutputFolder { get; set; } = string.Empty;
    public string LastFormat { get; set; } = "default";
    public string LastQuality { get; set; } = "best";
    public string LastPreset { get; set; } = "Normal";
    public string LastCookiesFile { get; set; } = string.Empty;
    public bool MultiDownload { get; set; }
    public bool ThumbnailOnly { get; set; }
    public bool PlaylistMode { get; set; }
    public string PlaylistItems { get; set; } = string.Empty;
    public bool DownloadSubs { get; set; }
    public string SubLangs { get; set; } = "en";
    public bool SponsorBlock { get; set; }

    // Cookies
    public CookieSource CookieSource { get; set; } = CookieSource.None;
    public string BrowserProfile { get; set; } = string.Empty;

    // Settings page: downloads
    public int MaxParallelDownloads { get; set; } = 2;
    public int ConcurrentFragments { get; set; } = 4;
    public string RateLimit { get; set; } = string.Empty;
    public int Retries { get; set; } = 10;
    public string Proxy { get; set; } = string.Empty;

    // Settings page: archive
    public bool UseDownloadArchive { get; set; }
    public string ArchivePath { get; set; } = string.Empty;

    // Settings page: naming
    public string FilenameTemplate { get; set; } = "%(title)s";

    // Settings page: behaviour
    public bool AutoPreview { get; set; } = true;
    public bool ClipboardAutoPaste { get; set; } = true;

    // Settings page: tools
    public YtDlpChannel YtDlpChannel { get; set; } = YtDlpChannel.Stable;

    // Set once the user has answered the "download ffmpeg?" prompt.
    public bool FfmpegPromptDismissed { get; set; }

    // Set once the old cookies.txt-only setting has been migrated to CookieSource.
    public bool CookieSourceMigrated { get; set; }
}
