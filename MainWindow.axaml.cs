using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Kleptos.Models;
using Kleptos.Services;

namespace Kleptos
{
    /// <summary>
    /// Main window. The logic is split across partial files:
    ///   MainWindow.axaml.cs       lifecycle, settings load/save, tabs, window chrome
    ///   MainWindow.Downloads.cs   queue runner (parallel), output handling
    ///   MainWindow.Preview.cs     URL preview card and exact-format picker
    ///   MainWindow.Stats.cs       Progress tab: KPI tiles, outcome bar, speed chart, file list
    ///   MainWindow.Lists.cs       Queue and History tabs
    ///   MainWindow.Segments.cs    Segments tab (trim timeline)
    ///   MainWindow.SettingsPage.cs Settings page, yt-dlp / ffmpeg management
    ///   MainWindow.Ui.cs          small control factories shared by the above
    /// Everything that does not touch controls lives in Services/ and is unit tested.
    /// </summary>
    public partial class MainWindow : Window
    {
        // Single shared HttpClient for the lifetime of the app.
        private static readonly HttpClient SharedHttp = CreateHttpClient();
        private static HttpClient CreateHttpClient()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("Kleptos/1.0 (+https://github.com/Ghilliexyz/Kleptos)");
            return h;
        }

        private readonly YtDlpRunner _ytDlp = new(ToolManager.YtDlpPath);
        private readonly ToolManager _tools = new(SharedHttp);

        private KleptosSettings _settings = new();
        private List<HistoryEntry> _history = new();
        private string cookiesTxtFile = string.Empty;
        private bool hasUpdate;
        private string? _lastAutoFilledUrl;
        // True until the saved settings have been pushed into the controls. XAML-set
        // selections fire change events during InitializeComponent; without this guard
        // those would save defaults over the user's settings file before it is applied.
        private bool _loadingSettings = true;

        public MainWindow()
        {
            InitializeComponent();

            _settings = Stores.LoadSettings();

            var v = Assembly.GetExecutingAssembly().GetName().Version;
            if (v != null) txtKleptosVersion.Text = $"v{v.Major}.{v.Minor}.{v.Build}";

            Opened += MainWindow_Loaded;
            Closing += MainWindow_Closing;
            Activated += OnWindowActivated;

            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            AddHandler(DragDrop.DropEvent, OnDrop);

            txtURL.TextChanged += (_, _) => { UpdatePlaylistHint(); SchedulePreview(); };
            cbPlaylist.IsCheckedChanged += (_, _) => { UpdatePlaylistHint(); SchedulePreview(force: true); };
            cbMultiDownload.IsCheckedChanged += (_, _) => SchedulePreview(force: true);
            cbThumbnailOnly.IsCheckedChanged += (_, _) => ApplyPresetEnabledState();
            cmbFileFormats.SelectionChanged += (_, _) => ApplyPresetEnabledState();

            InitSettingsPage();
            InitStatsTimer();
        }

        // ── Clipboard / drag-and-drop ──

        private void UpdatePlaylistHint()
        {
            txtPlaylistHint.IsVisible = FileNames.LooksLikePlaylist(txtURL.Text) && cbPlaylist.IsChecked != true
                                        && cbMultiDownload.IsChecked != true;
        }

        // Auto-paste a URL from the clipboard whenever the window regains focus. Only
        // overwrites the URL box when it is empty or still holds the previous auto-fill.
        private async void OnWindowActivated(object? sender, EventArgs e)
        {
            if (!_settings.ClipboardAutoPaste || _isDownloading) return;
            try
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard == null) return;

                var text = (await clipboard.GetTextAsync())?.Trim();
                if (string.IsNullOrEmpty(text) || !FileNames.UrlRegex.IsMatch(text) || text.Contains('\n')) return;

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

            var urls = FileNames.UrlRegex.Matches(text).Select(m => m.Value).ToList();
            if (urls.Count == 0) return;

