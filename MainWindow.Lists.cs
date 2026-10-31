using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Kleptos.Models;
using Kleptos.Services;

namespace Kleptos
{
    public partial class MainWindow
    {
        private readonly List<Task> _extraWorkers = new();

        // ── Queue tab ──

        private void UpdateQueuePanel()
        {
            if (queuePanel == null) return;
            queuePanel.Children.Clear();

            int running = _queue.Count(e => e.Status == QueueStatus.Downloading);
            int waiting = _queue.Count(e => e.Status == QueueStatus.Pending);
            int done = _queue.Count(e => e.Status is QueueStatus.Succeeded or QueueStatus.Skipped);
            int failed = _queue.Count(e => e.Status == QueueStatus.Failed);
            int cancelled = _queue.Count(e => e.Status == QueueStatus.Canceled);
            txtQueueSummary.Text = _queue.Count == 0
                ? "Nothing queued yet."
                : string.Join("  ·  ", new[]
                {
                    running > 0 ? $"{running} running" : null,
                    waiting > 0 ? $"{waiting} waiting" : null,
                    done > 0 ? $"{done} done" : null,
                    failed > 0 ? $"{failed} failed" : null,
                    cancelled > 0 ? $"{cancelled} cancelled" : null,
                }.Where(x => x != null));

            foreach (var entry in _queue)
                queuePanel.Children.Add(BuildQueueRow(entry));
        }

        private Control BuildQueueRow(QueueEntry entry)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            var (label, brush) = entry.Status switch
            {
                QueueStatus.Downloading => ("LIVE", ResBrush("AccentColor", "#ff3c3c")),
                QueueStatus.Pending => ("WAIT", StatusBrush(QueueStatus.Pending)),
                QueueStatus.Succeeded => ("OK", StatusBrush(QueueStatus.Succeeded)),
                QueueStatus.Skipped => ("SKIP", StatusBrush(QueueStatus.Skipped)),
                QueueStatus.Canceled => ("STOP", StatusBrush(QueueStatus.Canceled)),
                _ => ("FAIL", StatusBrush(QueueStatus.Failed)),
            };
            var pill = Pill(label, brush);
            pill.Margin = new Thickness(0, 0, 10, 0);
            pill.VerticalAlignment = VerticalAlignment.Top;
            grid.Children.Add(pill);

            var stack = new StackPanel();
            stack.Children.Add(Plain(entry.Title, 11, "TextPrimary", "#e1e1e1"));

            string sub = entry.Status switch
            {
                QueueStatus.Downloading => DownloadingLine(entry),
                QueueStatus.Failed when !string.IsNullOrEmpty(entry.ErrorMessage) => entry.ErrorMessage,
                QueueStatus.Succeeded when !string.IsNullOrEmpty(entry.ErrorMessage) => "Done, " + entry.ErrorMessage,
                QueueStatus.Pending => "Waiting for a free slot",
                QueueStatus.Skipped => "Already downloaded",
                QueueStatus.Canceled => "Cancelled",
                _ => entry.Url == entry.Title ? "Done" : entry.Url,
            };
            var subBlock = Plain(sub, 10);
            if (entry.Status == QueueStatus.Failed) subBlock.Foreground = ResBrush("StatusBad", "#ff3c3c");
            subBlock.Margin = new Thickness(0, 2, 0, 0);
            ToolTip.SetTip(subBlock, sub);
            stack.Children.Add(subBlock);

