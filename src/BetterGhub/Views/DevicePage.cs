using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterGhub.Core;
using BetterGhub.Services;

namespace BetterGhub.Views;

internal sealed class DevicePage : UserControl, IPage
{
    private readonly MouseService service;
    private readonly StackPanel connection = new();
    private readonly StackPanel battery = new();
    private readonly Border batteryCard;
    private TextBlock? mouseFirmware, receiverFirmware;
    private readonly StackPanel appCard = new();
    private readonly WrapPanel lamps = new();
    private readonly ListBox log = new();
    private readonly Dictionary<string, Border> lampsById = [];
    private readonly Dictionary<string, System.Windows.Threading.DispatcherTimer> wheelTimers = [];

    public DevicePage(MouseService service)
    {
        this.service = service;
        Grid layout = new() { Margin = new Thickness(36, 8, 36, 28) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        StackPanel left = new();
        left.Children.Add(Ui.Card(connection, new Thickness(22)));
        batteryCard = Ui.Card(battery, new Thickness(22)).With(new Thickness(0, 16, 0, 0));
        left.Children.Add(batteryCard);
        left.Children.Add(Ui.Card(appCard, new Thickness(22)).With(new Thickness(0, 16, 0, 0)));
        left.Children.Add(Ui.Card(About(), new Thickness(22)).With(new Thickness(0, 16, 0, 0)));
        layout.Children.Add(Ui.Scroll(left));

        DockPanel right = new();
        DockPanel logHead = new() { Margin = new Thickness(0, 0, 0, 12) };
        Button clear = Ui.Button("Clear", () => { service.Log.Clear(); log.Items.Clear(); }, "GhostBtn", "");
        DockPanel.SetDock(clear, Dock.Right);
        logHead.Children.Add(clear);
        logHead.Children.Add(Ui.Text("Live input", "H2"));
        DockPanel.SetDock(logHead, Dock.Top);
        right.Children.Add(logHead);
        TextBlock help = Ui.Text("Every control lights up while pressed. The log shows each HID bit, which is handy when a button seems to do nothing.", "Body", size: 12);
        DockPanel.SetDock(help, Dock.Top);
        right.Children.Add(help);
        DockPanel.SetDock(lamps, Dock.Top);
        lamps.Margin = new Thickness(0, 14, 0, 14);
        right.Children.Add(lamps);
        log.Background = Ui.Brush("Bg");
        log.Foreground = Ui.Brush("Text");
        log.BorderThickness = new Thickness(0);
        log.FontFamily = Ui.Font("Mono");
        log.FontSize = 12;
        log.Padding = new Thickness(8);
        VirtualizingPanel.SetIsVirtualizing(log, true);
        log.ItemContainerStyle = LogItemStyle();
        right.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = Ui.Brush("Bg"), Child = log, ClipToBounds = true });
        Border logCard = Ui.Card(right, new Thickness(22));
        logCard.Margin = new Thickness(24, 0, 0, 0);
        Grid.SetColumn(logCard, 1);
        layout.Children.Add(logCard);
        Content = layout;

