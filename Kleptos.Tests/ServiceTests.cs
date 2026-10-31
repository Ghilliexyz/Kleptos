using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Kleptos.Models;
using Kleptos.Services;
using Xunit;

namespace Kleptos.Tests;

public class FileNameTests
{
    [Theory]
    [InlineData("a:b*c?", "a_b_c_")]
    [InlineData("  spaced   out  ", "spaced out")]
    [InlineData("..\\..\\evil", "____evil")]
    [InlineData("trailing. ", "trailing")]
    [InlineData("", "download")]
    public void Sanitize(string input, string expected) => Assert.Equal(expected, FileNames.Sanitize(input));

    [Fact]
    public void Template_fields_survive_sanitising()
    {
        Assert.Equal("%(title)s - %(upload_date>%Y-%m-%d)s", FileNames.SanitizeTemplateSafe("%(title)s - %(upload_date>%Y-%m-%d)s"));
        Assert.Equal("%(playlist_index)03d_ x", FileNames.SanitizeTemplateSafe("%(playlist_index)03d: x"));
    }

    [Theory]
    [InlineData("https://x.com/v", "https://x.com/v", "%(title)s")]
    [InlineData("My Song https://x.com/v", "https://x.com/v", "My Song")]
    [InlineData("3 My Song https://x.com/v", "https://x.com/v", "3 - My Song")]
    [InlineData("https://x.com/v   After Name", "https://x.com/v", "After Name")]
    public void Multi_line_parsing(string line, string url, string baseName)
    {
        Assert.True(FileNames.TryParseMultiLine(line, out var u, out var b));
        Assert.Equal(url, u);
        Assert.Equal(baseName, b);
    }

    [Theory]
    [InlineData("1-10", true)]
    [InlineData("1,3,5", true)]
    [InlineData("1-10, 15, 20:30", true)]
    [InlineData("-5:", true)]
    [InlineData("::2", false)]
    [InlineData("abc", false)]
    [InlineData("", true)]
    public void Playlist_items_validation(string spec, bool ok) => Assert.Equal(ok, FileNames.IsValidPlaylistItems(spec));

    [Theory]
    [InlineData("500K", true)]
    [InlineData("1.5M", true)]
    [InlineData("2 m", true)]
    [InlineData("fast", false)]
    public void Rate_limit_validation(string rate, bool ok) => Assert.Equal(ok, FileNames.IsValidRateLimit(rate));

    [Theory]
    [InlineData("https://www.youtube.com/playlist?list=PL1", true)]
    [InlineData("https://www.youtube.com/watch?v=a&list=PL1", true)]
    [InlineData("https://www.youtube.com/@chan/videos", true)]
    [InlineData("https://www.youtube.com/watch?v=a", false)]
    public void Playlist_detection(string url, bool expected) => Assert.Equal(expected, FileNames.LooksLikePlaylist(url));
}

public class FormattingTests
{
    [Theory]
    [InlineData("1:02:03", 3723)]
    [InlineData("4:05", 245)]
    [InlineData("12.5", 12.5)]
    [InlineData("x", -1)]
    [InlineData("1:-2", -1)]
    public void ParseClock(string s, double expected) => Assert.Equal(expected, Formatting.ParseClock(s));

    [Fact]
    public void Sizes()
    {
        Assert.Equal("512 KB", Formatting.Size(0.5));
        Assert.Equal("12.3 MB", Formatting.Size(12.34));
        Assert.Equal("2.00 GB", Formatting.Size(2048));
    }
}

public class TemplateTests
{
    [Fact]
    public void Preview_renders_fields_dates_and_padding()
    {
        Assert.Equal("Some Channel/2026-10-30 - Never Gonna Build It Myself.mp4",
            FilenameTemplates.Preview("%(uploader)s/%(upload_date>%Y-%m-%d)s - %(title)s"));
        Assert.Equal("Favourites/007 - Never Gonna Build It Myself.mp4",
            FilenameTemplates.Preview("%(playlist_title,uploader)s/%(playlist_index)03d - %(title)s"));
    }

    [Fact]
    public void Every_preset_previews_without_NA()
    {
        foreach (var p in FilenameTemplates.Presets)
            Assert.DoesNotContain("NA", FilenameTemplates.Preview(p.Template));
    }
}

public class MediaInfoTests
{
    private const string VideoJson = """
    WARNING: something noisy first
    {"_type":"video","title":"Clip","uploader":"Chan","duration":100,"thumbnail":"https://i/t.jpg","extractor_key":"Youtube",
     "filesize_approx":5000000,
     "formats":[
       {"format_id":"sb0","ext":"mhtml","vcodec":"none","acodec":"none","format_note":"storyboard"},
       {"format_id":"140","ext":"m4a","vcodec":"none","acodec":"mp4a.40.2","abr":129.5,"filesize":1600000},
       {"format_id":"251","ext":"webm","vcodec":"none","acodec":"opus","abr":160,"filesize":1800000},
       {"format_id":"137","ext":"mp4","vcodec":"avc1.640028","acodec":"none","height":1080,"fps":30,"tbr":4000,"filesize":50000000},
       {"format_id":"248","ext":"webm","vcodec":"vp9","acodec":"none","height":1080,"fps":30,"tbr":3000},
       {"format_id":"299","ext":"mp4","vcodec":"avc1.64002a","acodec":"none","height":1080,"fps":60,"tbr":6000,"filesize":80000000},
       {"format_id":"18","ext":"mp4","vcodec":"avc1.42001E","acodec":"mp4a.40.2","height":360,"fps":30,"tbr":500,"filesize":6000000}
     ]}
    """;