            if (urls.Count > 1 || cbMultiDownload.IsChecked == true)
            {
                cbMultiDownload.IsChecked = true;
                var existing = (txtMultiUrls.Text ?? string.Empty).TrimEnd();
                var added = string.Join(Environment.NewLine, urls);
                txtMultiUrls.Text = existing.Length == 0 ? added : existing + Environment.NewLine + added;
            }
            else
            {
                txtURL.Text = urls[0];
            }
        }

        // ── Window state / tray ──

        // Minimizing mid-download hides to the tray; the TrayIcon menu ("Show Kleptos") restores it.
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

        private async void MainWindow_Loaded(object? sender, EventArgs e)
        {
            try
            {
                pnlTimeline.PropertyChanged += (_, args) =>
                {
                    if (args.Property == BoundsProperty)
                        UpdateTimelineVisualization();
                };
                progressBarTrack.PropertyChanged += (_, args) =>
                {
                    if (args.Property == BoundsProperty) RenderOverallProgress();
                };

                ApplySettingsToUi();
                SetDefaultOutputLocation();
                _history = Stores.LoadHistory();
                UpdateHistoryPanel();
                UpdateStatsPanel();
                UpdateQueuePanel();

                if (!_ytDlp.Exists)
                    AppendOutput($"{PlatformHelper.YtDlpBinaryName} not found. Attempting to download...\n");

                // Fire-and-forget Kleptos update probe (silent if no update is available).
                _ = CheckForKleptosUpdateAsync();

                // Await the yt-dlp check so its progress lines flow into the console in order.
                await CheckForYtDlpUpdateAsync(userInitiated: false);

                await CheckFfmpegOnStartupAsync();
            }
            catch (Exception ex)
            {
                ShowError("Startup error", ex);
            }
        }

        private void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
        {
            try { _runCts?.Cancel(); } catch { }
            try { _previewCts?.Cancel(); } catch { }
            try { SaveSettings(); } catch { }
        }

        // ── Small UI helpers ──

        private const int MaxConsoleChars = 400_000;

        private void AppendOutput(string text)
        {
            var current = txtOutput.Text ?? string.Empty;
            // Keep the console from growing without bound on long playlist runs.
            if (current.Length > MaxConsoleChars)
                current = "[...older output trimmed...]\n" + current.Substring(current.Length - MaxConsoleChars / 2);
            txtOutput.Text = current + text;
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

        private void SaveSettings()
        {
            if (_loadingSettings) return;
            try
            {
                _settings.OutputFolder = txtFileOutput?.Text ?? string.Empty;
                _settings.LastFormat = ComboText(cmbFileFormats) ?? "default";
                _settings.LastQuality = ComboText(cmbQuality) ?? "best";
                _settings.LastPreset = ComboText(cmbPreset) ?? "Normal";
                _settings.LastCookiesFile = cookiesTxtFile;
                _settings.CookieSource = SelectedCookieSource();
                _settings.MultiDownload = cbMultiDownload?.IsChecked == true;
                _settings.ThumbnailOnly = cbThumbnailOnly?.IsChecked == true;
                _settings.PlaylistMode = cbPlaylist?.IsChecked == true;
                _settings.PlaylistItems = txtPlaylistItems?.Text?.Trim() ?? string.Empty;
                _settings.UseDownloadArchive = cbArchive?.IsChecked == true;
                _settings.DownloadSubs = cbSubs?.IsChecked == true;
                _settings.SubLangs = (cmbSubLangs?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "en";
                _settings.SponsorBlock = cbSponsorBlock?.IsChecked == true;
                ReadSettingsPage();

                Stores.SaveSettings(_settings);
            }
            catch { /* never crash on settings save */ }
        }

        private static string? ComboText(ComboBox? combo) =>
            (combo?.SelectedItem as ComboBoxItem)?.Content?.ToString();

        private static void SelectByContent(ComboBox combo, string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            // ComboBoxItem.IsSelected only propagates for realized containers; select via the ComboBox.
            var item = combo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals(i.Content?.ToString(), text, StringComparison.OrdinalIgnoreCase));
            if (item != null) combo.SelectedItem = item;
        }

        private static bool SelectByTag(ComboBox combo, string? tag)
        {
            var item = combo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals(i.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase));
            if (item != null) combo.SelectedItem = item;
            return item != null;
        }

        private void ApplySettingsToUi()
        {
            _loadingSettings = true;
            try
            {
                if (!string.IsNullOrWhiteSpace(_settings.OutputFolder))
                    txtFileOutput.Text = _settings.OutputFolder;

                SelectByContent(cmbFileFormats, _settings.LastFormat);
                SelectByContent(cmbQuality, _settings.LastQuality);
                SelectByContent(cmbPreset, _settings.LastPreset);
                ApplyPresetEnabledState();

                if (!string.IsNullOrWhiteSpace(_settings.LastCookiesFile) && File.Exists(_settings.LastCookiesFile))
                {
                    cookiesTxtFile = _settings.LastCookiesFile;
                    btnCookies.Content = Path.GetFileName(cookiesTxtFile);
                }
                SelectByTag(cmbCookieSource, _settings.CookieSource.ToString());
                UpdateCookieUi();

                cbMultiDownload.IsChecked = _settings.MultiDownload;
                cbThumbnailOnly.IsChecked = _settings.ThumbnailOnly;
                cbPlaylist.IsChecked = _settings.PlaylistMode;
                txtPlaylistItems.Text = _settings.PlaylistItems;
                cbArchive.IsChecked = _settings.UseDownloadArchive;
                cbSubs.IsChecked = _settings.DownloadSubs;
                if (!SelectByTag(cmbSubLangs, _settings.SubLangs)) cmbSubLangs.SelectedIndex = 1; // "en"
                cbSponsorBlock.IsChecked = _settings.SponsorBlock;

                ApplySettingsPage();
            }
            finally
            {
                _loadingSettings = false;
            }
        }

        // ── Tabs / pages ──

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
                UpdateStatsPanel();
            }
            else if (ReferenceEquals(sender, rbConsole))
            {
                pnlConsole.IsVisible = true;
                scrollViewer.ScrollToEnd();
            }
            else if (ReferenceEquals(sender, rbSegments))
            {
                pnlSegmentsTab.IsVisible = true;
                if (pnlSegmentRows.Children.Count == 0)
                    AddSegmentRow();
                ApplyPreviewToSegments();
            }
            else if (ReferenceEquals(sender, rbQueue))
            {
                pnlQueue.IsVisible = true;
                UpdateQueuePanel();
            }
            else if (ReferenceEquals(sender, rbHistory))
            {
                pnlHistory.IsVisible = true;
                UpdateHistoryPanel();
            }
        }

        private void ShowPage(Control? page)
        {
            pnlHelp.IsVisible = ReferenceEquals(page, pnlHelp);
            pnlSettings.IsVisible = ReferenceEquals(page, pnlSettings);
            pnlMain.IsVisible = page == null;
        }

        private void Help_Click(object? sender, RoutedEventArgs e) =>
            ShowPage(pnlHelp.IsVisible ? null : pnlHelp);

        private void HelpBack_Click(object? sender, RoutedEventArgs e) => ShowPage(null);

        private void Settings_Click(object? sender, RoutedEventArgs e)
        {
            if (pnlSettings.IsVisible)
            {
                SettingsBack_Click(sender, e);
                return;
            }
            RefreshSettingsPageStatus();
            ShowPage(pnlSettings);
        }

        private void SettingsBack_Click(object? sender, RoutedEventArgs e)
        {
            SaveSettings();
            ShowPage(null);
        }

        private static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
        }

        private void Support_Click(object? sender, RoutedEventArgs e) => OpenUrl("https://buymeacoffee.com/ghillie");

        private void HelpExtension_Click(object? sender, RoutedEventArgs e) =>
            OpenUrl("https://chromewebstore.google.com/detail/cclelndahbckbenkjhflpdbgdldlbecc");

        // ── Output folder ──

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
                SaveSettings();
            }
            else
            {
                SetDefaultOutputLocation();
            }
        }

        private void OpenOutput_Click(object? sender, RoutedEventArgs e) => OpenFolder(NormalizedOutputFolder());

        private void SetDefaultOutputLocation()
        {
            if (string.IsNullOrEmpty(txtFileOutput.Text))
                txtFileOutput.Text = AppPaths.DefaultDownloadsFolder;
        }

        private string NormalizedOutputFolder()
        {
            var s = (txtFileOutput.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(s)) s = AppPaths.DefaultDownloadsFolder;
            return s.TrimEnd('\\', '/');
        }

        private static void OpenFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
            try
            {
                if (OperatingSystem.IsWindows())
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
                else if (OperatingSystem.IsMacOS())
                    Process.Start("open", new[] { folder });
                else
                    Process.Start("xdg-open", new[] { folder });
            }
            catch { /* best effort */ }
        }

        private static void OpenFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch { }
        }

        // ── Kleptos auto-update via Velopack ──

        private async Task CheckForKleptosUpdateAsync()
        {
            try
            {
                var manager = new Velopack.UpdateManager(GetGithubSource());
                if (!manager.IsInstalled) return; // Portable build: skip silently.

                var updateInfo = await manager.CheckForUpdatesAsync();
                var currentVersion = manager.CurrentVersion;
                var targetVersion = updateInfo?.TargetFullRelease?.Version;

                Debug.WriteLine($"Kleptos update probe: installed={currentVersion}, candidate={targetVersion?.ToString() ?? "<none>"}");

                // Only flag when strictly newer; Velopack can return equal-version "updates".
                if (updateInfo == null || targetVersion == null || currentVersion == null) return;
                if (targetVersion <= currentVersion) return;

                hasUpdate = true;
                UpdateButton.IsVisible = hasUpdate;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Kleptos update check failed: " + ex);
            }
        }

        private static async Task UpdateKleptosAsync()
        {
            var logger = new FileLogger(AppPaths.LogPath);
            try
            {
                var manager = new Velopack.UpdateManager(GetGithubSource());
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
                logger.Write($"Error during update process: {ex.Message}");
                throw;
            }
        }

        private static Velopack.Sources.GithubSource GetGithubSource() => new(
            repoUrl: "https://github.com/Ghilliexyz/Kleptos",
            accessToken: null,
            prerelease: false);

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

        // ── Window chrome ──

        private void MinimizeButton_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();

        private void TitleBar_MouseLeftButtonDown(object? sender, PointerPressedEventArgs e)
        {
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

    /// <summary>Tiny append-only log used by the self-updater.</summary>
    internal sealed class FileLogger
    {
        private readonly string _filePath;

        public FileLogger(string filePath)
        {
            _filePath = filePath;
            try { Directory.CreateDirectory(Path.GetDirectoryName(filePath)!); } catch { }
        }

        public void Write(string message)
        {
            try { File.AppendAllText(_filePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}"); }
            catch { }
        }
    }
}
