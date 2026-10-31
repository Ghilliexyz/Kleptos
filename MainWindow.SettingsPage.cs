using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Kleptos.Models;
using Kleptos.Services;

namespace Kleptos
{
    /// <summary>Settings page, cookie source UI, and yt-dlp / ffmpeg management.</summary>
    public partial class MainWindow
    {
        private bool _syncingTemplate;

        private void InitSettingsPage()
        {
            foreach (var p in FilenameTemplates.Presets)
            {
                cmbTemplatePreset.Items.Add(new ComboBoxItem
                {
                    Content = p.Name,
                    Tag = p.Template,
                    Classes = { "RoundedItem" },
                    FontFamily = JetBrainsFont,
                });
            }
            cmbTemplatePreset.Items.Add(new ComboBoxItem { Content = "Custom", Tag = null, Classes = { "RoundedItem" }, FontFamily = JetBrainsFont });

            txtTemplate.TextChanged += (_, _) =>
            {
                UpdateTemplatePreview();
                if (_syncingTemplate) return;
                SyncTemplatePresetFromText();
            };
            txtArchivePath.LostFocus += (_, _) => { ReadSettingsPage(); UpdateArchiveInfo(); };
            cmbChannel.SelectionChanged += async (_, _) =>
            {
                if (_loadingSettings) return;
                var channel = SelectedChannel();
                if (channel == _settings.YtDlpChannel) return;
                _settings.YtDlpChannel = channel;
                SaveSettings();
                await CheckForYtDlpUpdateAsync(userInitiated: true);
            };
            cbAutoPreview.IsCheckedChanged += (_, _) =>
            {
                if (_loadingSettings) return;
                _settings.AutoPreview = cbAutoPreview.IsChecked == true;
                SchedulePreview(force: true);
            };
        }

        private void ApplySettingsPage()
        {
            SelectByTag(cmbParallel, _settings.MaxParallelDownloads.ToString());
            if (cmbParallel.SelectedItem == null) cmbParallel.SelectedIndex = 1;
            SelectByTag(cmbFragments, _settings.ConcurrentFragments.ToString());
            if (cmbFragments.SelectedItem == null) cmbFragments.SelectedIndex = 2;
            SelectByTag(cmbRetries, _settings.Retries.ToString());
            if (cmbRetries.SelectedItem == null) cmbRetries.SelectedIndex = 2;
            txtRateLimit.Text = _settings.RateLimit;
            txtProxy.Text = _settings.Proxy;

            _syncingTemplate = true;
            txtTemplate.Text = _settings.FilenameTemplate;
            _syncingTemplate = false;
            SyncTemplatePresetFromText();
            UpdateTemplatePreview();

            txtArchivePath.Text = EffectiveArchivePath();
            txtBrowserProfile.Text = _settings.BrowserProfile;
            SelectByTag(cmbChannel, _settings.YtDlpChannel.ToString());
            cbAutoPreview.IsChecked = _settings.AutoPreview;
            cbClipboard.IsChecked = _settings.ClipboardAutoPaste;
        }

