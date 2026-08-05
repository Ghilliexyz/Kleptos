using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Velopack;
using Velopack.Sources;
using Polygon = Avalonia.Controls.Shapes.Polygon;
using Rectangle = Avalonia.Controls.Shapes.Rectangle;

namespace Kleptos
{
    public class DownloadItemStats
    {
        public string Url { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public bool Succeeded { get; set; }
        public bool AlreadyDownloaded { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public double FileSizeMB { get; set; }
        public double SpeedMBps { get; set; }
        public TimeSpan Duration { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
    }

    public class SessionStats
    {
        public List<DownloadItemStats> Items { get; set; } = new();
        public int TotalItems { get; set; }
        public int CompletedItems { get; set; }
        public DateTime SessionStart { get; set; } = DateTime.Now;

        private bool _frozen;
        private TimeSpan _frozenDuration;

        public void Freeze()
        {
            if (_frozen) return;
            _frozenDuration = DateTime.Now - SessionStart;
            _frozen = true;
        }

        public int SuccessCount => Items.Count(i => i.Succeeded && !i.AlreadyDownloaded);
        public int FailCount => Items.Count(i => !i.Succeeded && !i.AlreadyDownloaded && i.EndTime != default);
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
        public DownloadItemStats? LargestFile => Items.Where(i => i.FileSizeMB > 0).OrderByDescending(i => i.FileSizeMB).FirstOrDefault();
        public DownloadItemStats? SmallestFile => Items.Where(i => i.FileSizeMB > 0).OrderBy(i => i.FileSizeMB).FirstOrDefault();
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
        public string Title = "";        // display name (multi line title or URL)
        public QueueStatus Status = QueueStatus.Pending;
        public string ErrorMessage = "";
        [System.Text.Json.Serialization.JsonIgnore] public CancellationTokenSource? ItemCts;
    }

    public class HistoryEntry
    {
        public string Url = "";
        public string Title = "";
        public DateTime When;
        public bool Succeeded;
    }

    public enum FormatPreset
    {
        Normal,
        YouTube4K,
        MusicLibrary,
        Podcast,
    }

    public class KleptosSettings
    {
        public string OutputFolder { get; set; } = string.Empty;
        public string LastFormat { get; set; } = "default";
        public string LastQuality { get; set; } = "best";
        public string LastPreset { get; set; } = "Normal";
        public string LastCookiesFile { get; set; } = string.Empty;
        public bool MultiDownload { get; set; }
        public bool ThumbnailOnly { get; set; }
        public bool PlaylistMode { get; set; }
        public bool DownloadSubs { get; set; }
        public string SubLangs { get; set; } = "en";
        public bool SponsorBlock { get; set; }
    }

    public partial class MainWindow : Window
    {
        private const string GitHubLatestApi = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";

        // Single shared HttpClient for the lifetime of the app.
        private static readonly HttpClient SharedHttp = CreateHttpClient();
        private static HttpClient CreateHttpClient()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("Kleptos/1.0 (+https://github.com/Ghilliexyz/Kleptos)");
            return h;
        }

        private string ytDlpPath = PlatformHelper.YtDlpBinaryName;
        private string cookiesTxtFile = string.Empty;
        private bool hasUpdate = false;
        private CancellationTokenSource? _currentCts;
        private bool _isDownloading;
        private string? _lastAutoFilledUrl;
        private readonly List<QueueEntry> _queue = new();
        private QueueEntry? _currentQueueEntry;
        private List<HistoryEntry> _history = new();

        private static readonly Regex UrlRegex =
            new Regex(@"https?://\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ProgressRegex =
            new Regex(@"\[download\]\s+([\d.]+)%\s+of\s+~?([\d.]+)([\w/]+)\s+at\s+([\d.]+)([\w/]+)(?:\s+ETA\s+(\S+))?", RegexOptions.Compiled);
        private static readonly Regex ProgressCompleteRegex =
            new Regex(@"\[download\]\s+100(?:\.0+)?%\s+of\s+~?([\d.]+)([\w/]+)", RegexOptions.Compiled);
        private static readonly Regex DestinationRegex =
            new Regex(@"\[download\]\s+Destination:\s+(.+)$", RegexOptions.Compiled);
        private static readonly Regex MergerRegex =
            new Regex(@"\[Merger\]\s+Merging formats into\s+""(.+)""", RegexOptions.Compiled);
        private static readonly Regex PlaylistProgressRegex =
            new Regex(@"\[download\]\s+Downloading item\s+(\d+)\s+of\s+(\d+)", RegexOptions.Compiled);
        private static readonly Regex AlreadyDownloadedRegex =
            new Regex(@"has already been downloaded", RegexOptions.Compiled);
        // Thumbnail-only runs (--skip-download --write-thumbnail) never emit a progress or
        // merge line, so success has to be detected from the thumbnail writer output instead.
        private static readonly Regex ThumbnailWrittenRegex =
            new Regex(@"Writing\s+.*\bthumbnail\b.*\bto:\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ThumbnailAlreadyRegex =
            new Regex(@"\bthumbnail\b.*\bis already present\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ErrorRegex =
            new Regex(@"ERROR:\s+(.+)$", RegexOptions.Compiled);
        // Curly apostrophe replaced with straight one; broadened to catch the common auth wording.
        private static readonly Regex AuthRequiredRegex =
            new Regex(@"Sign in to confirm|Please log in|This video is private|--cookies-from-browser|unable to download webpage",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private SessionStats? _currentSession;
        private DownloadItemStats? _currentItem;

        private double _videoDurationSeconds = 0;
        private string _videoTitle = string.Empty;

        private KleptosSettings _settings = new();
        private static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Kleptos", "settings.json");
        private static string HistoryPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Kleptos", "history.json");

        public MainWindow()
        {
            InitializeComponent();

            // yt-dlp lives next to the app
            ytDlpPath = Path.Combine(AppContext.BaseDirectory, PlatformHelper.YtDlpBinaryName);

            LoadSettings();

            Opened += MainWindow_Loaded;
            Closing += MainWindow_Closing;
            Activated += OnWindowActivated;

            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            AddHandler(DragDrop.DropEvent, OnDrop);

            txtURL.TextChanged += (_, _) => UpdatePlaylistHint();
            cbPlaylist.IsCheckedChanged += (_, _) => UpdatePlaylistHint();
        }

        // Show a hint under the URL box when the URL looks like a playlist but the
        // "Download playlists" setting is off.
        private void UpdatePlaylistHint()
        {
            var url = txtURL.Text ?? string.Empty;
            bool looksLikePlaylist = url.Contains("list=", StringComparison.OrdinalIgnoreCase)
                                  || url.Contains("/playlist", StringComparison.OrdinalIgnoreCase);
            txtPlaylistHint.IsVisible = looksLikePlaylist && cbPlaylist.IsChecked != true;
        }

        // Auto-paste a URL from the clipboard whenever the window regains focus. Only
        // overwrites the URL box when it is empty or still holds the previous auto-fill.
        private async void OnWindowActivated(object? sender, EventArgs e)
        {
            try
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard == null) return;

                var text = (await clipboard.GetTextAsync())?.Trim();
                if (string.IsNullOrEmpty(text) || !UrlRegex.IsMatch(text)) return;

                var current = (txtURL.Text ?? string.Empty).Trim();
                if (current.Length == 0 || current == _lastAutoFilledUrl)
                {
                    txtURL.Text = text;
                    _lastAutoFilledUrl = text;
                }
            }
            catch { /* clipboard access can throw; ignore */ }
        }

        private void OnDragOver(object? sender, DragEventArgs e)
        {
            e.DragEffects = e.Data.Contains(DataFormats.Text)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnDrop(object? sender, DragEventArgs e)
        {
            var text = e.Data.Get(DataFormats.Text) as string;
            if (string.IsNullOrWhiteSpace(text)) return;

            var m = UrlRegex.Match(text.Trim());
            if (!m.Success) return;

            if (cbMultiDownload.IsChecked == true)
            {
                var existing = (txtMultiUrls.Text ?? string.Empty).TrimEnd();
                txtMultiUrls.Text = existing.Length == 0
                    ? m.Value
                    : existing + Environment.NewLine + m.Value;
            }
            else
            {
                txtURL.Text = m.Value;
            }
        }

        // ── Window state / tray ──

        // WPF used StateChanged + HideToTray (WinForms NotifyIcon). Avalonia: watch the
        // WindowState property and Hide() the window when minimized mid-download; the
        // TrayIcon ("Show Kleptos" menu in App.axaml) restores it.
        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == WindowStateProperty
                && WindowState == WindowState.Minimized
                && _isDownloading)
            {
                Hide();
            }
        }

        // Balloon tips don't exist in Avalonia's TrayIcon; reflect the finished session
        // in the tray icon tooltip instead. Only when the window is hidden, as before.
        private void NotifyDownloadFinished()
        {
            if (IsVisible || _currentSession == null) return;
            var s = _currentSession;
            var parts = new List<string>();
            if (s.SuccessCount > 0) parts.Add($"{s.SuccessCount} succeeded");
            if (s.FailCount > 0) parts.Add($"{s.FailCount} failed");
            if (s.AlreadyDownloadedCount > 0) parts.Add($"{s.AlreadyDownloadedCount} skipped");
            var msg = parts.Count > 0 ? string.Join(", ", parts) : "Done.";
            try
            {
                var icons = TrayIcon.GetIcons(Application.Current!);
                if (icons != null && icons.Count > 0)
                    icons[0].ToolTipText = $"Kleptos - {msg}";
            }
            catch { }
        }

        private async void MainWindow_Loaded(object? sender, EventArgs e)
        {
            try
            {
                pnlTimeline.PropertyChanged += (_, args) =>
                {
                    if (args.Property == BoundsProperty)
                        UpdateTimelineVisualization();
                };

                ApplySettingsToUi();
                SetDefaultOutputLocation();
                LoadHistory();
                UpdateHistoryPanel();

                // ffmpeg is needed for merging/recode/thumbnail conversion only.
                if (PlatformHelper.FindFfmpeg() == null)
                    AppendOutput("ffmpeg not found next to the app or on PATH (needed for merging/recode/thumbnail convert only).\n");

                if (!File.Exists(ytDlpPath))
                {
                    AppendOutput($"{PlatformHelper.YtDlpBinaryName} not found. Attempting to download...\n");
                }

                // Fire-and-forget Kleptos update probe (silent if no update is available).
                _ = CheckForKleptosUpdateAsync();

                // Await the yt-dlp check so its progress lines flow into the console in order.
                await CheckForYTDLPUpdateAsync();
            }
            catch (Exception ex)
            {
                ShowError("Startup error", ex);
            }
        }

        private void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
        {
            try { _currentCts?.Cancel(); } catch { }
            try { SaveSettings(); } catch { }
        }

        // ── Small UI helpers (Avalonia has no TextBox.AppendText) ──

        private void AppendOutput(string text)
        {
            txtOutput.Text = (txtOutput.Text ?? string.Empty) + text;
        }

        private SolidColorBrush ResBrush(string key, string fallbackHex)
        {
            if (this.TryFindResource(key, out var value))
            {
                if (value is SolidColorBrush brush) return brush;
                if (value is Color color) return new SolidColorBrush(color);
            }
            return new SolidColorBrush(Color.Parse(fallbackHex));
        }

        private FontFamily JetBrainsFont =>
            this.TryFindResource("JetBrainsReg", out var value) && value is FontFamily font
                ? font
                : FontFamily.Default;

        // ── Settings persistence ──

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var s = JsonSerializer.Deserialize<KleptosSettings>(File.ReadAllText(SettingsPath));
                    if (s != null) _settings = s;
                }
            }
            catch { /* ignore corrupt settings */ }
        }

