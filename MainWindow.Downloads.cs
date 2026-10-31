using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Kleptos.Models;
using Kleptos.Services;

namespace Kleptos
{
    /// <summary>Per-run state for one queue entry while yt-dlp is running.</summary>
    internal sealed class DownloadJob
    {
        public DownloadJob(QueueEntry entry, DownloadItemStats first)
        {
            Entry = entry;
            Current = first;
            Items.Add(first);
        }

        public QueueEntry Entry { get; }
        public DownloadItemStats Current { get; set; }
        public List<DownloadItemStats> Items { get; } = new();

        // Live bytes of the stream currently downloading (finished streams are already in Current.FileSizeMB).
        public double LiveMB;
        public double SpeedSum;
        public int SpeedCount;

        public bool PlaylistCounted;
        public bool SawFinished;
        public bool SawAlready;
        public bool AuthRequired;
        public bool CookieDbError;
        public string LastError = string.Empty;
    }

    public partial class MainWindow
    {
        private bool _isDownloading;
        private CancellationTokenSource? _runCts;
        private SessionStats? _currentSession;
        private readonly List<QueueEntry> _queue = new();
        private readonly List<QueueEntry> _runEntries = new();
        private readonly Queue<QueueEntry> _pending = new();
        private readonly List<DownloadJob> _activeJobs = new();
        private DownloadOptions? _runOptions;
        private string _runCustomName = string.Empty;
        private bool _authPromptShown;   // set when a run hit a sign-in wall; dialog shown after the batch
        private bool _cookieDbPromptShown;

        // ── Entry point ──

        private async void Download_Click(object? sender, RoutedEventArgs e)
        {
            // The button doubles as "Cancel all" while a run is active.
            if (_isDownloading)
            {
                try { _runCts?.Cancel(); } catch { }
                return;
            }

            List<QueueEntry> entries;
            if (cbMultiDownload.IsChecked == true)
            {
                var lines = (txtMultiUrls.Text ?? string.Empty)
                    .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
                    .Select(l => l.Trim())
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .ToList();

                if (lines.Count == 0)
                {
                    ShowStatusMessage("No links found (the multi-download box is empty).");
                    return;
                }

                entries = new List<QueueEntry>();
                foreach (var line in lines)
                {
                    if (FileNames.TryParseMultiLine(line, out var url, out var baseName))
                    {
                        entries.Add(new QueueEntry
                        {
                            Url = url,
                            BaseName = baseName,
                            Title = baseName == "%(title)s" ? url : baseName,
                        });
                    }
                }

                if (entries.Count == 0)
                {
                    ShowStatusMessage("No valid links found in the multi-download box.");
                    return;
                }
            }
            else
            {
                if (!FileNames.TryGetValidUrl(txtURL.Text, out var url, out var error))
                {
                    ShowStatusMessage(error);
                    return;
                }
                var title = _preview != null && _preview.Url == url && !string.IsNullOrEmpty(_preview.Title) ? _preview.Title : url;
                entries = new List<QueueEntry> { new QueueEntry { Url = url, Title = title, SingleNaming = true } };
            }

            if (cbPlaylist.IsChecked == true && !FileNames.IsValidPlaylistItems(txtPlaylistItems.Text))
            {
                ShowStatusMessage("Playlist items should look like 1-10,15 or -5: (or be empty for all).");
                return;
            }

            _queue.AddRange(entries);
            if (entries.Count > 1) rbQueue.IsChecked = true;

            await RunEntriesAsync(entries);
        }

        private void ShowStatusMessage(string text)
        {
            AppendOutput(text + "\n");
            txtCurrentStatus.Text = text;
            rbShowInfo.IsChecked = true;
        }

        // ── Options snapshot ──