        foreach (LogEntry entry in service.Log) log.Items.Add(Format(entry));
        service.Logged += entry =>
        {
            log.Items.Add(Format(entry));
            if (log.Items.Count > 400) log.Items.RemoveAt(0);
            if (IsVisible) log.ScrollIntoView(log.Items[^1]);
        };
        service.StateChanged += () => { if (IsLoaded) { RenderConnection(); RenderBattery(); RenderFirmware(); } };
        service.ButtonChanged += (bit, down) =>
        {
            MouseControl? control = service.Settings.ControlFor(bit);
            if (control is null || !lampsById.TryGetValue(control.Id, out Border? lamp)) return;
            bool pulse = bit >= Input.RawMouseWheel.Up;
            if (pulse && !down) return; // Wheel pulses release instantly; the timer below turns the lamp off.
            SetLamp(lamp, down);
            if (pulse)
            {
                if (!wheelTimers.TryGetValue(control.Id, out System.Windows.Threading.DispatcherTimer? off))
                {
                    off = new() { Interval = TimeSpan.FromMilliseconds(180) };
                    string id = control.Id;
                    off.Tick += (_, _) => { wheelTimers[id].Stop(); if (lampsById.TryGetValue(id, out Border? current)) SetLamp(current, false); };
                    wheelTimers[control.Id] = off;
                }
                off.Stop();
                off.Start();
            }
        };
        Refresh();
    }

    public void Refresh()
    {
        RenderConnection();
        RenderBattery();
        RenderFirmware();
        RenderApp();
        lamps.Children.Clear();
        lampsById.Clear();
        foreach (MouseControl control in MouseControls.All.OrderBy(c => c.Id.Length > 2 ? 1 : 0).ThenBy(c => c.Id))
        {
            bool known = service.Settings.BitFor(control) is not null;
            Border lamp = new()
            {
                Background = Ui.Brush("Surface2"), CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 6, 6),
                Child = new TextBlock { Text = control.Id is "WheelUp" ? "Scroll ▲" : control.Id is "WheelDown" ? "Scroll ▼" : control.Id is "TiltLeft" ? "Tilt ◀" : control.Id is "TiltRight" ? "Tilt ▶" : control.Id, FontWeight = FontWeights.SemiBold, FontSize = 12, Foreground = Ui.Brush("Muted") },
                Opacity = known ? 1 : 0.45,
                ToolTip = known ? control.Label : $"{control.Label} (not calibrated)"
            };
            lampsById[control.Id] = lamp;
            lamps.Children.Add(lamp);
        }
        if (log.Items.Count > 0) log.ScrollIntoView(log.Items[^1]);
    }

    private static void SetLamp(Border lamp, bool on)
    {
        lamp.Background = on ? Ui.Brush("Accent") : Ui.Brush("Surface2");
        ((TextBlock)lamp.Child).Foreground = on ? Ui.Brush("OnAccent") : Ui.Brush("Muted");
    }

    private static string Format(LogEntry entry) => $"{entry.Time:HH:mm:ss.fff}  {entry.Text}";

    private static Style LogItemStyle()
    {
        Style style = new(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4, 1, 4, 1)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Ui.Brush("Text")));
        style.Setters.Add(new Setter(Control.TemplateProperty, CreateItemTemplate()));
        return style;
    }

    private static ControlTemplate CreateItemTemplate()
    {
        ControlTemplate template = new(typeof(ListBoxItem));
        FrameworkElementFactory presenter = new(typeof(ContentPresenter));
        presenter.SetValue(MarginProperty, new Thickness(4, 1, 4, 1));
        template.VisualTree = presenter;
        return template;
    }

    /// <summary>Charge, estimated hours and G HUB's power breakdown; hidden until the mouse reports its battery.</summary>
    private void RenderBattery()
    {
        battery.Children.Clear();
        if (service.State != ConnectionState.Connected || service.BatteryPercent is not int percent)
        {
            batteryCard.Visibility = Visibility.Collapsed;
            return;
        }
        batteryCard.Visibility = Visibility.Visible;
        int interval = service.PowerIntervalMs;
        battery.Children.Add(Ui.Text("Battery", "H2"));
        TextBlock hours = Ui.Text(service.BatteryCharging ? "Charging" : $"Approx {service.BatteryHoursLeft} hours left", "Body", size: 13);
        hours.VerticalAlignment = VerticalAlignment.Center;
        battery.Children.Add(Ui.Row(10, Ui.Text($"{percent}%", size: 26, bold: true, color: "Text"), hours).With(new Thickness(0, 10, 0, 16)));
        battery.Children.Add(Ui.Text("POWER CONSUMPTION", "Overline").With(new Thickness(0, 0, 0, 10)));
        void Line(string name, string value)
        {
            DockPanel row = new() { Margin = new Thickness(0, 0, 0, 8) };
            TextBlock label = Ui.Text(name, size: 12, color: "Muted");
            label.Width = 110;
            row.Children.Add(label);
            row.Children.Add(Ui.Text(value, size: 12, color: "Text"));
            battery.Children.Add(row);
        }
        Line("System", $"{PowerModel.SystemMilliwatts:0} mW");
        Line("Report rate", $"{PowerModel.ReportRateMilliwatts(interval):0} mW at {1000 / interval} Hz");
        Line("Max charge", $"Approx {PowerModel.MaxHours(interval):0} hours");
        battery.Children.Add(Ui.Text("Estimated like G HUB, from typical power draw. Lower report rates last longer.", "Body", size: 11.5, color: "Faint").With(new Thickness(0, 6, 0, 0)));
    }

    private void RenderFirmware()
    {
        if (mouseFirmware is null || receiverFirmware is null) return;
        mouseFirmware.Text = service.MouseFirmware ?? "Connect to read";
        receiverFirmware.Text = service.ReceiverFirmware ?? "Connect to read";
    }

    private void SwitchToOnboard(Core.OnboardSlot slot)
    {
        Window owner = Window.GetWindow(this);
        try { service.SwitchToOnboard(slot); }
        catch (Exception error) { MessageBox.Show(owner, error.Message, "Could not switch", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void RenderConnection()
    {
        connection.Children.Clear();
        connection.Children.Add(Ui.Text("Connection", "H2"));
        (string title, string color) = service.State switch
        {
            ConnectionState.Connected => ("Connected", "Success"),
            ConnectionState.Connecting => ("Waiting for the mouse", "Warning"),
            ConnectionState.GHubRunning => ("G HUB is running", "Warning"),
            _ => ("Disconnected", "Muted")
        };
        connection.Children.Add(Ui.Row(8,
            new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Fill = Ui.Brush(color == "Muted" ? "Faint" : color), VerticalAlignment = VerticalAlignment.Center },
            Ui.Text(title, size: 15, bold: true, color: "Text")).With(new Thickness(0, 14, 0, 4)));
        string detail = service.IsOnboard
            ? $"Mouse runs {service.ActiveOnboardSlot?.DisplayName ?? "an on-board slot"} from its own memory. The Assignments and Sensitivity pages edit that slot."
            : service.State == ConnectionState.Connected
            ? $"{service.DeviceDpi} DPI · {service.DeviceReportRate} Hz · host mode and button reporting on"
            : service.StateDetail.Length > 0 ? service.StateDetail : "Click Connect to take over the mouse from its onboard profile.";
        connection.Children.Add(Ui.Text(detail, "Body", size: 12).With(new Thickness(18, 0, 0, 16)));
        if (service.State == ConnectionState.GHubRunning)
            connection.Children.Add(new Border
            {
                Background = Ui.Brush("WarningDim"), CornerRadius = new CornerRadius(9), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 14),
                Child = Ui.Text("G HUB and BetterGhub can't control the receiver at the same time. Right-click the G HUB tray icon and choose Quit, then BetterGhub connects by itself.", "Body", size: 12, color: "Warning")
            });
        Button action = service.State is ConnectionState.Connected or ConnectionState.Connecting
            ? Ui.Button("Disconnect", service.Disconnect, "Btn")
            : Ui.Button("Connect mouse", service.Connect, "PrimaryBtn");
        action.HorizontalAlignment = HorizontalAlignment.Left;
        if (service.IsOnboard)
        {
            Button host = Ui.Button("Use BetterGhub profiles", service.UseHostMode, "Btn", "",
                "Hand the buttons back to BetterGhub: its profiles, macros and app switching");
            connection.Children.Add(Ui.Row(8, action, host));
        }
        else if (service.State == ConnectionState.Connected && service.OnboardSlots?.FirstOrDefault(s => s.Enabled) is { } slot)
        {
            Button onboard = Ui.Button("Use on-board memory", () => SwitchToOnboard(slot), "Btn", "",
                $"Run the mouse from {slot.DisplayName} (slot {slot.Number}) in its own memory, and edit that slot on the Assignments and Sensitivity pages. Pick another slot from the profile menu.");
            connection.Children.Add(Ui.Row(8, action, onboard));
        }
        else connection.Children.Add(action);

        CheckBox auto = new()
        {
            Style = Ui.Style("Switch"), IsChecked = service.Settings.AutoConnect, Margin = new Thickness(0, 18, 0, 0),
            Content = Ui.Text("Connect automatically when BetterGhub starts", color: "Text")
        };
        auto.Click += (_, _) => { service.Settings.AutoConnect = auto.IsChecked == true; service.Save(); };
        connection.Children.Add(auto);
        connection.Children.Add(Ui.Text("Connecting switches the mouse to host mode in its working memory only. Quit BetterGhub and power-cycle the mouse to return to its onboard profile.", "Body", size: 11.5, color: "Faint").With(new Thickness(0, 14, 0, 0)));
    }

    private void RenderApp()
    {
        appCard.Children.Clear();
        appCard.Children.Add(Ui.Text("App", "H2").With(new Thickness(0, 0, 0, 14)));

        CheckBox startup = new()
        {
            Style = Ui.Style("Switch"), IsChecked = AutoStart.IsEnabled,
            Content = Ui.Text("Start with Windows, in the tray", color: "Text")
        };
        startup.Click += (_, _) =>
        {
            try { AutoStart.Set(startup.IsChecked == true); }
            catch (Exception error) { service.Write("Could not change startup: " + error.Message); startup.IsChecked = AutoStart.IsEnabled; }
        };
        appCard.Children.Add(startup);

        CheckBox tray = new()
        {
            Style = Ui.Style("Switch"), IsChecked = service.Settings.CloseToTray, Margin = new Thickness(0, 12, 0, 0),
            Content = new StackPanel
            {
                Children =
                {
                    Ui.Text("Keep running when the window is closed", color: "Text"),
                    Ui.Text("Macros only work while BetterGhub runs. Quit from the tray icon.", "Body", size: 12).With(new Thickness(0, 3, 0, 0))
                }
            }
        };
        tray.Click += (_, _) => { service.Settings.CloseToTray = tray.IsChecked == true; service.Save(); };
        appCard.Children.Add(tray);
        appCard.Children.Add(Ui.Text($"Version {AutoStart.Version} · portable, nothing is installed. If you move the exe, start it once from the new place and autostart follows.", "Body", size: 12).With(new Thickness(0, 18, 0, 0)));
    }

    private FrameworkElement About()
    {
        StackPanel panel = new();
        panel.Children.Add(Ui.Text("Device", "H2").With(new Thickness(0, 0, 0, 12)));
        TextBlock Line(string name, string value)
        {
            DockPanel row = new() { Margin = new Thickness(0, 0, 0, 8) };
            TextBlock label = Ui.Text(name, size: 12, color: "Muted");
            label.Width = 110;
            row.Children.Add(label);
            TextBlock text = Ui.Text(value, size: 12, color: "Text", wrap: true);
            row.Children.Add(text);
            panel.Children.Add(row);
            return text;
        }
        Line("Mouse", "G502 X LIGHTSPEED");
        mouseFirmware = Line("Mouse firmware", "");
        Line("Receiver", "LIGHTSPEED 046D:C547, slot 1");
        receiverFirmware = Line("Receiver firmware", "");
        Line("Protocol", "HID++ 2.0 · 0x8100 host mode, 0x8110 button spy, 0x2201 DPI, 0x8060 report rate, 0x1004 battery");
        Line("Settings", Settings.FilePath);
        Button open = Ui.Button("Open settings folder", () =>
        {
            string? folder = Path.GetDirectoryName(Settings.FilePath);
            if (folder is not null && Directory.Exists(folder)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }, "Btn", "");
        open.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(open.With(new Thickness(0, 6, 0, 0)));
        panel.Children.Add(Ui.Row(8, Ui.Button("Export settings…", ExportSettings, "Btn", ""), Ui.Button("Import settings…", ImportSettings, "Btn", "")).With(new Thickness(0, 10, 0, 0)));
        panel.Children.Add(Ui.Text("Export saves profiles, macros, assignments and calibration to a file. Import replaces all of them.", "Body", size: 12).With(new Thickness(0, 8, 0, 0)));
        return panel;
    }

    private const string SettingsFilter = "BetterGhub settings (*.json)|*.json|All files (*.*)|*.*";

    private void ExportSettings()
    {
        Microsoft.Win32.SaveFileDialog dialog = new() { Filter = SettingsFilter, FileName = $"BetterGhub settings {DateTime.Now:yyyy-MM-dd}.json", DefaultExt = ".json" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try { service.ExportSettings(dialog.FileName); }
        catch (Exception error) { MessageBox.Show(Window.GetWindow(this), error.Message, "Could not export settings", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void ImportSettings()
    {
        Microsoft.Win32.OpenFileDialog dialog = new() { Filter = SettingsFilter };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        if (MessageBox.Show(Window.GetWindow(this), "Importing replaces all your profiles, macros, assignments and calibration. Continue?", "Import settings",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try { service.ImportSettings(dialog.FileName); }
        catch (Exception error) { MessageBox.Show(Window.GetWindow(this), $"{Path.GetFileName(dialog.FileName)} isn't a readable settings file ({error.Message}). Nothing was changed.", "Could not import settings", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
