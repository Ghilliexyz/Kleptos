using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Kleptos.Models;

namespace Kleptos
{
    /// <summary>Small factories for the code-built parts of the UI (stats, queue, history).</summary>
    public partial class MainWindow
    {
        private TextBlock Text(string text, string cls, double? size = null, IBrush? fg = null)
        {
            var tb = new TextBlock { Text = text, FontFamily = JetBrainsFont };
            tb.Classes.Add(cls);
            if (size is double s) tb.FontSize = s;
            if (fg != null) tb.Foreground = fg;
            return tb;
        }

        private TextBlock Plain(string text, double size, string brushKey = "TextSecondary", string fallback = "#888888")
        {
            return new TextBlock
            {
                Text = text,
                FontFamily = JetBrainsFont,
                FontSize = size,
                Foreground = ResBrush(brushKey, fallback),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        /// <summary>A KPI tile: small label, big value, optional sub line.</summary>
        private Border Tile(string label, string value, string? sub = null, string? tooltip = null)
        {
            var stack = new StackPanel();
            stack.Children.Add(Text(label.ToUpperInvariant(), "TileLabel"));
            stack.Children.Add(Text(value, "TileValue"));
            if (!string.IsNullOrEmpty(sub)) stack.Children.Add(Text(sub, "TileSub"));
            var b = new Border { Child = stack };
            b.Classes.Add("Tile");
            if (tooltip != null) ToolTip.SetTip(b, tooltip);
            return b;
        }

        /// <summary>A row of equally sized tiles with an 8px gutter.</summary>
        private Grid TileRow(params Control[] tiles)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            for (int i = 0; i < tiles.Length; i++)
            {
                if (i > 0) g.ColumnDefinitions.Add(new ColumnDefinition(8, GridUnitType.Pixel));
                g.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
                Grid.SetColumn(tiles[i], i * 2);
                g.Children.Add(tiles[i]);
            }
            return g;
        }

        /// <summary>A rounded card on the input background with a title and optional right-hand note.</summary>
        private Border Card(string title, Control content, string? note = null)
        {
            var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            header.Children.Add(Text(title, "CardTitle"));
            if (!string.IsNullOrEmpty(note))
            {
                var n = Plain(note, 10.5);
                n.HorizontalAlignment = HorizontalAlignment.Right;
                header.Children.Add(n);
            }

            var stack = new StackPanel();
            stack.Children.Add(header);
            stack.Children.Add(content);

            var b = new Border { Child = stack, Margin = new Thickness(0, 0, 0, 12) };
            b.Classes.Add("Tile");
            b.Padding = new Thickness(14, 12);
            return b;
        }

        private IBrush StatusBrush(QueueStatus s) => s switch
        {
            QueueStatus.Succeeded => ResBrush("StatusGood", "#4ecdc4"),
            QueueStatus.Failed => ResBrush("StatusBad", "#ff3c3c"),
            QueueStatus.Downloading => ResBrush("AccentColor", "#ff3c3c"),
            _ => ResBrush("StatusNeutral", "#7a7a7a"),
        };

        /// <summary>Status pill: colored outline + text label, so state is never color alone.</summary>
        private Border Pill(string text, IBrush color)
        {
            return new Border
            {
                BorderBrush = color,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(7, 1),
                MinWidth = 46,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = text,
                    FontFamily = JetBrainsFont,
                    FontSize = 9.5,
                    Foreground = color,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
            };
        }

        /// <summary>A thin progress/proportion bar.</summary>
        private Border MiniBar(double fraction, IBrush fill, double height = 4)
        {
            fraction = System.Math.Clamp(fraction, 0, 1);
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition(fraction, GridUnitType.Star));
            g.ColumnDefinitions.Add(new ColumnDefinition(1 - fraction, GridUnitType.Star));
            var f = new Border { Background = fill, CornerRadius = new CornerRadius(height / 2) };
            g.Children.Add(f);
            return new Border
            {
                Height = height,
                CornerRadius = new CornerRadius(height / 2),
                Background = ResBrush("BackgroundCard", "#1e1e1e"),
                ClipToBounds = true,
                Child = fraction > 0 ? g : null,
            };
        }

        private Button MakeSmallButton(string content, string? tooltip = null)
        {
            var b = new Button
            {
                Content = content,
                Classes = { "Secondary" },
                FontFamily = JetBrainsFont,
                Foreground = ResBrush("TextPrimary", "#e1e1e1"),
                Height = 24,
                Padding = new Thickness(8, 0),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
            };
            if (tooltip != null) ToolTip.SetTip(b, tooltip);
            return b;
        }
    }
}