        private DownloadOptions BuildOptions()
        {
            string? ffmpeg = PlatformHelper.FindFfmpeg();
            // yt-dlp finds ffmpeg next to itself or on PATH; only Kleptos's managed copy needs pointing at.
            string? ffmpegLocation = ffmpeg != null && ffmpeg.StartsWith(AppPaths.ToolsDir, StringComparison.OrdinalIgnoreCase)
                ? AppPaths.ToolsDir
                : null;

            return new DownloadOptions
            {
                OutputFolder = NormalizedOutputFolder(),
                FileExtension = GetSelectedExtension(),
                QualityHeight = GetSelectedQualityHeight(),
                Preset = GetSelectedPreset(),
                ThumbnailOnly = cbThumbnailOnly.IsChecked == true,
                Playlist = cbPlaylist.IsChecked == true,
                PlaylistItems = txtPlaylistItems.Text?.Trim() ?? string.Empty,
                Subtitles = cbSubs.IsChecked == true,
                SubLangs = (cmbSubLangs.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "en",
                SponsorBlock = cbSponsorBlock.IsChecked == true,
                CookieSource = SelectedCookieSource(),
                CookiesFile = cookiesTxtFile,
                BrowserProfile = _settings.BrowserProfile,
                UseArchive = cbArchive.IsChecked == true,
                ArchivePath = EffectiveArchivePath(),
                RateLimit = _settings.RateLimit,
                ConcurrentFragments = _settings.ConcurrentFragments,
                Retries = _settings.Retries,
                Proxy = _settings.Proxy,
                FilenameTemplate = _settings.FilenameTemplate,
                FfmpegLocation = ffmpegLocation,
            };
        }

        private string EffectiveArchivePath() =>
            string.IsNullOrWhiteSpace(_settings.ArchivePath) ? AppPaths.DefaultArchivePath : _settings.ArchivePath;

        private string GetSelectedExtension()
        {
            var raw = ComboText(cmbFileFormats);
            return string.IsNullOrEmpty(raw) || string.Equals(raw, "default", StringComparison.OrdinalIgnoreCase)
                ? "ext"
                : raw.ToLowerInvariant();
        }

        private FormatPreset GetSelectedPreset() => ComboText(cmbPreset) switch
        {
            "YouTube 4K" => FormatPreset.YouTube4K,
            "Music library" => FormatPreset.MusicLibrary,
            "Podcast" => FormatPreset.Podcast,
            _ => FormatPreset.Normal,
        };

        // 0 = best (no height cap).
        private int GetSelectedQualityHeight()
        {
            var raw = ComboText(cmbQuality)?.ToLowerInvariant() ?? "best";
            return raw.EndsWith("p") && int.TryParse(raw.TrimEnd('p'), out var h) ? h : 0;
        }

        private void ApplyPresetEnabledState()
        {
            if (cmbFileFormats == null || cmbQuality == null || cmbPreset == null) return;
            bool exact = SelectedExactFormat() != null;
            bool normal = GetSelectedPreset() == FormatPreset.Normal;
            cmbPreset.IsEnabled = !exact;
            cmbFileFormats.IsEnabled = normal || exact;
            cmbQuality.IsEnabled = normal && !exact;
        }

        private void cmbPreset_SelectionChanged(object? sender, SelectionChangedEventArgs e) => ApplyPresetEnabledState();

        private async Task<bool> EnsureValidOutputFolder()
        {
            var folder = NormalizedOutputFolder();
            if (!Directory.Exists(folder))
            {
                var result = await MessageDialog.ShowAsync(this,
                    "Folder Not Found",
                    "The folder \"" + folder + "\" does not exist. Would you like to create it?",
                    MessageDialogButtons.YesNo);
                if (result != MessageDialogResult.Yes) return false;
                try { Directory.CreateDirectory(folder); }
                catch (Exception ex)
                {
                    ShowError("Could not create folder", ex);
                    return false;
                }
            }
            return true;
        }

        // ── Runner ──

        /// <summary>
        /// Runs a batch of entries with up to N yt-dlp processes at once. Everything here runs on
        /// the UI thread (awaits resume on it), so the shared state needs no locking; only the
        /// process output callbacks arrive on other threads and they marshal via Dispatcher.Post.
        /// </summary>
        private async Task RunEntriesAsync(List<QueueEntry> entries)
        {
            if (!await EnsureValidOutputFolder()) return;

            // Segments only apply to a single download (the Segments tab trims one URL).
            List<SegmentInfo>? segments = null;
            if (entries.Count == 1 && entries[0].SingleNaming && HasAnySegmentInput())
            {
                var segs = ParseSegments(out var segError);
                if (segs.Count == 0)
                {
                    ShowStatusMessage(segError);
                    rbSegments.IsChecked = true;
                    return;
                }
                segments = segs;
            }

            var baseOptions = BuildOptions();
            _runOptions = baseOptions with
            {
                Segments = segments,
                ExactFormat = entries.Count == 1 && entries[0].SingleNaming && _preview?.Url == entries[0].Url
                    ? SelectedExactFormat()
                    : null,
            };
            _runCustomName = (txtFileName.Text ?? string.Empty).Trim();

            _runEntries.Clear();
            _runEntries.AddRange(entries);
            _pending.Clear();
            foreach (var en in entries) _pending.Enqueue(en);
            _activeJobs.Clear();
            _authPromptShown = false;
            _cookieDbPromptShown = false;

            _currentSession = new SessionStats { TotalItems = entries.Count };
            _runCts = new CancellationTokenSource();
            var ct = _runCts.Token;

            try
            {
                SetDownloading(true);
                AppendOutput($"\n=== {DateTime.Now:HH:mm:ss} Starting {entries.Count} download(s), up to {_settings.MaxParallelDownloads} at once ===\n");
                UpdateQueuePanel();
                UpdateStatsPanel();

                if (!_ytDlp.Exists)
                {
                    AppendOutput("yt-dlp missing. Trying to fetch it...\n");
                    await CheckForYtDlpUpdateAsync(userInitiated: true);
                    if (!_ytDlp.Exists)
                    {
                        ShowStatusMessage("yt-dlp could not be downloaded. Check your connection and try again.");
                        foreach (var en in entries) if (en.Status == QueueStatus.Pending) { en.Status = QueueStatus.Failed; en.ErrorMessage = "yt-dlp missing"; }
                        return;
                    }
                }

                int workers = Math.Max(1, Math.Min(_settings.MaxParallelDownloads, entries.Count));
                _extraWorkers.Clear();
                await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => WorkerAsync(ct)).ToList());
                // Retries added mid-run may have started extra workers.
                while (_extraWorkers.Any(t => !t.IsCompleted))
                    await Task.WhenAll(_extraWorkers.ToList());

                if (ct.IsCancellationRequested)
                {
                    AppendOutput("\nDownload cancelled.\n");
                    foreach (var en in _runEntries)
                        if (en.Status == QueueStatus.Pending) en.Status = QueueStatus.Canceled;
                }
            }
            catch (Exception ex)
            {
                ShowError("Download failed", ex);
            }
            finally
            {
                _currentSession?.Freeze();
                AppendHistory();
                SetDownloading(false);
                RenderOverallProgress();
                txtCurrentStatus.Text = SessionSummaryText();
                UpdateStatsPanel();
                UpdateQueuePanel();
                SaveSettings();
                NotifyDownloadFinished();
                _runCts?.Dispose();
                _runCts = null;
            }

