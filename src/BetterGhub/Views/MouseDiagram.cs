using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using BetterGhub.Core;
using BetterGhub.Services;

namespace BetterGhub.Views;

/// <summary>The G502 X artwork with a callout per control, like G HUB's assignment view.</summary>
internal sealed class MouseDiagram : Viewbox
{
    private const double CanvasWidth = 880, CanvasHeight = 700;
    private const double LabelWidth = 200, LeftColumn = 0, RightColumn = CanvasWidth - LabelWidth;
    private static readonly Dictionary<MouseView, BitmapSource> Artwork = [];

    private readonly MouseService service;
    private readonly Canvas canvas = new() { Width = CanvasWidth, Height = CanvasHeight };
    private readonly Dictionary<string, Callout> callouts = [];

    public MouseView View { get; set; } = MouseView.Top;
    public MouseControl? Selected { get; set; }
    public bool ShiftLayer { get; set; }
    /// <summary>Callouts show each control's signal instead of its action.</summary>
    public bool CalibrationMode { get; set; }
    public event Action<MouseControl>? ControlClicked;

    public MouseDiagram(MouseService service)
    {
        this.service = service;
        Stretch = Stretch.Uniform;
        StretchDirection = StretchDirection.Both;
        Child = canvas;
    }

    public void Rebuild()
    {
        canvas.Children.Clear();
        callouts.Clear();
        (BitmapSource image, double scale, double left, double top) = Layout(View);
        Image picture = new() { Source = image, Width = image.PixelWidth * scale, Height = image.PixelHeight * scale };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
        Canvas.SetLeft(picture, left);
        Canvas.SetTop(picture, top);
        canvas.Children.Add(picture);

        foreach (MouseControl control in MouseControls.All.Where(c => c.View == View))
        {
            Point dot = new(left + control.ImageX * scale, top + control.ImageY * scale);
            Callout callout = new(this, control, dot);
            callouts[control.Id] = callout;
        }
        foreach (Callout callout in callouts.Values) callout.AddTo(canvas);
        foreach (int bit in service.Pressed) SetPressed(bit, true);
    }

    private static (BitmapSource Image, double Scale, double Left, double Top) Layout(MouseView view)
    {
        BitmapSource image = DarkArtwork(view);
        if (view == MouseView.Top)
        {
            double scale = 640.0 / image.PixelHeight;
            return (image, scale, (CanvasWidth - image.PixelWidth * scale) / 2, 30);
        }
        double sideScale = 520.0 / image.PixelWidth;
        return (image, sideScale, (CanvasWidth - image.PixelWidth * sideScale) / 2, (CanvasHeight - image.PixelHeight * sideScale) / 2);
    }