        /// <summary>Copies the Settings page controls into <see cref="_settings"/>.</summary>
        private void ReadSettingsPage()
        {
            if (cmbParallel == null) return;
            int Tag(ComboBox c, int fallback) =>
                int.TryParse((c.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var v) ? v : fallback;

            _settings.MaxParallelDownloads = Tag(cmbParallel, 2);
            _settings.ConcurrentFragments = Tag(cmbFragments, 4);
            _settings.Retries = Tag(cmbRetries, 10);

            var rate = (txtRateLimit.Text ?? string.Empty).Trim();
            _settings.RateLimit = FileNames.IsValidRateLimit(rate) ? rate : _settings.RateLimit;
            _settings.Proxy = (txtProxy.Text ?? string.Empty).Trim();

            var template = (txtTemplate.Text ?? string.Empty).Trim();
            _settings.FilenameTemplate = string.IsNullOrEmpty(template) ? "%(title)s" : template;

            var archive = (txtArchivePath.Text ?? string.Empty).Trim();
            _settings.ArchivePath = archive == AppPaths.DefaultArchivePath ? string.Empty : archive;
            _settings.BrowserProfile = (txtBrowserProfile.Text ?? string.Empty).Trim();
            _settings.YtDlpChannel = SelectedChannel();
            _settings.AutoPreview = cbAutoPreview.IsChecked == true;
            _settings.ClipboardAutoPaste = cbClipboard.IsChecked == true;
        }

        private YtDlpChannel SelectedChannel() =>
            (cmbChannel.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Nightly" ? YtDlpChannel.Nightly : YtDlpChannel.Stable;

        private void RefreshSettingsPageStatus()
        {
            UpdateArchiveInfo();
            UpdateFfmpegStatus();
            _ = UpdateYtDlpVersionTextAsync();

            bool badRate = !FileNames.IsValidRateLimit(txtRateLimit.Text);
            txtRateLimit.BorderBrush = badRate ? ResBrush("AccentColor", "#ff3c3c") : null;
        }

        // ── Filename template ──

        private void cmbTemplatePreset_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_syncingTemplate) return;
            if ((cmbTemplatePreset.SelectedItem as ComboBoxItem)?.Tag is string template)
            {
                _syncingTemplate = true;
                txtTemplate.Text = template;
                _syncingTemplate = false;
                UpdateTemplatePreview();
            }
        }

        private void SyncTemplatePresetFromText()
        {
            var text = (txtTemplate.Text ?? string.Empty).Trim();
            _syncingTemplate = true;
            var match = cmbTemplatePreset.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag as string == text)
                        ?? cmbTemplatePreset.Items.OfType<ComboBoxItem>().Last();
            cmbTemplatePreset.SelectedItem = match;
            _syncingTemplate = false;
        }

        private void UpdateTemplatePreview()
        {
            var t = (txtTemplate.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(t)) t = "%(title)s";
            txtTemplatePreview.Text = "Preview: " + Path.Combine(NormalizedOutputFolder(), FilenameTemplates.Preview(t));
        }

        // ── Download archive ──

        private void UpdateArchiveInfo()
        {
            var path = (txtArchivePath.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(path)) path = AppPaths.DefaultArchivePath;
            try
            {
                if (File.Exists(path))
                {
                    int count = File.ReadLines(path).Count(l => !string.IsNullOrWhiteSpace(l));
                    txtArchiveInfo.Text = $"{count} video{(count == 1 ? "" : "s")} recorded.";
                }
                else
                {
                    txtArchiveInfo.Text = "Empty. It's created on the first download with 'Skip already downloaded' on.";
                }
            }
            catch (Exception ex)
            {
                txtArchiveInfo.Text = "Can't read the archive: " + ex.Message;
            }
        }