            if (_cookieDbPromptShown) await ShowCookieDbHelpAsync();
            else if (_authPromptShown) await ShowAuthHelpAsync();
        }

        private async Task WorkerAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                QueueEntry? entry = null;
                while (_pending.Count > 0)
                {
                    var candidate = _pending.Dequeue();
                    if (candidate.Status == QueueStatus.Pending) { entry = candidate; break; }
                }
                if (entry == null) return;

                await RunOneAsync(entry, ct);
            }
        }

        private async Task RunOneAsync(QueueEntry entry, CancellationToken runCt)
        {
            var o = _runOptions!;
            entry.Status = QueueStatus.Downloading;
            entry.ErrorMessage = string.Empty;
            entry.Percent = 0;
            entry.SpeedMBps = 0;
            entry.EtaSeconds = null;
            entry.PlaylistIndex = 0;
            entry.PlaylistCount = 0;

            var first = new DownloadItemStats
            {
                Url = entry.Url,
                Label = entry.Title,
                StartTime = DateTime.Now,
            };
            var job = new DownloadJob(entry, first);
            _currentSession!.Items.Add(first);
            _activeJobs.Add(job);
            UpdateQueuePanel();

            // Exact format and segments were captured for single runs only; drop them for others.
            var entryOptions = entry.SingleNaming ? o : o with { ExactFormat = null, Segments = null };
            var args = YtDlpArgsBuilder.Build(entryOptions, entry.Url,
                customName: entry.SingleNaming ? _runCustomName : string.Empty,
                multiBaseName: entry.SingleNaming ? null : entry.BaseName);

            string prefix = _runEntries.Count > 1 ? $"[{_runEntries.IndexOf(entry) + 1}] " : string.Empty;
            AppendOutput($"\n{prefix}> {entry.Url}\n");

            entry.ItemCts = CancellationTokenSource.CreateLinkedTokenSource(runCt);
            int exitCode;
            try
            {
                exitCode = await _ytDlp.RunAsync(args, line =>
                {
                    var ev = YtDlpOutputParser.Parse(line);
                    Dispatcher.UIThread.Post(() => OnOutput(job, ev, line, prefix));
                }, entry.ItemCts.Token);
            }
            catch (Exception ex)
            {
                exitCode = -2;
                job.LastError = ex.Message;
                AppendOutput($"{prefix}Could not start yt-dlp: {ex.Message}\n");
            }

            // Let queued output posts land before deciding the outcome.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            bool cancelled = entry.ItemCts.IsCancellationRequested;
            entry.ItemCts.Dispose();
            entry.ItemCts = null;
            _activeJobs.Remove(job);

            FinishJob(job, exitCode, cancelled);
            UpdateQueuePanel();
            UpdateStatsPanel();

            // Help dialogs wait until the whole batch is done so they don't stall other downloads.
            if (!runCt.IsCancellationRequested && !cancelled)
            {
                if (job.CookieDbError) _cookieDbPromptShown = true;
                else if (job.AuthRequired) _authPromptShown = true;
            }
        }

        private void FinishJob(DownloadJob job, int exitCode, bool cancelled)
        {
            var entry = job.Entry;
            var cur = job.Current;

            if (!cur.IsFinished)
            {
                cur.EndTime = DateTime.Now;
                cur.Duration = cur.EndTime - cur.StartTime;
                if (job.LiveMB > cur.FileSizeMB) cur.FileSizeMB = job.LiveMB;
                if (job.SpeedCount > 0) cur.SpeedMBps = job.SpeedSum / job.SpeedCount;

                if (cancelled)
                {
                    cur.Cancelled = true;
                    cur.ErrorMessage = "Cancelled";
                }
                else if (cur.AlreadyDownloaded)
                {
                    cur.Succeeded = true;
                }
                else if (string.IsNullOrEmpty(cur.ErrorMessage) && (job.SawFinished || exitCode == 0))
                {
                    cur.Succeeded = true;
                }
                else if (string.IsNullOrEmpty(cur.ErrorMessage))
                {
                    cur.ErrorMessage = string.IsNullOrEmpty(job.LastError) ? $"yt-dlp exited with code {exitCode}" : job.LastError;
                }
            }

            entry.SpeedMBps = 0;
            entry.EtaSeconds = null;

            int ok = job.Items.Count(i => i.Succeeded && !i.AlreadyDownloaded);
            int skipped = job.Items.Count(i => i.AlreadyDownloaded);
            int failed = job.Items.Count(i => i.IsFinished && !i.Succeeded && !i.AlreadyDownloaded && !i.Cancelled);

            if (cancelled)
            {
                entry.Status = QueueStatus.Canceled;
            }
            else if (ok == 0 && skipped > 0 && failed == 0)
            {
                entry.Status = QueueStatus.Skipped;
            }
            else if (ok == 0 && failed > 0)
            {
                entry.Status = QueueStatus.Failed;
                entry.ErrorMessage = job.Items.LastOrDefault(i => !string.IsNullOrEmpty(i.ErrorMessage))?.ErrorMessage ?? job.LastError;
            }
            else
            {
                entry.Status = QueueStatus.Succeeded;
                if (failed > 0) entry.ErrorMessage = $"{failed} of {job.Items.Count} failed";
            }

            string verdict = entry.Status switch
            {
                QueueStatus.Succeeded => "Done",
                QueueStatus.Skipped => "Already downloaded",
                QueueStatus.Canceled => "Cancelled",
                _ => "Failed",
            };
            AppendOutput($"{verdict}: {entry.Title}\n");
            if (pnlConsole.IsVisible) scrollViewer.ScrollToEnd();
        }

        // ── Output handling (UI thread) ──

        private void OnOutput(DownloadJob job, OutputEvent ev, string rawLine, string prefix)
        {
            // Console: structured progress lines are noise; print a readable line on completion only.
            if (ev.IsStructured)
            {
                var pretty = YtDlpOutputParser.ConsoleLine(ev);
                if (pretty != null) AppendOutput(prefix + pretty + "\n");
            }
            else
            {
                AppendOutput(prefix + rawLine + "\n");
            }
            if (pnlConsole.IsVisible) scrollViewer.ScrollToEnd();

            var entry = job.Entry;
            var cur = job.Current;

            if (ev.AuthRequired) job.AuthRequired = true;
            if (ev.Kind == OutputEventKind.Error && YtDlpOutputParser.IsCookieDatabaseError(rawLine)) job.CookieDbError = true;

            switch (ev.Kind)
            {
                case OutputEventKind.Progress:
                    if (ev.Percent >= 0) entry.Percent = ev.Percent;
                    if (ev.Status == "finished")
                    {
                        job.SawFinished = true;
                        // Each stream (video, audio) finishes separately; their sizes add up.
                        cur.FileSizeMB += ev.SizeMB;
                        job.LiveMB = 0;
                        entry.SpeedMBps = 0;
                    }
                    else
                    {
                        job.LiveMB = (ev.DownloadedBytes ?? 0) / (1024.0 * 1024.0);
                        if (ev.SpeedBytesPerSec is double) { entry.SpeedMBps = ev.SpeedMBps; job.SpeedSum += ev.SpeedMBps; job.SpeedCount++; }
                        entry.EtaSeconds = ev.EtaSeconds;
                        if (!ev.IsStructured && ev.SizeMB > 0) job.LiveMB = ev.SizeMB * Math.Max(0, ev.Percent) / 100.0;
                    }
                    if (ev.PlaylistCount > 0)
                    {
                        entry.PlaylistCount = ev.PlaylistCount;
                        if (ev.PlaylistIndex > 0) entry.PlaylistIndex = ev.PlaylistIndex;
                    }
                    if (!string.IsNullOrEmpty(ev.Path) && string.IsNullOrEmpty(cur.FileName))
                        cur.FileName = Path.GetFileName(ev.Path);
                    break;

                case OutputEventKind.Destination:
                case OutputEventKind.Merging:
                    cur.FileName = Path.GetFileName(ev.Path);
                    if (entry.Title == entry.Url && entry.PlaylistCount <= 1)
                        entry.Title = Path.GetFileNameWithoutExtension(StripFormatSuffix(cur.FileName));
                    if (ev.Kind == OutputEventKind.Merging) job.SawFinished = true;
                    break;

                case OutputEventKind.PlaylistItem:
                    entry.PlaylistIndex = ev.PlaylistIndex;
                    entry.PlaylistCount = ev.PlaylistCount;
                    entry.Percent = 0;
                    if (!job.PlaylistCounted && ev.PlaylistCount > 0)
                    {
                        // The entry was counted as one item; it is really PlaylistCount items.
                        _currentSession!.TotalItems += ev.PlaylistCount - 1;
                        job.PlaylistCounted = true;
                    }

                    bool placeholder = string.IsNullOrEmpty(cur.FileName) && cur.FileSizeMB == 0 && job.LiveMB == 0
                                       && string.IsNullOrEmpty(cur.ErrorMessage) && !cur.AlreadyDownloaded;
                    if (!placeholder)
                    {
                        CloseItem(job, cur);
                        var next = new DownloadItemStats { Url = entry.Url, StartTime = DateTime.Now };
                        job.Items.Add(next);
                        job.Current = next;
                        _currentSession!.Items.Add(next);
                    }
                    else
                    {
                        cur.Label = string.Empty; // the entry title names the whole playlist, not item 1
                    }
                    job.SawFinished = false;
                    break;

                case OutputEventKind.AlreadyDownloaded:
                case OutputEventKind.InArchive:
                    cur.AlreadyDownloaded = true;
                    cur.Succeeded = true;
                    job.SawAlready = true;
                    if (!string.IsNullOrEmpty(ev.Path)) cur.FileName = Path.GetFileName(ev.Path);
                    else if (ev.Kind == OutputEventKind.InArchive && string.IsNullOrEmpty(cur.FileName))
                        cur.FileName = string.IsNullOrEmpty(ev.Message) ? "Already in archive" : $"Already in archive ({ev.Message})";
                    break;

                case OutputEventKind.ThumbnailWritten:
                    cur.FileName = Path.GetFileName(ev.Path);
                    cur.Succeeded = true;
                    job.SawFinished = true;
                    entry.Percent = 100;
                    break;

                case OutputEventKind.Error:
                    cur.ErrorMessage = ev.Message;
                    cur.Succeeded = false;
                    job.LastError = ev.Message;
                    break;
            }
        }

        // "clip.f137.mp4" -> "clip.mp4" for display.
        private static string StripFormatSuffix(string name)
        {
            var m = System.Text.RegularExpressions.Regex.Match(name, @"^(.*)\.f[\w-]+(\.\w+)$");
            return m.Success ? m.Groups[1].Value + m.Groups[2].Value : name;
        }

        private void CloseItem(DownloadJob job, DownloadItemStats item)
        {
            if (item.IsFinished) return;
            item.EndTime = DateTime.Now;
            item.Duration = item.EndTime - item.StartTime;
            if (job.LiveMB > 0 && item.FileSizeMB == 0) item.FileSizeMB = job.LiveMB;
            if (job.SpeedCount > 0) item.SpeedMBps = job.SpeedSum / job.SpeedCount;
            job.LiveMB = 0;
            job.SpeedSum = 0;
            job.SpeedCount = 0;
            // "Downloading item N" means item N-1 just finished; it failed only if an ERROR was seen.
            if (string.IsNullOrEmpty(item.ErrorMessage)) item.Succeeded = true;
        }

        private void SetDownloading(bool downloading)
        {
            _isDownloading = downloading;
            btnDownload.Content = downloading ? "Cancel all" : "Download";
            txtSpeedEta.Text = string.Empty;
            if (downloading) StartStatsTimer(); else StopStatsTimer();

            if (downloading && WindowState == WindowState.Minimized)
                Hide();
        }

        // ── Help dialogs ──

        private async Task ShowAuthHelpAsync()
        {
            var source = SelectedCookieSource();
            string body = source == CookieSource.None
                ? "This site wants you signed in.\n\n" +
                  "Open Options > Cookies and pick the browser you're logged in with (Chrome, Firefox, Edge...). " +
                  "Kleptos will read your session straight from it, no exporting needed. Then hit retry in the Queue tab.\n\n" +
                  "Click OK to open the Cookies dropdown now."
                : "The site still refused access with the current cookies.\n\n" +
                  "Make sure you're logged in to the site in the selected browser (or re-export your cookies.txt), then retry from the Queue tab.\n\n" +
                  "Click OK to change the cookie source.";

            var result = await MessageDialog.ShowAsync(this, "Sign-in required", body, MessageDialogButtons.OKCancel);
            if (result == MessageDialogResult.OK)
            {
                ShowPage(null);
                cmbCookieSource.Focus();
                cmbCookieSource.IsDropDownOpen = true;
            }
        }

        private async Task ShowCookieDbHelpAsync()
        {
            var browser = SelectedCookieSource().ToString();
            await MessageDialog.ShowAsync(this, "Couldn't read browser cookies",
                $"yt-dlp couldn't read {browser}'s cookies.\n\n" +
                "On Windows, Chrome, Edge, Brave and Opera lock their cookie file while running: close the browser completely (check the tray) and retry.\n\n" +
                "If that doesn't help, choose Firefox, or export a cookies.txt and pick 'cookies.txt file' under Options > Cookies.",
                MessageDialogButtons.OK);
        }

        // Balloon tips don't exist in Avalonia's TrayIcon; reflect the finished session
        // in the tray icon tooltip instead. Only when the window is hidden.
        private void NotifyDownloadFinished()
        {
            if (IsVisible || _currentSession == null) return;
            try
            {
                var icons = TrayIcon.GetIcons(Avalonia.Application.Current!);
                if (icons != null && icons.Count > 0)
                    icons[0].ToolTipText = $"Kleptos - {SessionSummaryText()}";
            }
            catch { }
        }

        private string SessionSummaryText()
        {
            var s = _currentSession;
            if (s == null) return "Waiting to start...";
            var parts = new List<string>();
            if (s.SuccessCount > 0) parts.Add($"{s.SuccessCount} downloaded");
            if (s.FailCount > 0) parts.Add($"{s.FailCount} failed");
            if (s.AlreadyDownloadedCount > 0) parts.Add($"{s.AlreadyDownloadedCount} skipped");
            bool cancelled = s.CancelledCount > 0 || _runEntries.Any(e => e.Status == QueueStatus.Canceled);
            string head = cancelled ? "Cancelled" : "Finished";
            return parts.Count > 0 ? $"{head}: " + string.Join(", ", parts) : head + ".";
        }

        // ── History ──

        private void AppendHistory()
        {
            if (_currentSession == null) return;

            string folder = _runOptions?.OutputFolder ?? NormalizedOutputFolder();
            bool added = false;
            foreach (var item in _currentSession.Items)
            {
                if (string.IsNullOrEmpty(item.Url) || !item.IsFinished) continue;
                if (item.Cancelled) continue;

                _history.Insert(0, new HistoryEntry
                {
                    Url = item.Url,
                    Title = !string.IsNullOrEmpty(item.FileName) ? StripFormatSuffix(item.FileName)
                        : !string.IsNullOrEmpty(item.Label) ? item.Label : item.Url,
                    When = item.StartTime,
                    Succeeded = item.Succeeded || item.AlreadyDownloaded,
                    FileSizeMB = item.AlreadyDownloaded ? 0 : item.FileSizeMB,
                    Folder = folder,
                });
                added = true;
            }

            while (_history.Count > 500) _history.RemoveAt(_history.Count - 1);

            if (added)
            {
                Stores.SaveHistory(_history);
                UpdateHistoryPanel();
            }
        }
    }
}