    /// <summary>Recolours the white line drawings into a dark, low-glare version for the dark theme.</summary>
    private static BitmapSource DarkArtwork(MouseView view)
    {
        if (Artwork.TryGetValue(view, out BitmapSource? cached)) return cached;
        string name = view == MouseView.Top ? "mouse-top.png" : "mouse-side.png";
        BitmapImage source = new(new Uri($"pack://application:,,,/Assets/{name}"));
        FormatConvertedBitmap bgra = new(source, PixelFormats.Bgra32, null, 0);
        int stride = bgra.PixelWidth * 4;
        byte[] pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        // Light fill → body colour; dark ink → outline colour.
        (double r, double g, double b) body = (24, 28, 35), ink = (120, 131, 146);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] == 0) continue;
            double t = 1 - (pixels[i] + pixels[i + 1] + pixels[i + 2]) / (3 * 255.0);
            t = Math.Clamp(t * 1.15, 0, 1);
            pixels[i] = (byte)(body.b + (ink.b - body.b) * t);
            pixels[i + 1] = (byte)(body.g + (ink.g - body.g) * t);
            pixels[i + 2] = (byte)(body.r + (ink.r - body.r) * t);
        }
        BitmapSource result = BitmapSource.Create(bgra.PixelWidth, bgra.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return Artwork[view] = result;
    }

    public void SetPressed(int bit, bool down)
    {
        MouseControl? control = service.Settings.ControlFor(bit);
        if (control is not null && callouts.TryGetValue(control.Id, out Callout? callout)) callout.SetPressed(down);
    }

    public void Pulse(int bit)
    {
        MouseControl? control = service.Settings.ControlFor(bit);
        if (control is not null && callouts.TryGetValue(control.Id, out Callout? callout)) callout.Flash();
    }

    /// <summary>One control's label, leader line and hotspot.</summary>
    private sealed class Callout
    {
        private readonly MouseDiagram owner;
        private readonly MouseControl control;
        private readonly Point dot;
        private readonly Ellipse hotspot = new() { Width = 16, Height = 16 };
        private readonly Ellipse halo = new() { Width = 34, Height = 34, IsHitTestVisible = false, Opacity = 0 };
        private readonly Polyline leader = new() { StrokeThickness = 1.2, IsHitTestVisible = false };
        private readonly Border label = new() { Width = LabelWidth, Background = Brushes.Transparent, Cursor = Cursors.Hand, Padding = new Thickness(0, 0, 0, 7) };
        private readonly TextBlock title = new() { FontSize = 19, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        private bool pressed;

        public Callout(MouseDiagram owner, MouseControl control, Point dot)
        {
            this.owner = owner;
            this.control = control;
            this.dot = dot;
            Build();
        }

        private bool IsSelected => owner.Selected?.Id == control.Id;

        private void Build()
        {
            Settings settings = owner.service.Settings;
            int? bit = settings.BitFor(control);
            MouseProfile profile = owner.service.ActiveProfile;
            string? assigned = bit is int b ? (owner.ShiftLayer ? profile.ShiftAssignments : profile.Assignments).GetValueOrDefault(b) : null;
            bool isMacro = assigned is not null && !BuiltinActions.IsBuiltin(assigned);
            bool calibrated = bit is not null;
            bool left = control.Side == CalloutSide.Left;

            TextBlock overline = new()
            {
                Text = control.Label.ToUpperInvariant(), FontSize = 12.5, FontWeight = FontWeights.SemiBold,
                FontFamily = Ui.Font("DisplayFont"), Foreground = Ui.Brush("Muted"), Margin = new Thickness(0, 0, 0, 2)
            };
            if (owner.CalibrationMode)
            {
                bool learning = owner.service.Learning?.Id == control.Id;
                title.Text = learning ? "Press it now…"
                    : !control.Calibratable ? "Fixed"
                    : bit is int raw ? (raw >= Input.RawMouseWheel.Up ? "Wheel signal" : $"HID 0x{raw:x4}")
                    : "Not calibrated";
                title.Foreground = learning || !calibrated ? Ui.Brush("Warning") : control.Calibratable ? Ui.Brush("Text") : Ui.Brush("Faint");
                assigned = null;
                isMacro = false;
            }
            else
            {
                title.Text = !calibrated ? "Not calibrated"
                    : assigned is not null ? settings.DescribeAssignment(assigned)
                    : owner.ShiftLayer ? "Same as default" : control.DefaultAction;
                title.Foreground = !calibrated ? Ui.Brush("Warning") : assigned is not null ? Ui.Brush("Text") : Ui.Brush("Muted");
            }
            StackPanel titleRow = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right };
            if (isMacro) titleRow.Children.Add(Ui.MacroMark().With(new Thickness(0, 0, 8, 0)));
            else if (assigned is not null) titleRow.Children.Add(Ui.Glyph("", 12, "Accent").With(new Thickness(0, 0, 7, 0)));
            titleRow.Children.Add(title);
            overline.HorizontalAlignment = titleRow.HorizontalAlignment;
            label.Child = new StackPanel { Children = { overline, titleRow } };
            label.MouseLeftButtonUp += (_, _) => owner.ControlClicked?.Invoke(control);
            label.MouseEnter += (_, _) => Highlight(true);
            label.MouseLeave += (_, _) => Highlight(false);

            hotspot.Cursor = Cursors.Hand;
            hotspot.MouseLeftButtonUp += (_, _) => owner.ControlClicked?.Invoke(control);
            hotspot.MouseEnter += (_, _) => Highlight(true);
            hotspot.MouseLeave += (_, _) => Highlight(false);
            if (!calibrated)
            {
                hotspot.StrokeDashArray = [2, 2];
            }
            halo.Fill = new SolidColorBrush(Color.FromArgb(70, 0x2E, 0xC5, 0xEA));
            Highlight(false);
        }

        public void AddTo(Canvas canvas)
        {
            bool left = control.Side == CalloutSide.Left;
            double x = left ? LeftColumn : RightColumn;
            double y = 36 + control.LabelY * 610;
            Canvas.SetLeft(label, x);
            Canvas.SetTop(label, y - 50);
            double underlineY = y + 2;
            double inner = left ? x + LabelWidth : x;
            double outer = left ? x : x + LabelWidth;
            leader.Points = [new Point(outer, underlineY), new Point(inner, underlineY), dot];
            Canvas.SetLeft(hotspot, dot.X - 8);
            Canvas.SetTop(hotspot, dot.Y - 8);
            Canvas.SetLeft(halo, dot.X - 17);
            Canvas.SetTop(halo, dot.Y - 17);
            canvas.Children.Add(leader);
            canvas.Children.Add(halo);
            canvas.Children.Add(hotspot);
            canvas.Children.Add(label);
        }

        private void Highlight(bool hover)
        {
            bool calibrated = owner.service.Settings.BitFor(control) is not null;
            bool active = IsSelected || pressed;
            Brush line = active ? Ui.Brush("Accent") : hover ? Ui.Brush("Muted") : new SolidColorBrush(Color.FromRgb(0x3A, 0x42, 0x4E));
            leader.Stroke = line;
            if (pressed)
            {
                hotspot.Fill = Ui.Brush("Accent");
                hotspot.Stroke = Brushes.White;
                hotspot.StrokeThickness = 2;
            }
            else if (!calibrated)
            {
                hotspot.Fill = Ui.Brush("Bg");
                hotspot.Stroke = Ui.Brush("Warning");
                hotspot.StrokeThickness = 2;
            }
            else
            {
                hotspot.Fill = IsSelected ? Ui.Brush("Accent") : new SolidColorBrush(Color.FromRgb(0xD5, 0xDB, 0xE3));
                hotspot.Stroke = IsSelected ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x0B, 0x0D, 0x10));
                hotspot.StrokeThickness = IsSelected ? 2 : 3;
            }
            halo.Opacity = IsSelected || pressed ? 1 : hover ? 0.6 : 0;
            label.Opacity = hover || active ? 1 : 0.92;
            if (IsSelected) title.Foreground = Ui.Brush("Accent");
        }

        public void SetPressed(bool down)
        {
            pressed = down;
            Highlight(false);
            if (down) Grow();
        }

        public void Flash()
        {
            pressed = true;
            Highlight(false);
            Grow();
            System.Windows.Threading.DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(160) };
            timer.Tick += (_, _) => { timer.Stop(); pressed = false; Highlight(false); };
            timer.Start();
        }

        private void Grow()
        {
            ScaleTransform scale = new(1, 1, 17, 17);
            halo.RenderTransform = scale;
            DoubleAnimation grow = new(0.6, 1.25, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }
    }
}