        private void SaveSettings()
        {
            try
            {
                _settings.OutputFolder = txtFileOutput?.Text ?? string.Empty;
                _settings.LastFormat = (cmbFileFormats?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "default";
                _settings.LastQuality = (cmbQuality?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "best";
                _settings.LastPreset = (cmbPreset?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Normal";
                _settings.LastCookiesFile = cookiesTxtFile;
                _settings.MultiDownload = cbMultiDownload?.IsChecked == true;
                _settings.ThumbnailOnly = cbThumbnailOnly?.IsChecked == true;
                _settings.PlaylistMode = cbPlaylist?.IsChecked == true;
                _settings.DownloadSubs = cbSubs?.IsChecked == true;
                _settings.SubLangs = (cmbSubLangs?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "en";
                _settings.SponsorBlock = cbSponsorBlock?.IsChecked == true;

                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* never crash on settings save */ }
        }

        // ── History persistence ──

        private void LoadHistory()
        {
            try
            {
                if (File.Exists(HistoryPath))
                {
                    var h = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(HistoryPath));
                    if (h != null) _history = h;
                }
            }
            catch { /* ignore corrupt history */ }
        }

        private void SaveHistory()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
                File.WriteAllText(HistoryPath, JsonSerializer.Serialize(_history, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* never crash on history save */ }
        }

        // Appends the finished session's items to the history (newest first, capped at 200).
        private void AppendHistory()
        {
            if (_currentSession == null) return;

            bool added = false;
            foreach (var item in _currentSession.Items)
            {
                // Playlist runs create extra session items without a Url; those got stamped
                // with the queue entry's Url in ParseOutputLine, so anything still empty is skipped.
                if (string.IsNullOrEmpty(item.Url)) continue;

                _history.Insert(0, new HistoryEntry
                {
                    Url = item.Url,
                    Title = string.IsNullOrEmpty(item.FileName) ? item.Url : item.FileName,
                    When = item.StartTime,
                    Succeeded = item.Succeeded || item.AlreadyDownloaded,
                });
                added = true;
            }

            while (_history.Count > 200) _history.RemoveAt(_history.Count - 1);

            if (added)
            {
                SaveHistory();
                UpdateHistoryPanel();
            }
        }

        private void ApplySettingsToUi()
        {
            if (!string.IsNullOrWhiteSpace(_settings.OutputFolder))
                txtFileOutput.Text = _settings.OutputFolder;

            if (!string.IsNullOrWhiteSpace(_settings.LastFormat))
            {
                foreach (var item in cmbFileFormats.Items.OfType<ComboBoxItem>())
                {
                    if (string.Equals(item.Content?.ToString(), _settings.LastFormat, StringComparison.OrdinalIgnoreCase))
                    {
                        // ComboBoxItem.IsSelected only propagates for realized containers
                        // (dropdown opened); select via the ComboBox instead.
                        cmbFileFormats.SelectedItem = item;
                        break;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(_settings.LastQuality))
            {
                foreach (var item in cmbQuality.Items.OfType<ComboBoxItem>())
                {
                    if (string.Equals(item.Content?.ToString(), _settings.LastQuality, StringComparison.OrdinalIgnoreCase))
                    {
                        cmbQuality.SelectedItem = item;
                        break;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(_settings.LastPreset))
            {
                foreach (var item in cmbPreset.Items.OfType<ComboBoxItem>())
                {
                    if (string.Equals(item.Content?.ToString(), _settings.LastPreset, StringComparison.OrdinalIgnoreCase))
                    {
                        cmbPreset.SelectedItem = item;
                        break;
                    }
                }
            }
            ApplyPresetEnabledState();

            if (!string.IsNullOrWhiteSpace(_settings.LastCookiesFile) && File.Exists(_settings.LastCookiesFile))
            {
                cookiesTxtFile = _settings.LastCookiesFile;
                btnCookies.Content = "Cookies: " + Path.GetFileName(cookiesTxtFile);
                ToolTip.SetTip(btnCookies, cookiesTxtFile);
                WarnIfCookiesStale(cookiesTxtFile);
            }

            cbMultiDownload.IsChecked = _settings.MultiDownload;
            cbThumbnailOnly.IsChecked = _settings.ThumbnailOnly;
            cbPlaylist.IsChecked = _settings.PlaylistMode;
            cbSubs.IsChecked = _settings.DownloadSubs;
            // Select the saved language if it's in the list, otherwise fall back to "en"
            bool subLangFound = false;
            foreach (var item in cmbSubLangs.Items)
            {
                if (item is ComboBoxItem cbi && string.Equals(cbi.Tag?.ToString(), _settings.SubLangs, StringComparison.OrdinalIgnoreCase))
                {
                    cmbSubLangs.SelectedItem = cbi;
                    subLangFound = true;
                    break;
                }
            }
            if (!subLangFound) cmbSubLangs.SelectedIndex = 1; // "en"
            cbSponsorBlock.IsChecked = _settings.SponsorBlock;
        }

        // ── Download flow ──

        private async void Download_Click(object? sender, RoutedEventArgs e)
        {
            // Repurpose the button as a Cancel control while a download is running.
            if (_isDownloading)
            {
                try { _currentCts?.Cancel(); } catch { }
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
                    AppendOutput("No links found (multi box is empty).\n");
                    return;
                }

                entries = new List<QueueEntry>();
                foreach (var line in lines)
                {
                    if (TryParseMultiLine(line, out var url, out var baseName))
                        entries.Add(new QueueEntry { Url = url, Title = baseName });
                }

                if (entries.Count == 0)
                {
                    AppendOutput("No valid links found in multi box.\n");
                    return;
                }
            }
            else
            {
                if (!TryGetValidUrl(txtURL.Text, out var url, out var error))
                {
                    AppendOutput(error + "\n");
                    return;
                }
                entries = new List<QueueEntry> { new QueueEntry { Url = url, Title = url } };
            }

            _queue.AddRange(entries);
            UpdateQueuePanel();
            if (entries.Count > 1) rbQueue.IsChecked = true;

            await RunEntriesAsync(entries, singleNaming: cbMultiDownload.IsChecked != true);
        }

        // Shared wrapper around RunQueueAsync used by both Download_Click and queue retry:
        // owns the downloading flag, the main cancellation token, and session teardown.
        private async Task RunEntriesAsync(List<QueueEntry> entries, bool singleNaming)
        {
            try
            {
                SetDownloading(true);
                txtOutput.Clear();
                ResetShowInfoPanel();
                _currentCts = new CancellationTokenSource();

                await RunQueueAsync(entries, singleNaming, _currentCts.Token);
            }
            catch (OperationCanceledException)
            {
                AppendOutput("\nDownload cancelled.\n");
                UpdateCurrentStatus("Cancelled");
            }
            catch (Exception ex)
            {
                ShowError("Download failed", ex);
            }
            finally
            {
                if (_currentSession != null) _currentSession.Freeze();
                AppendHistory();
                UpdateStatsPanel();
                SetDownloading(false);
                SaveSettings();
                NotifyDownloadFinished();
                UpdateQueuePanel();
            }
        }

        private async Task RunQueueAsync(List<QueueEntry> entries, bool singleNaming, CancellationToken ct)
        {
            _currentSession = new SessionStats { TotalItems = entries.Count };

            if (!await EnsureValidOutputFolder()) return;

            // Segments only apply to single downloads (the Segments tab trims one URL).
            List<SegmentInfo>? segments = null;
            if (singleNaming && rbSegments.IsChecked == true)
            {
                var segs = ParseSegments(out var segError);
                if (segs.Count > 0) segments = segs;
                else if (!string.IsNullOrEmpty(segError))
                {
                    AppendOutput(segError + "\n");
                    return;
                }
            }

            var preset = GetSelectedPreset();
            int qualityHeight = GetSelectedQualityHeight();
            // Single mode historically used the format extension even in thumbnail-only mode,
            // multi used the preset extension; keep both behaviors.
            string fileExtension = preset != FormatPreset.Normal && !(singleNaming && cbThumbnailOnly.IsChecked == true)
                ? PresetExtension(preset)
                : GetSelectedExtension();

            int i = 0;
            foreach (var entry in entries)
            {
                if (entry.Status != QueueStatus.Pending) continue;

                if (ct.IsCancellationRequested)
                {
                    entry.Status = QueueStatus.Canceled;
                    continue;
                }
                i++;

                entry.Status = QueueStatus.Downloading;
                entry.ErrorMessage = "";
                _currentQueueEntry = entry;
                UpdateQueuePanel();

                _currentItem = new DownloadItemStats
                {
                    Url = entry.Url,
                    FileName = singleNaming ? string.Empty : entry.Title,
                    StartTime = DateTime.Now,
                };
                _currentSession.Items.Add(_currentItem);

                if (entries.Count > 1)
                {
                    UpdateCurrentStatus($"Downloading {i} of {entries.Count}: {entry.Title}");
                    AppendOutput($"[{i}/{entries.Count}] {entry.Title}\n{entry.Url}\n\n");
                }

                // Single downloads name the file from the File Name textbox (AppendOutputArg),
                // multi builds it from the entry title.
                bool useSingleOutput = singleNaming && entries.Count == 1;
                var args = BuildDownloadArgs(entry.Url, useSingleOutput ? string.Empty : entry.Title,
                    fileExtension, preset, qualityHeight, isSingle: useSingleOutput);
                if (segments != null)
                    AppendSegmentArgs(args, segments);
                args.Add(entry.Url);

                entry.ItemCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                await RunYtDlpAsync(args, entry.ItemCts.Token);

                if (entry.ItemCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    // Only this entry was cancelled; keep going with the rest of the queue.
                    entry.Status = QueueStatus.Canceled;
                    entry.ItemCts = null;
                    UpdateQueuePanel();
                    continue;
                }

                if (ct.IsCancellationRequested)
                {
                    entry.Status = QueueStatus.Canceled;
                    foreach (var rest in entries)
                        if (rest.Status == QueueStatus.Pending) rest.Status = QueueStatus.Canceled;
                    entry.ItemCts = null;
                    UpdateQueuePanel();
                    break;
                }

                // RunYtDlpAsync records the run outcome on _currentItem (for playlist runs that
                // is the last playlist item, which still reflects the run as a whole).
                if (_currentItem != null && _currentItem.AlreadyDownloaded) entry.Status = QueueStatus.Skipped;
                else if (_currentItem != null && _currentItem.Succeeded) entry.Status = QueueStatus.Succeeded;
                else
                {
                    entry.Status = QueueStatus.Failed;
                    entry.ErrorMessage = _currentItem?.ErrorMessage ?? "";
                }

                entry.ItemCts = null;
                UpdateQueuePanel();
            }

            _currentQueueEntry = null;
        }

        private void SetDownloading(bool downloading)
        {
            _isDownloading = downloading;
            btnDownload.Content = downloading ? "Cancel" : "Download";
            txtSpeedEta.Text = string.Empty;

            if (downloading && WindowState == WindowState.Minimized)
                Hide();
        }

        // Shared yt-dlp argument construction for single and multi downloads. The URL itself
        // is appended by the callers, not here. In single mode the output name comes from the
        // File Name textbox (AppendOutputArg); in multi mode it is built from baseName.
        private List<string> BuildDownloadArgs(string url, string baseName, string fileExtension, FormatPreset preset, int qualityHeight, bool isSingle)
        {
            var args = new List<string>();
            bool thumbnailOnly = cbThumbnailOnly.IsChecked == true;

            if (thumbnailOnly)
            {
                args.Add("--skip-download");
                args.Add("--write-thumbnail");
            }
            else if (preset != FormatPreset.Normal)
            {
                AppendPresetArgs(args, preset);
            }
            else
            {
                AppendFormatArgs(args, fileExtension, qualityHeight);
            }

            AppendCookiesArgs(args);

            if (isSingle)
            {
                AppendOutputArg(args, fileExtension);
            }
            else
            {
                var folder = NormalizedOutputFolder();
                var safeBase = SanitizeFileNameTemplateSafe(baseName);
                var outputBase = Path.Combine(folder, safeBase);
                args.Add("-o");
                args.Add(string.Equals(fileExtension, "ext", StringComparison.OrdinalIgnoreCase)
                    ? $"{outputBase}.%(ext)s"
                    : $"{outputBase}.{fileExtension}");
            }
            args.Add("--no-mtime");

            args.Add(cbPlaylist.IsChecked == true ? "--yes-playlist" : "--no-playlist");

            if (!thumbnailOnly && cbSubs.IsChecked == true)
            {
                var langs = (cmbSubLangs.SelectedItem as ComboBoxItem)?.Tag?.ToString()?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(langs)) langs = "en";
                args.Add("--write-subs");
                args.Add("--write-auto-subs");
                args.Add("--sub-langs");
                args.Add(langs);
            }

            if (!thumbnailOnly && cbSponsorBlock.IsChecked == true)
            {
                args.Add("--sponsorblock-remove");
                args.Add("default");
            }

            return args;
        }

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

        private string NormalizedOutputFolder()
        {
            var s = (txtFileOutput.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(s))
                s = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            return s.TrimEnd('\\', '/');
        }

        private string GetSelectedExtension()
        {
            string fileExtension = "ext";
            if (cmbFileFormats?.SelectedItem is ComboBoxItem typeItem &&
                typeItem.Content?.ToString() is string raw &&
                !string.Equals(raw, "default", StringComparison.OrdinalIgnoreCase))
            {
                fileExtension = raw.ToLowerInvariant();
            }
            return fileExtension;
        }

        private FormatPreset GetSelectedPreset()
        {
            if (cmbPreset?.SelectedItem is ComboBoxItem item &&
                item.Content?.ToString() is string raw)
            {
                return raw switch
                {
                    "YouTube 4K" => FormatPreset.YouTube4K,
                    "Music library" => FormatPreset.MusicLibrary,
                    "Podcast" => FormatPreset.Podcast,
                    _ => FormatPreset.Normal,
                };
            }
            return FormatPreset.Normal;
        }

        // Returns the output extension a preset locks in, or empty for Normal (caller picks from cmbFileFormats).
        private static string PresetExtension(FormatPreset preset) => preset switch
        {
            FormatPreset.YouTube4K => "mp4",
            FormatPreset.MusicLibrary => "mp3",
            FormatPreset.Podcast => "m4a",
            _ => string.Empty,
        };

        private static void AppendPresetArgs(List<string> args, FormatPreset preset)
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

        private void ApplyPresetEnabledState()
        {
            bool normalMode = GetSelectedPreset() == FormatPreset.Normal;
            if (cmbFileFormats != null) cmbFileFormats.IsEnabled = normalMode;
            if (cmbQuality != null) cmbQuality.IsEnabled = normalMode;
        }

        private void cmbPreset_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            ApplyPresetEnabledState();
        }

        // 0 = best (no height cap).
        private int GetSelectedQualityHeight()
        {
            if (cmbQuality?.SelectedItem is ComboBoxItem item &&
                item.Content?.ToString() is string raw)
            {
                return raw.ToLowerInvariant() switch
                {
                    "1080p" => 1080,
                    "720p" => 720,
                    "480p" => 480,
                    "360p" => 360,
                    _ => 0,
                };
            }
            return 0;
        }

        private static bool TryGetValidUrl(string? input, out string url, out string error)
        {
            url = string.Empty;

            if (string.IsNullOrWhiteSpace(input) || input.Trim().Length < 8)
            {
                error = "No link found.";
                return false;
            }

            input = input.Trim();

            if (!(input.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                  input.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
            {
                error = "Invalid link (must start with http:// or https://).";
                return false;
            }

            error = string.Empty;
            url = input;
            return true;
        }

        private static bool TryParseMultiLine(string line, out string url, out string baseName)
        {
            url = string.Empty;
            baseName = string.Empty;

            var m = UrlRegex.Match(line);
            if (!m.Success) return false;

            url = m.Value.Trim();

            var left = line.Substring(0, m.Index).Trim();

            int? indexNum = null;
            string titlePart = left;

            if (!string.IsNullOrWhiteSpace(left))
            {
                var parts = left.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 1 && int.TryParse(parts[0], out var n))
                {
                    indexNum = n;
                    titlePart = parts.Length == 2 ? parts[1].Trim() : string.Empty;
                }
            }

            if (string.IsNullOrWhiteSpace(titlePart))
                titlePart = "%(title)s";
            else
                titlePart = SanitizeFileName(titlePart);

            baseName = indexNum.HasValue
                ? $"{indexNum.Value} - {titlePart}"
                : titlePart;

            return true;
        }

        private static string SanitizeFileNameTemplateSafe(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "%(title)s";

            const string TITLE_TOKEN = "___YTDLP_TITLE___";
            const string EXT_TOKEN = "___YTDLP_EXT___";

            name = name.Replace("%(title)s", TITLE_TOKEN, StringComparison.OrdinalIgnoreCase)
                       .Replace("%(ext)s", EXT_TOKEN, StringComparison.OrdinalIgnoreCase);

            name = SanitizeFileName(name);

            name = name.Replace(TITLE_TOKEN, "%(title)s")
                       .Replace(EXT_TOKEN, "%(ext)s");

            if (string.IsNullOrWhiteSpace(name))
                name = "%(title)s";

            return name;
        }

        // Windows-superset of invalid filename chars, applied on every OS so filenames stay
        // portable even when writing to e.g. an NTFS drive from Linux.
        private static readonly char[] InvalidFileNameChars = BuildInvalidFileNameChars();

        private static char[] BuildInvalidFileNameChars()
        {
            var chars = new List<char> { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };
            for (int i = 0; i < 32; i++) chars.Add((char)i);
            return chars.ToArray();
        }

        private static string SanitizeFileName(string name)
        {
            foreach (var c in InvalidFileNameChars)
                name = name.Replace(c, '_');

            // Strip any directory traversal attempts
            name = name.Replace("..", "_");

            name = Regex.Replace(name, @"\s+", " ").Trim();
            name = name.TrimEnd('.', ' ');

            if (string.IsNullOrWhiteSpace(name))
                name = "download";

            return name;
        }

        // ── Args builders ──

        private void AppendFormatArgs(List<string> args, string fileExtension, int qualityHeight)
        {
            string[] extractableAudioFormats = { "mp3", "aac", "m4a", "wav" };
            string[] videoFormats = { "mp4", "mov", "mkv", "flv", "3gp" };

            string cap = qualityHeight > 0 ? $"[height<={qualityHeight}]" : string.Empty;

            if (extractableAudioFormats.Contains(fileExtension))
            {
                args.Add("--extract-audio");
                args.Add("--audio-format"); args.Add(fileExtension);
                args.Add("-f"); args.Add("bestaudio");
            }
            else if (fileExtension == "webm")
            {
                args.Add("-f"); args.Add("bestaudio");
            }
            else if (videoFormats.Contains(fileExtension))
            {
                if (fileExtension == "mp4")
                {
                    if (qualityHeight > 0)
                    {
                        args.Add("-f"); args.Add($"bv*{cap}+ba/b{cap}");
                    }
                    args.Add("-S"); args.Add("vcodec:h264,res,acodec:aac");
                    args.Add("--merge-output-format"); args.Add("mp4");
                }
                else if (fileExtension == "mov")
                {
                    if (qualityHeight > 0)
                    {
                        args.Add("-f"); args.Add($"bv*{cap}+ba/b{cap}");
                    }
                    args.Add("-S"); args.Add("vcodec:h264,res,acodec:aac");
                    args.Add("--merge-output-format"); args.Add("mov");
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

        private void AppendCookiesArgs(List<string> args)
        {
            if (!string.IsNullOrWhiteSpace(cookiesTxtFile) && File.Exists(cookiesTxtFile))
            {
                args.Add("--cookies");
                args.Add(cookiesTxtFile);
            }
        }

        private readonly struct CookiesExpiryReport
        {
            public int Total { get; init; }
            public int Expired { get; init; }
            public int ExpiringSoon { get; init; }
            public int Session { get; init; }
            public DateTime? EarliestUpcomingExpiry { get; init; }
            public bool ParseFailed { get; init; }
        }

        private static CookiesExpiryReport InspectCookiesFile(string path)
        {
            // Netscape cookies.txt: tab-separated, 7 fields. Field 5 (index 4) is a Unix
            // expiry timestamp in seconds; 0 means a session cookie. Lines starting with
            // '#' are comments (except "#HttpOnly_" prefix, which precedes a real entry).
            const int soonWindowDays = 7;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var soonCutoff = now + soonWindowDays * 24L * 60 * 60;

            int total = 0, expired = 0, soon = 0, session = 0;
            long? earliestUpcoming = null;

            try
            {
                foreach (var raw in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    var line = raw.StartsWith("#HttpOnly_", StringComparison.Ordinal) ? raw.Substring("#HttpOnly_".Length) : raw;
                    if (line.StartsWith("#", StringComparison.Ordinal)) continue;

                    var fields = line.Split('\t');
                    if (fields.Length < 7) continue;
                    if (!long.TryParse(fields[4], out var expiry)) continue;

                    total++;
                    if (expiry == 0) { session++; continue; }
                    if (expiry <= now) { expired++; continue; }
                    if (expiry <= soonCutoff) soon++;
                    if (earliestUpcoming is null || expiry < earliestUpcoming) earliestUpcoming = expiry;
                }
            }
            catch
            {
                return new CookiesExpiryReport { ParseFailed = true };
            }

            DateTime? earliest = earliestUpcoming is null
                ? null
                : DateTimeOffset.FromUnixTimeSeconds(earliestUpcoming.Value).LocalDateTime;

            return new CookiesExpiryReport
            {
                Total = total,
                Expired = expired,
                ExpiringSoon = soon,
                Session = session,
                EarliestUpcomingExpiry = earliest,
            };
        }

        private void WarnIfCookiesStale(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

            var report = InspectCookiesFile(path);
            string tooltip = path;

            if (report.ParseFailed)
            {
                AppendOutput($"\n[Cookies] Could not read '{Path.GetFileName(path)}' to check expiry.\n");
                ToolTip.SetTip(btnCookies, tooltip);
                return;
            }

            if (report.Total == 0)
            {
                AppendOutput($"\n[Cookies] '{Path.GetFileName(path)}' contains no cookie entries. Re-export from your browser.\n");
                ToolTip.SetTip(btnCookies, tooltip + "\n(no entries)");
                return;
            }

            var sb = new StringBuilder();
            if (report.Expired > 0)
                sb.Append($"{report.Expired} expired");
            if (report.ExpiringSoon > 0)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append($"{report.ExpiringSoon} expiring within 7 days");
            }

            if (sb.Length > 0)
            {
                AppendOutput(
                    $"\n[Cookies] '{Path.GetFileName(path)}': {sb} (of {report.Total} entries). " +
                    "Stale cookies often cause auth failures. Consider re-exporting.\n");
                tooltip += $"\n⚠ {sb}";
            }
            else if (report.EarliestUpcomingExpiry is DateTime next)
            {
                tooltip += $"\nAll {report.Total} entries valid (earliest expiry: {next:yyyy-MM-dd})";
            }

            ToolTip.SetTip(btnCookies, tooltip);
        }

        private void AppendOutputArg(List<string> args, string fileExtension)
        {
            string ext = fileExtension == "ext" ? "%(ext)s" : fileExtension;
            string folder = NormalizedOutputFolder();
            string nameText = (txtFileName.Text ?? string.Empty).Trim();

            string outputName;
            if (cbThumbnailOnly.IsChecked == true)
            {
                outputName = string.IsNullOrEmpty(nameText)
                    ? Path.Combine(folder, "%(title)s")
                    : Path.Combine(folder, SanitizeFileNameTemplateSafe(nameText));
            }
            else
            {
                outputName = string.IsNullOrEmpty(nameText)
                    ? Path.Combine(folder, $"%(title)s.{ext}")
                    : Path.Combine(folder, $"{SanitizeFileNameTemplateSafe(nameText)}.{ext}");
            }

            args.Add("-o");
            args.Add(outputName);
        }

        private void AppendSegmentArgs(List<string> args, List<SegmentInfo> segments)
        {
            foreach (var seg in segments)
            {
                args.Add("--download-sections");
                args.Add($"*{seg.StartTime}-{seg.EndTime}");
            }
        }

        // ── Process runner ──

        private async Task RunYtDlpAsync(List<string> args, CancellationToken ct)
        {
            if (!File.Exists(ytDlpPath))
            {
                AppendOutput("yt-dlp missing. Trying to fetch it...\n");
                try { await DownloadYtDlpLatestAsync(); }
                catch (Exception ex)
                {
                    AppendOutput("Could not fetch yt-dlp: " + ex.Message + "\n");
                    return;
                }
            }

            var psi = new ProcessStartInfo
            {
                FileName = ytDlpPath,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var process = new Process { StartInfo = psi };

            bool sawHundred = false;
            bool sawMerge = false;
            bool sawThumbnail = false;
            bool sawAlready = false;
            bool sawError = false;
            bool authRequired = false;

            void OnLine(string? data)
            {
                if (string.IsNullOrEmpty(data)) return;

                if (ProgressCompleteRegex.IsMatch(data)) sawHundred = true;
                if (MergerRegex.IsMatch(data)) sawMerge = true;
                if (AlreadyDownloadedRegex.IsMatch(data)) sawAlready = true;
                if (ThumbnailWrittenRegex.IsMatch(data)) sawThumbnail = true;
                if (ThumbnailAlreadyRegex.IsMatch(data)) sawAlready = true;
                if (ErrorRegex.IsMatch(data)) sawError = true;
                if (AuthRequiredRegex.IsMatch(data)) authRequired = true;

                Dispatcher.UIThread.Post(() =>
                {
                    AppendOutput(data + "\n");
                    scrollViewer.ScrollToEnd();
                    ParseOutputLine(data);
                });
            }

            process.OutputDataReceived += (_, a) => OnLine(a.Data);
            process.ErrorDataReceived += (_, a) => OnLine(a.Data);

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var reg = ct.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            });

            await process.WaitForExitAsync(CancellationToken.None);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_currentItem != null && _currentItem.EndTime == default)
                {
                    _currentItem.EndTime = DateTime.Now;
                    _currentItem.Duration = _currentItem.EndTime - _currentItem.StartTime;

                    if (sawAlready) { _currentItem.AlreadyDownloaded = true; _currentItem.Succeeded = true; }
                    else if (sawError) { _currentItem.Succeeded = false; }
                    else if (sawHundred || sawMerge || sawThumbnail) { _currentItem.Succeeded = true; }
                }

                if (_currentSession != null)
                {
                    int observedItems = _currentSession.Items.Count;
                    if (_currentSession.CompletedItems < observedItems)
                    {
                        _currentSession.CompletedItems = observedItems;
                    }
                    // Render at the completion boundary. No in-progress item, so currentPercent is 0.
                    // For multi-URL/playlist runs this keeps the bar at (completed / total) instead of
                    // double-counting and snapping to 100% after every individual item.
                    UpdateProgressBar(0);
                    UpdateStatsPanel();
                }

                if (sawAlready) { AppendOutput("\n✅ File was already downloaded!\n"); UpdateCurrentStatus("Already downloaded"); }
                else if (sawError) { AppendOutput("\n❌ Download failed!\n"); UpdateCurrentStatus("Download failed"); }
                else if (sawHundred || sawMerge || sawThumbnail) { AppendOutput("\n🎉 Download successful!\n"); UpdateCurrentStatus("Download complete!"); }

                scrollViewer.ScrollToEnd();
            });

            if (authRequired && !ct.IsCancellationRequested)
            {
                var result = await MessageDialog.ShowAsync(this,
                    "Authentication Required",
                    "This site requires authentication.\n\n" +
                    "Install a browser extension that can export cookies (e.g. 'Get cookies.txt LOCALLY'), " +
                    "then click 'Cookies' below and select the exported file.\n\n" +
                    "Click OK to open the extension page in your browser, or Cancel to dismiss.",
                    MessageDialogButtons.OKCancel);

                if (result == MessageDialogResult.OK)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(
                            "https://chromewebstore.google.com/detail/cclelndahbckbenkjhflpdbgdldlbecc")
                        { UseShellExecute = true });
                    }
                    catch { }
                }
            }
        }