    [Fact]
    public void Parses_metadata_and_builds_choices()
    {
        var info = MediaInfo.Parse(VideoJson, "https://u");
        Assert.NotNull(info);
        Assert.Equal("Clip", info!.Title);
        Assert.Equal("Chan", info.Uploader);
        Assert.Equal(100, info.DurationSeconds);
        Assert.Equal(5000000, info.EstimatedBytes);
        Assert.False(info.IsPlaylist);

        Assert.Equal(FormatChoiceKind.Auto, info.Formats[0].Kind);
        var videos = info.Formats.Where(f => f.Kind == FormatChoiceKind.Video).ToList();
        Assert.Equal(4, videos.Count);
        Assert.StartsWith("1080p60", videos[0].Label);
        Assert.Equal("299+bestaudio/299", videos[0].Spec);
        Assert.Equal("18", videos.Last().Spec); // progressive: no audio merge needed
        // 248 had no size: estimated from tbr * duration
        Assert.Contains(videos, v => v.Spec!.StartsWith("248") && v.EstimatedBytes > 0);

        var audios = info.Formats.Where(f => f.Kind == FormatChoiceKind.Audio).ToList();
        Assert.Equal("251", audios[0].Spec);
        Assert.DoesNotContain(info.Formats, f => f.Spec == "sb0");
    }

    [Fact]
    public void Parses_playlist()
    {
        var info = MediaInfo.Parse("""{"_type":"playlist","title":"PL","playlist_count":42,"thumbnails":[{"url":"a.webp"},{"url":"b.jpg"}]}""", "u");
        Assert.True(info!.IsPlaylist);
        Assert.Equal(42, info.EntryCount);
        Assert.Equal("b.jpg", info.ThumbnailUrl);
    }

    [Fact]
    public void Garbage_returns_null() => Assert.Null(MediaInfo.Parse("not json", "u"));
}

public class StoreTests
{
    [Fact]
    public void Old_field_based_history_entries_are_dropped_and_new_ones_roundtrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kh_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "[{},{}]");
            Assert.Empty(Stores.LoadHistory(path));

            var list = new List<HistoryEntry> { new() { Url = "https://a", Title = "A", When = DateTime.Today, Succeeded = true, FileSizeMB = 12 } };
            Stores.SaveHistory(list, path);
            var back = Stores.LoadHistory(path);
            Assert.Single(back);
            Assert.Equal(12, back[0].FileSizeMB);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Settings_migrate_cookie_file_and_clamp()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ks_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{"LastCookiesFile":"C:\\c.txt","MaxParallelDownloads":99,"OutputFolder":"D:\\x"}""");
            var s = Stores.LoadSettings(path);
            Assert.Equal(CookieSource.File, s.CookieSource);
            Assert.Equal(6, s.MaxParallelDownloads);
            Assert.Equal("D:\\x", s.OutputFolder);

            Stores.SaveSettings(s, path);
            Assert.Contains("\"File\"", File.ReadAllText(path));
            Assert.Equal(CookieSource.File, Stores.LoadSettings(path).CookieSource);
        }
        finally { File.Delete(path); }
    }
}

public class StatsTests
{
    [Fact]
    public void Lifetime_stats()
    {
        var today = new DateTime(2026, 10, 30);
        var h = new List<HistoryEntry>
        {
            new() { Url = "https://www.youtube.com/watch?v=1", When = today, Succeeded = true, FileSizeMB = 100 },
            new() { Url = "https://youtu.be/2", When = today.AddDays(-1), Succeeded = true, FileSizeMB = 50 },
            new() { Url = "https://vimeo.com/3", When = today.AddDays(-10), Succeeded = false, FileSizeMB = 0 },
            new() { Url = "https://vimeo.com/4", When = today.AddDays(-40), Succeeded = true, FileSizeMB = 10 },
        };
        var s = LifetimeStats.From(h, today);
        Assert.Equal(4, s.Total);
        Assert.Equal(75, s.SuccessRate);
        Assert.Equal(160, s.TotalSizeMB);
        Assert.Equal(2, s.Last7Days);
        Assert.Equal(1, s.Daily[13]);
        Assert.Equal(1, s.Daily[12]);
        Assert.Equal(1, s.Daily[3]);
        Assert.Equal("youtube.com", s.TopSite);
    }

    [Fact]
    public void Queue_entry_overall_percent_is_playlist_aware()
    {
        var e = new QueueEntry { Status = QueueStatus.Downloading, PlaylistIndex = 3, PlaylistCount = 4, Percent = 50 };
        Assert.Equal(62.5, e.OverallPercent);
        e.Status = QueueStatus.Succeeded;
        Assert.Equal(100, e.OverallPercent);
    }

    [Fact]
    public void Cookie_inspector_counts()
    {
        var path = Path.GetTempFileName();
        try
        {
            var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
            File.WriteAllLines(path, new[]
            {
                "# Netscape HTTP Cookie File",
                ".youtube.com\tTRUE\t/\tTRUE\t0\tSESSION\tx",
                ".youtube.com\tTRUE\t/\tTRUE\t1700000000\tOLD\tx",
                "#HttpOnly_.youtube.com\tTRUE\t/\tTRUE\t1800100000\tSOON\tx",
                ".youtube.com\tTRUE\t/\tTRUE\t1900000000\tLATER\tx",
            });
            var r = CookiesInspector.Inspect(path, now);
            Assert.Equal(4, r.Total);
            Assert.Equal(1, r.Session);
            Assert.Equal(1, r.Expired);
            Assert.Equal(1, r.ExpiringSoon);
        }
        finally { File.Delete(path); }
    }
}
