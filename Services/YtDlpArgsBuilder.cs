using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Kleptos.Models;

namespace Kleptos.Services;

/// <summary>Turns a <see cref="DownloadOptions"/> into a yt-dlp argument list. Pure, no I/O.</summary>
public static class YtDlpArgsBuilder
{
    private static readonly string[] ExtractableAudioFormats = { "mp3", "aac", "m4a", "wav", "opus", "flac" };
    private static readonly string[] VideoFormats = { "mp4", "mov", "mkv", "flv", "3gp" };
    private static readonly string[] MergeContainers = { "mp4", "mov", "mkv", "webm" };

    public static string PresetExtension(FormatPreset preset) => preset switch
    {
        FormatPreset.YouTube4K => "mp4",
        FormatPreset.MusicLibrary => "mp3",
        FormatPreset.Podcast => "m4a",
        _ => string.Empty,
    };

    /// <summary>
    /// Builds the full argument list for one URL, URL included as the last argument.
    /// </summary>
    /// <param name="customName">Single mode: the File Name box. Empty means "use the filename template".</param>
    /// <param name="multiBaseName">Multi mode: the base name parsed from the line, or null in single mode.</param>
    public static List<string> Build(DownloadOptions o, string url, string customName = "", string? multiBaseName = null)
    {
        var args = new List<string>();

        // Effective extension: presets lock one in (except in thumbnail mode, which has none).
        string ext = o.FileExtension;
        if (!o.ThumbnailOnly && o.Preset != FormatPreset.Normal && string.IsNullOrEmpty(o.ExactFormat))
            ext = PresetExtension(o.Preset);

        // Stream selection
        if (o.ThumbnailOnly)
        {
            args.Add("--skip-download");
            args.Add("--write-thumbnail");
        }
        else if (!string.IsNullOrWhiteSpace(o.ExactFormat))
        {
            AppendExactFormatArgs(args, o.ExactFormat!, ext);
        }
        else if (o.Preset != FormatPreset.Normal)
        {
            AppendPresetArgs(args, o.Preset);
        }
        else
        {
            AppendFormatArgs(args, ext, o.QualityHeight);
        }

        AppendCookiesArgs(args, o);

        args.Add("-o");
        args.Add(BuildOutputTemplate(o, ext, customName, multiBaseName));

        args.Add("--no-mtime");

        if (o.StructuredProgress)
        {
            args.Add("--newline");
            args.Add("--progress-template");
            args.Add("download:" + YtDlpOutputParser.ProgressTemplate);
        }

        if (o.Playlist)
        {
            args.Add("--yes-playlist");
            if (!string.IsNullOrWhiteSpace(o.PlaylistItems))
            {
                args.Add("--playlist-items");
                args.Add(o.PlaylistItems.Replace(" ", string.Empty));
            }
        }
        else
        {
            args.Add("--no-playlist");
        }

        if (o.UseArchive && !string.IsNullOrWhiteSpace(o.ArchivePath) && !o.ThumbnailOnly)
        {
            args.Add("--download-archive");
            args.Add(o.ArchivePath);
        }

        // Network
        if (!string.IsNullOrWhiteSpace(o.RateLimit) && FileNames.IsValidRateLimit(o.RateLimit))
        {
            args.Add("--limit-rate");
            args.Add(o.RateLimit.Replace(" ", string.Empty).ToUpperInvariant());
        }
        if (o.ConcurrentFragments > 1)
        {
            args.Add("--concurrent-fragments");
            args.Add(Math.Min(o.ConcurrentFragments, 16).ToString());
        }
        if (o.Retries != 10 && o.Retries >= 0)
        {
            args.Add("--retries");
            args.Add(o.Retries.ToString());
        }
        if (!string.IsNullOrWhiteSpace(o.Proxy))
        {
            args.Add("--proxy");
            args.Add(o.Proxy.Trim());
        }

        if (!string.IsNullOrWhiteSpace(o.FfmpegLocation))
        {
            args.Add("--ffmpeg-location");
            args.Add(o.FfmpegLocation!);
        }

        if (!o.ThumbnailOnly && o.Subtitles)
        {
            var langs = string.IsNullOrWhiteSpace(o.SubLangs) ? "en" : o.SubLangs.Trim();
            args.Add("--write-subs");
            args.Add("--write-auto-subs");
            args.Add("--sub-langs");
            args.Add(langs);
        }

        if (!o.ThumbnailOnly && o.SponsorBlock)
        {
            args.Add("--sponsorblock-remove");
            args.Add("default");
        }

        if (o.Segments != null)
        {
            foreach (var seg in o.Segments)
            {
                args.Add("--download-sections");
                args.Add($"*{seg.StartTime}-{seg.EndTime}");
            }
        }

        args.Add(url);
        return args;
    }

