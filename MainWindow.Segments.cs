using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Kleptos.Models;
using Kleptos.Services;
using Polygon = Avalonia.Controls.Shapes.Polygon;
using Rectangle = Avalonia.Controls.Shapes.Rectangle;

namespace Kleptos
{
    /// <summary>Segments tab: trim a single video to one or more time ranges.</summary>
    public partial class MainWindow
    {
        private double _videoDurationSeconds;

        /// <summary>Segments apply when the Segments tab is the active tab (same rule as before).</summary>
        private bool HasAnySegmentInput() => rbSegments.IsChecked == true;

        private void AddSegment_Click(object? sender, RoutedEventArgs e)
        {
            AddSegmentRow();
            UpdateTimelineVisualization();
        }

        private void AddSegmentRow()
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            row.ColumnDefinitions.Add(new ColumnDefinition(20, GridUnitType.Pixel));
            row.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            row.ColumnDefinitions.Add(new ColumnDefinition(28, GridUnitType.Pixel));

            var startBox = CreateTimeTextBox("start");
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

            var endBox = CreateTimeTextBox("end");
            Grid.SetColumn(endBox, 2);

            var removeBtn = new Button
            {
                Content = "×",
                Foreground = ResBrush("TextSecondary", "#888888"),
                FontFamily = JetBrainsFont,
                FontSize = 14,
                Margin = new Thickness(4, 0, 0, 0),
            };
            removeBtn.Classes.Add("Secondary");
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

        private TextBox CreateTimeTextBox(string tip)
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
            ToolTip.SetTip(tb, $"{tip} (m:ss or h:mm:ss)");
            tb.Classes.Add("SegmentTime");
            tb.TextChanged += SegmentTime_TextChanged;
            return tb;
        }

        private void SegmentTime_TextChanged(object? sender, TextChangedEventArgs e)
        {
            if (sender is not TextBox tb) return;
            double seconds = Formatting.ParseClock(tb.Text);
            tb.BorderBrush = (string.IsNullOrWhiteSpace(tb.Text) || seconds >= 0)
                ? ResBrush("BorderSubtle", "#333333")
                : ResBrush("AccentColor", "#ff3c3c");

            UpdateTimelineVisualization();
        }

        /// <summary>Fills the Segments tab from the preview card's info, if it matches the URL box.</summary>
        private void ApplyPreviewToSegments()
        {
            var info = _preview;
            if (info == null || info.IsPlaylist || info.Url != (txtURL.Text ?? string.Empty).Trim()) return;

            if (info.DurationSeconds > 0)
            {
                _videoDurationSeconds = info.DurationSeconds;
                txtSegmentDuration.Text = $"Duration: {Formatting.Clock(info.DurationSeconds)}";
                pnlTimeline.IsVisible = true;
                UpdateTimelineVisualization();
            }
            if (!string.IsNullOrWhiteSpace(info.Title))
            {
                txtVideoTitle.Text = info.Title;
                txtVideoTitle.IsVisible = true;
            }
            if (_previewBitmap != null)
            {
                imgThumbnail.Source = _previewBitmap;
                bdrThumbnail.IsVisible = true;
            }
        }

