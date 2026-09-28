using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BetterGhub.Views;

/// <summary>Small factory helpers so code-built views use the theme consistently.</summary>
internal static class Ui
{
    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
    public static Style Style(string key) => (Style)Application.Current.FindResource(key);
    public static FontFamily Font(string key) => (FontFamily)Application.Current.FindResource(key);

    public static TextBlock Text(string text, string? style = null, double? size = null, string? color = null, bool bold = false, bool wrap = false)
    {
        TextBlock block = new() { Text = text };
        if (style is not null) block.Style = Style(style);
        if (size.HasValue) block.FontSize = size.Value;
        if (color is not null) block.Foreground = Brush(color);
        if (bold) block.FontWeight = FontWeights.SemiBold;
        if (wrap) block.TextWrapping = TextWrapping.Wrap;
        return block;
    }

    public static TextBlock Glyph(string glyph, double size = 14, string? color = null)
    {
        TextBlock block = new() { Text = glyph, FontFamily = Font("Icons"), FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        if (color is not null) block.Foreground = Brush(color); // Otherwise inherit from the host control.
        return block;
    }

    public static Button Button(string text, Action click, string style = "Btn", string? glyph = null, string? tooltip = null)
    {
        object content = text;
        if (glyph is not null)
        {
            StackPanel row = new() { Orientation = Orientation.Horizontal };
            row.Children.Add(Glyph(glyph, 12.5));
            if (text.Length > 0) row.Children.Add(new TextBlock { Text = text, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            content = row;
        }
        Button button = new() { Content = content, Style = Style(style) };
        if (tooltip is not null) button.ToolTip = tooltip;
        button.Click += (_, _) => click();
        return button;
    }

    public static StackPanel Row(double spacing, params UIElement[] children)
    {
        StackPanel panel = new() { Orientation = Orientation.Horizontal };
        for (int i = 0; i < children.Length; i++)
        {
            if (i > 0 && children[i] is FrameworkElement element)
                element.Margin = new Thickness(element.Margin.Left + spacing, element.Margin.Top, element.Margin.Right, element.Margin.Bottom);
            panel.Children.Add(children[i]);
        }
        return panel;
    }

    public static T With<T>(this T element, Thickness margin) where T : FrameworkElement
    {
        element.Margin = margin;
        return element;
    }

    public static Border Card(UIElement child, Thickness? padding = null) =>
        new() { Style = Style("Card"), Child = child, Padding = padding ?? new Thickness(20) };

    public static Border Badge(string text, string foreground, string background) =>
        new()
        {
            Background = Brush(background), CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 1, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, Foreground = Brush(foreground), FontSize = 10.5, FontWeight = FontWeights.Bold }
        };

    /// <summary>A small square "M" marker used wherever a macro is referenced.</summary>
    public static Border MacroMark() =>
        new()
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(4), Background = Brush("Hover"), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = "M", FontSize = 11, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("Text") }
        };

    /// <summary>Wraps a text box with a search glyph and placeholder text.</summary>
    public static Grid Placeholder(TextBox box, string placeholder)
    {
        box.Padding = new Thickness(32, 7, 10, 7);
        TextBlock hint = new() { Text = placeholder, Foreground = Brush("Faint"), Margin = new Thickness(34, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        TextBlock icon = Glyph("\uE721", 12, "Faint");
        icon.Margin = new Thickness(12, 0, 0, 0);
        icon.IsHitTestVisible = false;
        box.TextChanged += (_, _) => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Grid grid = new() { Margin = box.Margin };
        box.Margin = new Thickness(0);
        grid.Children.Add(box);
        grid.Children.Add(icon);
        grid.Children.Add(hint);
        return grid;
    }

    public static ScrollViewer Scroll(UIElement content) =>
        new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
}