    /// <summary>Output template ("-o") for one run. Relative template parts are joined onto the output folder.</summary>
    public static string BuildOutputTemplate(DownloadOptions o, string ext, string customName, string? multiBaseName)
    {
        string folder = (o.OutputFolder ?? string.Empty).TrimEnd('\\', '/');
        if (string.IsNullOrEmpty(folder)) folder = AppPaths.DefaultDownloadsFolder;

        string extPart = o.ThumbnailOnly ? string.Empty
            : "." + (string.Equals(ext, "ext", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(ext) ? "%(ext)s" : ext);

        string name;
        bool hasCustom = multiBaseName != null
            ? multiBaseName.Trim() != "%(title)s"
            : !string.IsNullOrWhiteSpace(customName);

        if (hasCustom)
        {
            name = multiBaseName ?? FileNames.SanitizeTemplateSafe(customName.Trim());
            // A fixed name would make every playlist entry overwrite the last one. The
            // "&...|" form only adds the suffix when there is an index (not for single videos).
            if (o.Playlist && !name.Contains("%(", StringComparison.Ordinal))
                name += "%(playlist_index& - {}|)s";
        }
        else
        {
            name = FileNames.SanitizeTemplatePath(o.FilenameTemplate);
        }

        return Path.Combine(folder, name + extPart);
    }

    public static void AppendCookiesArgs(List<string> args, DownloadOptions o)
    {
        switch (o.CookieSource)
        {
            case CookieSource.None:
                return;
            case CookieSource.File:
                if (!string.IsNullOrWhiteSpace(o.CookiesFile) && File.Exists(o.CookiesFile))
                {
                    args.Add("--cookies");
                    args.Add(o.CookiesFile);
                }
                return;
            default:
                args.Add("--cookies-from-browser");
                args.Add(BrowserSpec(o.CookieSource, o.BrowserProfile));
                return;
        }
    }

    public static string BrowserSpec(CookieSource source, string profile)
    {
        var name = source.ToString().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(profile) ? name : $"{name}:{profile.Trim()}";
    }

    public static void AppendPresetArgs(List<string> args, FormatPreset preset)
    {
        switch (preset)
        {
            case FormatPreset.YouTube4K:
                args.Add("-f"); args.Add("bv*[height<=2160]+ba/b[height<=2160]");
                args.Add("-S"); args.Add("res,vcodec:h264,acodec:aac");
                args.Add("--merge-output-format"); args.Add("mp4");
                break;

            case FormatPreset.MusicLibrary:
                args.Add("--extract-audio");
                args.Add("--audio-format"); args.Add("mp3");
                args.Add("--audio-quality"); args.Add("0");
                args.Add("-f"); args.Add("bestaudio");
                args.Add("--embed-thumbnail");
                args.Add("--embed-metadata");
                break;

            case FormatPreset.Podcast:
                args.Add("--extract-audio");
                args.Add("--audio-format"); args.Add("m4a");
                args.Add("-f"); args.Add("bestaudio");
                args.Add("--embed-chapters");
                args.Add("--embed-metadata");
                break;
        }
    }

    public static void AppendFormatArgs(List<string> args, string fileExtension, int qualityHeight)
    {
        string cap = qualityHeight > 0 ? $"[height<={qualityHeight}]" : string.Empty;

        if (ExtractableAudioFormats.Contains(fileExtension))
        {
            args.Add("--extract-audio");
            args.Add("--audio-format"); args.Add(fileExtension);
            args.Add("-f"); args.Add("bestaudio");
        }
        else if (fileExtension == "webm")
        {
            args.Add("-f"); args.Add("bestaudio");
        }
        else if (VideoFormats.Contains(fileExtension))
        {
            if (fileExtension is "mp4" or "mov")
            {
                if (qualityHeight > 0)
                {
                    args.Add("-f"); args.Add($"bv*{cap}+ba/b{cap}");
                }
                args.Add("-S"); args.Add("vcodec:h264,res,acodec:aac");
                args.Add("--merge-output-format"); args.Add(fileExtension);
            }
            else if (fileExtension == "flv")
            {
                args.Add("-f"); args.Add(qualityHeight > 0
                    ? $"bestvideo{cap}+bestaudio/b{cap}"
                    : "bestvideo+bestaudio");
                args.Add("--recode-video"); args.Add("flv");
            }
            else
            {
                args.Add("-f");
                args.Add($"bv*{cap}[ext={fileExtension}]+ba[ext=m4a]/b{cap}[ext={fileExtension}]");
            }
        }
        else
        {
            args.Add("-f");
            args.Add($"bv*{cap}[ext=mp4]+ba[ext=m4a]/bv*{cap}+ba/b{cap}[ext=mp4]/b{cap}");
        }
    }

    private static void AppendExactFormatArgs(List<string> args, string spec, string ext)
    {
        args.Add("-f");
        args.Add(spec);

        if (ExtractableAudioFormats.Contains(ext))
        {
            args.Add("--extract-audio");
            args.Add("--audio-format"); args.Add(ext);
        }
        else if (MergeContainers.Contains(ext))
        {
            args.Add("--merge-output-format"); args.Add(ext);
        }
        else if (ext == "flv")
        {
            args.Add("--recode-video"); args.Add("flv");
        }
    }
}
