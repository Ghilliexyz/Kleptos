using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Velopack;
using Velopack.Sources;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Point = System.Windows.Point;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

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
    }

    public partial class MainWindow : Window
    {
        private const string YtDlpFileName = "yt-dlp.exe";
        private const string GitHubLatestApi = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";

        // Single shared HttpClient for the lifetime of the app.
        private static readonly HttpClient SharedHttp = CreateHttpClient();
        private static HttpClient CreateHttpClient()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("Kleptos/1.0 (+https://github.com/Ghilliexyz/Kleptos)");
            return h;
        }

        private string ytDlpPath = YtDlpFileName;
        private string cookiesTxtFile = string.Empty;
        private bool hasUpdate = false;
        private CancellationTokenSource? _currentCts;
        private bool _isDownloading;
        private System.Windows.Forms.NotifyIcon? _trayIcon;

        private static readonly Regex UrlRegex =
            new Regex(@"https?://\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ProgressRegex =
            new Regex(@"\[download\]\s+([\d.]+)%\s+of\s+~?([\d.]+)([\w/]+)\s+at\s+([\d.]+)([\w/]+)", RegexOptions.Compiled);
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

        public MainWindow()
        {
            VelopackApp.Build().Run();
            InitializeComponent();

            // yt-dlp lives next to the app
            ytDlpPath = Path.Combine(AppContext.BaseDirectory, YtDlpFileName);

            LoadSettings();
            InitializeTrayIcon();

            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
            StateChanged += MainWindow_StateChanged;
        }

        private void InitializeTrayIcon()
        {
            System.Drawing.Icon icon;
            try
            {
                var iconPath = Path.Combine(AppContext.BaseDirectory, "favicon.ico");
                icon = File.Exists(iconPath)
                    ? new System.Drawing.Icon(iconPath)
                    : System.Drawing.SystemIcons.Application;
            }
            catch { icon = System.Drawing.SystemIcons.Application; }

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Show Kleptos", null, (_, _) => RestoreFromTray());
            menu.Items.Add("-");
            menu.Items.Add("Quit", null, (_, _) => Close());

            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = icon,
                Text = "Kleptos",
                Visible = false,
                ContextMenuStrip = menu,
            };
            _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        }

        private void RestoreFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            ShowInTaskbar = true;
            Activate();
            if (_trayIcon != null) _trayIcon.Visible = false;
        }

        private void HideToTray()
        {
            if (_trayIcon == null) return;
            ShowInTaskbar = false;
            _trayIcon.Text = _isDownloading ? "Kleptos — downloading…" : "Kleptos";
            _trayIcon.Visible = true;
        }

        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized && _isDownloading)
                HideToTray();
        }

        private void NotifyDownloadFinished()
        {
            if (_trayIcon == null || !_trayIcon.Visible || _currentSession == null) return;
            var s = _currentSession;
            var parts = new List<string>();
            if (s.SuccessCount > 0) parts.Add($"{s.SuccessCount} succeeded");
            if (s.FailCount > 0) parts.Add($"{s.FailCount} failed");
            if (s.AlreadyDownloadedCount > 0) parts.Add($"{s.AlreadyDownloadedCount} skipped");
            var msg = parts.Count > 0 ? string.Join(", ", parts) : "Done.";
            _trayIcon.ShowBalloonTip(5000, "Kleptos", msg, System.Windows.Forms.ToolTipIcon.Info);
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (pnlTimeline != null)
                    pnlTimeline.SizeChanged += (_, _) => UpdateTimelineVisualization();

                ApplySettingsToUi();
                SetDefaultOutputLocation();

                if (!File.Exists(ytDlpPath))
                {
                    txtOutput.AppendText("yt-dlp.exe not found. Attempting to download...\n");
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

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            try { _currentCts?.Cancel(); } catch { }
            try { SaveSettings(); } catch { }
            try { if (_trayIcon != null) { _trayIcon.Visible = false; _trayIcon.Dispose(); } } catch { }
        }

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

                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* never crash on settings save */ }
        }

        private void ApplySettingsToUi()
        {
            if (!string.IsNullOrWhiteSpace(_settings.OutputFolder))
                txtFileOutput.Text = _settings.OutputFolder;

            if (!string.IsNullOrWhiteSpace(_settings.LastFormat))
            {
                foreach (ComboBoxItem item in cmbFileFormats.Items)
                {
                    if (string.Equals(item.Content?.ToString(), _settings.LastFormat, StringComparison.OrdinalIgnoreCase))
                    {
                        item.IsSelected = true;
                        break;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(_settings.LastQuality))
            {
                foreach (ComboBoxItem item in cmbQuality.Items)
                {
                    if (string.Equals(item.Content?.ToString(), _settings.LastQuality, StringComparison.OrdinalIgnoreCase))
                    {
                        item.IsSelected = true;
                        break;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(_settings.LastPreset))
            {
                foreach (ComboBoxItem item in cmbPreset.Items)
                {
                    if (string.Equals(item.Content?.ToString(), _settings.LastPreset, StringComparison.OrdinalIgnoreCase))
                    {
                        item.IsSelected = true;
                        break;
                    }
                }
            }
            ApplyPresetEnabledState();

            if (!string.IsNullOrWhiteSpace(_settings.LastCookiesFile) && File.Exists(_settings.LastCookiesFile))
            {
                cookiesTxtFile = _settings.LastCookiesFile;
                btnCookies.Content = "Cookies: " + Path.GetFileName(cookiesTxtFile);
                btnCookies.ToolTip = cookiesTxtFile;
                WarnIfCookiesStale(cookiesTxtFile);
            }

            cbMultiDownload.IsChecked = _settings.MultiDownload;
            cbThumbnailOnly.IsChecked = _settings.ThumbnailOnly;
        }

        // ── Download flow ──

        private async void Download_Click(object sender, RoutedEventArgs e)
        {
            // Repurpose the button as a Cancel control while a download is running.
            if (_isDownloading)
            {
                try { _currentCts?.Cancel(); } catch { }
                return;
            }

            try
            {
                SetDownloading(true);
                txtOutput.Clear();
                ResetShowInfoPanel();
                _currentCts = new CancellationTokenSource();

                if (cbMultiDownload.IsChecked == true)
                    await DownloadMultiAsync(_currentCts.Token);
                else
                    await DownloadSingleAsync(_currentCts.Token);
            }
            catch (OperationCanceledException)
            {
                txtOutput.AppendText("\nDownload cancelled.\n");
                UpdateCurrentStatus("Cancelled");
            }
            catch (Exception ex)
            {
                ShowError("Download failed", ex);
            }
            finally
            {
                if (_currentSession != null) _currentSession.Freeze();
                UpdateStatsPanel();
                SetDownloading(false);
                SaveSettings();
                NotifyDownloadFinished();
            }
        }

        private void SetDownloading(bool downloading)
        {
            _isDownloading = downloading;
            btnDownload.Content = downloading ? "Cancel" : "Download";

            if (downloading && WindowState == WindowState.Minimized)
                HideToTray();
        }

        private async Task DownloadSingleAsync(CancellationToken ct)
        {
            _currentSession = new SessionStats { TotalItems = 1 };
            _currentItem = new DownloadItemStats { StartTime = DateTime.Now, Url = txtURL.Text ?? string.Empty };
            _currentSession.Items.Add(_currentItem);

            if (!TryGetValidUrl(txtURL.Text, out var url, out var error))
            {
                txtOutput.AppendText(error + "\n");
                return;
            }

            if (!EnsureValidOutputFolder()) return;

            var preset = GetSelectedPreset();
            var args = new List<string>();
            string fileExtension;

            if (cbThumbnailOnly.IsChecked == true)
            {
                fileExtension = GetSelectedExtension();
                args.Add("--skip-download");
                args.Add("--write-thumbnail");
            }
            else if (preset != FormatPreset.Normal)
            {
                fileExtension = PresetExtension(preset);
                AppendPresetArgs(args, preset);
            }
            else
            {
                fileExtension = GetSelectedExtension();
                AppendFormatArgs(args, fileExtension, GetSelectedQualityHeight());
            }

            AppendCookiesArgs(args);
            AppendOutputArg(args, fileExtension);
            args.Add("--no-mtime");

            if (rbSegments.IsChecked == true)
            {
                var segments = ParseSegments(out var segError);
                if (segments.Count > 0)
                {
                    AppendSegmentArgs(args, segments);
                }
                else if (!string.IsNullOrEmpty(segError))
                {
                    txtOutput.AppendText(segError + "\n");
                    return;
                }
            }

            args.Add(url);

            await RunYtDlpAsync(args, ct);
        }

        private async Task DownloadMultiAsync(CancellationToken ct)
        {
            _currentSession = new SessionStats();

            var lines = (txtMultiUrls.Text ?? string.Empty)
                .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            if (lines.Count == 0)
            {
                txtOutput.AppendText("No links found (multi box is empty).\n");
                return;
            }

            if (!EnsureValidOutputFolder()) return;

            var preset = GetSelectedPreset();
            int qualityHeight = GetSelectedQualityHeight();
            string fileExtension = preset != FormatPreset.Normal
                ? PresetExtension(preset)
                : GetSelectedExtension();

            var entries = new List<(string Url, string BaseName)>();
            foreach (var line in lines)
            {
                if (TryParseMultiLine(line, out var url, out var baseName))
                    entries.Add((url, baseName));
            }

            if (entries.Count == 0)
            {
                txtOutput.AppendText("No valid links found in multi box.\n");
                return;
            }

            _currentSession.TotalItems = entries.Count;

            int i = 0;
            foreach (var (url, baseName) in entries)
            {
                if (ct.IsCancellationRequested) break;
                i++;

                _currentItem = new DownloadItemStats { Url = url, FileName = baseName, StartTime = DateTime.Now };
                _currentSession.Items.Add(_currentItem);
                UpdateCurrentStatus($"Downloading {i} of {entries.Count}: {baseName}");

                var args = new List<string>();
                if (cbThumbnailOnly.IsChecked == true)
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

                var folder = NormalizedOutputFolder();
                var safeBase = SanitizeFileNameTemplateSafe(baseName);
                var outputBase = Path.Combine(folder, safeBase);
                args.Add("-o");
                args.Add(string.Equals(fileExtension, "ext", StringComparison.OrdinalIgnoreCase)
                    ? $"{outputBase}.%(ext)s"
                    : $"{outputBase}.{fileExtension}");
                args.Add("--no-mtime");
                args.Add(url);

                txtOutput.AppendText($"[{i}/{entries.Count}] {baseName}\n{url}\n\n");

                await RunYtDlpAsync(args, ct);
            }
        }

        private bool EnsureValidOutputFolder()
        {
            var folder = NormalizedOutputFolder();
            if (!Directory.Exists(folder))
            {
                var result = MessageBox.Show(
                    "The folder \"" + folder + "\" does not exist. Would you like to create it?",
                    "Folder Not Found",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (result == MessageBoxResult.No) return false;
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

        private void cmbPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
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

        private static string SanitizeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
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
                txtOutput?.AppendText($"\n[Cookies] Could not read '{Path.GetFileName(path)}' to check expiry.\n");
                btnCookies.ToolTip = tooltip;
                return;
            }

            if (report.Total == 0)
            {
                txtOutput?.AppendText($"\n[Cookies] '{Path.GetFileName(path)}' contains no cookie entries — re-export from your browser.\n");
                btnCookies.ToolTip = tooltip + "\n(no entries)";
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
                txtOutput?.AppendText(
                    $"\n[Cookies] '{Path.GetFileName(path)}': {sb} (of {report.Total} entries). " +
                    "Stale cookies often cause auth failures — consider re-exporting.\n");
                tooltip += $"\n⚠ {sb}";
            }
            else if (report.EarliestUpcomingExpiry is DateTime next)
            {
                tooltip += $"\nAll {report.Total} entries valid (earliest expiry: {next:yyyy-MM-dd})";
            }

            btnCookies.ToolTip = tooltip;
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
                txtOutput.AppendText("yt-dlp.exe missing. Trying to fetch it...\n");
                try { await DownloadYtDlpLatestAsync(); }
                catch (Exception ex)
                {
                    txtOutput.AppendText("Could not fetch yt-dlp: " + ex.Message + "\n");
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
            bool sawAlready = false;
            bool sawError = false;
            bool authRequired = false;

            void OnLine(string? data)
            {
                if (string.IsNullOrEmpty(data)) return;

                if (ProgressCompleteRegex.IsMatch(data)) sawHundred = true;
                if (MergerRegex.IsMatch(data)) sawMerge = true;
                if (AlreadyDownloadedRegex.IsMatch(data)) sawAlready = true;
                if (ErrorRegex.IsMatch(data)) sawError = true;
                if (AuthRequiredRegex.IsMatch(data)) authRequired = true;

                Dispatcher.BeginInvoke(() =>
                {
                    txtOutput.AppendText(data + "\n");
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

            Dispatcher.Invoke(() =>
            {
                if (_currentItem != null && _currentItem.EndTime == default)
                {
                    _currentItem.EndTime = DateTime.Now;
                    _currentItem.Duration = _currentItem.EndTime - _currentItem.StartTime;

                    if (sawAlready) { _currentItem.AlreadyDownloaded = true; _currentItem.Succeeded = true; }
                    else if (sawError) { _currentItem.Succeeded = false; }
                    else if (sawHundred || sawMerge) { _currentItem.Succeeded = true; }
                }

                if (_currentSession != null)
                {
                    // Don't fight the playlist parser — it sets CompletedItems directly.
                    // Only bump it when this run wasn't a playlist (i.e. no playlist progress line was seen).
                    int observedItems = _currentSession.Items.Count;
                    if (_currentSession.CompletedItems < observedItems &&
                        _currentSession.TotalItems == observedItems)
                    {
                        _currentSession.CompletedItems = observedItems;
                    }
                    UpdateProgressBar(100);
                    UpdateStatsPanel();
                }

                if (sawAlready) { txtOutput.AppendText("\n✅ File was already downloaded!\n"); UpdateCurrentStatus("Already downloaded"); }
                else if (sawError) { txtOutput.AppendText("\n❌ Download failed!\n"); UpdateCurrentStatus("Download failed"); }
                else if (sawHundred || sawMerge) { txtOutput.AppendText("\n🎉 Download successful!\n"); UpdateCurrentStatus("Download complete!"); }

                scrollViewer.ScrollToEnd();
            });

            if (authRequired && !ct.IsCancellationRequested)
            {
                Dispatcher.Invoke(() =>
                {
                    var result = MessageBox.Show(
                        "This site requires authentication.\n\n" +
                        "Install a browser extension that can export cookies (e.g. 'Get cookies.txt LOCALLY'), " +
                        "then click 'Cookies' below and select the exported file.\n\n" +
                        "Click OK to open the extension page in your browser, or Cancel to dismiss.",
                        "Authentication Required",
                        MessageBoxButton.OKCancel,
                        MessageBoxImage.Warning,
                        MessageBoxResult.Cancel);

                    if (result == MessageBoxResult.OK)
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo(
                                "https://chromewebstore.google.com/detail/cclelndahbckbenkjhflpdbgdldlbecc")
                            { UseShellExecute = true });
                        }
                        catch { }
                    }
                });
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
            // Drain stderr even though we ignore it — pipe must be read or the child will block.
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
                        }

                        _currentSession.TotalItems = Math.Max(_currentSession.TotalItems, total);
                        _currentSession.CompletedItems = Math.Max(_currentSession.CompletedItems, current - 1);

                        _currentItem = new DownloadItemStats { StartTime = DateTime.Now };
                        _currentSession.Items.Add(_currentItem);
                    }
                    UpdateCurrentStatus($"Downloading item {current} of {total}");
                }
                return;
            }

            if (AlreadyDownloadedRegex.IsMatch(line))
            {
                if (_currentItem != null) { _currentItem.AlreadyDownloaded = true; _currentItem.Succeeded = true; }
                UpdateCurrentStatus("Already downloaded");
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

            if (progressBarFill.Parent is System.Windows.Controls.Border parent && parent.ActualWidth > 0)
                progressBarFill.Width = parent.ActualWidth * (overallPercent / 100.0);
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
                    AddStatRow("Largest", $"{FormatSize(_currentSession.LargestFile.FileSizeMB)} — {_currentSession.LargestFile.FileName}");
                    if (_currentSession.SmallestFile != null)
                        AddStatRow("Smallest", $"{FormatSize(_currentSession.SmallestFile.FileSizeMB)} — {_currentSession.SmallestFile.FileName}");
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
                Foreground = (SolidColorBrush)FindResource("TextPrimary"),
                FontFamily = (FontFamily)FindResource("JetBrainsReg"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 4, 0, 4)
            });
        }

        private void AddStatRow(string label, string value, string? color = null)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var lblBlock = new TextBlock
            {
                Text = label,
                Foreground = color != null
                    ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(color))
                    : (SolidColorBrush)FindResource("TextSecondary"),
                FontFamily = (FontFamily)FindResource("JetBrainsReg"),
                FontSize = 11,
                Margin = new Thickness(0, 1, 12, 1)
            };
            Grid.SetColumn(lblBlock, 0);

            var valBlock = new TextBlock
            {
                Text = value,
                Foreground = (SolidColorBrush)FindResource("TextPrimary"),
                FontFamily = (FontFamily)FindResource("JetBrainsReg"),
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
            pnlStats.Children.Add(new System.Windows.Controls.Border
            {
                Height = 1,
                Background = (SolidColorBrush)FindResource("BorderSubtle"),
                Margin = new Thickness(0, 6, 0, 6)
            });
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
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });

            var startBox = CreateTimeTextBox();
            Grid.SetColumn(startBox, 0);

            var sep = new TextBlock
            {
                Text = "—",
                Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                FontFamily = (FontFamily)FindResource("JetBrainsReg"),
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
                Style = (Style)FindResource("FilePathButton"),
                Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                FontFamily = (FontFamily)FindResource("JetBrainsReg"),
                FontSize = 14,
                Margin = new Thickness(4, 0, 0, 0),
                ToolTip = "Remove segment"
            };
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
                Style = (Style)FindResource("SegmentTimeTextBox"),
                Height = 26,
                FontSize = 11,
                FontFamily = (FontFamily)FindResource("JetBrainsReg"),
                Foreground = (SolidColorBrush)FindResource("TextPrimary"),
                CaretBrush = (SolidColorBrush)FindResource("TextSecondary"),
                SelectionBrush = (SolidColorBrush)FindResource("AccentColor"),
                MaxLength = 8,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            tb.TextChanged += SegmentTime_TextChanged;
            return tb;
        }

        private void SegmentTime_TextChanged(object sender, TextChangedEventArgs e)
        {
            var tb = (TextBox)sender;
            double seconds = ParseTimeToSeconds(tb.Text);
            tb.BorderBrush = (string.IsNullOrWhiteSpace(tb.Text) || seconds >= 0)
                ? (SolidColorBrush)FindResource("BorderSubtle")
                : (SolidColorBrush)FindResource("AccentColor");

            UpdateTimelineVisualization();
        }

        private async void btnFetchDuration_Click(object sender, RoutedEventArgs e)
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
                    pnlTimeline.Visibility = Visibility.Visible;
                    UpdateTimelineVisualization();

                    if (!string.IsNullOrWhiteSpace(_videoTitle))
                    {
                        txtVideoTitle.Text = _videoTitle;
                        txtVideoTitle.Visibility = Visibility.Visible;
                    }

                    if (lines.Length >= 3 && !string.IsNullOrWhiteSpace(lines[2]))
                        await LoadThumbnailAsync(lines[2]);
                }
                else
                {
                    _videoDurationSeconds = 0;
                    txtSegmentDuration.Text = "Duration: unknown";
                    pnlTimeline.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                txtSegmentDuration.Text = "Duration: error";
                pnlTimeline.Visibility = Visibility.Collapsed;
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

                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                        bitmap.BeginInit();
                        bitmap.StreamSource = new MemoryStream(bytes);
                        bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        bitmap.EndInit();
                        bitmap.Freeze();
                        imgThumbnail.Source = bitmap;
                        bdrThumbnail.Visibility = Visibility.Visible;
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
                        Dispatcher.Invoke(() =>
                        {
                            try
                            {
                                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                                bitmap.BeginInit();
                                bitmap.StreamSource = new MemoryStream(bytes);
                                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                                bitmap.EndInit();
                                bitmap.Freeze();
                                imgThumbnail.Source = bitmap;
                                bdrThumbnail.Visibility = Visibility.Visible;
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

            double timelineWidth = pnlTimeline.ActualWidth;
            double timelineHeight = pnlTimeline.ActualHeight;
            if (timelineWidth <= 0) timelineWidth = 200;
            if (timelineHeight <= 0) timelineHeight = 16;

            if (pnlSegmentRows.Children.Count == 0) return;

            foreach (var child in pnlSegmentRows.Children)
            {
                if (child is not Grid row || row.Tag is not TextBox[] boxes) continue;

                double startSec = ParseTimeToSeconds(boxes[0].Text);
                double endSec = ParseTimeToSeconds(boxes[1].Text);

                if (startSec >= 0 && endSec >= 0 && startSec < endSec)
                {
                    double left = (startSec / _videoDurationSeconds) * timelineWidth;
                    double width = ((endSec - startSec) / _videoDurationSeconds) * timelineWidth;

                    var rect = new System.Windows.Shapes.Rectangle
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
            var line = new System.Windows.Shapes.Rectangle
            {
                Width = 2,
                Height = timelineHeight,
                Fill = new SolidColorBrush(color),
            };
            Canvas.SetLeft(line, Math.Max(0, x - 1));
            Canvas.SetTop(line, 0);
            cnvTimeline.Children.Add(line);

            var triangle = new System.Windows.Shapes.Polygon
            {
                Points = new PointCollection
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
            double width = pnlTimeline.ActualWidth;
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

        private void Timeline_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_videoDurationSeconds <= 0) return;

            double x = e.GetPosition(pnlTimeline).X;
            double seconds = GetTimelineSeconds(x);

            txtSeekTimestamp.Text = FormatTimeInput(seconds);

            // Clamp the popup so it doesn't fall off the left/right edge.
            double width = pnlTimeline.ActualWidth;
            double offset = Math.Clamp(x - 30, 0, Math.Max(0, width - 60));
            popSeekPreview.HorizontalOffset = offset;

            if (!popSeekPreview.IsOpen) popSeekPreview.IsOpen = true;
        }

        private void Timeline_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            popSeekPreview.IsOpen = false;
        }

        private void Timeline_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_videoDurationSeconds <= 0) return;
            double seconds = GetTimelineSeconds(e.GetPosition(pnlTimeline).X);
            SetActiveSegmentTime(isStart: true, FormatTimeInput(seconds));
        }

        private void Timeline_MouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
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
                string startText = boxes[0].Text.Trim();
                string endText = boxes[1].Text.Trim();

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

        private void OutputToggle_Checked(object sender, RoutedEventArgs e)
        {
            if (pnlShowInfo == null || pnlConsole == null || pnlSegmentsTab == null) return;

            pnlShowInfo.Visibility = Visibility.Collapsed;
            pnlConsole.Visibility = Visibility.Collapsed;
            pnlSegmentsTab.Visibility = Visibility.Collapsed;

            if (rbShowInfo.IsChecked == true)
            {
                pnlShowInfo.Visibility = Visibility.Visible;
            }
            else if (rbConsole.IsChecked == true)
            {
                pnlConsole.Visibility = Visibility.Visible;
            }
            else if (rbSegments.IsChecked == true)
            {
                pnlSegmentsTab.Visibility = Visibility.Visible;
                if (pnlSegmentRows.Children.Count == 0)
                    AddSegmentRow();
            }
        }

        private void FileOutput_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog();
            if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.FolderName))
            {
                txtFileOutput.Text = dlg.FolderName;
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
                txtOutput.AppendText("Checking yt-dlp version...\n");

                string? currentVersion = await GetCurrentYtDlpVersionAsync();

                string? latestVersion = await GetLatestYtDlpVersionAsync();
                if (string.IsNullOrEmpty(latestVersion))
                {
                    txtOutput.AppendText("Could not fetch latest yt-dlp version (offline?).\n");
                    return;
                }

                if (string.IsNullOrEmpty(currentVersion) || currentVersion != latestVersion)
                {
                    txtOutput.AppendText($"Updating yt-dlp → {latestVersion}...\n");
                    await DownloadYtDlpUpdateAsync(latestVersion);
                    txtOutput.AppendText($"yt-dlp updated to {latestVersion}.\n");
                }
                else
                {
                    txtOutput.AppendText($"yt-dlp is up-to-date ({currentVersion}).\n");
                }
            }
            catch (Exception ex)
            {
                txtOutput.AppendText("yt-dlp update check failed: " + ex.Message + "\n");
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
            string downloadUrl = $"https://github.com/yt-dlp/yt-dlp/releases/download/{version}/yt-dlp.exe";
            string tempPath = Path.Combine(Path.GetTempPath(), "kleptos_yt-dlp.exe.new");
            string backupPath = ytDlpPath + ".old";

            using (var resp = await SharedHttp.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(tempPath);
                await resp.Content.CopyToAsync(fs);
            }

            // Verify the binary is at least non-empty and starts with the PE header.
            var info = new FileInfo(tempPath);
            if (info.Length < 1_000_000) // yt-dlp is well over 1 MB
                throw new InvalidOperationException("Downloaded yt-dlp.exe looks truncated.");

            using (var fs = File.OpenRead(tempPath))
            {
                var header = new byte[2];
                if (fs.Read(header, 0, 2) != 2 || header[0] != (byte)'M' || header[1] != (byte)'Z')
                    throw new InvalidOperationException("Downloaded yt-dlp.exe is not a valid executable.");
            }

            // Atomic replace if the destination exists, otherwise just move.
            if (File.Exists(ytDlpPath))
            {
                File.Replace(tempPath, ytDlpPath, backupPath, ignoreMetadataErrors: true);
                try { File.Delete(backupPath); } catch { }
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ytDlpPath)!);
                File.Move(tempPath, ytDlpPath);
            }
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
                    // Portable build — skip update check silently (the modal-on-startup was disruptive).
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

        private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
                DragMove();
        }

        private void CopyLogs_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(txtOutput.Text); } catch { }
        }

        private async void Update_Click(object sender, RoutedEventArgs e)
        {
            UpdateButton.IsEnabled = false;
            var textBlock = (TextBlock)UpdateButton.Content;
            textBlock.Text = "Downloading update...";

            try
            {
                await Task.Run(UpdateKleptosAsync);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Update failed: " + ex.Message, "Update Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                textBlock.Text = "Update Available";
                UpdateButton.IsEnabled = true;
            }
        }

        private void ManageUpdateButton()
        {
            UpdateButton.Visibility = hasUpdate ? Visibility.Visible : Visibility.Collapsed;
        }

        private void FileCookies_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog();
            if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.FileName))
            {
                cookiesTxtFile = dlg.FileName;
                btnCookies.Content = "Cookies: " + Path.GetFileName(cookiesTxtFile);
                btnCookies.ToolTip = cookiesTxtFile;
                WarnIfCookiesStale(cookiesTxtFile);
            }
        }

        private void ShowError(string title, Exception ex)
        {
            try
            {
                txtOutput.AppendText($"\n[{title}] {ex.Message}\n");
                MessageBox.Show(ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
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
