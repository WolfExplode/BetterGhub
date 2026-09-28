using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BetterGhub.Core;
using BetterGhub.Input;

namespace BetterGhub.Views;

/// <summary>Records key down/up edges with their timing while this window has focus.</summary>
internal sealed class RecordKeysWindow : Window
{
    private readonly Stopwatch stopwatch = new();
    private readonly HashSet<ushort> held = [];
    private readonly WrapPanel preview = new();
    private readonly TextBlock timer = Ui.Text("0.0 s", size: 12, color: "Muted");
    private readonly CheckBox keepDelays = new() { IsChecked = true, Focusable = false };
    private long previous;
    public List<MacroStep> Recorded { get; } = [];

    public RecordKeysWindow()
    {
        Title = "Record keystrokes";
        Width = 620;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Ui.Brush("Panel");
        Foreground = Ui.Brush("Text");
        FontFamily = Ui.Font("BodyFont");
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = false;
        BorderBrush = Ui.Brush("LineStrong");
        BorderThickness = new Thickness(1);

        DockPanel root = new() { Margin = new Thickness(26, 22, 26, 22) };
        StackPanel header = new();
        header.Children.Add(Ui.Row(10,
            new System.Windows.Shapes.Ellipse { Width = 12, Height = 12, Fill = Ui.Brush("Danger"), VerticalAlignment = VerticalAlignment.Center },
            Ui.Text("Recording", "H2"), timer));
        timer.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(Ui.Text("Type the keys for your macro now. Each key's press and release is recorded with its timing. Only keys typed while this window is focused are captured.", "Body")
            .With(new Thickness(0, 8, 0, 14)));
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        keepDelays.Style = Ui.Style("Switch");
        keepDelays.Content = "Keep recorded delays";
        Button cancel = Ui.Button("Cancel", () => { DialogResult = false; }, "GhostBtn");
        Button done = Ui.Button("Done", () => { DialogResult = true; }, "PrimaryBtn");
        cancel.Focusable = false;
        done.Focusable = false;
        DockPanel footer = new() { Margin = new Thickness(0, 16, 0, 0) };
        StackPanel buttons = Ui.Row(8, cancel, done);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(keepDelays);
        keepDelays.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        Border area = new()
        {
            Background = Ui.Brush("Bg"), CornerRadius = new CornerRadius(10), Padding = new Thickness(14),
            Child = new ScrollViewer { Content = preview, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false }
        };
        root.Children.Add(area);
        Content = root;

        MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch (InvalidOperationException) { } };
        PreviewKeyDown += (_, e) => { Capture(e, down: true); e.Handled = true; };
        PreviewKeyUp += (_, e) => { Capture(e, down: false); e.Handled = true; };
        System.Windows.Threading.DispatcherTimer tick = new() { Interval = TimeSpan.FromMilliseconds(100) };
        tick.Tick += (_, _) => timer.Text = $"{stopwatch.Elapsed.TotalSeconds:0.0} s";
        ContentRendered += (_, _) => { stopwatch.Start(); tick.Start(); Focus(); };
        Closed += (_, _) => tick.Stop();
    }

    private void Capture(KeyEventArgs e, bool down)
    {
        if (e.IsRepeat) return;
        ushort key = KeyCapture.VirtualKey(e);
        if (key == 0) return;
        if (down ? !held.Add(key) : !held.Remove(key)) return;
        long now = stopwatch.ElapsedMilliseconds;
        if (Recorded.Count > 0 && now - previous > 0)
        {
            MacroStep delay = new() { Kind = ActionKind.Delay, DelayMs = (int)Math.Min(now - previous, 60000) };
            Recorded.Add(delay);
            preview.Children.Add(StepChips.Build(delay, null, false));
        }
        MacroStep step = new() { Kind = down ? ActionKind.KeyDown : ActionKind.KeyUp, Value = VirtualKeys.NameOf(key) };
        Recorded.Add(step);
        preview.Children.Add(StepChips.Build(step, null, false));
        previous = now;
    }

    /// <summary>Recorded steps, with any keys still held released at the end.</summary>
    public List<MacroStep> Result()
    {
        List<MacroStep> steps = keepDelays.IsChecked == true ? [.. Recorded] : Recorded.Where(s => s.Kind != ActionKind.Delay).ToList();
        foreach (ushort key in held)
            steps.Add(new MacroStep { Kind = ActionKind.KeyUp, Value = VirtualKeys.NameOf(key) });
        return steps;
    }
}
