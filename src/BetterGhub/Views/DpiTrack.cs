using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BetterGhub.Views;

/// <summary>Logarithmic 100–25600 DPI slider with draggable stage dots and a DPI Shift diamond.</summary>
internal sealed class DpiTrack : Canvas
{
    public const int Min = 100, Max = 25600, Step = 50;
    public static readonly Color[] StageColors =
    [
        Color.FromRgb(0xE8, 0xEC, 0xF1), Color.FromRgb(0xF2, 0x6B, 0x2A), Color.FromRgb(0x22, 0xD3, 0xB8),
        Color.FromRgb(0xE6, 0xD2, 0x3A), Color.FromRgb(0xC0, 0x4B, 0xE0)
    ];
    private const double Pad = 36, TrackY = 128;

    private List<int> stages = [];
    private int current;
    private int shift;
    private int? dragging; // stage index, or -1 for the shift diamond
    private int? selectedIndex;

    /// <summary>Raised after a drag or click finishes: (stages, current, shift).</summary>
    public event Action<List<int>, int, int>? Changed;
    public event Action<int?>? SelectionChanged;

    public DpiTrack()
    {
        Height = 220;
        Background = Brushes.Transparent;
        ClipToBounds = false;
        SizeChanged += (_, _) => Render();
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        LostMouseCapture += (_, _) => dragging = null;
    }

    public int? SelectedIndex => selectedIndex;

    public void Set(List<int> values, int currentDpi, int shiftDpi)
    {
        stages = values.Order().ToList();
        current = currentDpi;
        shift = shiftDpi;
        if (selectedIndex >= stages.Count) selectedIndex = null;
        Render();
    }

    private double Width0 => Math.Max(10, ActualWidth - Pad * 2);
    private double X(int dpi) => Pad + Math.Log((double)Math.Clamp(dpi, Min, Max) / Min) / Math.Log((double)Max / Min) * Width0;
    private int Dpi(double x)
    {
        double t = Math.Clamp((x - Pad) / Width0, 0, 1);
        double raw = Min * Math.Pow((double)Max / Min, t);
        int snap = raw < 1000 ? Step : raw < 5000 ? 100 : 200;
        return Math.Clamp((int)Math.Round(raw / snap) * snap, Min, Max);
    }

    private void Render()
    {
        Children.Clear();
        if (ActualWidth <= 0) return;
        Brush faint = Ui.Brush("LineStrong");

        // Rail and ticks
        Add(new Rectangle { Width = Width0, Height = 4, RadiusX = 2, RadiusY = 2, Fill = Ui.Brush("Surface2") }, Pad, TrackY - 2);
        foreach (int tick in new[] { 100, 200, 400, 800, 1600, 3200, 6400, 12800, 25600 })
        {
            double x = X(tick);
            Add(new Rectangle { Width = 1.5, Height = 10, Fill = faint }, x - 0.75, TrackY + 10);
            TextBlock label = new() { Text = tick >= 1000 ? $"{tick / 1000.0:0.#}K" : tick.ToString(), FontSize = 11.5, Foreground = Ui.Brush("Faint") };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Add(label, x - label.DesiredSize.Width / 2, TrackY + 24);
        }

        // DPI Shift diamond below the rail
        double sx = X(shift);
        Rectangle diamond = new()
        {
            Width = 13, Height = 13, Fill = Ui.Brush("Muted"), Stroke = Ui.Brush("Bg"), StrokeThickness = 2,
            RenderTransform = new RotateTransform(45, 6.5, 6.5), Cursor = Cursors.SizeWE, ToolTip = $"DPI Shift speed: {shift}"
        };
        diamond.MouseLeftButtonDown += (_, e) => { dragging = -1; CaptureMouse(); e.Handled = true; };
        Add(diamond, sx - 6.5, TrackY + 50);
        TextBlock shiftLabel = new() { Text = $"SHIFT {shift}", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Muted") };
        shiftLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Add(shiftLabel, sx - shiftLabel.DesiredSize.Width / 2, TrackY + 70);

        // Stage dots with value labels above
        bool previousRaised = false;
        for (int i = 0; i < stages.Count; i++)
        {
            int index = i;
            double x = X(stages[i]);
            Color color = StageColors[i % StageColors.Length];
            bool isCurrent = stages[i] == current;
            bool isSelected = selectedIndex == i;
            Ellipse ring = new() { Width = 26, Height = 26, Fill = new SolidColorBrush(Color.FromArgb(isSelected ? (byte)70 : (byte)0, color.R, color.G, color.B)), IsHitTestVisible = false };
            Add(ring, x - 13, TrackY - 13);
            Ellipse dot = new()
            {
                Width = 16, Height = 16, Fill = new SolidColorBrush(color), Stroke = Ui.Brush("Bg"), StrokeThickness = 3,
                Cursor = Cursors.SizeWE, ToolTip = "Drag to change · click to make current"
            };
            dot.MouseLeftButtonDown += (_, e) =>
            {
                dragging = index;
                selectedIndex = index;
                SelectionChanged?.Invoke(index);
                CaptureMouse();
                e.Handled = true;
                Render();
            };
            Add(dot, x - 8, TrackY - 8);

            // Stagger labels that would collide.
            bool raise = i > 0 && x - X(stages[i - 1]) < 54 && !previousRaised;
            previousRaised = raise;
            TextBlock value = new()
            {
                Text = stages[i].ToString(), FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(color),
                TextDecorations = isCurrent ? TextDecorations.Underline : null, Cursor = Cursors.Hand,
                ToolTip = isCurrent ? "Current speed" : "Click to make this the current speed"
            };
            value.MouseLeftButtonUp += (_, e) =>
            {
                current = stages[index];
                selectedIndex = index;
                SelectionChanged?.Invoke(index);
                Changed?.Invoke(stages, current, shift);
                Render();
                e.Handled = true;
            };
            value.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Add(value, x - value.DesiredSize.Width / 2, TrackY - (raise ? 70 : 44));
        }
    }

    private void Add(UIElement element, double left, double top)
    {
        SetLeft(element, left);
        SetTop(element, top);
        Children.Add(element);
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (dragging is not int index || e.LeftButton != MouseButtonState.Pressed) return;
        int value = Dpi(e.GetPosition(this).X);
        if (index == -1) shift = value;
        else
        {
            bool wasCurrent = stages[index] == current;
            stages[index] = value;
            if (wasCurrent) current = value;
        }
        Render();
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (dragging is null) return;
        dragging = null;
        ReleaseMouseCapture();
        int selectedValue = selectedIndex is int s && s < stages.Count ? stages[s] : 0;
        stages = stages.Distinct().Order().ToList();
        if (selectedIndex is not null) selectedIndex = stages.IndexOf(selectedValue);
        Changed?.Invoke(stages, current, shift);
        Render();
    }

    public void Select(int? index)
    {
        selectedIndex = index;
        Render();
    }
}