            if (entry.Status == QueueStatus.Downloading)
            {
                var bar = MiniBar(entry.OverallPercent / 100.0, ResBrush("AccentColor", "#ff3c3c"), 4);
                bar.Margin = new Thickness(0, 6, 0, 0);
                stack.Children.Add(bar);
            }
            Grid.SetColumn(stack, 1);
            grid.Children.Add(stack);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            var captured = entry;
            switch (entry.Status)
            {
                case QueueStatus.Downloading:
                case QueueStatus.Pending:
                    var cancel = MakeSmallButton("✕", "Cancel this one");
                    cancel.Click += (_, _) => CancelEntry(captured);
                    buttons.Children.Add(cancel);
                    break;
                case QueueStatus.Failed:
                case QueueStatus.Canceled:
                    var retry = MakeSmallButton("↻", "Retry");
                    retry.Click += (_, _) => RetryEntry(captured);
                    buttons.Children.Add(retry);
                    break;
                case QueueStatus.Succeeded:
                    var folder = MakeSmallButton("Folder", "Open the output folder");
                    folder.Click += (_, _) => OpenFolder(_runOptions?.OutputFolder ?? NormalizedOutputFolder());
                    buttons.Children.Add(folder);
                    break;
            }
            Grid.SetColumn(buttons, 2);
            grid.Children.Add(buttons);
            return grid;
        }

        private static string DownloadingLine(QueueEntry e)
        {
            var parts = new List<string> { $"{e.OverallPercent:F0}%" };
            if (e.SpeedMBps > 0) parts.Add(Formatting.Speed(e.SpeedMBps));
            if (e.EtaSeconds is double eta) parts.Add($"ETA {Formatting.Clock(eta)}");
            if (e.PlaylistCount > 1) parts.Add($"item {Math.Max(1, e.PlaylistIndex)} of {e.PlaylistCount}");
            return string.Join("  ·  ", parts);
        }

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

        /// <summary>Retries a failed/cancelled entry. Joins the running batch if there is one.</summary>
        private async void RetryEntry(QueueEntry entry)
        {
            if (entry.Status is not (QueueStatus.Failed or QueueStatus.Canceled)) return;
            entry.Status = QueueStatus.Pending;
            entry.ErrorMessage = string.Empty;

            if (_isDownloading && _runCts != null && _currentSession != null)
            {
                if (!_runEntries.Contains(entry)) _runEntries.Add(entry);
                _currentSession.TotalItems++;
                _pending.Enqueue(entry);
                // Every worker may already have exited its loop; make sure someone picks this up.
                if (_activeJobs.Count < _settings.MaxParallelDownloads)
                    _extraWorkers.Add(WorkerAsync(_runCts.Token));
                UpdateQueuePanel();
                return;
            }

            UpdateQueuePanel();
            await RunEntriesAsync(new List<QueueEntry> { entry });
        }

        private void ClearFinished_Click(object? sender, RoutedEventArgs e)
        {
            _queue.RemoveAll(entry => entry.Status is QueueStatus.Succeeded or QueueStatus.Skipped
                or QueueStatus.Failed or QueueStatus.Canceled);
            UpdateQueuePanel();
        }

        // ── History tab ──

        private void UpdateHistoryPanel()
        {
            if (historyPanel == null) return;
            historyPanel.Children.Clear();
            pnlLifetime.Children.Clear();

            if (_history.Count == 0)
            {
                historyPanel.Children.Add(Plain("Finished downloads show up here.", 11));
                btnClearHistory.IsVisible = false;
                return;
            }
            btnClearHistory.IsVisible = true;

            BuildLifetime();

            const int maxRows = 200;
            foreach (var entry in _history.Take(maxRows))
                historyPanel.Children.Add(BuildHistoryRow(entry));
            if (_history.Count > maxRows)
                historyPanel.Children.Add(Plain($"+ {_history.Count - maxRows} older", 10.5));
        }

        private void BuildLifetime()
        {
            var life = LifetimeStats.From(_history);

            pnlLifetime.Children.Add(TileRow(
                Tile("Downloads", Formatting.Count(life.Total), $"{life.Last7Days} in the last 7 days"),
                Tile("Downloaded", Formatting.Size(life.TotalSizeMB), "all time"),
                Tile("Success rate", $"{life.SuccessRate:F0}%", $"{life.Total - life.Succeeded} failed"),
                Tile("Top site", string.IsNullOrEmpty(life.TopSite) ? "-" : life.TopSite, null)));

            pnlLifetime.Children.Add(BuildDailyChart(life));
        }