        private async Task<string> RunYtDlpAndCaptureAsync(List<string> args, CancellationToken ct)
        {
            if (!File.Exists(ytDlpPath)) return string.Empty;

            var psi = new ProcessStartInfo
            {
                FileName = ytDlpPath,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var process = new Process { StartInfo = psi };
            var stdout = new StringBuilder();

            process.OutputDataReceived += (_, a) => { if (a.Data != null) stdout.AppendLine(a.Data); };
            // Drain stderr even though we ignore it; the pipe must be read or the child will block.
            process.ErrorDataReceived += (_, _) => { };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(20));

            try { await process.WaitForExitAsync(linked.Token); }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            }

            return stdout.ToString();
        }

        private void ParseOutputLine(string line)
        {
            if (string.IsNullOrEmpty(line) || _currentItem == null) return;

            Match m;

            m = DestinationRegex.Match(line);
            if (m.Success)
            {
                _currentItem.FileName = Path.GetFileName(m.Groups[1].Value);
                UpdateCurrentStatus($"Downloading: {_currentItem.FileName}");
                return;
            }

            m = ProgressRegex.Match(line);
            if (m.Success)
            {
                if (double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double percent))
                    UpdateProgressBar(percent);
                if (double.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double size))
                    _currentItem.FileSizeMB = ConvertToMB(size, m.Groups[3].Value);
                if (double.TryParse(m.Groups[4].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double speed))
                    _currentItem.SpeedMBps = ConvertToMB(speed, m.Groups[5].Value);
                if (m.Groups[6].Success)
                    txtSpeedEta.Text = $"{m.Groups[4].Value} {m.Groups[5].Value} • ETA {m.Groups[6].Value}";
                return;
            }

            m = ProgressCompleteRegex.Match(line);
            if (m.Success)
            {
                if (double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double size))
                    _currentItem.FileSizeMB = ConvertToMB(size, m.Groups[2].Value);
                UpdateProgressBar(100);
                return;
            }

            m = MergerRegex.Match(line);
            if (m.Success)
            {
                _currentItem.FileName = Path.GetFileName(m.Groups[1].Value);
                UpdateCurrentStatus($"Merging: {_currentItem.FileName}");
                return;
            }

            m = PlaylistProgressRegex.Match(line);
            if (m.Success)
            {
                if (int.TryParse(m.Groups[1].Value, out int current) && int.TryParse(m.Groups[2].Value, out int total))
                {
                    if (_currentSession != null)
                    {
                        if (_currentItem != null && _currentItem.StartTime != default && _currentItem.EndTime == default)
                        {
                            _currentItem.EndTime = DateTime.Now;
                            _currentItem.Duration = _currentItem.EndTime - _currentItem.StartTime;
                            // "Downloading item N" means item N-1 just finished; it only counts
                            // as failed if an ERROR line was seen while it was active. (At N=1
                            // the item being closed is the pre-run placeholder, so skip it.)
                            if (current > 1 && string.IsNullOrEmpty(_currentItem.ErrorMessage))
                                _currentItem.Succeeded = true;
                        }

                        _currentSession.TotalItems = Math.Max(_currentSession.TotalItems, total);
                        _currentSession.CompletedItems = Math.Max(_currentSession.CompletedItems, current - 1);

                        _currentItem = new DownloadItemStats { StartTime = DateTime.Now, Url = _currentQueueEntry?.Url ?? string.Empty };
                        _currentSession.Items.Add(_currentItem);
                    }
                    UpdateCurrentStatus($"Downloading item {current} of {total}");
                }
                return;
            }

            if (AlreadyDownloadedRegex.IsMatch(line) || ThumbnailAlreadyRegex.IsMatch(line))
            {
                if (_currentItem != null) { _currentItem.AlreadyDownloaded = true; _currentItem.Succeeded = true; }
                UpdateCurrentStatus("Already downloaded");
                return;
            }

            m = ThumbnailWrittenRegex.Match(line);
            if (m.Success)
            {
                _currentItem.FileName = Path.GetFileName(m.Groups[1].Value.Trim());
                _currentItem.Succeeded = true;
                UpdateProgressBar(100);
                UpdateCurrentStatus($"Saved thumbnail: {_currentItem.FileName}");
                return;
            }

            m = ErrorRegex.Match(line);
            if (m.Success)
            {
                if (_currentItem != null) { _currentItem.ErrorMessage = m.Groups[1].Value; _currentItem.Succeeded = false; }
                UpdateCurrentStatus($"Error: {m.Groups[1].Value}");
                return;
            }
        }

        private static double ConvertToMB(double value, string unit)
        {
            var u = unit.Trim().TrimEnd('/', 's').ToLowerInvariant();
            return u switch
            {
                "kib" => value / 1024.0,
                "mib" => value,
                "gib" => value * 1024.0,
                "kb" => value / 1000.0,
                "mb" => value,
                "gb" => value * 1000.0,
                _ => value
            };
        }

        private void UpdateProgressBar(double currentPercent)
        {
            double overallPercent = currentPercent;
            if (_currentSession != null && _currentSession.TotalItems > 1)
                overallPercent = (_currentSession.CompletedItems * 100.0 + currentPercent) / _currentSession.TotalItems;
            overallPercent = Math.Max(0, Math.Min(100, overallPercent));

            txtProgressPercent.Text = $"{overallPercent:F0}%";

            if (progressBarFill.Parent is Border parent && parent.Bounds.Width > 0)
                progressBarFill.Width = parent.Bounds.Width * (overallPercent / 100.0);
        }

        private void UpdateCurrentStatus(string text) => txtCurrentStatus.Text = text;

        private void ResetShowInfoPanel()
        {
            txtProgressPercent.Text = "0%";
            progressBarFill.Width = 0;
            txtCurrentStatus.Text = "Starting download...";
            pnlStats.Children.Clear();
        }

        private void UpdateStatsPanel()
        {
            if (_currentSession == null) return;

            pnlStats.Children.Clear();

            AddStatHeader("Summary");
            AddStatRow("Total Files", _currentSession.CompletedItems.ToString());
            if (_currentSession.SuccessCount > 0)
                AddStatRow("Succeeded", _currentSession.SuccessCount.ToString(), "#4ecdc4");
            if (_currentSession.FailCount > 0)
                AddStatRow("Failed", _currentSession.FailCount.ToString(), "#ff3c3c");
            if (_currentSession.AlreadyDownloadedCount > 0)
                AddStatRow("Already Downloaded", _currentSession.AlreadyDownloadedCount.ToString(), "#888888");

            if (_currentSession.TotalSizeMB > 0)
            {
                AddStatSeparator();
                AddStatHeader("Size");
                AddStatRow("Total Size", FormatSize(_currentSession.TotalSizeMB));
                if (_currentSession.LargestFile != null && _currentSession.Items.Count(i => i.FileSizeMB > 0) > 1)
                {
                    AddStatRow("Largest", $"{FormatSize(_currentSession.LargestFile.FileSizeMB)} - {_currentSession.LargestFile.FileName}");
                    if (_currentSession.SmallestFile != null)
                        AddStatRow("Smallest", $"{FormatSize(_currentSession.SmallestFile.FileSizeMB)} - {_currentSession.SmallestFile.FileName}");
                }
            }

            AddStatSeparator();
            AddStatHeader("Performance");
            if (_currentSession.AverageSpeedMBps > 0)
                AddStatRow("Avg Speed", $"{_currentSession.AverageSpeedMBps:F2} MB/s");
            AddStatRow("Total Time", FormatDuration(_currentSession.TotalDuration));
            if (_currentSession.Items.Count > 1 && _currentSession.AverageItemDuration > TimeSpan.Zero)
                AddStatRow("Avg Per File", FormatDuration(_currentSession.AverageItemDuration));

            if (_currentSession.Items.Count > 1)
            {
                AddStatSeparator();
                AddStatHeader("Files");
                foreach (var item in _currentSession.Items)
                {
                    string status;
                    string color;
                    if (item.AlreadyDownloaded) { status = "[SKIP]"; color = "#888888"; }
                    else if (item.Succeeded) { status = "[OK]"; color = "#4ecdc4"; }
                    else if (item.EndTime == default) { status = "[…]"; color = "#888888"; }
                    else { status = "[FAIL]"; color = "#ff3c3c"; }

                    var name = string.IsNullOrEmpty(item.FileName) ? "Unknown" : item.FileName;
                    AddStatRow(status, name, color);
                }
            }
        }

        private void AddStatHeader(string text)
        {
            pnlStats.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = ResBrush("TextPrimary", "#e1e1e1"),
                FontFamily = JetBrainsFont,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 4, 0, 4)
            });
        }

        private void AddStatRow(string label, string value, string? color = null)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

            var lblBlock = new TextBlock
            {
                Text = label,
                Foreground = color != null
                    ? new SolidColorBrush(Color.Parse(color))
                    : ResBrush("TextSecondary", "#888888"),
                FontFamily = JetBrainsFont,
                FontSize = 11,
                Margin = new Thickness(0, 1, 12, 1)
            };
            Grid.SetColumn(lblBlock, 0);

            var valBlock = new TextBlock
            {
                Text = value,
                Foreground = ResBrush("TextPrimary", "#e1e1e1"),
                FontFamily = JetBrainsFont,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 1, 0, 1)
            };
            Grid.SetColumn(valBlock, 1);

            grid.Children.Add(lblBlock);
            grid.Children.Add(valBlock);
            pnlStats.Children.Add(grid);
        }

        private void AddStatSeparator()
        {
            pnlStats.Children.Add(new Border
            {
                Height = 1,
                Background = ResBrush("BorderSubtle", "#333333"),
                Margin = new Thickness(0, 6, 0, 6)
            });
        }

        // ── Queue panel ──

        private void UpdateQueuePanel()
        {
            if (queuePanel == null) return;
            queuePanel.Children.Clear();

            foreach (var entry in _queue)
            {
                var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
                grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

                var dot = new Border
                {
                    Width = 10,
                    Height = 10,
                    CornerRadius = new CornerRadius(5),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                    Background = entry.Status == QueueStatus.Downloading
                        ? ResBrush("AccentColor", "#ff3c3c")
                        : new SolidColorBrush(Color.Parse(QueueStatusColor(entry.Status))),
                };
                Grid.SetColumn(dot, 0);

                var textStack = new StackPanel();
                textStack.Children.Add(new TextBlock
                {
                    Text = entry.Title,
                    Foreground = ResBrush("TextPrimary", "#e1e1e1"),
                    FontFamily = JetBrainsFont,
                    FontSize = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                textStack.Children.Add(new TextBlock
                {
                    Text = QueueStatusText(entry),
                    Foreground = ResBrush("TextSecondary", "#888888"),
                    FontFamily = JetBrainsFont,
                    FontSize = 10,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                Grid.SetColumn(textStack, 1);

                grid.Children.Add(dot);
                grid.Children.Add(textStack);

                Button? action = entry.Status switch
                {
                    QueueStatus.Downloading => MakeSmallButton("✕"),
                    QueueStatus.Pending => MakeSmallButton("✕"),
                    QueueStatus.Failed => MakeSmallButton("↻"),
                    _ => null,
                };
                if (action != null)
                {
                    var captured = entry;
                    if (entry.Status == QueueStatus.Failed)
                        action.Click += (_, _) => RetryEntry(captured);
                    else
                        action.Click += (_, _) => CancelEntry(captured);
                    Grid.SetColumn(action, 2);
                    grid.Children.Add(action);
                }

                queuePanel.Children.Add(grid);
            }
        }

        private static string QueueStatusColor(QueueStatus status) => status switch
        {
            QueueStatus.Downloading => "#ff3c3c",
            QueueStatus.Succeeded => "#4ecdc4",
            QueueStatus.Failed => "#ff3c3c",
            _ => "#888888", // Pending, Skipped, Canceled
        };

        private static string QueueStatusText(QueueEntry entry) => entry.Status switch
        {
            QueueStatus.Failed when !string.IsNullOrEmpty(entry.ErrorMessage) => $"Failed: {entry.ErrorMessage}",
            _ => entry.Status.ToString(),
        };

        private Button MakeSmallButton(string content) => new Button
        {
            Content = content,
            Classes = { "Secondary" },
            FontFamily = JetBrainsFont,
            Foreground = ResBrush("TextPrimary", "#e1e1e1"),
            Height = 24,
            Padding = new Thickness(8, 0),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        private void CancelEntry(QueueEntry entry)
        {
            if (entry.Status == QueueStatus.Downloading)
            {
                try { entry.ItemCts?.Cancel(); } catch { }
            }
            else if (entry.Status == QueueStatus.Pending)
            {
                entry.Status = QueueStatus.Canceled;
                UpdateQueuePanel();
            }
        }

        // Retry reuses the same run wrapper as Download_Click, with a run of just this entry.
        private async void RetryEntry(QueueEntry entry)
        {
            if (_isDownloading || entry.Status != QueueStatus.Failed) return;
            entry.Status = QueueStatus.Pending;
            entry.ErrorMessage = "";
            UpdateQueuePanel();
            await RunEntriesAsync(new List<QueueEntry> { entry }, singleNaming: cbMultiDownload.IsChecked != true);
        }

        private void ClearFinished_Click(object? sender, RoutedEventArgs e)
        {
            _queue.RemoveAll(entry => entry.Status is QueueStatus.Succeeded or QueueStatus.Skipped
                or QueueStatus.Failed or QueueStatus.Canceled);
            UpdateQueuePanel();
        }

        // ── History panel ──

        private void UpdateHistoryPanel()
        {
            if (historyPanel == null) return;
            historyPanel.Children.Clear();

            foreach (var entry in _history)
            {
                var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
                grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

                var dot = new Border
                {
                    Width = 10,
                    Height = 10,
                    CornerRadius = new CornerRadius(5),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                    Background = new SolidColorBrush(Color.Parse(entry.Succeeded ? "#4ecdc4" : "#ff3c3c")),
                };
                Grid.SetColumn(dot, 0);

                var textStack = new StackPanel();
                textStack.Children.Add(new TextBlock
                {
                    Text = entry.Title,
                    Foreground = ResBrush("TextPrimary", "#e1e1e1"),
                    FontFamily = JetBrainsFont,
                    FontSize = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                textStack.Children.Add(new TextBlock
                {
                    Text = entry.When.ToString("yyyy-MM-dd HH:mm"),
                    Foreground = ResBrush("TextSecondary", "#888888"),
                    FontFamily = JetBrainsFont,
                    FontSize = 10,
                });
                Grid.SetColumn(textStack, 1);

                grid.Children.Add(dot);
                grid.Children.Add(textStack);

                var captured = entry;
                var folderButton = MakeSmallButton("Folder");
                folderButton.Click += (_, _) => OpenOutputFolder();
                Grid.SetColumn(folderButton, 2);
                grid.Children.Add(folderButton);

                var retryButton = MakeSmallButton("↻");
                retryButton.Click += (_, _) => ReDownload(captured);
                Grid.SetColumn(retryButton, 3);
                grid.Children.Add(retryButton);

                historyPanel.Children.Add(grid);
            }
        }

        private void OpenOutputFolder()
        {
            var folder = NormalizedOutputFolder();
            if (!Directory.Exists(folder)) return;
            try
            {
                if (OperatingSystem.IsWindows())
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
                else if (OperatingSystem.IsMacOS())
                    Process.Start("open", $"\"{folder}\"");
                else
                    Process.Start("xdg-open", $"\"{folder}\"");
            }
            catch { /* best effort */ }
        }

        // Loads the URL back into the single-download box; does not start the download.
        private void ReDownload(HistoryEntry entry)
        {
            cbMultiDownload.IsChecked = false;
            txtURL.Text = entry.Url;
            rbShowInfo.IsChecked = true;
        }

        private void ClearHistory_Click(object? sender, RoutedEventArgs e)
        {
            _history.Clear();
            SaveHistory();
            UpdateHistoryPanel();
        }

        private static string FormatSize(double mb)
        {
            if (mb >= 1024) return $"{mb / 1024.0:F2} GB";
            if (mb >= 1) return $"{mb:F2} MB";
            return $"{mb * 1024.0:F0} KB";
        }

        private static string FormatDuration(TimeSpan ts)
        {
            if (ts.TotalHours >= 1) return $"{ts.Hours}h {ts.Minutes}m {ts.Seconds}s";
            if (ts.TotalMinutes >= 1) return $"{ts.Minutes}m {ts.Seconds}s";
            return $"{ts.TotalSeconds:F1}s";
        }

        // ── Segment Download ──

        private void AddSegmentRow()
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            row.ColumnDefinitions.Add(new ColumnDefinition(20, GridUnitType.Pixel));
            row.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            row.ColumnDefinitions.Add(new ColumnDefinition(28, GridUnitType.Pixel));

            var startBox = CreateTimeTextBox();
            Grid.SetColumn(startBox, 0);

            var sep = new TextBlock
            {
                Text = "-",
                Foreground = ResBrush("TextSecondary", "#888888"),
                FontFamily = JetBrainsFont,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(sep, 1);

            var endBox = CreateTimeTextBox();
            Grid.SetColumn(endBox, 2);

            var removeBtn = new Button
            {
                Content = "×",
                Foreground = ResBrush("TextSecondary", "#888888"),
                FontFamily = JetBrainsFont,
                FontSize = 14,
                Margin = new Thickness(4, 0, 0, 0),
            };
            removeBtn.Classes.Add("Secondary"); // WPF "FilePathButton" style
            ToolTip.SetTip(removeBtn, "Remove segment");
            removeBtn.Click += (_, _) =>
            {
                pnlSegmentRows.Children.Remove(row);
                UpdateTimelineVisualization();
            };
            Grid.SetColumn(removeBtn, 3);

            row.Tag = new[] { startBox, endBox };
            row.Children.Add(startBox);
            row.Children.Add(sep);
            row.Children.Add(endBox);
            row.Children.Add(removeBtn);

            pnlSegmentRows.Children.Add(row);
        }

        private TextBox CreateTimeTextBox()
        {
            var tb = new TextBox
            {
                Height = 26,
                FontSize = 11,
                FontFamily = JetBrainsFont,
                Foreground = ResBrush("TextPrimary", "#e1e1e1"),
                CaretBrush = ResBrush("TextSecondary", "#888888"),
                SelectionBrush = ResBrush("AccentColor", "#ff3c3c"),
                MaxLength = 8,
                TextAlignment = TextAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            tb.Classes.Add("SegmentTime"); // WPF "SegmentTimeTextBox" style
            tb.TextChanged += SegmentTime_TextChanged;
            return tb;
        }

        private void SegmentTime_TextChanged(object? sender, TextChangedEventArgs e)
        {
            if (sender is not TextBox tb) return;
            double seconds = ParseTimeToSeconds(tb.Text ?? string.Empty);
            tb.BorderBrush = (string.IsNullOrWhiteSpace(tb.Text) || seconds >= 0)
                ? ResBrush("BorderSubtle", "#333333")
                : ResBrush("AccentColor", "#ff3c3c");

            UpdateTimelineVisualization();
        }

        private async void btnFetchDuration_Click(object? sender, RoutedEventArgs e)
        {
            var url = txtURL.Text?.Trim();
            if (string.IsNullOrWhiteSpace(url) || url.Length < 8)
            {
                txtSegmentDuration.Text = "Duration: enter a URL first";
                return;
            }

            btnFetchDuration.IsEnabled = false;
            btnFetchDuration.Content = "Fetching...";
            txtSegmentDuration.Text = "Duration: fetching...";

            try
            {
                var args = new List<string>
                {
                    "--print", "duration",
                    "--print", "title",
                    "--print", "thumbnail",
                    "--no-download",
                };
                AppendCookiesArgs(args);
                args.Add(url);

                var metaOutput = await RunYtDlpAndCaptureAsync(args, CancellationToken.None);
                var lines = metaOutput.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

                if (lines.Length >= 1 && double.TryParse(lines[0], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double duration) && duration > 0)
                {
                    _videoDurationSeconds = duration;
                    _videoTitle = lines.Length >= 2 ? lines[1] : string.Empty;
                    txtSegmentDuration.Text = $"Duration: {FormatDuration(duration)}";
                    pnlTimeline.IsVisible = true;
                    UpdateTimelineVisualization();

                    if (!string.IsNullOrWhiteSpace(_videoTitle))
                    {
                        txtVideoTitle.Text = _videoTitle;
                        txtVideoTitle.IsVisible = true;
                    }

                    if (lines.Length >= 3 && !string.IsNullOrWhiteSpace(lines[2]))
                        await LoadThumbnailAsync(lines[2]);
                }
                else
                {
                    _videoDurationSeconds = 0;
                    txtSegmentDuration.Text = "Duration: unknown";
                    pnlTimeline.IsVisible = false;
                }
            }
            catch (Exception ex)
            {
                txtSegmentDuration.Text = "Duration: error";
                pnlTimeline.IsVisible = false;
                Debug.WriteLine("Fetch duration failed: " + ex);
            }
            finally
            {
                btnFetchDuration.IsEnabled = true;
                btnFetchDuration.Content = "Fetch Info";
            }
        }

        private async Task LoadThumbnailAsync(string thumbnailUrl)
        {
            try
            {
                var bytes = await SharedHttp.GetByteArrayAsync(thumbnailUrl);
                if (bytes.Length == 0) return;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    try
                    {
                        imgThumbnail.Source = new Bitmap(new MemoryStream(bytes));
                        bdrThumbnail.IsVisible = true;
                    }
                    catch { }
                });
            }
            catch
            {
                // Best-effort fallback: ask yt-dlp to convert to PNG locally.
                try
                {
                    var tempDir = Path.Combine(Path.GetTempPath(), "kleptos_thumb");
                    Directory.CreateDirectory(tempDir);

                    var args = new List<string>
                    {
                        "--skip-download",
                        "--write-thumbnail",
                        "--convert-thumbnails", "png",
                        "-o", Path.Combine(tempDir, "thumb"),
                        "--no-mtime",
                        txtURL.Text?.Trim() ?? string.Empty,
                    };
                    await RunYtDlpAndCaptureAsync(args, CancellationToken.None);

                    var thumbFile = Directory.GetFiles(tempDir, "thumb*.png").FirstOrDefault();
                    if (thumbFile != null && File.Exists(thumbFile))
                    {
                        var bytes = await File.ReadAllBytesAsync(thumbFile);
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            try
                            {
                                imgThumbnail.Source = new Bitmap(new MemoryStream(bytes));
                                bdrThumbnail.IsVisible = true;
                            }
                            catch { }
                        });
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
                catch { }
            }
        }

        private static string FormatDuration(double totalSeconds)
        {
            var ts = TimeSpan.FromSeconds(totalSeconds);
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{ts.Minutes}:{ts.Seconds:D2}";
        }

        private void UpdateTimelineVisualization()
        {
            if (cnvTimeline == null) return;
            cnvTimeline.Children.Clear();

            if (_videoDurationSeconds <= 0) return;

            double timelineWidth = pnlTimeline.Bounds.Width;
            double timelineHeight = pnlTimeline.Bounds.Height;
            if (timelineWidth <= 0) timelineWidth = 200;
            if (timelineHeight <= 0) timelineHeight = 16;

            if (pnlSegmentRows.Children.Count == 0) return;

            foreach (var child in pnlSegmentRows.Children)
            {
                if (child is not Grid row || row.Tag is not TextBox[] boxes) continue;

                double startSec = ParseTimeToSeconds(boxes[0].Text ?? string.Empty);
                double endSec = ParseTimeToSeconds(boxes[1].Text ?? string.Empty);

                if (startSec >= 0 && endSec >= 0 && startSec < endSec)
                {
                    double left = (startSec / _videoDurationSeconds) * timelineWidth;
                    double width = ((endSec - startSec) / _videoDurationSeconds) * timelineWidth;

                    var rect = new Rectangle
                    {
                        Width = Math.Max(2, width),
                        Height = 8,
                        RadiusX = 2,
                        RadiusY = 2,
                        Fill = new SolidColorBrush(Color.FromArgb(100, 255, 60, 60)),
                    };
                    Canvas.SetLeft(rect, Math.Max(0, left));
                    Canvas.SetTop(rect, (timelineHeight - 8) / 2);
                    cnvTimeline.Children.Add(rect);
                }

                if (startSec >= 0)
                {
                    double x = (startSec / _videoDurationSeconds) * timelineWidth;
                    AddMarker(x, timelineHeight, Color.FromRgb(76, 175, 80));
                }
                if (endSec >= 0)
                {
                    double x = (endSec / _videoDurationSeconds) * timelineWidth;
                    AddMarker(x, timelineHeight, Color.FromRgb(255, 60, 60));
                }
            }
        }

        private void AddMarker(double x, double timelineHeight, Color color)
        {
            var line = new Rectangle
            {
                Width = 2,
                Height = timelineHeight,
                Fill = new SolidColorBrush(color),
            };
            Canvas.SetLeft(line, Math.Max(0, x - 1));
            Canvas.SetTop(line, 0);
            cnvTimeline.Children.Add(line);

            var triangle = new Polygon
            {
                Points = new Points
                {
                    new Point(0, 0),
                    new Point(8, 0),
                    new Point(4, 5),
                },
                Fill = new SolidColorBrush(color),
            };
            Canvas.SetLeft(triangle, Math.Max(0, x - 4));
            Canvas.SetTop(triangle, 0);
            cnvTimeline.Children.Add(triangle);
        }

        private double GetTimelineSeconds(double mouseX)
        {
            double width = pnlTimeline.Bounds.Width;
            if (width <= 0) return 0;
            double fraction = Math.Clamp(mouseX / width, 0, 1);
            return fraction * _videoDurationSeconds;
        }

        private static string FormatTimeInput(double seconds)
        {
            var ts = TimeSpan.FromSeconds((int)seconds);
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{ts.Minutes}:{ts.Seconds:D2}";
        }

        private void Timeline_MouseMove(object? sender, PointerEventArgs e)
        {
            if (_videoDurationSeconds <= 0) return;

            double x = e.GetPosition(pnlTimeline).X;
            double seconds = GetTimelineSeconds(x);

            txtSeekTimestamp.Text = FormatTimeInput(seconds);

            // Clamp the popup so it doesn't fall off the left/right edge.
            double width = pnlTimeline.Bounds.Width;
            double offset = Math.Clamp(x - 30, 0, Math.Max(0, width - 60));
            popSeekPreview.HorizontalOffset = offset;

            if (!popSeekPreview.IsOpen) popSeekPreview.IsOpen = true;
        }

        private void Timeline_MouseLeave(object? sender, PointerEventArgs e)
        {
            popSeekPreview.IsOpen = false;
        }

        // WPF had separate MouseLeftButtonDown / MouseRightButtonDown events; Avalonia
        // only has PointerPressed, so this dispatcher keeps the original handler names.
        private void Timeline_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(pnlTimeline).Properties.IsRightButtonPressed)
                Timeline_MouseRightButtonDown(sender, e);
            else
                Timeline_MouseLeftButtonDown(sender, e);
        }

        private void Timeline_MouseLeftButtonDown(object? sender, PointerPressedEventArgs e)
        {
            if (_videoDurationSeconds <= 0) return;
            double seconds = GetTimelineSeconds(e.GetPosition(pnlTimeline).X);
            SetActiveSegmentTime(isStart: true, FormatTimeInput(seconds));
        }

        private void Timeline_MouseRightButtonDown(object? sender, PointerPressedEventArgs e)
        {
            if (_videoDurationSeconds <= 0) return;
            double seconds = GetTimelineSeconds(e.GetPosition(pnlTimeline).X);
            SetActiveSegmentTime(isStart: false, FormatTimeInput(seconds));
            e.Handled = true;
        }

        private void SetActiveSegmentTime(bool isStart, string timeText)
        {
            if (pnlSegmentRows.Children.Count == 0) AddSegmentRow();

            // Edit the last row (the one without both fields filled, or the most recent).
            Grid? row = pnlSegmentRows.Children[pnlSegmentRows.Children.Count - 1] as Grid;
            if (row?.Tag is not TextBox[] boxes) return;

            if (isStart) boxes[0].Text = timeText;
            else boxes[1].Text = timeText;

            UpdateTimelineVisualization();
        }

        private static double ParseTimeToSeconds(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return -1;
            input = input.Trim();

            var parts = input.Split(':');
            if (parts.Length == 3 &&
                int.TryParse(parts[0], out int h) &&
                int.TryParse(parts[1], out int m) &&
                int.TryParse(parts[2], out int s))
            {
                return h * 3600 + m * 60 + s;
            }
            if (parts.Length == 2 &&
                int.TryParse(parts[0], out int m2) &&
                int.TryParse(parts[1], out int s2))
            {
                return m2 * 60 + s2;
            }

            if (double.TryParse(input, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double sec) && sec >= 0)
            {
                return sec;
            }

            return -1;
        }

        private List<SegmentInfo> ParseSegments(out string error)
        {
            error = string.Empty;
            var segments = new List<SegmentInfo>();

            for (int i = 0; i < pnlSegmentRows.Children.Count; i++)
            {
                if (pnlSegmentRows.Children[i] is not Grid row || row.Tag is not TextBox[] boxes) continue;
                string startText = (boxes[0].Text ?? string.Empty).Trim();
                string endText = (boxes[1].Text ?? string.Empty).Trim();

                if (string.IsNullOrWhiteSpace(startText) && string.IsNullOrWhiteSpace(endText))
                    continue;

                if (string.IsNullOrWhiteSpace(startText) || string.IsNullOrWhiteSpace(endText))
                {
                    error = $"Segment #{i + 1}: both start and end times are required.";
                    return new List<SegmentInfo>();
                }

                double startSec = ParseTimeToSeconds(startText);
                double endSec = ParseTimeToSeconds(endText);

                if (startSec < 0) { error = $"Segment #{i + 1}: invalid start time \"{startText}\"."; return new List<SegmentInfo>(); }
                if (endSec < 0) { error = $"Segment #{i + 1}: invalid end time \"{endText}\"."; return new List<SegmentInfo>(); }
                if (startSec >= endSec) { error = $"Segment #{i + 1}: start time must be before end time."; return new List<SegmentInfo>(); }

                segments.Add(new SegmentInfo { StartTime = startText, EndTime = endText });
            }

            if (segments.Count == 0)
                error = "No valid segments defined. Add at least one segment with start and end times.";

            return segments;
        }

        private void OutputToggle_Checked(object? sender, RoutedEventArgs e)
        {
            if (pnlShowInfo == null || pnlConsole == null || pnlSegmentsTab == null || pnlQueue == null || pnlHistory == null) return;

            pnlShowInfo.IsVisible = false;
            pnlConsole.IsVisible = false;
            pnlSegmentsTab.IsVisible = false;
            pnlQueue.IsVisible = false;
            pnlHistory.IsVisible = false;

            // Avalonia raises Checked BEFORE the group manager unchecks the previous
            // RadioButton, so decide from the sender rather than sibling IsChecked state.
            if (ReferenceEquals(sender, rbShowInfo))
            {
                pnlShowInfo.IsVisible = true;
            }
            else if (ReferenceEquals(sender, rbConsole))
            {
                pnlConsole.IsVisible = true;
            }
            else if (ReferenceEquals(sender, rbSegments))
            {
                pnlSegmentsTab.IsVisible = true;
                if (pnlSegmentRows.Children.Count == 0)
                    AddSegmentRow();
            }
            else if (ReferenceEquals(sender, rbQueue))
            {
                pnlQueue.IsVisible = true;
            }
            else if (ReferenceEquals(sender, rbHistory))
            {
                pnlHistory.IsVisible = true;
            }
        }

        private void Help_Click(object? sender, RoutedEventArgs e)
        {
            // Toggle: the ? button both opens and closes the help page
            pnlHelp.IsVisible = !pnlHelp.IsVisible;
            pnlMain.IsVisible = !pnlHelp.IsVisible;
        }

        private void HelpBack_Click(object? sender, RoutedEventArgs e)
        {
            pnlHelp.IsVisible = false;
            pnlMain.IsVisible = true;
        }

        private void Support_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(
                    "https://buymeacoffee.com/ghillie")
                { UseShellExecute = true });
            }
            catch { }
        }

        private void HelpExtension_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(
                    "https://chromewebstore.google.com/detail/cclelndahbckbenkjhflpdbgdldlbecc")
                { UseShellExecute = true });
            }
            catch { }
        }

        private async void FileOutput_Click(object? sender, RoutedEventArgs e)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select Output Folder",
                AllowMultiple = false,
            });

            if (folders.Count > 0 && !string.IsNullOrEmpty(folders[0].TryGetLocalPath()))
            {
                txtFileOutput.Text = folders[0].TryGetLocalPath();
            }
            else
            {
                SetDefaultOutputLocation();
            }
        }

        private void SetDefaultOutputLocation()
        {
            if (string.IsNullOrEmpty(txtFileOutput.Text))
            {
                string downloadsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                txtFileOutput.Text = downloadsPath;
            }
        }

        // ── yt-dlp update ──

        private async Task CheckForYTDLPUpdateAsync()
        {
            try
            {
                AppendOutput("Checking yt-dlp version...\n");

                string? currentVersion = await GetCurrentYtDlpVersionAsync();

                string? latestVersion = await GetLatestYtDlpVersionAsync();
                if (string.IsNullOrEmpty(latestVersion))
                {
                    AppendOutput("Could not fetch latest yt-dlp version (offline?).\n");
                    return;
                }

                if (string.IsNullOrEmpty(currentVersion) || currentVersion != latestVersion)
                {
                    AppendOutput($"Updating yt-dlp → {latestVersion}...\n");
                    await DownloadYtDlpUpdateAsync(latestVersion);
                    AppendOutput($"yt-dlp updated to {latestVersion}.\n");
                }
                else
                {
                    AppendOutput($"yt-dlp is up-to-date ({currentVersion}).\n");
                }
            }
            catch (Exception ex)
            {
                AppendOutput("yt-dlp update check failed: " + ex.Message + "\n");
            }
        }

        private async Task<string?> GetCurrentYtDlpVersionAsync()
        {
            if (!File.Exists(ytDlpPath)) return null;

            var psi = new ProcessStartInfo
            {
                FileName = ytDlpPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--version");

            using var process = new Process { StartInfo = psi };
            process.Start();
            // Drain stderr to avoid pipe block.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync();

            return process.ExitCode == 0 ? stdoutTask.Result.Trim() : null;
        }

        private async Task<string?> GetLatestYtDlpVersionAsync()
        {
            try
            {
                using var resp = await SharedHttp.GetAsync(GitHubLatestApi);
                if (!resp.IsSuccessStatusCode) return null;

                using var stream = await resp.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);

                if (doc.RootElement.TryGetProperty("tag_name", out var tag))
                    return tag.GetString();
                return null;
            }
            catch
            {
                return null;
            }
        }

        private async Task DownloadYtDlpUpdateAsync(string version)
        {
            string downloadUrl = $"https://github.com/yt-dlp/yt-dlp/releases/download/{version}/{PlatformHelper.YtDlpAssetName}";
            string tempPath = Path.Combine(Path.GetTempPath(), "kleptos_yt-dlp.new");
            string backupPath = ytDlpPath + ".old";

            using (var resp = await SharedHttp.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(tempPath);
                await resp.Content.CopyToAsync(fs);
            }

            // Verify the binary is at least non-empty and has the right executable magic
            // for this OS (PE on Windows, Mach-O on macOS, ELF on Linux).
            if (!PlatformHelper.IsValidYtDlpBinary(tempPath))
                throw new InvalidOperationException($"Downloaded {PlatformHelper.YtDlpAssetName} looks truncated or is not a valid executable.");

            // Atomic replace if the destination exists, otherwise just move.
            if (File.Exists(ytDlpPath))
            {
                if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        File.Replace(tempPath, ytDlpPath, backupPath, ignoreMetadataErrors: true);
                        try { File.Delete(backupPath); } catch { }
                    }
                    catch
                    {
                        // File.Replace can be flaky (locked dest, cross-fs); fall back to a plain move.
                        try { File.Move(tempPath, ytDlpPath, overwrite: true); }
                        catch { try { File.Delete(tempPath); } catch { } throw; }
                    }
                }
                else
                {
                    File.Move(tempPath, ytDlpPath, overwrite: true);
                }
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ytDlpPath)!);
                File.Move(tempPath, ytDlpPath);
            }

            // Freshly downloaded binaries lose the exec bit on Unix.
            PlatformHelper.MakeExecutable(ytDlpPath);
        }

        private async Task DownloadYtDlpLatestAsync()
        {
            var latest = await GetLatestYtDlpVersionAsync();
            if (string.IsNullOrEmpty(latest)) throw new InvalidOperationException("Could not look up latest yt-dlp version.");
            await DownloadYtDlpUpdateAsync(latest);
        }

        // ── Kleptos auto-update via Velopack ──

        private async Task CheckForKleptosUpdateAsync()
        {
            try
            {
                var manager = new UpdateManager(GetGithubSource());

                if (!manager.IsInstalled)
                {
                    // Portable build: skip update check silently (the modal-on-startup was disruptive).
                    return;
                }

                var updateInfo = await manager.CheckForUpdatesAsync();
                var currentVersion = manager.CurrentVersion;
                var targetVersion = updateInfo?.TargetFullRelease?.Version;

                Debug.WriteLine($"Kleptos update probe: installed={currentVersion}, candidate={targetVersion?.ToString() ?? "<none>"}");

                // Only flag as "Update Available" when the candidate is strictly newer than what's installed.
                // Velopack sometimes returns an UpdateInfo for equal-version reinstalls, which would otherwise
                // pin the badge on forever.
                if (updateInfo == null || targetVersion == null || currentVersion == null) return;
                if (targetVersion <= currentVersion) return;

                hasUpdate = true;
                ManageUpdateButton();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Kleptos update check failed: " + ex);
            }
        }

        private static async Task UpdateKleptosAsync()
        {
            ILogger logger = new FileLogger(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Kleptos", "log.txt"));

            try
            {
                var manager = new UpdateManager(GetGithubSource());
                var updateInfo = await manager.CheckForUpdatesAsync();
                if (updateInfo == null) return;

                var currentVersion = manager.CurrentVersion;
                var targetVersion = updateInfo.TargetFullRelease?.Version;
                if (targetVersion == null || currentVersion == null || targetVersion <= currentVersion) return;

                await manager.DownloadUpdatesAsync(updateInfo);
                manager.ApplyUpdatesAndRestart(updateInfo);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during update process: {Message}", ex.Message);
                throw;
            }
        }

        private static GithubSource GetGithubSource() => new(
            repoUrl: "https://github.com/Ghilliexyz/Kleptos",
            accessToken: null,
            prerelease: false);

        // ── Window chrome ──

        private void MinimizeButton_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();

        private void TitleBar_MouseLeftButtonDown(object? sender, PointerPressedEventArgs e)
        {
            // WPF: DragMove on left-button press.
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        }

        private async void CopyLogs_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard != null)
                    await clipboard.SetTextAsync(txtOutput.Text ?? string.Empty);
            }
            catch { }
        }

        private async void Update_Click(object? sender, RoutedEventArgs e)
        {
            UpdateButton.IsEnabled = false;
            var textBlock = (TextBlock)UpdateButton.Content!;
            textBlock.Text = "Downloading update...";

            try
            {
                await Task.Run(UpdateKleptosAsync);
            }
            catch (Exception ex)
            {
                await MessageDialog.ShowAsync(this, "Update Error", "Update failed: " + ex.Message, MessageDialogButtons.OK);
                textBlock.Text = "Update Available";
                UpdateButton.IsEnabled = true;
            }
        }

        private void ManageUpdateButton()
        {
            UpdateButton.IsVisible = hasUpdate;
        }

        private async void FileCookies_Click(object? sender, RoutedEventArgs e)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select cookies file",
                AllowMultiple = false,
                FileTypeFilter = new[] { FilePickerFileTypes.All },
            });

            if (files.Count > 0 && !string.IsNullOrEmpty(files[0].TryGetLocalPath()))
            {
                cookiesTxtFile = files[0].TryGetLocalPath()!;
                btnCookies.Content = "Cookies: " + Path.GetFileName(cookiesTxtFile);
                ToolTip.SetTip(btnCookies, cookiesTxtFile);
                WarnIfCookiesStale(cookiesTxtFile);
            }
        }

        private void ShowError(string title, Exception ex)
        {
            try
            {
                AppendOutput($"\n[{title}] {ex.Message}\n");
                _ = MessageDialog.ShowAsync(this, title, ex.Message, MessageDialogButtons.OK);
            }
            catch { }
        }
    }

    internal class FileLogger : ILogger
    {
        private readonly string filePath;

        public FileLogger(string filePath)
        {
            this.filePath = filePath;
            try { Directory.CreateDirectory(Path.GetDirectoryName(filePath)!); } catch { }
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null!;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            try
            {
                var message = formatter(state, exception);
                File.AppendAllText(filePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
