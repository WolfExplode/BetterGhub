using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using BetterGhub.Input;

namespace BetterGhub.Views;

/// <summary>
/// Click-through outline drawn over the window a <see cref="WindowPicker"/> is hovering, labelled with its executable.
/// It is layered and transparent to hit testing, so the picker's WindowFromPoint looks straight through it.
/// </summary>
internal sealed class PickHighlight : Window
{
    private readonly TextBlock label = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("OnAccent") };
    private nint handle;

    public PickHighlight()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        // Parked off screen until the first target so the initial Show never flashes.
        Left = Top = -32000;
        Width = Height = 1;

        Color accent = ((SolidColorBrush)Ui.Brush("Accent")).Color;
        Border tag = new()
        {
            Background = Ui.Brush("Accent"), CornerRadius = new CornerRadius(0, 0, 6, 0), Padding = new Thickness(8, 3, 10, 4),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Child = label
        };
        Content = new Border
        {
            BorderBrush = Ui.Brush("Accent"), BorderThickness = new Thickness(3),
            Background = new SolidColorBrush(Color.FromArgb(0x24, accent.R, accent.G, accent.B)),
            Child = tag
        };
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            // WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE on top of the layered style WPF already set.
            _ = SetWindowLongPtr(handle, -20, GetWindowLongPtr(handle, -20) | 0x20 | 0x80 | 0x08000000);
        };
    }

    /// <summary>Outlines <paramref name="target"/>, or hides the outline when there is nothing pickable.</summary>
    public void Track(PickTarget? target)
    {
        if (target is null) { Hide(); return; }
        label.Text = Path.GetFileName(target.Path);
        if (!IsVisible) Show();
        // Twice: moving onto a monitor with another DPI makes WPF apply its own suggested size during the first call.
        for (int i = 0; i < 2; i++)
            _ = SetWindowPos(handle, -1, target.X, target.Y, target.Width, target.Height, 0x0010); // HWND_TOPMOST, SWP_NOACTIVATE
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
