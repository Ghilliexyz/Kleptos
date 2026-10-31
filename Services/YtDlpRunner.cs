using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Kleptos.Services;

/// <summary>Starts yt-dlp (no shell, ArgumentList) and streams or captures its output.</summary>
public sealed class YtDlpRunner
{
    public string ExecutablePath { get; set; }

    public YtDlpRunner(string executablePath) => ExecutablePath = executablePath;

    public bool Exists => File.Exists(ExecutablePath);

    private ProcessStartInfo CreateStartInfo(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Make yt-dlp's own output UTF-8 regardless of the console code page.
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>
    /// Runs yt-dlp and calls <paramref name="onLine"/> for every stdout/stderr line, on a
    /// background thread. Cancelling kills the whole process tree. Returns the exit code
    /// (-1 when cancelled).
    /// </summary>
    public async Task<int> RunAsync(IEnumerable<string> args, Action<string> onLine, CancellationToken ct)
    {
        using var process = new Process { StartInfo = CreateStartInfo(args) };

        process.OutputDataReceived += (_, a) => { if (a.Data != null) onLine(a.Data); };
        process.ErrorDataReceived += (_, a) => { if (a.Data != null) onLine(a.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var reg = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        });

        // Waits for exit AND for the redirected streams to drain.
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        return ct.IsCancellationRequested ? -1 : process.ExitCode;
    }

    /// <summary>Runs yt-dlp and returns stdout. Stderr is drained and returned separately.</summary>
    public async Task<(string Stdout, string Stderr, int ExitCode)> CaptureAsync(
        IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
    {
        if (!Exists) return (string.Empty, "yt-dlp not found", -1);

        using var process = new Process { StartInfo = CreateStartInfo(args) };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, a) => { if (a.Data != null) lock (stdout) stdout.AppendLine(a.Data); };
        process.ErrorDataReceived += (_, a) => { if (a.Data != null) lock (stderr) stderr.AppendLine(a.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            ct.ThrowIfCancellationRequested();
            return (stdout.ToString(), "Timed out", -1);
        }

        return (stdout.ToString(), stderr.ToString(), process.ExitCode);
    }

    public async Task<string?> GetVersionAsync()
    {
        if (!Exists) return null;
        try
        {
            var (stdout, _, code) = await CaptureAsync(new[] { "--version" }, TimeSpan.FromSeconds(30), CancellationToken.None);
            return code == 0 ? stdout.Trim() : null;
        }
        catch { return null; }
    }
}
