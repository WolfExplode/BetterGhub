using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using BetterGhub.Core;

namespace BetterGhub.Views;

/// <summary>Visual building blocks for the macro timeline.</summary>
internal static class StepChips
{
    public enum Family { Key, Text, Mouse, Media, Launch, Delay }

    public static Family FamilyOf(ActionKind kind) => kind switch
    {
        ActionKind.Key or ActionKind.KeyDown or ActionKind.KeyUp => Family.Key,
        ActionKind.Text => Family.Text,
        ActionKind.LeftClick or ActionKind.RightClick or ActionKind.MiddleClick or ActionKind.Wheel => Family.Mouse,
        ActionKind.Launch => Family.Launch,
        ActionKind.Delay => Family.Delay,
        _ => Family.Media
    };

    public static (Color Background, Color Foreground, string Glyph) Palette(Family family) => family switch
    {
        Family.Key => (Color.FromRgb(0x5B, 0x16, 0x28), Color.FromRgb(0xFF, 0xE1, 0xE7), ""),
        Family.Text => (Color.FromRgb(0x4D, 0x3F, 0x0C), Color.FromRgb(0xFF, 0xE9, 0xA8), ""),
        Family.Mouse => (Color.FromRgb(0x5A, 0x2D, 0x0C), Color.FromRgb(0xFF, 0xD9, 0xB8), ""),
        Family.Media => (Color.FromRgb(0x2F, 0x2A, 0x4E), Color.FromRgb(0xDC, 0xD6, 0xFF), ""),
        Family.Launch => (Color.FromRgb(0x39, 0x40, 0x4A), Color.FromRgb(0xE6, 0xEA, 0xF0), ""),
        _ => (Color.FromRgb(0x2E, 0x33, 0x50), Color.FromRgb(0xD6, 0xDC, 0xF5), "")
    };

    public static string Describe(MacroStep step) => step.Kind switch
    {
        ActionKind.Key => string.IsNullOrEmpty(step.Value) ? "Press a key…" : step.Value,
        ActionKind.KeyDown or ActionKind.KeyUp => step.Value,
        ActionKind.Text => step.Value.Length == 0 ? "Type text…" : step.Value.Length > 18 ? step.Value[..17] + "…" : step.Value,
        ActionKind.LeftClick => "Left click",
        ActionKind.RightClick => "Right click",
        ActionKind.MiddleClick => "Middle click",
        ActionKind.Wheel => int.TryParse(step.Value, out int t) && t < 0 ? $"Scroll down {-t}" : $"Scroll up {(int.TryParse(step.Value, out int u) ? u : 1)}",
        ActionKind.VolumeUp => "Volume up",
        ActionKind.VolumeDown => "Volume down",
        ActionKind.Mute => "Mute",
        ActionKind.PlayPause => "Play / pause",
        ActionKind.NextTrack => "Next track",
        ActionKind.PreviousTrack => "Previous track",
        ActionKind.Launch => step.Value.Length == 0 ? "Choose app…" : System.IO.Path.GetFileNameWithoutExtension(step.Value),
        ActionKind.Delay => FormatDelay(step.DelayMs).Value,
        _ => step.Kind.ToString()
    };

    public static (string Value, string Unit) FormatDelay(int ms) =>
        ms >= 1000 ? ((ms / 1000.0).ToString(ms % 1000 == 0 ? "0" : ms % 100 == 0 ? "0.0" : "0.00"), "s") : (ms.ToString(), "ms");

    /// <summary>A timeline element. Key down/up get a ▼/▲ marker like G HUB.</summary>
    public static FrameworkElement Build(MacroStep step, Action? click, bool selected, bool faded = false)
    {
        Family family = FamilyOf(step.Kind);
        FrameworkElement body;
        if (family == Family.Delay)
        {
            (string value, string unit) = FormatDelay(step.DelayMs);
            StackPanel stack = new() { MinWidth = 44, Margin = new Thickness(2, 0, 2, 0) };
            stack.Children.Add(new TextBlock { Text = value, FontSize = 16, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Ui.Brush("Text") });
            stack.Children.Add(new Border { Height = 1.5, Background = Ui.Brush("Muted"), Margin = new Thickness(0, 1, 0, 1), Width = 40 });
            stack.Children.Add(new TextBlock { Text = unit, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Ui.Brush("Muted") });
            body = new Border
            {
                Child = stack, Padding = new Thickness(6, 3, 6, 3), CornerRadius = new CornerRadius(8),
                Background = Brushes.Transparent, BorderThickness = new Thickness(1.5),
                BorderBrush = selected ? Ui.Brush("Accent") : Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center
            };
        }
        else
        {
            (Color background, Color foreground, string glyph) = Palette(family);
            StackPanel content = new() { Orientation = Orientation.Horizontal };
            bool keyEdge = step.Kind is ActionKind.KeyDown or ActionKind.KeyUp;
            if (!keyEdge)
                content.Children.Add(new TextBlock { Text = glyph, FontFamily = Ui.Font("Icons"), FontSize = 12, Foreground = new SolidColorBrush(foreground), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0), Opacity = 0.85 });
            content.Children.Add(new TextBlock { Text = Describe(step), FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(foreground), VerticalAlignment = VerticalAlignment.Center });
            body = new Border
            {
                Background = new SolidColorBrush(background), CornerRadius = new CornerRadius(7), MinWidth = 42, Height = 42,
                Padding = new Thickness(12, 0, 12, 0), Child = content,
                BorderThickness = new Thickness(2), BorderBrush = selected ? Ui.Brush("Accent") : new SolidColorBrush(background),
                VerticalAlignment = VerticalAlignment.Center
            };
            ((StackPanel)((Border)body).Child).HorizontalAlignment = HorizontalAlignment.Center;
            ((Border)body).ToolTip = step.Kind switch
            {
                ActionKind.KeyDown => $"{step.Value} down",
                ActionKind.KeyUp => $"{step.Value} up",
                ActionKind.Text => step.Value,
                ActionKind.Launch => step.Value,
                _ => null
            };
        }

        // Marker rows keep every chip aligned on the same centre line.
        Grid cell = new() { Margin = new Thickness(3, 0, 3, 8), Opacity = faded ? 0.35 : 1, Background = Brushes.Transparent };
        cell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        cell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        cell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        Grid.SetRow(body, 1);
        cell.Children.Add(body);
        if (step.Kind is ActionKind.KeyDown or ActionKind.KeyUp)
        {
            bool up = step.Kind == ActionKind.KeyUp;
            Polygon marker = new()
            {
                Points = up ? [new Point(0, 8), new Point(6, 1), new Point(12, 8)] : [new Point(0, 2), new Point(6, 9), new Point(12, 2)],
                Fill = Ui.Brush("Muted"), HorizontalAlignment = HorizontalAlignment.Center, Width = 12, Height = 10
            };
            Grid.SetRow(marker, up ? 0 : 2);
            cell.Children.Add(marker);
        }
        if (click is not null)
        {
            cell.Cursor = Cursors.Hand;
            cell.MouseLeftButtonUp += (_, e) => { click(); e.Handled = true; };
        }
        return cell;
    }
}
