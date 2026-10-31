using System.IO;
using Kleptos.Models;
using Kleptos.Services;
using Xunit;

namespace Kleptos.Tests;

public class ArgsBuilderTests
{
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "kleptos-out");
    private const string Url = "https://www.youtube.com/watch?v=abc";

    private static string ValueAfter(System.Collections.Generic.List<string> args, string flag)
    {
        int i = args.IndexOf(flag);
        Assert.True(i >= 0, $"{flag} missing from: {string.Join(' ', args)}");
        return args[i + 1];
    }

    [Fact]
    public void Url_is_last_and_defaults_are_sane()
    {
        var args = YtDlpArgsBuilder.Build(new DownloadOptions { OutputFolder = Folder }, Url);
        Assert.Equal(Url, args[^1]);
        Assert.Contains("--no-playlist", args);
        Assert.Contains("--no-mtime", args);
        Assert.Contains("--newline", args);
        Assert.StartsWith("download:" + YtDlpOutputParser.Marker, ValueAfter(args, "--progress-template"));
        Assert.Equal(Path.Combine(Folder, "%(title)s.%(ext)s"), ValueAfter(args, "-o"));
        Assert.DoesNotContain("--cookies", args);
        Assert.DoesNotContain("--cookies-from-browser", args);
        Assert.DoesNotContain("--download-archive", args);
    }

    [Theory]
    [InlineData(CookieSource.Chrome, "", "chrome")]
    [InlineData(CookieSource.Firefox, "default-release", "firefox:default-release")]
    [InlineData(CookieSource.Edge, "  Profile 1 ", "edge:Profile 1")]
    public void Browser_cookies(CookieSource src, string profile, string expected)
    {
        var args = YtDlpArgsBuilder.Build(new DownloadOptions { OutputFolder = Folder, CookieSource = src, BrowserProfile = profile }, Url);
        Assert.Equal(expected, ValueAfter(args, "--cookies-from-browser"));
    }

    [Fact]
    public void Cookie_file_only_when_it_exists()
    {
        var missing = new DownloadOptions { OutputFolder = Folder, CookieSource = CookieSource.File, CookiesFile = "/nope/cookies.txt" };
        Assert.DoesNotContain("--cookies", YtDlpArgsBuilder.Build(missing, Url));

        var tmp = Path.GetTempFileName();
        try
        {
            var present = new DownloadOptions { OutputFolder = Folder, CookieSource = CookieSource.File, CookiesFile = tmp };
            Assert.Equal(tmp, ValueAfter(YtDlpArgsBuilder.Build(present, Url), "--cookies"));
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Archive_playlist_items_and_network()
    {
        var o = new DownloadOptions
        {
            OutputFolder = Folder,
            Playlist = true,
            PlaylistItems = "1-10, 15",
            UseArchive = true,
            ArchivePath = "/tmp/archive.txt",
            RateLimit = "2m",
            ConcurrentFragments = 4,
            Retries = 3,
            Proxy = "socks5://127.0.0.1:1080",
        };
        var args = YtDlpArgsBuilder.Build(o, Url);
        Assert.Contains("--yes-playlist", args);
        Assert.Equal("1-10,15", ValueAfter(args, "--playlist-items"));
        Assert.Equal("/tmp/archive.txt", ValueAfter(args, "--download-archive"));
        Assert.Equal("2M", ValueAfter(args, "--limit-rate"));
        Assert.Equal("4", ValueAfter(args, "--concurrent-fragments"));
        Assert.Equal("3", ValueAfter(args, "--retries"));
        Assert.Equal("socks5://127.0.0.1:1080", ValueAfter(args, "--proxy"));
    }

    [Fact]
    public void Invalid_rate_limit_is_ignored()
    {
        var args = YtDlpArgsBuilder.Build(new DownloadOptions { OutputFolder = Folder, RateLimit = "fast please" }, Url);
        Assert.DoesNotContain("--limit-rate", args);
    }

    [Fact]
    public void Archive_skipped_for_thumbnail_only()
    {
        var o = new DownloadOptions { OutputFolder = Folder, ThumbnailOnly = true, UseArchive = true, ArchivePath = "/tmp/a.txt" };
        var args = YtDlpArgsBuilder.Build(o, Url);
        Assert.DoesNotContain("--download-archive", args);
        Assert.Contains("--skip-download", args);
        Assert.Equal(Path.Combine(Folder, "%(title)s"), ValueAfter(args, "-o"));
    }

    [Fact]
    public void Filename_template_with_subfolder()
    {
        var o = new DownloadOptions { OutputFolder = Folder, FilenameTemplate = "%(uploader)s/%(upload_date>%Y-%m-%d)s - %(title)s", FileExtension = "mp4" };
        var output = ValueAfter(YtDlpArgsBuilder.Build(o, Url), "-o");
        Assert.Equal(Path.Combine(Folder, "%(uploader)s/%(upload_date>%Y-%m-%d)s - %(title)s.mp4"), output);
    }

    [Fact]
    public void Template_traversal_is_stripped()
    {
        var o = new DownloadOptions { OutputFolder = Folder, FilenameTemplate = "../../etc/%(title)s" };
        var output = ValueAfter(YtDlpArgsBuilder.Build(o, Url), "-o");
        Assert.DoesNotContain("..", output.Replace(Folder, ""));
        Assert.EndsWith("etc/%(title)s.%(ext)s", output);
    }

    [Fact]
    public void Custom_name_overrides_template_and_gets_index_in_playlist_mode()
    {
        var o = new DownloadOptions { OutputFolder = Folder, FilenameTemplate = "%(uploader)s/%(title)s", Playlist = true, FileExtension = "mp3" };
        var output = ValueAfter(YtDlpArgsBuilder.Build(o, Url, customName: "My: Mix"), "-o");
        Assert.Equal(Path.Combine(Folder, "My_ Mix%(playlist_index& - {}|)s.mp3"), output);
    }

    [Fact]
    public void Multi_line_name_used_and_bare_title_falls_back_to_template()
    {
        var o = new DownloadOptions { OutputFolder = Folder, FilenameTemplate = "%(uploader)s - %(title)s" };
        Assert.Equal(Path.Combine(Folder, "3 - Song.%(ext)s"), ValueAfter(YtDlpArgsBuilder.Build(o, Url, multiBaseName: "3 - Song"), "-o"));
        Assert.Equal(Path.Combine(Folder, "%(uploader)s - %(title)s.%(ext)s"), ValueAfter(YtDlpArgsBuilder.Build(o, Url, multiBaseName: "%(title)s"), "-o"));
    }

    [Fact]
    public void Exact_format_overrides_quality_and_sets_container()
    {
        var o = new DownloadOptions { OutputFolder = Folder, ExactFormat = "137+bestaudio/137", FileExtension = "mkv", QualityHeight = 480, Preset = FormatPreset.YouTube4K };
        var args = YtDlpArgsBuilder.Build(o, Url);
        Assert.Equal("137+bestaudio/137", ValueAfter(args, "-f"));
        Assert.Equal("mkv", ValueAfter(args, "--merge-output-format"));
        Assert.DoesNotContain("-S", args);
        Assert.Single(args, a => a == "-f");
    }

    [Fact]
    public void Exact_audio_format_with_mp3_extracts()
    {
        var o = new DownloadOptions { OutputFolder = Folder, ExactFormat = "251", FileExtension = "mp3" };
        var args = YtDlpArgsBuilder.Build(o, Url);
        Assert.Contains("--extract-audio", args);
        Assert.Equal("mp3", ValueAfter(args, "--audio-format"));
    }

    [Fact]
    public void Preset_locks_extension()
    {
        var o = new DownloadOptions { OutputFolder = Folder, Preset = FormatPreset.MusicLibrary, FileExtension = "mp4" };
        var args = YtDlpArgsBuilder.Build(o, Url);
        Assert.EndsWith(".mp3", ValueAfter(args, "-o"));
        Assert.Contains("--embed-thumbnail", args);
    }

    [Fact]
    public void Quality_cap_applied_for_mp4()
    {
        var o = new DownloadOptions { OutputFolder = Folder, FileExtension = "mp4", QualityHeight = 720 };
        var args = YtDlpArgsBuilder.Build(o, Url);
        Assert.Equal("bv*[height<=720]+ba/b[height<=720]", ValueAfter(args, "-f"));
        Assert.Equal("mp4", ValueAfter(args, "--merge-output-format"));
    }

    [Fact]
    public void Segments_and_ffmpeg_location()
    {
        var o = new DownloadOptions
        {
            OutputFolder = Folder,
            FfmpegLocation = "/home/me/.config/Kleptos/tools",
            Segments = new() { new SegmentInfo { StartTime = "0:10", EndTime = "0:20" } },
        };
        var args = YtDlpArgsBuilder.Build(o, Url);
        Assert.Equal("*0:10-0:20", ValueAfter(args, "--download-sections"));
        Assert.Equal("/home/me/.config/Kleptos/tools", ValueAfter(args, "--ffmpeg-location"));
    }
}