        private async void ArchiveBrowse_Click(object? sender, RoutedEventArgs e)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Choose or create a download archive",
                SuggestedFileName = "archive.txt",
                DefaultExtension = "txt",
                ShowOverwritePrompt = false,
            });
            var path = file?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;
            txtArchivePath.Text = path;
            ReadSettingsPage();
            UpdateArchiveInfo();
            SaveSettings();
        }

        private void ArchiveOpen_Click(object? sender, RoutedEventArgs e)
        {
            var path = EffectiveArchivePath();
            if (File.Exists(path)) OpenFile(path);
            else OpenFolder(Path.GetDirectoryName(path) ?? AppPaths.DataDir);
        }

        private async void ArchiveClear_Click(object? sender, RoutedEventArgs e)
        {
            var path = EffectiveArchivePath();
            if (!File.Exists(path)) return;
            var result = await MessageDialog.ShowAsync(this, "Clear download archive",
                "Forget every recorded video? Next time, everything will be downloaded again. Files on disk are not touched.",
                MessageDialogButtons.YesNo);
            if (result != MessageDialogResult.Yes) return;
            try { File.WriteAllText(path, string.Empty); }
            catch (Exception ex) { ShowError("Could not clear the archive", ex); }
            UpdateArchiveInfo();
        }

        // ── Cookies ──

        private CookieSource SelectedCookieSource()
        {
            var tag = (cmbCookieSource?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            return Enum.TryParse<CookieSource>(tag, out var src) ? src : CookieSource.None;
        }

        private void cmbCookieSource_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            UpdateCookieUi();
            if (_loadingSettings) return;
            _previewCache.Clear(); // previews may differ when signed in
            SaveSettings();
        }

        private void UpdateCookieUi()
        {
            if (pnlCookieFile == null || txtCookieHint == null) return;
            var src = SelectedCookieSource();
            pnlCookieFile.IsVisible = src == CookieSource.File;

            switch (src)
            {
                case CookieSource.None:
                    txtCookieHint.IsVisible = false;
                    break;
                case CookieSource.File:
                    txtCookieHint.IsVisible = true;
                    txtCookieHint.Text = string.IsNullOrEmpty(cookiesTxtFile)
                        ? "Choose an exported cookies.txt."
                        : CookieFileStatus(cookiesTxtFile);
                    break;
                default:
                    txtCookieHint.IsVisible = true;
                    bool chromium = src is CookieSource.Chrome or CookieSource.Edge or CookieSource.Brave or CookieSource.Opera or CookieSource.Vivaldi or CookieSource.Chromium;
                    txtCookieHint.Text = $"Uses your {src} login." +
                        (chromium && OperatingSystem.IsWindows() ? " Close the browser fully if yt-dlp can't read its cookies." : string.Empty) +
                        (src == CookieSource.Safari && OperatingSystem.IsMacOS() ? " Kleptos may need Full Disk Access." : string.Empty);
                    break;
            }
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
                btnCookies.Content = Path.GetFileName(cookiesTxtFile);
                ToolTip.SetTip(btnCookies, cookiesTxtFile);
                UpdateCookieUi();
                SaveSettings();
            }
        }

        private static string CookieFileStatus(string path)
        {
            if (!File.Exists(path)) return "File not found. Choose it again.";
            var report = CookiesInspector.Inspect(path);
            if (report.ParseFailed) return "Couldn't read the file to check expiry.";
            if (report.Total == 0) return "No cookie entries in this file. Re-export it from your browser.";

            var sb = new StringBuilder();
            if (report.Expired > 0) sb.Append($"{report.Expired} expired");
            if (report.ExpiringSoon > 0)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append($"{report.ExpiringSoon} expiring within 7 days");
            }
            if (sb.Length > 0) return $"Warning: {sb} of {report.Total}. Stale cookies cause sign-in errors; consider re-exporting.";
            return report.EarliestUpcomingExpiry is DateTime next
                ? $"All {report.Total} cookies valid (earliest expiry {next:yyyy-MM-dd})."
                : $"{report.Total} session cookies.";
        }

        // ── yt-dlp ──

        private async Task UpdateYtDlpVersionTextAsync()
        {
            var v = await _ytDlp.GetVersionAsync();
            txtYtDlpVersion.Text = v == null ? "Not installed yet." : $"Installed: {v} ({_settings.YtDlpChannel.ToString().ToLowerInvariant()} channel)";
        }

        private async void CheckYtDlp_Click(object? sender, RoutedEventArgs e) => await CheckForYtDlpUpdateAsync(userInitiated: true);

        private async Task CheckForYtDlpUpdateAsync(bool userInitiated)
        {
            if (_updatingYtDlp) return;
            if (_isDownloading && userInitiated)
            {
                txtYtDlpVersion.Text = "Wait for the current downloads to finish, then update.";
                return;
            }

            _updatingYtDlp = true;
            btnCheckYtDlp.IsEnabled = false;
            var channel = _settings.YtDlpChannel;
            try
            {
                AppendOutput($"Checking yt-dlp ({channel.ToString().ToLowerInvariant()})...\n");
                txtYtDlpVersion.Text = "Checking...";

                string? current = await _ytDlp.GetVersionAsync();
                string? latest = await _tools.GetLatestYtDlpVersionAsync(channel);
                if (string.IsNullOrEmpty(latest))
                {
                    AppendOutput("Could not fetch the latest yt-dlp version (offline?).\n");
                    txtYtDlpVersion.Text = current == null ? "Not installed, and GitHub is unreachable." : $"Installed: {current} (couldn't check for updates)";
                    return;
                }

                if (!ToolManager.SameVersion(current, latest))
                {
                    AppendOutput($"Updating yt-dlp {current ?? "(none)"} -> {latest}...\n");
                    txtYtDlpVersion.Text = $"Downloading {latest}...";
                    var progress = new Progress<double>(p => txtYtDlpVersion.Text = $"Downloading {latest}... {p:F0}%");
                    await _tools.DownloadYtDlpAsync(channel, latest, progress);
                    AppendOutput($"yt-dlp updated to {latest}.\n");
                }
                else
                {
                    AppendOutput($"yt-dlp is up to date ({current}).\n");
                }
                await UpdateYtDlpVersionTextAsync();
            }
            catch (Exception ex)
            {
                AppendOutput("yt-dlp update failed: " + ex.Message + "\n");
                txtYtDlpVersion.Text = "Update failed: " + ex.Message;
            }
            finally
            {
                _updatingYtDlp = false;
                btnCheckYtDlp.IsEnabled = true;
            }
        }

        // ── ffmpeg ──

        private void UpdateFfmpegStatus()
        {
            var path = PlatformHelper.FindFfmpeg();
            if (path == null)
            {
                txtFfmpegStatus.Text = ToolManager.FfmpegSources() == null
                    ? "Not found. Install it with your package manager."
                    : "Not found. Kleptos can download a copy for you.";
                btnInstallFfmpeg.Content = "Download ffmpeg";
                btnInstallFfmpeg.IsVisible = ToolManager.FfmpegSources() != null;
            }
            else
            {
                bool managed = path.StartsWith(AppPaths.ToolsDir, StringComparison.OrdinalIgnoreCase);
                txtFfmpegStatus.Text = managed ? $"Installed by Kleptos: {path}" : $"Found: {path}";
                btnInstallFfmpeg.Content = "Re-download";
                btnInstallFfmpeg.IsVisible = managed;
            }
        }

        private async void InstallFfmpeg_Click(object? sender, RoutedEventArgs e) => await InstallFfmpegAsync();

        private async Task InstallFfmpegAsync()
        {
            btnInstallFfmpeg.IsEnabled = false;
            try
            {
                txtFfmpegStatus.Text = "Downloading ffmpeg...";
                AppendOutput("Downloading ffmpeg...\n");
                var progress = new Progress<double>(p => txtFfmpegStatus.Text = $"Downloading ffmpeg... {p:F0}%");
                var path = await _tools.InstallFfmpegAsync(progress);
                AppendOutput($"ffmpeg installed: {path}\n");
            }
            catch (Exception ex)
            {
                AppendOutput("ffmpeg download failed: " + ex.Message + "\n");
                txtFfmpegStatus.Text = "Download failed: " + ex.Message;
                btnInstallFfmpeg.IsEnabled = true;
                return;
            }
            btnInstallFfmpeg.IsEnabled = true;
            UpdateFfmpegStatus();
        }

        /// <summary>On first launch without ffmpeg, offer to fetch it (once; the Settings page can do it later).</summary>
        private async Task CheckFfmpegOnStartupAsync()
        {
            if (PlatformHelper.FindFfmpeg() != null) return;
            AppendOutput("ffmpeg not found (needed for merging, audio conversion and trimming).\n");
            if (_settings.FfmpegPromptDismissed || ToolManager.FfmpegSources() == null) return;

            var result = await MessageDialog.ShowAsync(this, "ffmpeg is missing",
                "Kleptos needs ffmpeg to merge video and audio, convert to mp3/m4a, trim segments and embed thumbnails.\n\n" +
                "Download a copy now? It goes into Kleptos's own folder (about 30-80 MB) and doesn't touch your system.\n\n" +
                "You can also do this later from Settings > Tools.",
                MessageDialogButtons.YesNo);

            _settings.FfmpegPromptDismissed = true;
            SaveSettings();
            if (result == MessageDialogResult.Yes) await InstallFfmpegAsync();
        }
    }
}
