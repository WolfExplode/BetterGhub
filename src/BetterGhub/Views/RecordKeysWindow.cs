using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BetterGhub.Core;
using BetterGhub.Input;

namespace BetterGhub.Views;

/// <summary>
/// Records key and mouse button down/up edges and wheel notches with their timing while this window has focus,
/// swallowing keys so they don't act on Windows. Mouse input is recorded inside the recording area only,
/// so the window's own buttons still work.
/// </summary>
internal sealed class RecordKeysWindow : Window
{
    // Wheel notches in the same direction this close together merge into one scroll step.
    private const int WheelMergeMs = 250;

    private readonly Stopwatch stopwatch = new();
    private readonly HashSet<ushort> held = [];
    private readonly HashSet<int> heldButtons = [];
    private readonly WrapPanel preview = new();
    private readonly ScrollViewer scroller;
    private readonly TextBlock timer = Ui.Text("0.0 s", size: 12, color: "Muted");
    private readonly CheckBox keepDelays = new() { IsChecked = true, Focusable = false };
    private KeyboardHook? hook;
    private long previous;
    public List<MacroStep> Recorded { get; } = [];

    public RecordKeysWindow()
    {
        Title = "Record keystrokes and mouse";
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
        header.Children.Add(Ui.Text("Type the keys for your macro now, and click or scroll inside the box below to record mouse buttons and the wheel. Each press and release is recorded with its timing. Only input while this window is focused is captured.", "Body")
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

        scroller = new ScrollViewer { Content = preview, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        Border area = new()
        {
            Background = Ui.Brush("Bg"), CornerRadius = new CornerRadius(10), Padding = new Thickness(14),
            Child = scroller, Cursor = Cursors.Cross
        };
        root.Children.Add(area);
        Content = root;

        // Mouse input inside the area is recorded rather than acted on (no window drag, no scrolling).
        area.PreviewMouseDown += (_, e) =>
        {
            if (ButtonIndex(e.ChangedButton) is int button && CaptureButton(button, down: true)) area.CaptureMouse();
            e.Handled = true;
        };
        area.PreviewMouseUp += (_, e) =>
        {
            if (ButtonIndex(e.ChangedButton) is int button) CaptureButton(button, down: false);
            if (heldButtons.Count == 0) area.ReleaseMouseCapture();
            e.Handled = true;
        };
        area.PreviewMouseWheel += (_, e) => { CaptureWheel(e.Delta > 0 ? 1 : -1); e.Handled = true; };

        MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch (InvalidOperationException) { } };
        // Fallback for when the hook can't be installed; the hook swallows keys before they get here.
        PreviewKeyDown += (_, e) => { if (!e.IsRepeat) Capture(KeyCapture.VirtualKey(e), down: true); e.Handled = true; };
        PreviewKeyUp += (_, e) => { Capture(KeyCapture.VirtualKey(e), down: false); e.Handled = true; };
        System.Windows.Threading.DispatcherTimer tick = new() { Interval = TimeSpan.FromMilliseconds(100) };
        tick.Tick += (_, _) => timer.Text = $"{stopwatch.Elapsed.TotalSeconds:0.0} s";
        ContentRendered += (_, _) =>
        {
            stopwatch.Start();
            tick.Start();
            Focus();
            // Swallow keys while recording so shortcuts like Alt+Tab or Win are recorded instead of acted on.
            try { hook = new KeyboardHook((key, down) => { if (!IsActive) return false; Capture(key, down); return true; }); }
            catch (System.ComponentModel.Win32Exception) { }
        };
        Closed += (_, _) => { tick.Stop(); hook?.Dispose(); };
    }

    /// <summary>A WPF mouse button as an index into <see cref="MacroStep.MouseButtons"/>.</summary>
    private static int? ButtonIndex(MouseButton button) => button switch
    {
        MouseButton.Left => 0,
        MouseButton.Right => 1,
        MouseButton.Middle => 2,
        MouseButton.XButton1 => 3,
        MouseButton.XButton2 => 4,
        _ => null
    };

    private void Capture(ushort key, bool down)
    {
        if (key == 0) return;
        if (down ? !held.Add(key) : !held.Remove(key)) return;
        Add(new MacroStep { Kind = down ? ActionKind.KeyDown : ActionKind.KeyUp, Value = VirtualKeys.NameOf(key) });
    }

    /// <summary>Records a mouse button edge; false when it repeats the button's current state.</summary>
    private bool CaptureButton(int button, bool down)
    {
        if (down ? !heldButtons.Add(button) : !heldButtons.Remove(button)) return false;
        Add(new MacroStep { Kind = down ? ActionKind.MouseDown : ActionKind.MouseUp, Value = MacroStep.MouseButtons[button] });
        return true;
    }

    private void CaptureWheel(int direction)
    {
        long now = stopwatch.ElapsedMilliseconds;
        if (Recorded.Count > 0 && Recorded[^1] is { Kind: ActionKind.Wheel } last && now - previous <= WheelMergeMs
            && int.TryParse(last.Value, out int ticks) && Math.Sign(ticks) == direction)
        {
            last.Value = (ticks + direction).ToString();
            preview.Children[^1] = StepChips.Build(last, null, false);
            previous = now;
            return;
        }
        Add(new MacroStep { Kind = ActionKind.Wheel, Value = direction.ToString() });
    }

    /// <summary>Appends a step, preceded by the delay since the previous one.</summary>
    private void Add(MacroStep step)
    {
        long now = stopwatch.ElapsedMilliseconds;
        if (Recorded.Count > 0 && now - previous > 0)
        {
            MacroStep delay = new() { Kind = ActionKind.Delay, DelayMs = (int)Math.Min(now - previous, 60000) };
            Recorded.Add(delay);
            preview.Children.Add(StepChips.Build(delay, null, false));
        }
        Recorded.Add(step);
        preview.Children.Add(StepChips.Build(step, null, false));
        scroller.ScrollToEnd();
        previous = now;
    }

    /// <summary>Recorded steps, with any keys or mouse buttons still held released at the end.</summary>
    public List<MacroStep> Result()
    {
        List<MacroStep> steps = keepDelays.IsChecked == true ? [.. Recorded] : Recorded.Where(s => s.Kind != ActionKind.Delay).ToList();
        foreach (ushort key in held)
            steps.Add(new MacroStep { Kind = ActionKind.KeyUp, Value = VirtualKeys.NameOf(key) });
        foreach (int button in heldButtons)
            steps.Add(new MacroStep { Kind = ActionKind.MouseUp, Value = MacroStep.MouseButtons[button] });
        return steps;
    }
}
