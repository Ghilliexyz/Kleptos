using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Kleptos.Models;
using Kleptos.Services;
using Xunit;

namespace Kleptos.Tests;

/// <summary>Exercises the download/extract/validate paths against a fake HTTP server.</summary>
public class ToolManagerTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(_respond(request));
    }

    /// <summary>A file the current OS would accept as an executable (right magic, big enough).</summary>
    private static byte[] FakeExecutable(int size = 1_200_000)
    {
        var b = new byte[size];
        if (OperatingSystem.IsWindows()) { b[0] = (byte)'M'; b[1] = (byte)'Z'; }
        else if (OperatingSystem.IsMacOS()) { b[0] = 0xCF; b[1] = 0xFA; b[2] = 0xED; b[3] = 0xFE; }
        else { b[0] = 0x7F; b[1] = (byte)'E'; b[2] = (byte)'L'; b[3] = (byte)'F'; }
        return b;
    }

    private static byte[] Zip(params (string Name, byte[] Data)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, data) in files)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(data);
            }
        return ms.ToArray();
    }

    [Fact]
    public async Task Latest_version_reads_tag_for_each_channel()
    {
        string? asked = null;
        var http = new HttpClient(new FakeHandler(r =>
        {
            asked = r.RequestUri!.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"tag_name\":\"2026.10.29.233024\"}") };
        }));
        var tm = new ToolManager(http);
        Assert.Equal("2026.10.29.233024", await tm.GetLatestYtDlpVersionAsync(YtDlpChannel.Nightly));
        Assert.Contains("yt-dlp-nightly-builds", asked);
        await tm.GetLatestYtDlpVersionAsync(YtDlpChannel.Stable);
        Assert.Contains("repos/yt-dlp/yt-dlp/releases", asked);
    }

    [Fact]
    public async Task Offline_returns_null()
    {
        var http = new HttpClient(new FakeHandler(_ => throw new HttpRequestException("offline")));
        Assert.Null(await new ToolManager(http).GetLatestYtDlpVersionAsync(YtDlpChannel.Stable));
    }

    [Fact]
    public async Task Ffmpeg_install_extracts_validates_and_marks_executable()
    {
        // Needs XDG_CONFIG_HOME redirection so it never writes into the real profile; Windows has no equivalent.
        if (ToolManager.FfmpegSources() == null || OperatingSystem.IsWindows()) return;

        var config = Path.Combine(Path.GetTempPath(), $"kleptos_cfg_{Guid.NewGuid():N}");
        var oldXdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
        try
        {
            var ffmpegZip = Zip(($"ffmpeg-build/bin/{PlatformHelper.FfmpegBinaryName}", FakeExecutable()),
                                ("ffmpeg-build/README.txt", new byte[] { 1, 2, 3 }));
            var probeZip = Zip((PlatformHelper.FfprobeBinaryName, FakeExecutable()));
            var http = new HttpClient(new FakeHandler(r =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(r.RequestUri!.ToString().Contains("ffprobe") ? probeZip : ffmpegZip),
                }));

            double lastProgress = 0;
            var path = await new ToolManager(http).InstallFfmpegAsync(new Progress<double>(p => lastProgress = p));

            Assert.True(File.Exists(path));
            Assert.Equal(ToolManager.ManagedFfmpegPath, path);
            Assert.True(Path.IsPathRooted(path));
            Assert.StartsWith(config, path);
            Assert.True(File.GetUnixFileMode(path).HasFlag(UnixFileMode.UserExecute));
            Assert.True(File.Exists(Path.Combine(AppPaths.ToolsDir, PlatformHelper.FfprobeBinaryName)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", oldXdg);
            try { Directory.Delete(config, true); } catch { }
        }
    }

    [Fact]
    public async Task Ffmpeg_install_rejects_non_executables()
    {
        if (ToolManager.FfmpegSources() == null || OperatingSystem.IsWindows()) return;
        var config = Path.Combine(Path.GetTempPath(), $"kleptos_cfg_{Guid.NewGuid():N}");
        var oldXdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
        try
        {
            var bad = Zip((PlatformHelper.FfmpegBinaryName, new byte[2_000_000])); // right name, no magic
            var http = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bad) }));
            await Assert.ThrowsAsync<InvalidOperationException>(() => new ToolManager(http).InstallFfmpegAsync());
            Assert.StartsWith(config, ToolManager.ManagedFfmpegPath);
            Assert.False(File.Exists(ToolManager.ManagedFfmpegPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", oldXdg);
            try { Directory.Delete(config, true); } catch { }
        }
    }

    [Fact]
    public async Task YtDlp_download_rejects_truncated_binary()
    {
        var http = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[100]) }));
        var before = File.Exists(ToolManager.YtDlpPath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ToolManager(http).DownloadYtDlpAsync(YtDlpChannel.Stable, "2026.10.01"));
        Assert.Equal(before, File.Exists(ToolManager.YtDlpPath));
    }

    [Fact]
    public async Task YtDlp_download_installs_valid_binary()
    {
        var target = ToolManager.YtDlpPath;
        try
        {
            var http = new HttpClient(new FakeHandler(r =>
            {
                Assert.EndsWith("/2026.10.01/" + PlatformHelper.YtDlpAssetName, r.RequestUri!.ToString());
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(FakeExecutable()) };
            }));
            await new ToolManager(http).DownloadYtDlpAsync(YtDlpChannel.Stable, "2026.10.01");
            Assert.True(PlatformHelper.IsValidYtDlpBinary(target));
        }
        finally
        {
            try { File.Delete(target); } catch { }
        }
    }
}