        /// <summary>14-day bar chart: one bar per day, hover for the exact count.</summary>
        private Control BuildDailyChart(LifetimeStats life)
        {
            const double chartHeight = 56;
            int max = Math.Max(1, life.Daily.Max());
            var today = DateTime.Today;

            var bars = new Grid { Height = chartHeight };
            for (int i = 0; i < life.Daily.Length; i++)
            {
                if (i > 0) bars.ColumnDefinitions.Add(new ColumnDefinition(4, GridUnitType.Pixel));
                bars.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

                int count = life.Daily[i];
                var day = today.AddDays(i - (life.Daily.Length - 1));
                double h = count == 0 ? 2 : Math.Max(4, count / (double)max * chartHeight);

                // Full-height transparent hit target so thin bars are easy to hover.
                var hit = new Border { Background = Brushes.Transparent };
                var bar = new Border
                {
                    Height = h,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    CornerRadius = new CornerRadius(3, 3, 0, 0),
                    Background = count == 0 ? ResBrush("BorderSubtle", "#333333") : ResBrush("AccentColor", "#ff3c3c"),
                };
                hit.Child = bar;
                ToolTip.SetTip(hit, $"{day:ddd d MMM}: {count} download{(count == 1 ? "" : "s")}");
                Grid.SetColumn(hit, bars.ColumnDefinitions.Count - 1);
                bars.Children.Add(hit);
            }

            var axis = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            axis.Children.Add(Plain(today.AddDays(-13).ToString("d MMM"), 9.5));
            var end = Plain("today", 9.5);
            end.HorizontalAlignment = HorizontalAlignment.Right;
            axis.Children.Add(end);

            var content = new StackPanel();
            content.Children.Add(bars);
            content.Children.Add(axis);
            return Card("Last 14 days", content, $"busiest day: {max}");
        }

        private Control BuildHistoryRow(HistoryEntry entry)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            var pill = Pill(entry.Succeeded ? "OK" : "FAIL", StatusBrush(entry.Succeeded ? QueueStatus.Succeeded : QueueStatus.Failed));
            pill.Margin = new Thickness(0, 0, 10, 0);
            grid.Children.Add(pill);

            var meta = new List<string> { entry.When.ToString("yyyy-MM-dd HH:mm") };
            if (entry.FileSizeMB > 0) meta.Add(Formatting.Size(entry.FileSizeMB));
            var host = LifetimeStats.Host(entry.Url);
            if (host.Length > 0) meta.Add(host);

            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(Plain(entry.Title, 11, "TextPrimary", "#e1e1e1"));
            var sub = Plain(string.Join("  ·  ", meta), 10);
            sub.Margin = new Thickness(0, 2, 0, 0);
            stack.Children.Add(sub);
            ToolTip.SetTip(stack, entry.Url);
            Grid.SetColumn(stack, 1);
            grid.Children.Add(stack);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var captured = entry;
            var folderButton = MakeSmallButton("Folder", "Open the folder it was saved to");
            folderButton.Click += (_, _) => OpenFolder(string.IsNullOrEmpty(captured.Folder) ? NormalizedOutputFolder() : captured.Folder);
            buttons.Children.Add(folderButton);
            var again = MakeSmallButton("↻", "Put this link back in the URL box");
            again.Click += (_, _) => ReDownload(captured);
            buttons.Children.Add(again);
            Grid.SetColumn(buttons, 2);
            grid.Children.Add(buttons);

            return grid;
        }

        // Loads the URL back into the single-download box; does not start the download.
        private void ReDownload(HistoryEntry entry)
        {
            cbMultiDownload.IsChecked = false;
            txtURL.Text = entry.Url;
            rbShowInfo.IsChecked = true;
        }

        private async void ClearHistory_Click(object? sender, RoutedEventArgs e)
        {
            var result = await MessageDialog.ShowAsync(this, "Clear history",
                $"Remove all {_history.Count} history entries? Downloaded files are not touched.", MessageDialogButtons.YesNo);
            if (result != MessageDialogResult.Yes) return;
            _history.Clear();
            Stores.SaveHistory(_history);
            UpdateHistoryPanel();
        }
    }
}
