using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Kleptos.Models;
using Kleptos.Services;

namespace Kleptos
{
    /// <summary>The Progress tab: overall bar, KPI tiles, outcome bar, speed chart and file list.</summary>
    public partial class MainWindow
    {
        private const int MaxSpeedSamples = 240;

        private DispatcherTimer? _statsTimer;
        private Canvas? _speedCanvas;
        private double? _speedHoverX;

        private void InitStatsTimer()
        {
            _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _statsTimer.Tick += (_, _) => OnStatsTick();
        }

        private void StartStatsTimer() => _statsTimer?.Start();
        private void StopStatsTimer() => _statsTimer?.Stop();

        private double AggregateSpeedMBps() =>
            _runEntries.Where(e => e.Status == QueueStatus.Downloading).Sum(e => e.SpeedMBps);

        private void OnStatsTick()
        {
            var s = _currentSession;
            if (s == null || s.IsFrozen) return;

            // Speed history for the chart. Down-sample by halving when it gets long so a
            // multi-hour run still fits a fixed number of points.
            s.SpeedSamples.Add(AggregateSpeedMBps());
            if (s.SpeedSamples.Count > MaxSpeedSamples)
            {
                var merged = new List<double>(s.SpeedSamples.Count / 2 + 1);
                for (int i = 0; i + 1 < s.SpeedSamples.Count; i += 2)
                    merged.Add((s.SpeedSamples[i] + s.SpeedSamples[i + 1]) / 2);
                s.SpeedSamples.Clear();
                s.SpeedSamples.AddRange(merged);
                s.SampleIntervalSeconds *= 2;
            }

            RenderOverallProgress();
            UpdateLiveStatus();
            if (pnlShowInfo.IsVisible) UpdateStatsPanel();
            if (pnlQueue.IsVisible) UpdateQueuePanel();
        }

        private double OverallPercent()
        {
            if (_runEntries.Count == 0) return 0;

            // After a run: share of items that reached an outcome (cancelled ones did not).
            if (!_isDownloading && _currentSession is { } done)
            {
                int total = Math.Max(done.TotalItems, done.Items.Count);
                int decided = done.SuccessCount + done.FailCount + done.AlreadyDownloadedCount;
                return total > 0 ? Math.Clamp(decided * 100.0 / total, 0, 100) : 0;
            }

            double sum = 0;
            foreach (var e in _runEntries)
            {
                sum += e.Status switch
                {
                    QueueStatus.Pending or QueueStatus.Canceled => 0,
                    QueueStatus.Downloading => e.OverallPercent,
                    _ => 100,
                };
            }
            return Math.Clamp(sum / _runEntries.Count, 0, 100);
        }

        private void RenderOverallProgress()
        {
            double pct = OverallPercent();
            txtProgressPercent.Text = $"{pct:F0}%";
            double w = progressBarTrack.Bounds.Width;
            progressBarFill.Width = w > 0 ? w * pct / 100.0 : 0;
        }

        private void UpdateLiveStatus()
        {
            var active = _runEntries.Where(e => e.Status == QueueStatus.Downloading).ToList();
            double speed = active.Sum(e => e.SpeedMBps);

            if (active.Count == 1)
            {
                var e = active[0];
                var eta = e.EtaSeconds is double secs ? $"  ·  ETA {Formatting.Clock(secs)}" : string.Empty;
                txtSpeedEta.Text = speed > 0 ? Formatting.Speed(speed) + eta : string.Empty;

                var item = e.PlaylistCount > 1 ? $" (item {Math.Max(1, e.PlaylistIndex)} of {e.PlaylistCount})" : string.Empty;
                txtCurrentStatus.Text = $"Downloading: {e.Title}{item}";
            }
            else if (active.Count > 1)
            {
                txtSpeedEta.Text = $"{active.Count} active  ·  {Formatting.Speed(speed)}";
                int done = _runEntries.Count(x => x.Status is QueueStatus.Succeeded or QueueStatus.Failed or QueueStatus.Skipped);
                txtCurrentStatus.Text = $"Downloading {active.Count} at once  ·  {done} of {_runEntries.Count} finished";
            }
            else if (_isDownloading)
            {
                txtSpeedEta.Text = string.Empty;
                txtCurrentStatus.Text = "Working...";
            }
        }

        // ── Panel ──

