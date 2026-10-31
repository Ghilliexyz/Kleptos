using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Kleptos.Models;
using Kleptos.Services;

namespace Kleptos
{
    public partial class MainWindow
    {
        private MediaInfo? _preview;
        private Bitmap? _previewBitmap;
        private CancellationTokenSource? _previewCts;
        private DispatcherTimer? _previewTimer;
        private string _previewKeyShown = string.Empty;
        private readonly Dictionary<string, MediaInfo> _previewCache = new();
        private readonly Dictionary<string, Bitmap> _thumbCache = new();
        private bool _updatingYtDlp;

        /// <summary>Debounces URL edits so a preview is fetched once typing/pasting settles.</summary>
        private void SchedulePreview(bool force = false)
        {
            if (force) _previewKeyShown = string.Empty;
            _previewTimer ??= CreatePreviewTimer();
            _previewTimer.Stop();
            _previewTimer.Start();
        }

        private DispatcherTimer CreatePreviewTimer()
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
            t.Tick += async (_, _) =>
            {
                t.Stop();
                await RefreshPreviewAsync();
            };
            return t;
        }

        private string PreviewKey(string url) =>
            url + (cbPlaylist.IsChecked == true && FileNames.LooksLikePlaylist(url) ? "|playlist" : "|single");

        private async Task RefreshPreviewAsync()
        {
            if (!_settings.AutoPreview || cbMultiDownload.IsChecked == true ||
                !FileNames.TryGetValidUrl(txtURL.Text, out var url, out _))
            {
                _previewCts?.Cancel();
                HidePreview();
                return;
            }

            // Don't run yt-dlp while its binary is being replaced; try again shortly.
            if (_updatingYtDlp || !_ytDlp.Exists)
            {
                _previewTimer!.Interval = TimeSpan.FromSeconds(2);
                _previewTimer.Start();
                return;
            }
            _previewTimer!.Interval = TimeSpan.FromMilliseconds(650);

            var key = PreviewKey(url);
            if (key == _previewKeyShown && pnlPreview.IsVisible) return;
            _previewKeyShown = key;

            if (_previewCache.TryGetValue(key, out var cached))
            {
                ShowPreview(cached);
                return;
            }

            _previewCts?.Cancel();
            var cts = new CancellationTokenSource();
            _previewCts = cts;

            ShowPreviewLoading(url);

            bool playlist = key.EndsWith("|playlist", StringComparison.Ordinal);
            var args = new List<string> { "-J", "--no-warnings" };
            if (playlist) { args.Add("--flat-playlist"); args.Add("--yes-playlist"); }
            else args.Add("--no-playlist");
            YtDlpArgsBuilder.AppendCookiesArgs(args, BuildOptions());
            if (!string.IsNullOrWhiteSpace(_settings.Proxy)) { args.Add("--proxy"); args.Add(_settings.Proxy.Trim()); }
            args.Add(url);

            try
            {
                var (stdout, stderr, _) = await _ytDlp.CaptureAsync(args, TimeSpan.FromSeconds(60), cts.Token);
                if (cts.IsCancellationRequested || PreviewKey((txtURL.Text ?? string.Empty).Trim()) != key) return;

                var info = MediaInfo.Parse(stdout, url);
                if (info == null)
                {
                    var err = stderr.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("ERROR:", StringComparison.Ordinal))
                              ?? (string.IsNullOrWhiteSpace(stderr) ? "no details" : stderr.Trim().Split('\n')[0]);
                    ShowPreviewError(url, err.Replace("ERROR: ", string.Empty));
                    return;
                }

                _previewCache[key] = info;
                ShowPreview(info);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                ShowPreviewError(url, ex.Message);
            }
        }

        private void HidePreview()
        {
            pnlPreview.IsVisible = false;
            _preview = null;
            _previewKeyShown = string.Empty;
            pnlFormatPicker.IsVisible = false;
            ApplyPresetEnabledState();
        }

        private void ShowPreviewLoading(string url)
        {
            _preview = null;
            pnlPreview.IsVisible = true;
            imgPreviewThumb.Source = null;
            bdrPreviewDuration.IsVisible = false;
            txtPreviewTitle.Text = url;
            txtPreviewMeta.Text = "Fetching info...";
            txtPreviewStatus.IsVisible = false;
            pnlFormatPicker.IsVisible = false;
            ApplyPresetEnabledState();
        }

        private void ShowPreviewError(string url, string error)
        {
            _preview = null;
            pnlPreview.IsVisible = true;
            imgPreviewThumb.Source = null;
            bdrPreviewDuration.IsVisible = false;
            txtPreviewTitle.Text = url;
            txtPreviewMeta.Text = "Couldn't load a preview. You can still try downloading.";
            txtPreviewStatus.Text = error;
            txtPreviewStatus.Foreground = ResBrush("AccentColor", "#ff3c3c");
            txtPreviewStatus.IsVisible = true;
            ToolTip.SetTip(txtPreviewStatus, error);
            pnlFormatPicker.IsVisible = false;
            ApplyPresetEnabledState();
        }

        private void ShowPreview(MediaInfo info)
        {
            _preview = info;
            pnlPreview.IsVisible = true;
            txtPreviewTitle.Text = string.IsNullOrWhiteSpace(info.Title) ? info.Url : info.Title;
            ToolTip.SetTip(txtPreviewTitle, txtPreviewTitle.Text);
            txtPreviewStatus.IsVisible = false;

            var meta = new List<string>();
            if (info.IsPlaylist)
            {
                meta.Add("Playlist");
                if (info.EntryCount > 0) meta.Add($"{info.EntryCount} items");
                if (!string.IsNullOrEmpty(info.Uploader)) meta.Add(info.Uploader);
                bdrPreviewDuration.IsVisible = false;
            }
            else
            {
                if (!string.IsNullOrEmpty(info.Uploader)) meta.Add(info.Uploader);
                if (info.EstimatedBytes is long b && b > 0) meta.Add($"~{Formatting.SizeFromBytes(b)}");
                if (!string.IsNullOrEmpty(info.Extractor)) meta.Add(info.Extractor);
                bdrPreviewDuration.IsVisible = info.DurationSeconds > 0;
                txtPreviewDuration.Text = Formatting.Clock(info.DurationSeconds);
            }
            txtPreviewMeta.Text = string.Join("  ·  ", meta);

            // Format picker: single videos only.
            var choices = info.IsPlaylist ? new List<FormatChoice>() : info.Formats;
            cmbExactFormat.Items.Clear();
            foreach (var c in choices)
            {
                cmbExactFormat.Items.Add(new ComboBoxItem
                {
                    Content = c.Label,
                    Tag = c,
                    Classes = { "RoundedItem" },
                    FontFamily = JetBrainsFont,
                    FontSize = 11,
                });
            }
            if (cmbExactFormat.Items.Count > 0) cmbExactFormat.SelectedIndex = 0;
            pnlFormatPicker.IsVisible = choices.Count > 1;
            ApplyPresetEnabledState();

            if (info.IsPlaylist && cbPlaylist.IsChecked != true)
                txtPlaylistHint.IsVisible = true;

            _ = LoadPreviewThumbnailAsync(info);
            if (pnlSegmentsTab.IsVisible) ApplyPreviewToSegments();
        }

        private async Task LoadPreviewThumbnailAsync(MediaInfo info)
        {
            imgPreviewThumb.Source = null;
            _previewBitmap = null;
            if (string.IsNullOrWhiteSpace(info.ThumbnailUrl)) return;

            if (!_thumbCache.TryGetValue(info.ThumbnailUrl, out var bmp))
            {
                try
                {
                    var bytes = await SharedHttp.GetByteArrayAsync(info.ThumbnailUrl);
                    bmp = new Bitmap(new MemoryStream(bytes));
                    _thumbCache[info.ThumbnailUrl] = bmp;
                }
                catch
                {
                    return; // no thumbnail is fine
                }
            }

            if (!ReferenceEquals(_preview, info)) return;
            _previewBitmap = bmp;
            imgPreviewThumb.Source = bmp;
            if (pnlSegmentsTab.IsVisible) ApplyPreviewToSegments();
        }

        /// <summary>yt-dlp -f spec chosen in the preview card, or null for Auto / not applicable.</summary>
        private string? SelectedExactFormat()
        {
            if (cmbExactFormat == null || pnlFormatPicker == null || !pnlFormatPicker.IsVisible || _preview == null) return null;
            if (cbMultiDownload.IsChecked == true || cbThumbnailOnly.IsChecked == true) return null;
            if (!string.Equals(_preview.Url, (txtURL.Text ?? string.Empty).Trim(), StringComparison.Ordinal)) return null;
            return ((cmbExactFormat.SelectedItem as ComboBoxItem)?.Tag as FormatChoice)?.Spec;
        }

        private void cmbExactFormat_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            ApplyPresetEnabledState();
            var choice = (cmbExactFormat.SelectedItem as ComboBoxItem)?.Tag as FormatChoice;
            if (choice?.Kind == FormatChoiceKind.Audio && GetSelectedExtension() is "mp4" or "mov" or "mkv" or "flv" or "3gp")
                SelectByContent(cmbFileFormats, "default");
        }
    }
}
