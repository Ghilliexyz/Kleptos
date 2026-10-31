using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Kleptos.Services;

public enum FormatChoiceKind { Auto, Video, Audio }

/// <summary>An entry in the preview card's format picker.</summary>
public sealed class FormatChoice
{
    public FormatChoiceKind Kind { get; init; }
    public string Label { get; init; } = string.Empty;
    /// <summary>yt-dlp -f spec, null for Auto.</summary>
    public string? Spec { get; init; }
    public long? EstimatedBytes { get; init; }
    public int Height { get; init; }

    public override string ToString() => Label;
}

/// <summary>What the preview card shows, parsed from `yt-dlp -J`.</summary>
public sealed class MediaInfo
{
    public string Url { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Uploader { get; init; } = string.Empty;
    public double DurationSeconds { get; init; }
    public string ThumbnailUrl { get; init; } = string.Empty;
    public bool IsPlaylist { get; init; }
    public int EntryCount { get; init; }
    public long? EstimatedBytes { get; init; }
    public string Extractor { get; init; } = string.Empty;
    public List<FormatChoice> Formats { get; init; } = new();

    private sealed class RawFormat
    {
        public string Id = "";
        public string Ext = "";
        public string VCodec = "none";
        public string ACodec = "none";
        public int Height;
        public double Fps;
        public double Tbr;
        public double Abr;
        public long? Size;
        public string Note = "";
        public bool IsVideo => VCodec != "none" && Height > 0;
        public bool IsAudioOnly => ACodec != "none" && VCodec == "none";
    }

    public static MediaInfo? Parse(string json, string url)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        // -J prints exactly one JSON document, but stray warnings can precede it.
        int start = json.IndexOf('{');
        if (start < 0) return null;
        json = json.Substring(start);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string type = Str(root, "_type");
            bool isPlaylist = type == "playlist";
            double duration = Num(root, "duration") ?? 0;

            int entryCount = 0;
            if (isPlaylist)
            {
                entryCount = (int)(Num(root, "playlist_count") ?? 0);
                if (entryCount == 0 && root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
                    entryCount = entries.GetArrayLength();
            }

            var raw = new List<RawFormat>();
            if (root.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in formats.EnumerateArray())
                {
                    var rf = new RawFormat
                    {
                        Id = Str(f, "format_id"),
                        Ext = Str(f, "ext"),
                        VCodec = Str(f, "vcodec", "none"),
                        ACodec = Str(f, "acodec", "none"),
                        Height = (int)(Num(f, "height") ?? 0),
                        Fps = Num(f, "fps") ?? 0,
                        Tbr = Num(f, "tbr") ?? 0,
                        Abr = Num(f, "abr") ?? 0,
                        Size = (long?)(Num(f, "filesize") ?? Num(f, "filesize_approx")),
                        Note = Str(f, "format_note"),
                    };
                    if (string.IsNullOrEmpty(rf.Id) || rf.Ext == "mhtml" || rf.Note.Contains("storyboard", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (rf.Size is null && rf.Tbr > 0 && duration > 0)
                        rf.Size = (long)(rf.Tbr * 1000 / 8 * duration);
                    raw.Add(rf);
                }
            }

            long? estimate = (long?)(Num(root, "filesize") ?? Num(root, "filesize_approx"));
            if (estimate is null && root.TryGetProperty("requested_formats", out var req) && req.ValueKind == JsonValueKind.Array)
            {
                long sum = 0;
                foreach (var f in req.EnumerateArray())
                    sum += (long)(Num(f, "filesize") ?? Num(f, "filesize_approx") ?? 0);
                if (sum > 0) estimate = sum;
            }

            return new MediaInfo
            {
                Url = url,
                Title = Str(root, "title"),
                Uploader = FirstNonEmpty(Str(root, "uploader"), Str(root, "channel"), Str(root, "playlist_uploader")),
                DurationSeconds = duration,
                ThumbnailUrl = PickThumbnail(root),
                IsPlaylist = isPlaylist,
                EntryCount = entryCount,
                EstimatedBytes = estimate,
                Extractor = Str(root, "extractor_key"),
                Formats = BuildChoices(raw),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<FormatChoice> BuildChoices(List<RawFormat> raw)
    {
        var choices = new List<FormatChoice>
        {
            new() { Kind = FormatChoiceKind.Auto, Label = "Auto (use Format / Quality)" },
        };

        var bestAudio = raw.Where(r => r.IsAudioOnly).OrderByDescending(r => r.Abr > 0 ? r.Abr : r.Tbr).FirstOrDefault();

        var videos = raw.Where(r => r.IsVideo)
            .GroupBy(r => (r.Height, Fps: (int)Math.Round(r.Fps), Codec: CodecFamily(r.VCodec)))
            .Select(g => g.OrderByDescending(r => r.Tbr).First())
            .OrderByDescending(r => r.Height)
            .ThenByDescending(r => r.Fps)
            .ThenByDescending(r => r.Tbr)
            .Take(16);

        foreach (var v in videos)
        {
            bool needsAudio = v.ACodec == "none";
            long? size = v.Size;
            if (needsAudio && size is long vs && bestAudio?.Size is long asz) size = vs + asz;

            string fps = v.Fps > 30.5 ? ((int)Math.Round(v.Fps)).ToString(CultureInfo.InvariantCulture) : string.Empty;
            var label = $"{v.Height}p{fps}  {CodecFamily(v.VCodec)}  {v.Ext}";
            if (size is long s) label += $"  ~{Formatting.SizeFromBytes(s)}";

            choices.Add(new FormatChoice
            {
                Kind = FormatChoiceKind.Video,
                Label = label,
                Spec = needsAudio ? $"{v.Id}+bestaudio/{v.Id}" : v.Id,
                EstimatedBytes = size,
                Height = v.Height,
            });
        }

        var audios = raw.Where(r => r.IsAudioOnly)
            .GroupBy(r => (Codec: CodecFamily(r.ACodec), Abr: (int)Math.Round((r.Abr > 0 ? r.Abr : r.Tbr) / 16) * 16))
            .Select(g => g.OrderByDescending(r => r.Abr).First())
            .OrderByDescending(r => r.Abr > 0 ? r.Abr : r.Tbr)
            .Take(5);

        foreach (var a in audios)
        {
            double kbps = a.Abr > 0 ? a.Abr : a.Tbr;
            var label = $"audio  {CodecFamily(a.ACodec)}  {(kbps > 0 ? $"{kbps:F0}k" : a.Ext)}";
            if (a.Size is long s) label += $"  ~{Formatting.SizeFromBytes(s)}";
            choices.Add(new FormatChoice
            {
                Kind = FormatChoiceKind.Audio,
                Label = label,
                Spec = a.Id,
                EstimatedBytes = a.Size,
            });
        }

        return choices;
    }

    public static string CodecFamily(string codec)
    {
        if (string.IsNullOrEmpty(codec) || codec == "none") return "-";
        var c = codec.ToLowerInvariant();
        if (c.StartsWith("avc") || c.StartsWith("h264")) return "H.264";
        if (c.StartsWith("hev") || c.StartsWith("hvc") || c.StartsWith("h265")) return "HEVC";
        if (c.StartsWith("vp09") || c.StartsWith("vp9")) return "VP9";
        if (c.StartsWith("vp8")) return "VP8";
        if (c.StartsWith("av01") || c.StartsWith("av1")) return "AV1";
        if (c.StartsWith("mp4a") || c == "aac") return "AAC";
        if (c.StartsWith("opus")) return "Opus";
        if (c.StartsWith("vorbis")) return "Vorbis";
        if (c.StartsWith("mp3")) return "MP3";
        if (c.StartsWith("flac")) return "FLAC";
        var dot = c.IndexOf('.');
        return (dot > 0 ? c[..dot] : c).ToUpperInvariant();
    }

    private static string PickThumbnail(JsonElement root)
    {
        var direct = Str(root, "thumbnail");
        if (!string.IsNullOrEmpty(direct)) return direct;

        // Playlists: thumbnails array, prefer the last (largest) jpg/png.
        if (root.TryGetProperty("thumbnails", out var thumbs) && thumbs.ValueKind == JsonValueKind.Array)
        {
            string best = string.Empty;
            foreach (var t in thumbs.EnumerateArray())
            {
                var u = Str(t, "url");
                if (!string.IsNullOrEmpty(u) && !u.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) best = u;
            }
            return best;
        }
        return string.Empty;
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;

    private static string Str(JsonElement e, string name, string fallback = "")
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v))
        {
            if (v.ValueKind == JsonValueKind.String) return v.GetString() ?? fallback;
            if (v.ValueKind == JsonValueKind.Number) return v.GetRawText();
        }
        return fallback;
    }

    private static double? Num(JsonElement e, string name)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d))
            return d;
        return null;
    }
}