        private void UpdateStatsPanel()
        {
            if (pnlStats == null) return;
            pnlStats.Children.Clear();

            var s = _currentSession;
            if (s == null)
            {
                BuildEmptyState();
                return;
            }

            double liveMB = _activeJobs.Sum(j => j.LiveMB);
            double totalMB = s.TotalSizeMB + liveMB;
            int finished = s.CompletedItems;
            int total = Math.Max(s.TotalItems, s.Items.Count);
            bool running = !s.IsFrozen;

            double currentSpeed = running ? AggregateSpeedMBps() : 0;
            double avgSpeed = s.SpeedSamples.Where(v => v > 0).DefaultIfEmpty(s.AverageSpeedMBps).Average();

            string filesSub = running
                ? (_activeJobs.Count > 0 ? $"{_activeJobs.Count} in progress" : "starting...")
                : s.FailCount > 0 ? $"{s.FailCount} failed"
                : s.CancelledCount > 0 || finished < total ? "cancelled"
                : "all finished";
            if (!running) finished -= s.CancelledCount;

            var largest = s.Items.Where(i => i.FileSizeMB > 0).OrderByDescending(i => i.FileSizeMB).FirstOrDefault();

            pnlStats.Children.Add(TileRow(
                Tile("Files", $"{finished} / {total}", filesSub),
                Tile("Downloaded", Formatting.Size(totalMB),
                    largest != null && s.Items.Count > 1 ? $"largest {Formatting.Size(largest.FileSizeMB)}" : (running ? "so far" : "total"),
                    largest != null ? $"Largest: {largest.FileName}" : null),
                Tile(running ? "Speed now" : "Avg speed",
                    Formatting.Speed(running ? currentSpeed : avgSpeed),
                    s.PeakSpeedMBps > 0 ? $"peak {Formatting.Speed(s.PeakSpeedMBps)}" : null),
                Tile("Elapsed", Formatting.Duration(s.TotalDuration),
                    s.Items.Count > 1 && s.AverageItemDuration > TimeSpan.Zero ? $"~{Formatting.Duration(s.AverageItemDuration)} per file" : null)));

            if (finished > 0) pnlStats.Children.Add(BuildOutcomeCard(s));
            if (s.SpeedSamples.Count >= 2) pnlStats.Children.Add(BuildSpeedCard(s));
            if (s.Items.Count > 0) pnlStats.Children.Add(BuildFilesCard(s));
        }

        private void BuildEmptyState()
        {
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 30, 0, 0), MaxWidth = 420 };
            var title = Text("Nothing downloading yet", "CardTitle", 13);
            title.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(title);
            var hint = Text("Paste a link above and hit Download. Live speed, sizes and a per-file breakdown show up here.", "RowHint");
            hint.TextAlignment = TextAlignment.Center;
            hint.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(hint);