        private async void btnFetchDuration_Click(object? sender, RoutedEventArgs e)
        {
            if (!FileNames.TryGetValidUrl(txtURL.Text, out _, out _))
            {
                txtSegmentDuration.Text = "Duration: enter a URL first";
                return;
            }

            btnFetchDuration.IsEnabled = false;
            btnFetchDuration.Content = "Fetching...";
            txtSegmentDuration.Text = "Duration: fetching...";
            try
            {
                bool previous = _settings.AutoPreview;
                _settings.AutoPreview = true; // a manual fetch works even with auto-preview off
                try
                {
                    _previewKeyShown = string.Empty;
                    await RefreshPreviewAsync();
                }
                finally { _settings.AutoPreview = previous; }

                if (_preview == null || _preview.DurationSeconds <= 0)
                {
                    _videoDurationSeconds = 0;
                    txtSegmentDuration.Text = "Duration: unknown";
                    pnlTimeline.IsVisible = false;
                }
                else
                {
                    ApplyPreviewToSegments();
                }
            }
            finally
            {
                btnFetchDuration.IsEnabled = true;
                btnFetchDuration.Content = "Fetch Info";
            }
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

            foreach (var child in pnlSegmentRows.Children)
            {
                if (child is not Grid row || row.Tag is not TextBox[] boxes) continue;

                double startSec = Formatting.ParseClock(boxes[0].Text);
                double endSec = Formatting.ParseClock(boxes[1].Text);

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

                if (startSec >= 0) AddMarker(startSec / _videoDurationSeconds * timelineWidth, timelineHeight, Color.FromRgb(76, 175, 80));
                if (endSec >= 0) AddMarker(endSec / _videoDurationSeconds * timelineWidth, timelineHeight, Color.FromRgb(255, 60, 60));
            }
        }

        private void AddMarker(double x, double timelineHeight, Color color)
        {
            var line = new Rectangle { Width = 2, Height = timelineHeight, Fill = new SolidColorBrush(color) };
            Canvas.SetLeft(line, Math.Max(0, x - 1));
            Canvas.SetTop(line, 0);
            cnvTimeline.Children.Add(line);

            var triangle = new Polygon
            {
                Points = new Points { new Point(0, 0), new Point(8, 0), new Point(4, 5) },
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
            return Math.Clamp(mouseX / width, 0, 1) * _videoDurationSeconds;
        }

        private void Timeline_MouseMove(object? sender, PointerEventArgs e)
        {
            if (_videoDurationSeconds <= 0) return;

            double x = e.GetPosition(pnlTimeline).X;
            txtSeekTimestamp.Text = Formatting.Clock(GetTimelineSeconds(x));

            // Clamp the popup so it doesn't fall off the left/right edge.
            double width = pnlTimeline.Bounds.Width;
            popSeekPreview.HorizontalOffset = Math.Clamp(x - 30, 0, Math.Max(0, width - 60));

            if (!popSeekPreview.IsOpen) popSeekPreview.IsOpen = true;
        }

        private void Timeline_MouseLeave(object? sender, PointerEventArgs e) => popSeekPreview.IsOpen = false;

        // Left-click sets the start of the last segment, right-click its end.
        private void Timeline_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (_videoDurationSeconds <= 0) return;
            bool right = e.GetCurrentPoint(pnlTimeline).Properties.IsRightButtonPressed;
            double seconds = GetTimelineSeconds(e.GetPosition(pnlTimeline).X);
            SetActiveSegmentTime(isStart: !right, Formatting.Clock(seconds));
            if (right) e.Handled = true;
        }

        private void SetActiveSegmentTime(bool isStart, string timeText)
        {
            if (pnlSegmentRows.Children.Count == 0) AddSegmentRow();

            if (pnlSegmentRows.Children[^1] is not Grid row || row.Tag is not TextBox[] boxes) return;
            if (isStart) boxes[0].Text = timeText;
            else boxes[1].Text = timeText;

            UpdateTimelineVisualization();
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

                double startSec = Formatting.ParseClock(startText);
                double endSec = Formatting.ParseClock(endText);

                if (startSec < 0) { error = $"Segment #{i + 1}: invalid start time \"{startText}\"."; return new List<SegmentInfo>(); }
                if (endSec < 0) { error = $"Segment #{i + 1}: invalid end time \"{endText}\"."; return new List<SegmentInfo>(); }
                if (startSec >= endSec) { error = $"Segment #{i + 1}: start time must be before end time."; return new List<SegmentInfo>(); }

                segments.Add(new SegmentInfo { StartTime = startText, EndTime = endText });
            }

            if (segments.Count == 0)
                error = "No valid segments defined. Add at least one segment with start and end times, or switch away from the Segments tab to download the whole thing.";

            return segments;
        }
    }
}
