using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

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

        public int SuccessCount => Items.Count(i => i.Succeeded && !i.AlreadyDownloaded);
        public int FailCount => Items.Count(i => !i.Succeeded && !i.AlreadyDownloaded);
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
        public TimeSpan TotalDuration => DateTime.Now - SessionStart;
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

    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly string ytDlpPath = "yt-dlp.exe";
        private readonly string gitHubReleaseUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest";

        private string cookiesTxtFile = string.Empty;

        private static bool hasUpdate = false;

        private static readonly Regex UrlRegex =
            new Regex(@"https?://\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // yt-dlp output parsing regexes
        private static readonly Regex ProgressRegex =
            new Regex(@"\[download\]\s+([\d.]+)%\s+of\s+~?([\d.]+)([\w/]+)\s+at\s+([\d.]+)([\w/]+)", RegexOptions.Compiled);
        private static readonly Regex ProgressCompleteRegex =
            new Regex(@"\[download\]\s+100%\s+of\s+~?([\d.]+)([\w/]+)", RegexOptions.Compiled);
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

        private SessionStats? _currentSession;
        private DownloadItemStats? _currentItem;

        private double _videoDurationSeconds = 0;
        private string _videoTitle = string.Empty;

        public MainWindow()
        {
            // Installer Build
            VelopackApp.Build().Run();

            InitializeComponent();

            // Check for a new version for Kleptos
            CheckForKleptosUpdate();

            // Set the default download location to the user's "Downloads" folder
            SetDefaultOutputLocation();
            // Check for Updates
            CheckForYTDLPUpdate();
            // Manage the update buttons visibility
            ManageUpdateButton();
        }

        private async void Download_Click(object sender, RoutedEventArgs e)
        {
            txtOutput.Text = string.Empty;

            // Multi-download mode?
            if (cbMultiDownload.IsChecked == true)
            {
                await DownloadMultiAsync();
                return;
            }

            // Initialize stats tracking
            _currentSession = new SessionStats { TotalItems = 1 };
            _currentItem = new DownloadItemStats { StartTime = DateTime.Now, Url = txtURL.Text ?? string.Empty };
            _currentSession.Items.Add(_currentItem);
            ResetShowInfoPanel();

            // ---- Single URL mode ----
            if (!TryGetValidUrl(txtURL.Text, out var url, out var error))
            {
                txtOutput.Text = error;
                return;
            }

            SetDefaultOutputLocation();

            // Ensure output path ends with a backslash
            if (!txtFileOutput.Text.EndsWith("\\"))
            {
                txtFileOutput.Text += "\\";
            }

            // Validate folder existence
            if (!Directory.Exists(txtFileOutput.Text))
            {
                MessageBoxResult result = MessageBox.Show("The folder " + txtFileOutput.Text + " does not exist. Would you like to continue? \nThis will create a new folder.", "Folder Not Found!", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.No) return;
            }

            // Get selected format
            string fileExtension = "ext"; // Default (yt-dlp decides)
            ComboBoxItem typeItem = (ComboBoxItem)cmbFileFormats.SelectedItem;
            string cmbFileExtension = typeItem.Content.ToString()!;

            if (cmbFileExtension != "default")
            {
                fileExtension = cmbFileExtension.ToLower(); // Normalize to lowercase
            }

            string command = string.Empty;

            if (cbThumbnailOnly.IsChecked == true)
            {
                //command = $"yt-dlp --skip-download --write-thumbnail --cookies \"{cookiesTxtFile}\" -o {GetVideoName(fileExtension)} --no-mtime \"{txtURL.Text}\"";
                command = $"yt-dlp --skip-download --write-thumbnail{GetCookiesArgument()} -o {GetVideoName(fileExtension)} --no-mtime \"{txtURL.Text}\"";
            }
            else
            {
                //command = $"yt-dlp {GetFormat(fileExtension)} --cookies \"{cookiesTxtFile}\" -o {GetVideoName(fileExtension)} --no-mtime \"{txtURL.Text}\"";
                command = $"yt-dlp {GetFormat(fileExtension)}{GetCookiesArgument()} -o {GetVideoName(fileExtension)} --no-mtime \"{txtURL.Text}\"";
            }

            // Append segment download args if Segments tab is active
            if (rbSegments.IsChecked == true && cbMultiDownload.IsChecked != true)
            {
                var segments = ParseSegments(out var segError);
                if (segments.Count > 0)
                {
                    command += BuildSegmentArgs(segments);
                }
                else if (!string.IsNullOrEmpty(segError))
                {
                    txtOutput.Text = segError;
                    return;
                }
            }

            // Run the command
            await RunCMD(command);
        }

        private async Task DownloadMultiAsync()
        {
            txtOutput.Text = string.Empty;

            // Initialize session stats
            _currentSession = new SessionStats();
            ResetShowInfoPanel();

            var raw = txtMultiUrls.Text ?? string.Empty;
            var lines = raw
                .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            if (lines.Count == 0)
            {
                txtOutput.Text = "No links found (multi box is empty).";
                return;
            }

            SetDefaultOutputLocation();

            if (!txtFileOutput.Text.EndsWith("\\"))
                txtFileOutput.Text += "\\";

            if (!Directory.Exists(txtFileOutput.Text))
            {
                var result = MessageBox.Show(
                    "The folder " + txtFileOutput.Text + " does not exist. Would you like to continue?\nThis will create a new folder.",
                    "Folder Not Found!",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.No) return;
            }

            string fileExtension = GetSelectedExtension();

            // Parse entries
            var entries = new List<(string Url, string BaseName)>();
            foreach (var line in lines)
            {
                if (!TryParseMultiLine(line, out var url, out var baseName))
                    continue;

                entries.Add((url, baseName));
            }

            if (entries.Count == 0)
            {
                txtOutput.Text = "No valid links found in multi box.";
                return;
            }

            _currentSession.TotalItems = entries.Count;

            // Sequential download (one after another)
            int i = 0;
            foreach (var (url, baseName) in entries)
            {
                i++;

                // Create per-item stats
                _currentItem = new DownloadItemStats { Url = url, FileName = baseName, StartTime = DateTime.Now };
                _currentSession.Items.Add(_currentItem);
                UpdateCurrentStatus($"Downloading {i} of {entries.Count}: {baseName}");

                // Build -o output template:
                // - if user chose "default": keep %(ext)s
                // - else: force chosen extension by naming it explicitly
                var outputTemplate = BuildOutputTemplate(baseName, fileExtension);

                string command;
                if (cbThumbnailOnly.IsChecked == true)
                {
                    command = $"yt-dlp --skip-download --write-thumbnail{GetCookiesArgument()} -o {outputTemplate} --no-mtime \"{url}\"";
                }
                else
                {
                    command = $"yt-dlp {GetFormat(fileExtension)}{GetCookiesArgument()} -o {outputTemplate} --no-mtime \"{url}\"";
                }

                // Optional: show progress in your console
                txtOutput.Text += $"[{i}/{entries.Count}] {baseName}\n{url}\n\n";

                await RunCMD(command);
            }

            // Final stats update
            UpdateStatsPanel();
        }

        private string GetSelectedExtension()
        {
            string fileExtension = "ext"; // default (yt-dlp decides)
            var typeItem = (ComboBoxItem)cmbFileFormats.SelectedItem;
            var cmbFileExtension = typeItem.Content.ToString()!;

            if (!string.Equals(cmbFileExtension, "default", StringComparison.OrdinalIgnoreCase))
                fileExtension = cmbFileExtension.ToLowerInvariant();

            return fileExtension;
        }

        private static bool TryGetValidUrl(string? input, out string url, out string error)
        {
            url = string.Empty;

            if (string.IsNullOrWhiteSpace(input) || input.Trim().Length < 8)
            {
                error = "No link Found";
                return false;
            }

            input = input.Trim();

            if (!(input.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                  input.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
            {
                error = "Invalid Link";
                return false;
            }

            error = string.Empty;
            url = input;
            return true;
        }

        // Parses:
        // "1 testTitle https://..."  -> baseName: "1 - testTitle"
        // "testTitle https://..."    -> baseName: "testTitle"
        // "https://..."              -> baseName: "%(title)s"
        // "1 https://..."            -> baseName: "1 - %(title)s"
        private static bool TryParseMultiLine(string line, out string url, out string baseName)
        {
            url = string.Empty;
            baseName = string.Empty;

            var m = UrlRegex.Match(line);
            if (!m.Success) return false;

            url = m.Value.Trim();

            // Everything BEFORE the URL is title (+ maybe leading number)
            var left = line.Substring(0, m.Index).Trim();

            int? indexNum = null;
            string titlePart = left;

            // Check if first token is a number
            if (!string.IsNullOrWhiteSpace(left))
            {
                var parts = left.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 1 && int.TryParse(parts[0], out var n))
                {
                    indexNum = n;
                    titlePart = parts.Length == 2 ? parts[1].Trim() : string.Empty;
                }
            }

            // If there is NO title text, use yt-dlp metadata title
            if (string.IsNullOrWhiteSpace(titlePart))
                titlePart = "%(title)s";
            else
                titlePart = SanitizeFileName(titlePart);

            baseName = indexNum.HasValue
                ? $"{indexNum.Value} - {titlePart}"
                : titlePart;

            return true;
        }

        private string BuildOutputTemplate(string baseName, string fileExtension)
        {
            // Preserve yt-dlp template variables like %(title)s
            // but still sanitize user text around it.
            baseName = SanitizeFileNameTemplateSafe(baseName);

            var fullBase = Path.Combine(txtFileOutput.Text, baseName);

            if (string.Equals(fileExtension, "ext", StringComparison.OrdinalIgnoreCase))
                return $"\"{fullBase}.%(ext)s\"";

            return $"\"{fullBase}.{fileExtension}\"";
        }

        private static string SanitizeFileNameTemplateSafe(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "%(title)s";

            // Temporarily protect yt-dlp tokens so they don't get destroyed by filename sanitizing
            const string TITLE_TOKEN = "___YTDLP_TITLE___";
            const string EXT_TOKEN = "___YTDLP_EXT___";

            name = name.Replace("%(title)s", TITLE_TOKEN, StringComparison.OrdinalIgnoreCase)
                       .Replace("%(ext)s", EXT_TOKEN, StringComparison.OrdinalIgnoreCase);

            // Sanitize the rest (your existing sanitiser)
            name = SanitizeFileName(name);

            // Restore tokens
            name = name.Replace(TITLE_TOKEN, "%(title)s")
                       .Replace(EXT_TOKEN, "%(ext)s");

            // If sanitizing nuked everything, fallback safely
            if (string.IsNullOrWhiteSpace(name))
                name = "%(title)s";

            return name;
        }

        private static string SanitizeFileName(string name)
        {
            // Remove invalid Windows filename chars
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');

            // Extra cleanup
            name = Regex.Replace(name, @"\s+", " ").Trim();

            // Windows doesn't like trailing dots/spaces
            name = name.TrimEnd('.', ' ');

            if (string.IsNullOrWhiteSpace(name))
                name = "download";

            return name;
        }

        private async Task RunCMD(string cmd)
        {
            using (Process process = new Process())
            {
                process.StartInfo.FileName = "cmd.exe";
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.RedirectStandardInput = true;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.UseShellExecute = false;

                ConcurrentQueue<string> outputQueue = new ConcurrentQueue<string>();

                process.OutputDataReceived += (sender, args) =>
                {
                    if (!string.IsNullOrEmpty(args.Data))
                    {
                        outputQueue.Enqueue(args.Data);
                        Dispatcher.BeginInvoke(() =>
                        {
                            txtOutput.Text += args.Data + "\n";
                            scrollViewer.ScrollToEnd();
                            ParseOutputLine(args.Data);
                        });
                    }
                };

                process.ErrorDataReceived += (sender, args) =>
                {
                    if (!string.IsNullOrEmpty(args.Data))
                    {
                        outputQueue.Enqueue(args.Data);
                        Dispatcher.BeginInvoke(() =>
                        {
                            txtOutput.Text += args.Data + "\n";
                            scrollViewer.ScrollToEnd();
                            ParseOutputLine(args.Data);
                        });
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (process.StandardInput.BaseStream.CanWrite)
                {
                    process.StandardInput.WriteLine(cmd);
                    process.StandardInput.WriteLine("exit");
                }

                await process.WaitForExitAsync();

                // Analyze Output to Determine Download Status
                bool isAlreadyDownloaded = outputQueue.Any(line =>
                    line.Contains("has already been downloaded") ||
                    line.Contains("[download]") && line.Contains("has already been downloaded"));

                bool isSuccess = outputQueue.Any(line =>
                    line.Contains("100%") || line.Contains("has been downloaded"));

                bool isFailure = outputQueue.Any(line =>
                    line.Contains("ERROR:") ||
                    line.Contains("unable to download") ||
                    line.Contains("not found"));

                // Finalize current item stats
                Dispatcher.Invoke(() =>
                {
                    if (_currentItem != null)
                    {
                        _currentItem.EndTime = DateTime.Now;
                        _currentItem.Duration = _currentItem.EndTime - _currentItem.StartTime;

                        if (isAlreadyDownloaded)
                        {
                            _currentItem.AlreadyDownloaded = true;
                            _currentItem.Succeeded = true;
                        }
                        else if (isSuccess)
                        {
                            _currentItem.Succeeded = true;
                        }
                        else if (isFailure)
                        {
                            _currentItem.Succeeded = false;
                        }
                    }

                    if (_currentSession != null)
                    {
                        _currentSession.CompletedItems++;
                        UpdateProgressBar(100);
                        UpdateStatsPanel();
                    }

                    if (isAlreadyDownloaded)
                    {
                        txtOutput.Text += "\n✅ File was already downloaded!\n";
                        UpdateCurrentStatus("Already downloaded");
                    }
                    else if (isSuccess)
                    {
                        txtOutput.Text += "\n🎉 Download successful!\n";
                        UpdateCurrentStatus("Download complete!");
                    }
                    else if (isFailure)
                    {
                        txtOutput.Text += "\n❌ Download failed!\n";
                        UpdateCurrentStatus("Download failed");
                    }

                    scrollViewer.ScrollToEnd();
                });

                // Check for authentication-related messages AFTER process exits
                bool authRequired = outputQueue.Any(line =>
                    line.Contains("Please log in") ||
                    line.Contains("This video is private") ||
                    line.Contains("unable to download webpage") ||
                    line.Contains("Sign in to confirm you’re not a bot") ||
                    line.Contains("Use --cookies-from-browser or --cookies"));

                if (authRequired)
                {
                    Dispatcher.Invoke(() =>
                    {
                        MessageBoxResult result = MessageBox.Show("Please download any extention that allows you to download cookies, I recommend `Get cookies.txt LOCALLY`. \n\nOnce downloaded press `cookies` and locate the file you extracted from chrome. \nPressing `Ok` will open the extention in your browser.",
                            "Authentication Required",
                            MessageBoxButton.OKCancel,
                            MessageBoxImage.Warning);

                        if(result == MessageBoxResult.OK)
                        {
                            string url = "https://chromewebstore.google.com/detail/cclelndahbckbenkjhflpdbgdldlbecc";
                            Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
                        }
                    });
                }
            }
        }

        private void ParseOutputLine(string line)
        {
            if (string.IsNullOrEmpty(line) || _currentItem == null) return;

            Match m;

            // Destination
            m = DestinationRegex.Match(line);
            if (m.Success)
            {
                _currentItem.FileName = Path.GetFileName(m.Groups[1].Value);
                UpdateCurrentStatus($"Downloading: {_currentItem.FileName}");
                return;
            }

            // Progress (not 100%)
            m = ProgressRegex.Match(line);
            if (m.Success)
            {
                if (double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double percent))
                {
                    UpdateProgressBar(percent);
                }
                if (double.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double size))
                {
                    _currentItem.FileSizeMB = ConvertToMB(size, m.Groups[3].Value);
                }
                if (double.TryParse(m.Groups[4].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double speed))
                {
                    _currentItem.SpeedMBps = ConvertToMB(speed, m.Groups[5].Value);
                }
                return;
            }

            // 100% complete
            m = ProgressCompleteRegex.Match(line);
            if (m.Success)
            {
                if (double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double size))
                {
                    _currentItem.FileSizeMB = ConvertToMB(size, m.Groups[2].Value);
                }
                UpdateProgressBar(100);
                return;
            }

            // Merger
            m = MergerRegex.Match(line);
            if (m.Success)
            {
                _currentItem.FileName = Path.GetFileName(m.Groups[1].Value);
                UpdateCurrentStatus($"Merging: {_currentItem.FileName}");
                return;
            }

            // Playlist item progress
            m = PlaylistProgressRegex.Match(line);
            if (m.Success)
            {
                if (int.TryParse(m.Groups[1].Value, out int current) && int.TryParse(m.Groups[2].Value, out int total))
                {
                    if (_currentSession != null)
                    {
                        // Finalize previous item if it exists and is different
                        if (_currentItem != null && _currentItem.StartTime != default && _currentItem.EndTime == default)
                        {
                            _currentItem.EndTime = DateTime.Now;
                            _currentItem.Duration = _currentItem.EndTime - _currentItem.StartTime;
                        }

                        _currentSession.TotalItems = total;
                        _currentSession.CompletedItems = current - 1;

                        // Create new item for this playlist entry
                        _currentItem = new DownloadItemStats { StartTime = DateTime.Now };
                        _currentSession.Items.Add(_currentItem);
                    }
                    UpdateCurrentStatus($"Downloading item {current} of {total}");
                }
                return;
            }

            // Already downloaded
            if (AlreadyDownloadedRegex.IsMatch(line))
            {
                if (_currentItem != null)
                {
                    _currentItem.AlreadyDownloaded = true;
                    _currentItem.Succeeded = true;
                }
                UpdateCurrentStatus("Already downloaded");
                return;
            }

            // Error
            m = ErrorRegex.Match(line);
            if (m.Success)
            {
                if (_currentItem != null)
                {
                    _currentItem.ErrorMessage = m.Groups[1].Value;
                    _currentItem.Succeeded = false;
                }
                UpdateCurrentStatus($"Error: {m.Groups[1].Value}");
                return;
            }
        }

        private static double ConvertToMB(double value, string unit)
        {
            var u = unit.Trim().TrimEnd('/','s').ToLowerInvariant();
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
            {
                overallPercent = (_currentSession.CompletedItems * 100.0 + currentPercent) / _currentSession.TotalItems;
            }
            overallPercent = Math.Max(0, Math.Min(100, overallPercent));

            txtProgressPercent.Text = $"{overallPercent:F0}%";

            // Set progress bar width relative to parent
            var parent = progressBarFill.Parent as System.Windows.Controls.Border;
            if (parent != null && parent.ActualWidth > 0)
            {
                progressBarFill.Width = parent.ActualWidth * (overallPercent / 100.0);
            }
        }

        private void UpdateCurrentStatus(string text)
        {
            txtCurrentStatus.Text = text;
        }

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

            // Summary section
            AddStatHeader("Summary");
            AddStatRow("Total Files", _currentSession.CompletedItems.ToString());
            if (_currentSession.SuccessCount > 0)
                AddStatRow("Succeeded", _currentSession.SuccessCount.ToString(), "#4ecdc4");
            if (_currentSession.FailCount > 0)
                AddStatRow("Failed", _currentSession.FailCount.ToString(), "#ff3c3c");
            if (_currentSession.AlreadyDownloadedCount > 0)
                AddStatRow("Already Downloaded", _currentSession.AlreadyDownloadedCount.ToString(), "#888888");

            // Size section
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

            // Performance section
            AddStatSeparator();
            AddStatHeader("Performance");
            if (_currentSession.AverageSpeedMBps > 0)
                AddStatRow("Avg Speed", $"{_currentSession.AverageSpeedMBps:F2} MB/s");
            AddStatRow("Total Time", FormatDuration(_currentSession.TotalDuration));
            if (_currentSession.Items.Count > 1 && _currentSession.AverageItemDuration > TimeSpan.Zero)
                AddStatRow("Avg Per File", FormatDuration(_currentSession.AverageItemDuration));

            // Files section (multi/playlist only)
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
                Foreground = (System.Windows.Media.SolidColorBrush)FindResource("TextPrimary"),
                FontFamily = (System.Windows.Media.FontFamily)FindResource("JetBrainsReg"),
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
                    ? new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color))
                    : (System.Windows.Media.SolidColorBrush)FindResource("TextSecondary"),
                FontFamily = (System.Windows.Media.FontFamily)FindResource("JetBrainsReg"),
                FontSize = 11,
                Margin = new Thickness(0, 1, 12, 1)
            };
            Grid.SetColumn(lblBlock, 0);

            var valBlock = new TextBlock
            {
                Text = value,
                Foreground = (System.Windows.Media.SolidColorBrush)FindResource("TextPrimary"),
                FontFamily = (System.Windows.Media.FontFamily)FindResource("JetBrainsReg"),
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
                Background = (System.Windows.Media.SolidColorBrush)FindResource("BorderSubtle"),
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
            if (pnlSegmentRows.Children.Count > 0) return; // only one segment allowed

            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var startBox = CreateTimeTextBox();
            Grid.SetColumn(startBox, 0);

            var sep = new TextBlock
            {
                Text = "\u2014",
                Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                FontFamily = (FontFamily)FindResource("JetBrainsReg"),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(sep, 1);

            var endBox = CreateTimeTextBox();
            Grid.SetColumn(endBox, 2);

            row.Tag = new[] { startBox, endBox };
            row.Children.Add(startBox);
            row.Children.Add(sep);
            row.Children.Add(endBox);

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

        // ── Segment Download: Metadata Fetch ──

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
                // Fetch duration, title, and thumbnail URL in one call
                var args = $"--print duration --print title --print thumbnail --no-download{GetCookiesArgument()} \"{url}\"";
                var metaOutput = await RunYtDlpPrint(args);
                var lines = metaOutput.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

                if (lines.Length >= 1 && double.TryParse(lines[0], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double duration) && duration > 0)
                {
                    _videoDurationSeconds = duration;
                    _videoTitle = lines.Length >= 2 ? lines[1] : string.Empty;
                    txtSegmentDuration.Text = $"Duration: {FormatDuration(duration)}";
                    pnlTimeline.Visibility = Visibility.Visible;
                    UpdateTimelineVisualization();

                    // Show video title
                    if (!string.IsNullOrWhiteSpace(_videoTitle))
                    {
                        txtVideoTitle.Text = _videoTitle;
                        txtVideoTitle.Visibility = Visibility.Visible;
                    }

                    // Fetch thumbnail
                    if (lines.Length >= 3 && !string.IsNullOrWhiteSpace(lines[2]))
                    {
                        await LoadThumbnailAsync(lines[2]);
                    }

                }
                else
                {
                    _videoDurationSeconds = 0;
                    txtSegmentDuration.Text = "Duration: unknown";
                    pnlTimeline.Visibility = Visibility.Collapsed;
                }
            }
            catch
            {
                txtSegmentDuration.Text = "Duration: error";
                pnlTimeline.Visibility = Visibility.Collapsed;
            }
            finally
            {
                btnFetchDuration.IsEnabled = true;
                btnFetchDuration.Content = "Fetch Info";
            }
        }

        private async Task<string> RunYtDlpPrint(string args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = ytDlpPath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            var output = new StringBuilder();

            process.OutputDataReceived += (s, a) =>
            {
                if (!string.IsNullOrEmpty(a.Data))
                    output.AppendLine(a.Data);
            };

            process.Start();
            process.BeginOutputReadLine();

            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch { }
            }

            return output.ToString();
        }

        private async Task LoadThumbnailAsync(string thumbnailUrl)
        {
            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
                var bytes = await client.GetByteArrayAsync(thumbnailUrl);

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
                // Fallback: use yt-dlp to download thumbnail to temp file
                try
                {
                    var tempDir = Path.Combine(Path.GetTempPath(), "kleptos_thumb");
                    Directory.CreateDirectory(tempDir);
                    var args = $"--skip-download --write-thumbnail --convert-thumbnails png -o \"{tempDir}\\thumb\" --no-mtime \"{txtURL.Text?.Trim()}\"";
                    await RunYtDlpPrint(args);

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

        // ── Segment Download: Timeline Visualization ──

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

            var row = (Grid)pnlSegmentRows.Children[0];
            var boxes = (TextBox[])row.Tag;
            double startSec = ParseTimeToSeconds(boxes[0].Text);
            double endSec = ParseTimeToSeconds(boxes[1].Text);

            // Draw range bar if both are valid
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

            // Green marker for start
            if (startSec >= 0)
            {
                double x = (startSec / _videoDurationSeconds) * timelineWidth;
                AddMarker(x, timelineHeight, Color.FromRgb(76, 175, 80)); // green
            }

            // Red marker for end
            if (endSec >= 0)
            {
                double x = (endSec / _videoDurationSeconds) * timelineWidth;
                AddMarker(x, timelineHeight, Color.FromRgb(255, 60, 60)); // red
            }
        }

        private void AddMarker(double x, double timelineHeight, Color color)
        {
            // Vertical line
            var line = new System.Windows.Shapes.Rectangle
            {
                Width = 2,
                Height = timelineHeight,
                Fill = new SolidColorBrush(color),
            };
            Canvas.SetLeft(line, Math.Max(0, x - 1));
            Canvas.SetTop(line, 0);
            cnvTimeline.Children.Add(line);

            // Small triangle arrow pointing down
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

        // ── Segment Download: Seek Preview ──

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
            popSeekPreview.HorizontalOffset = x - 30;
            if (!popSeekPreview.IsOpen)
                popSeekPreview.IsOpen = true;
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
            e.Handled = true; // prevent context menu
        }

        private void SetActiveSegmentTime(bool isStart, string timeText)
        {
            if (pnlSegmentRows.Children.Count == 0) AddSegmentRow();

            var row = (Grid)pnlSegmentRows.Children[0];
            var boxes = (TextBox[])row.Tag;

            if (isStart)
                boxes[0].Text = timeText;
            else
                boxes[1].Text = timeText;

            UpdateTimelineVisualization();
        }

        // ── Segment Download: Parsing & Command Building ──

        private static double ParseTimeToSeconds(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return -1;
            input = input.Trim();

            // Try HH:MM:SS or MM:SS
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

            // Try raw seconds
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
                var row = (Grid)pnlSegmentRows.Children[i];
                var boxes = (TextBox[])row.Tag;
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

                if (startSec < 0)
                {
                    error = $"Segment #{i + 1}: invalid start time \"{startText}\".";
                    return new List<SegmentInfo>();
                }
                if (endSec < 0)
                {
                    error = $"Segment #{i + 1}: invalid end time \"{endText}\".";
                    return new List<SegmentInfo>();
                }
                if (startSec >= endSec)
                {
                    error = $"Segment #{i + 1}: start time must be before end time.";
                    return new List<SegmentInfo>();
                }

                segments.Add(new SegmentInfo { StartTime = startText, EndTime = endText });
            }

            if (segments.Count == 0)
                error = "No valid segments defined. Add at least one segment with start and end times.";

            return segments;
        }

        private string BuildSegmentArgs(List<SegmentInfo> segments)
        {
            var sb = new StringBuilder();
            foreach (var seg in segments)
            {
                sb.Append($" --download-sections \"*{seg.StartTime}-{seg.EndTime}\"");
            }
            return sb.ToString();
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

        private string GetFormat(string fileExtension)
        {
            string[] extractableAudioFormats = { "mp3", "aac", "m4a", "wav" };
            string[] videoFormats = { "mp4", "mov", "mkv", "flv", "3gp" };

            if (extractableAudioFormats.Contains(fileExtension))
            {
                return $"--extract-audio --audio-format {fileExtension} -f bestaudio";
            }
            else if (fileExtension == "webm")
            {
                return "-f bestaudio"; // WebM audio-only download
            }
            else if (videoFormats.Contains(fileExtension))
            {
                if (fileExtension == "mp4")
                {
                    return "-S vcodec:h264,res,acodec:aac --merge-output-format mp4";
                }
                else if (fileExtension == "mov")
                {
                    return "-S vcodec:h264,res,acodec:aac --merge-output-format mov";
                }
                else if (fileExtension == "flv")
                {
                    return "-f bestvideo+bestaudio --recode-video flv";
                }
                else
                {
                    return $"-f \"bv*[ext={fileExtension}]+ba[ext=m4a]/b[ext={fileExtension}]\"";
                }
            }
            else if (fileExtension == "ext")
            {
                //return "-f bestvideo+bestaudio";
                //return "-f best";
                return "-f bv*[ext=mp4]+ba[ext=m4a]/bv*+ba/b[ext=mp4]/b";
            }
            else
            {
                txtOutput.Text = "Unsupported format selected!";
                return string.Empty;
            }
        }

        private string GetVideoName(string fileExtension)
        {
            if (fileExtension == "ext")
            {
                fileExtension = "%(ext)s";
            }

            if(cbThumbnailOnly.IsChecked == true)
            {
                if (txtFileName.Text == string.Empty)
                {
                    return $"\"{txtFileOutput.Text}\\%(title)s\"";
                }
                else
                {
                    return $"\"{txtFileOutput.Text}\\{txtFileName.Text}\"";
                }
            }
            else
            {
                if (txtFileName.Text == string.Empty)
                {
                    return $"\"{txtFileOutput.Text}\\%(title)s.{fileExtension}\"";
                }
                else
                {
                    return $"\"{txtFileOutput.Text}\\{txtFileName.Text}.{fileExtension}\"";
                }
            }
        }

        private async Task ExtractCookiesAndRetry(string originalCmd)
        {
            string browserName = GetDefaultBrowser();
            if (string.IsNullOrEmpty(browserName))
            {
                MessageBox.Show(
                    "Your default browser is not supported for cookie extraction.\n\n" +
                    "Supported browsers: Chrome, Edge, Firefox, Brave, Opera, Vivaldi, Chromium.",
                    "Unsupported Browser",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
                return;
            }

            MessageBox.Show("Using browser cookies from: " + browserName, "Info", MessageBoxButton.OK, MessageBoxImage.Information);

            // Modify the original command to use browser cookies directly
            string modifiedCmd = originalCmd + $" --cookies-from-browser {browserName}";
            txtOutput.Text = "Retrying download with browser cookies...";
            await RunCMD(modifiedCmd);
        }

        private void FileOutput_Click(object sender, RoutedEventArgs e)
        {
            OpenFolderDialog openFolderDialog = new OpenFolderDialog();

            openFolderDialog.ShowDialog();

            if (openFolderDialog.FolderName != string.Empty)
            {
                txtFileOutput.Text = openFolderDialog.FolderName;
            }
            else
            {
                SetDefaultOutputLocation();
            }

        }

        private void SetDefaultOutputLocation()
        {
            // Set the default download location to the user's "Downloads" folder
            if (string.IsNullOrEmpty(txtFileOutput.Text))
            {
                // Get the default Downloads folder path
                string downloadsPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Downloads";
                txtFileOutput.Text = downloadsPath;
            }
        }

        /// <summary>
        /// Detects the system's default browser name for yt-dlp.
        /// </summary>
        private string GetDefaultBrowser()
        {
            try
            {
                // Primary: Check current user's HTTP association
                string registryKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice";
                string browserProgId = Registry.GetValue(registryKey, "ProgId", null) as string;

                // Fallback: Check HKEY_CLASSES_ROOT for .html default handler
                if (string.IsNullOrEmpty(browserProgId))
                {
                    string fallbackKey = @"HKEY_CLASSES_ROOT\.html";
                    browserProgId = Registry.GetValue(fallbackKey, null, null) as string;
                }

                if (string.IsNullOrEmpty(browserProgId))
                    return null;

                browserProgId = browserProgId.ToLowerInvariant(); // normalize casing

                // Chromium Browsers
                if (browserProgId.Contains("chrome")) return "chrome";
                if (browserProgId.Contains("chromium")) return "chromium";
                if (browserProgId.Contains("opera")) return "opera";
                if (browserProgId.Contains("edge")) return "edge";
                if (browserProgId.Contains("vivaldi")) return "vivaldi";
                if (browserProgId.Contains("brave")) return "brave";

                // Non-Chromium
                if (browserProgId.Contains("firefox")) return "firefox";
                if (browserProgId.Contains("safari")) return "safari"; // Rare on Windows

                // Optional note: Whale and others not supported by yt-dlp yet
                return null;
            }
            catch
            {
                return null;
            }
        }

        private async void CheckForYTDLPUpdate()
        {
            txtOutput.Text = string.Empty;
            txtOutput.Text = "Status: Checking for updates...";

            try
            {
                // Step 1: Get current yt-dlp version
                string currentVersion = GetCurrentYtDlpVersion();
                if (string.IsNullOrEmpty(currentVersion))
                {
                    txtOutput.Text = "Status: yt-dlp not found or failed to get version.";
                    return;
                }

                // Step 2: Get latest version from GitHub
                string latestVersion = await GetLatestYtDlpVersion();
                if (string.IsNullOrEmpty(latestVersion))
                {
                    txtOutput.Text = "Status: Failed to fetch latest version.";
                    return;
                }

                // Step 3: Compare versions
                if (currentVersion != latestVersion)
                {
                    txtOutput.Text = $"Status: Update found! Downloading {latestVersion}...";
                    await DownloadYtDlpUpdate(latestVersion);
                    txtOutput.Text = $"Status: Updated to {latestVersion} Successfully!";
                }
                else
                {
                    txtOutput.Text = $"Status: yt-dlp is up-to-date (Version: {currentVersion}).";
                }
            }
            catch (Exception ex)
            {
                txtOutput.Text = $"Status: Error - {ex.Message}";
            }
        }

        private string GetCurrentYtDlpVersion()
        {
            if (!File.Exists(ytDlpPath))
                return null;

            ProcessStartInfo processInfo = new ProcessStartInfo
            {
                FileName = ytDlpPath,
                Arguments = "--version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (Process process = new Process { StartInfo = processInfo })
            {
                process.Start();
                string version = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                return process.ExitCode == 0 ? version : null;
            }
        }

        private async Task<string> GetLatestYtDlpVersion()
        {
            using (HttpClient client = new HttpClient())
            {
                // GitHub redirects /latest to the latest release URL
                HttpResponseMessage response = await client.GetAsync(gitHubReleaseUrl, HttpCompletionOption.ResponseHeadersRead);
                string redirectUrl = response.RequestMessage.RequestUri.ToString();
                // Extract version from URL (e.g., "2023.07.06" from "/yt-dlp/yt-dlp/releases/tag/2023.07.06")
                string version = Regex.Match(redirectUrl, @"tag/(.+)$").Groups[1].Value;
                return version;
            }
        }

        private async Task DownloadYtDlpUpdate(string version)
        {
            string downloadUrl = $"https://github.com/yt-dlp/yt-dlp/releases/download/{version}/yt-dlp.exe";
            string tempPath = Path.Combine(Path.GetTempPath(), "yt-dlp.exe");

            using (HttpClient client = new HttpClient())
            {
                byte[] fileBytes = await client.GetByteArrayAsync(downloadUrl);
                await File.WriteAllBytesAsync(tempPath, fileBytes);
            }

            // Replace the existing yt-dlp.exe (ensure it's not in use)
            if (File.Exists(ytDlpPath))
                File.Delete(ytDlpPath);
            File.Move(tempPath, ytDlpPath);
        }

        private async Task CheckForKleptosUpdate()
        {
            // Configure GithubSource with the logger
            GithubSource githubSource = GetGithubSource();

            // Pass the GithubSource and logger to UpdateManager
            UpdateManager manager = new UpdateManager(githubSource);

            if (!manager.IsInstalled)
            {
                MessageBoxResult result = MessageBox.Show(
                    "You cannot check for an update since you're using a version that isn't installed." +
                    "\nPlease go to \nhttps://github.com/Ghilliexyz/Kleptos/releases/latest" +
                    "\nand manually check for a new update or download and install Kleptos instead for auto updates." +
                    $"\n\nCurrent {txtKleptosVersion.Text}",
                    "Kleptos Uninstalled Version Detected",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                // Return if the app isn't installed and or correct Portable version
                return;
            }

            // Check for updates
            var newVersion = await manager.CheckForUpdatesAsync();
            if (newVersion == null)
            {
                return;
            }

            // make this depend if there is a new version or not
            hasUpdate = true;

            // show update button
            ManageUpdateButton();
        }

        private static async Task UpdateKleptos()
        {
            // Initialize your custom logger
            ILogger logger = new FileLogger("log.txt");

            // Configure GithubSource with the logger
            GithubSource githubSource = GetGithubSource();

            // Pass the GithubSource and logger to UpdateManager
            UpdateManager manager = new UpdateManager(githubSource);

            try
            {
                // Check for updates
                var newVersion = await manager.CheckForUpdatesAsync();
                if (newVersion == null)
                {
                    return;
                }

                // Download new version
                await manager.DownloadUpdatesAsync(newVersion);

                // Install new version and restart app
                manager.ApplyUpdatesAndRestart(newVersion);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during update process: {Message}", ex.Message);
            }
        }

        private static GithubSource GetGithubSource()
        {
            return new GithubSource(
                repoUrl: "https://github.com/Ghilliexyz/Kleptos", 
                accessToken: null,
                prerelease: false
            );
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
            {
                this.DragMove();
            }
        }

        private void CopyLogs_Click(object sender, RoutedEventArgs e)
        {
            Clipboard.SetText(txtOutput.Text);
        }

        private async void Update_Click(object sender, RoutedEventArgs e)
        {
            UpdateButton.IsEnabled = false;
            var textBlock = (System.Windows.Controls.TextBlock)UpdateButton.Content;
            textBlock.Text = "Downloading update...";

            try
            {
                await Task.Run(async () => await UpdateKleptos());
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Update failed: {ex.Message}", "Update Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                textBlock.Text = "Update Available";
                UpdateButton.IsEnabled = true;
            }
        }

        private void ManageUpdateButton()
        {
            if (hasUpdate)
            {
                UpdateButton.Visibility = Visibility.Visible;
            }
            else
            {
                UpdateButton.Visibility = Visibility.Collapsed;
            }
        }

        private void FileCookies_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();

            openFileDialog.ShowDialog();

            if (openFileDialog.FileName != string.Empty)
            {
                cookiesTxtFile = openFileDialog.FileName;
            }
        }

        private string GetCookiesArgument()
        {
            if (!string.IsNullOrWhiteSpace(cookiesTxtFile) && File.Exists(cookiesTxtFile))
            {
                return $" --cookies \"{cookiesTxtFile}\"";
            }
            else
            {
                return string.Empty;
            }
        }
    }

    class FileLogger : ILogger
    {
        private string filePath;

        public FileLogger(string filePath)
        {
            this.filePath = filePath;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            var message = formatter(state, exception);
            System.IO.File.AppendAllText(filePath, message + Environment.NewLine);
        }
    }
}