            if (_history.Count > 0)
            {
                var life = LifetimeStats.From(_history);
                var line = Text($"All time: {Formatting.Count(life.Total)} downloads  ·  {Formatting.Size(life.TotalSizeMB)}", "RowHint");
                line.HorizontalAlignment = HorizontalAlignment.Center;
                line.Margin = new Thickness(0, 16, 0, 0);
                stack.Children.Add(line);
            }
            pnlStats.Children.Add(stack);
        }

        /// <summary>Segmented bar (done / failed / skipped) with a labelled legend.</summary>
        private Control BuildOutcomeCard(SessionStats s)
        {
            var parts = new List<(int Count, string Label, IBrush Brush)>
            {
                (s.SuccessCount, "downloaded", ResBrush("StatusGood", "#4ecdc4")),
                (s.FailCount, "failed", ResBrush("StatusBad", "#ff3c3c")),
                (s.AlreadyDownloadedCount, "skipped", ResBrush("StatusNeutral", "#7a7a7a")),
            };
            int remaining = Math.Max(0, Math.Max(s.TotalItems, s.Items.Count) - s.CompletedItems + s.CancelledCount);
            var nonZero = parts.Where(p => p.Count > 0).ToList();

            var bar = new Grid { Height = 10 };
            int col = 0;
            foreach (var p in nonZero)
            {
                if (col > 0) bar.ColumnDefinitions.Add(new ColumnDefinition(2, GridUnitType.Pixel)); // 2px surface gap
                bar.ColumnDefinitions.Add(new ColumnDefinition(p.Count, GridUnitType.Star));
                var seg = new Border { Background = p.Brush, CornerRadius = new CornerRadius(3) };
                ToolTip.SetTip(seg, $"{p.Count} {p.Label}");
                Grid.SetColumn(seg, bar.ColumnDefinitions.Count - 1);
                bar.Children.Add(seg);
                col++;
            }
            if (remaining > 0)
            {
                if (col > 0) bar.ColumnDefinitions.Add(new ColumnDefinition(2, GridUnitType.Pixel));
                bar.ColumnDefinitions.Add(new ColumnDefinition(remaining, GridUnitType.Star));
                var rest = new Border { Background = ResBrush("BackgroundCard", "#1e1e1e"), CornerRadius = new CornerRadius(3) };
                ToolTip.SetTip(rest, s.IsFrozen ? $"{remaining} not downloaded (cancelled)" : $"{remaining} still to go");
                Grid.SetColumn(rest, bar.ColumnDefinitions.Count - 1);
                bar.Children.Add(rest);
            }

            var legend = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            foreach (var p in parts.Where(p => p.Count > 0))
                legend.Children.Add(LegendItem(p.Brush, $"{p.Count} {p.Label}"));
            if (remaining > 0)
                legend.Children.Add(LegendItem(ResBrush("BorderSubtle", "#333333"),
                    s.IsFrozen ? $"{remaining} not downloaded" : $"{remaining} to go"));

            var content = new StackPanel();
            content.Children.Add(bar);
            content.Children.Add(legend);

            int decided = s.SuccessCount + s.FailCount;
            string? note = decided > 0 ? $"{s.SuccessCount * 100.0 / decided:F0}% success" : null;
            return Card("Outcome", content, note);
        }

        private Control LegendItem(IBrush brush, string text)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0) };
            sp.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = brush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            sp.Children.Add(Plain(text, 10.5, "TextPrimary", "#e1e1e1"));
            return sp;
        }

        // ── Speed chart ──

        private Control BuildSpeedCard(SessionStats s)
        {
            if (_speedCanvas == null)
            {
                _speedCanvas = new Canvas { Height = 72, Background = Brushes.Transparent, ClipToBounds = true, Cursor = new Cursor(StandardCursorType.Cross) };
                _speedCanvas.PointerMoved += (_, e) => { _speedHoverX = e.GetPosition(_speedCanvas).X; DrawSpeedChart(); };
                _speedCanvas.PointerExited += (_, _) => { _speedHoverX = null; DrawSpeedChart(); };
                _speedCanvas.PropertyChanged += (_, e) => { if (e.Property == BoundsProperty) DrawSpeedChart(); };
            }
            (_speedCanvas.Parent as Panel)?.Children.Remove(_speedCanvas);

            var axis = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            axis.Children.Add(Plain("0:00", 9.5));
            var end = Plain(Formatting.Clock(s.SpeedSamples.Count * s.SampleIntervalSeconds), 9.5);
            end.HorizontalAlignment = HorizontalAlignment.Right;
            axis.Children.Add(end);

            var content = new StackPanel();
            content.Children.Add(_speedCanvas);
            content.Children.Add(axis);

            DrawSpeedChart();
            return Card("Speed over time", content, $"peak {Formatting.Speed(s.PeakSpeedMBps)}");
        }

        private void DrawSpeedChart()
        {
            var c = _speedCanvas;
            var s = _currentSession;
            if (c == null || s == null) return;
            c.Children.Clear();

            var data = s.SpeedSamples;
            double w = c.Bounds.Width, h = c.Bounds.Height;
            if (data.Count < 2 || w <= 0 || h <= 0) return;

            double max = Math.Max(data.Max(), 0.01) * 1.15;
            double X(int i) => i * w / (data.Count - 1);
            double Y(double v) => h - v / max * (h - 4) - 1;

            // Recessive gridline at the midpoint.
            c.Children.Add(new Line { StartPoint = new Point(0, h / 2), EndPoint = new Point(w, h / 2), Stroke = ResBrush("BorderSubtle", "#333333"), StrokeThickness = 1, StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 3, 3 } });

            var accent = ResBrush("AccentColor", "#ff3c3c");
            var line = new Points();
            var area = new Points { new Point(0, h) };
            for (int i = 0; i < data.Count; i++)
            {
                var p = new Point(X(i), Y(data[i]));
                line.Add(p);
                area.Add(p);
            }
            area.Add(new Point(w, h));

            c.Children.Add(new Polygon
            {
                Points = area,
                Fill = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Color.FromArgb(90, 255, 60, 60), 0), new GradientStop(Color.FromArgb(0, 255, 60, 60), 1) },
                },
            });
            c.Children.Add(new Polyline { Points = line, Stroke = accent, StrokeThickness = 2, StrokeJoin = PenLineJoin.Round });

            // Crosshair + readout.
            if (_speedHoverX is double hx && hx >= 0 && hx <= w)
            {
                int i = (int)Math.Round(hx / w * (data.Count - 1));
                i = Math.Clamp(i, 0, data.Count - 1);
                double x = X(i), y = Y(data[i]);
                c.Children.Add(new Line { StartPoint = new Point(x, 0), EndPoint = new Point(x, h), Stroke = ResBrush("TextSecondary", "#888888"), StrokeThickness = 1 });
                var dot = new Ellipse { Width = 8, Height = 8, Fill = accent, Stroke = ResBrush("BackgroundInput", "#252525"), StrokeThickness = 2 };
                Canvas.SetLeft(dot, x - 4);
                Canvas.SetTop(dot, y - 4);
                c.Children.Add(dot);

                var label = new Border
                {
                    Background = ResBrush("BackgroundCard", "#1e1e1e"),
                    BorderBrush = ResBrush("BorderSubtle", "#333333"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2),
                    Child = Plain($"{Formatting.Speed(data[i])} at {Formatting.Clock(i * s.SampleIntervalSeconds)}", 10, "TextPrimary", "#e1e1e1"),
                };
                Canvas.SetLeft(label, x > w * 0.6 ? Math.Max(0, x - 150) : x + 8);
                Canvas.SetTop(label, 2);
                c.Children.Add(label);
            }
        }

        // ── Files ──

        private Control BuildFilesCard(SessionStats s)
        {
            var list = new StackPanel();
            double largest = Math.Max(0.001, s.Items.Max(i => i.FileSizeMB));
            var activeItems = _activeJobs.Select(j => j.Current).ToHashSet();

            const int maxRows = 300;
            foreach (var item in s.Items.Take(maxRows))
            {
                string pillText; IBrush pillBrush;
                if (activeItems.Contains(item)) { pillText = "LIVE"; pillBrush = ResBrush("AccentColor", "#ff3c3c"); }
                else if (item.Cancelled) { pillText = "STOP"; pillBrush = StatusBrush(QueueStatus.Canceled); }
                else if (item.AlreadyDownloaded) { pillText = "SKIP"; pillBrush = StatusBrush(QueueStatus.Skipped); }
                else if (item.Succeeded) { pillText = "OK"; pillBrush = StatusBrush(QueueStatus.Succeeded); }
                else if (!item.IsFinished) { pillText = "WAIT"; pillBrush = StatusBrush(QueueStatus.Pending); }
                else { pillText = "FAIL"; pillBrush = StatusBrush(QueueStatus.Failed); }

                var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                row.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
                row.ColumnDefinitions.Add(new ColumnDefinition(74, GridUnitType.Pixel));
                row.ColumnDefinitions.Add(new ColumnDefinition(58, GridUnitType.Pixel));

                var pill = Pill(pillText, pillBrush);
                pill.Margin = new Thickness(0, 0, 10, 0);
                row.Children.Add(pill);

                var name = !string.IsNullOrEmpty(item.FileName) ? StripFormatSuffix(item.FileName)
                    : !string.IsNullOrEmpty(item.Label) ? item.Label
                    : item.Url.Length > 0 ? item.Url : "Unknown";
                var nameStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                nameStack.Children.Add(Plain(name, 11, "TextPrimary", "#e1e1e1"));
                double mb = activeItems.Contains(item) ? item.FileSizeMB + (_activeJobs.FirstOrDefault(j => j.Current == item)?.LiveMB ?? 0) : item.FileSizeMB;
                if (mb > 0)
                {
                    var bar = MiniBar(mb / largest, ResBrush("StatusNeutral", "#7a7a7a"), 3);
                    bar.Margin = new Thickness(0, 4, 12, 0);
                    nameStack.Children.Add(bar);
                }
                Grid.SetColumn(nameStack, 1);
                row.Children.Add(nameStack);

                var size = Plain(mb > 0 ? Formatting.Size(mb) : "-", 10.5);
                size.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetColumn(size, 2);
                row.Children.Add(size);

                var dur = item.IsFinished ? item.Duration : DateTime.Now - item.StartTime;
                var time = Plain(item.StartTime == default ? "-" : Formatting.Duration(dur), 10.5);
                time.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetColumn(time, 3);
                row.Children.Add(time);

                var tip = name;
                if (!string.IsNullOrEmpty(item.ErrorMessage)) tip += "\n" + item.ErrorMessage;
                if (item.SpeedMBps > 0) tip += $"\nAvg {Formatting.Speed(item.SpeedMBps)}";
                ToolTip.SetTip(row, tip);

                list.Children.Add(row);
            }
            if (s.Items.Count > maxRows)
                list.Children.Add(Plain($"+ {s.Items.Count - maxRows} more", 10.5));

            return Card($"Files ({s.Items.Count})", list);
        }
    }
}
