using Kleptos.Services;
using Xunit;

namespace Kleptos.Tests;

public class ParserTests
{
    [Fact]
    public void Structured_progress_line()
    {
        var e = YtDlpOutputParser.Parse("[KLEPTOS] downloading|5242880|10485760|NA|1048576.5|5|3|12|C:\\out\\a|b.mp4");
        Assert.Equal(OutputEventKind.Progress, e.Kind);
        Assert.True(e.IsStructured);
        Assert.Equal(50, e.Percent, 3);
        Assert.Equal(10485760, e.TotalBytes);
        Assert.Equal(1.0, e.SpeedMBps, 3);
        Assert.Equal(5, e.EtaSeconds);
        Assert.Equal(3, e.PlaylistIndex);
        Assert.Equal(12, e.PlaylistCount);
        Assert.Equal("C:\\out\\a|b.mp4", e.Path);
    }

    [Fact]
    public void Structured_progress_uses_estimate_and_handles_NA()
    {
        var e = YtDlpOutputParser.Parse("[KLEPTOS] downloading|1000|NA|4000.0|NA|NA|NA|NA|NA");
        Assert.Equal(25, e.Percent, 3);
        Assert.Null(e.SpeedBytesPerSec);
        Assert.Null(e.EtaSeconds);
        Assert.Equal(0, e.PlaylistIndex);
        Assert.Equal(string.Empty, e.Path);
    }

    [Fact]
    public void Structured_finished_is_100()
    {
        var e = YtDlpOutputParser.Parse("[KLEPTOS] finished|2048|2048|NA|NA|NA|NA|NA|x.webm");
        Assert.Equal("finished", e.Status);
        Assert.Equal(100, e.Percent);
        Assert.NotNull(YtDlpOutputParser.ConsoleLine(e));
    }

    [Theory]
    [InlineData("[download] Destination: C:\\Videos\\clip.f137.mp4", OutputEventKind.Destination)]
    [InlineData("[Merger] Merging formats into \"C:\\Videos\\clip.mp4\"", OutputEventKind.Merging)]
    [InlineData("[download] Downloading item 4 of 20", OutputEventKind.PlaylistItem)]
    [InlineData("[download] C:\\Videos\\clip.mp4 has already been downloaded", OutputEventKind.AlreadyDownloaded)]
    [InlineData("[download] abc123: has already been recorded in the archive", OutputEventKind.InArchive)]
    [InlineData("[info] Writing video thumbnail 2 to: C:\\Videos\\clip.webp", OutputEventKind.ThumbnailWritten)]
    [InlineData("ERROR: [youtube] abc: Video unavailable", OutputEventKind.Error)]
    [InlineData("[youtube] abc: Downloading webpage", OutputEventKind.None)]
    public void Line_kinds(string line, OutputEventKind kind) =>
        Assert.Equal(kind, YtDlpOutputParser.Parse(line).Kind);

    [Fact]
    public void Playlist_item_numbers()
    {
        var e = YtDlpOutputParser.Parse("[download] Downloading item 4 of 20");
        Assert.Equal(4, e.PlaylistIndex);
        Assert.Equal(20, e.PlaylistCount);
    }

    [Fact]
    public void Archive_skip_captures_id() =>
        Assert.Equal("abc123", YtDlpOutputParser.Parse("[download] abc123: has already been recorded in the archive").Message);

    [Fact]
    public void Auth_error_detected()
    {
        var e = YtDlpOutputParser.Parse("ERROR: [youtube] abc: Sign in to confirm you're not a bot. Use --cookies-from-browser");
        Assert.True(e.AuthRequired);
    }

    [Fact]
    public void Legacy_progress_fallback()
    {
        var e = YtDlpOutputParser.Parse("[download]  42.5% of ~ 10.00MiB at  2.00MiB/s ETA 00:03");
        Assert.Equal(OutputEventKind.Progress, e.Kind);
        Assert.False(e.IsStructured);
        Assert.Equal(42.5, e.Percent, 3);
        Assert.Equal(10, e.SizeMB, 2);
        Assert.Equal(2, e.SpeedMBps, 2);
        Assert.Equal(3, e.EtaSeconds);
    }

    [Theory]
    [InlineData("ERROR: Could not copy Chrome cookie database. See  https://github.com/yt-dlp/yt-dlp/issues/7271", true)]
    [InlineData("ERROR: Failed to decrypt with DPAPI", true)]
    [InlineData("ERROR: Video unavailable", false)]
    public void Cookie_database_errors(string line, bool expected) =>
        Assert.Equal(expected, YtDlpOutputParser.IsCookieDatabaseError(line));
}
