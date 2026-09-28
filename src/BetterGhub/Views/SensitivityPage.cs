using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterGhub.Core;
using BetterGhub.Services;

namespace BetterGhub.Views;

internal sealed class SensitivityPage : UserControl, IPage
{
    private readonly MouseService service;
    private readonly DpiTrack track = new() { Margin = new Thickness(0, 30, 0, 0) };
    private readonly StackPanel side = new();
    private readonly StackPanel stageTools = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 0) };
    private readonly TextBlock deviceLine = Ui.Text("", "Body", size: 12);

    public SensitivityPage(MouseService service)
    {
        this.service = service;
        Grid layout = new() { Margin = new Thickness(36, 8, 36, 28) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(Ui.Card(Ui.Scroll(side), new Thickness(22)));

        StackPanel main = new() { VerticalAlignment = VerticalAlignment.Center };
        TextBlock heading = Ui.Text("DPI SPEEDS", "H2");
        heading.HorizontalAlignment = HorizontalAlignment.Center;
        main.Children.Add(heading);
        TextBlock hint = Ui.Text("Drag a dot to change a speed · click a number to make it current · drag the diamond to set DPI Shift", "Body", size: 12);
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        hint.TextAlignment = TextAlignment.Center;
        main.Children.Add(hint.With(new Thickness(0, 6, 0, 0)));
        main.Children.Add(track);
        main.Children.Add(stageTools);
        deviceLine.HorizontalAlignment = HorizontalAlignment.Center;
        main.Children.Add(deviceLine.With(new Thickness(0, 22, 0, 0)));
        Border stage = Ui.Card(main, new Thickness(34, 28, 34, 28));
        stage.Margin = new Thickness(24, 0, 0, 0);
        Grid.SetColumn(stage, 1);
        layout.Children.Add(stage);
        Content = layout;

        track.Changed += (stages, current, shift) =>
        {
            MouseProfile profile = service.ActiveProfile;
            bool currentChanged = profile.Dpi != current;
            profile.DpiStages = stages;
            profile.Dpi = current;
            profile.ShiftDpi = shift;
            service.Save();
            // On-board, Save snaps DPI Shift and the current speed onto the speed list; show where they landed.
            if (profile.ShiftDpi != shift || profile.Dpi != current) track.Set(profile.DpiStages, profile.Dpi, profile.ShiftDpi);
            if (service.DpiShiftHeld) service.SendDpi(profile.ShiftDpi);
            else if (currentChanged) service.SendDpi(profile.Dpi);
            RenderSide();
            RenderTools();
        };
        track.SelectionChanged += _ => RenderTools();
        service.StateChanged += () =>
        {
            track.ShiftActive = service.DpiShiftHeld;
            if (IsLoaded) RenderDeviceLine();
        };
        Refresh();
    }

    public void Refresh()
    {
        MouseProfile profile = service.ActiveProfile;
        track.ShiftActive = service.DpiShiftHeld;
        track.Set(profile.DpiStages, profile.Dpi, profile.ShiftDpi);
        RenderSide();
        RenderTools();
        RenderDeviceLine();
    }

    private void RenderDeviceLine()
    {
        deviceLine.Text = service.IsOnboard
            ? $"Editing {service.ActiveProfile.Name} in the mouse's on-board memory. Changes are saved to the mouse."
            : service.State == ConnectionState.Connected
            ? $"Mouse reports {service.DeviceDpi} DPI at {service.DeviceReportRate} Hz. Changes apply instantly and are kept in the {service.ActiveProfile.Name} profile."
            : $"Mouse not connected. Changes are saved in the {service.ActiveProfile.Name} profile and applied when it connects.";
    }

    private void RenderTools()
    {
        stageTools.Children.Clear();
        MouseProfile profile = service.ActiveProfile;
        Button add = Ui.Button("Add speed", AddStage, "Btn", "");
        add.IsEnabled = profile.DpiStages.Count < 5;
        add.ToolTip = add.IsEnabled ? "Add a DPI speed" : "Up to five speeds";
        stageTools.Children.Add(add);
        if (track.SelectedIndex is int index && index < profile.DpiStages.Count)
        {
            int value = profile.DpiStages[index];
            TextBox exact = new() { Text = value.ToString(), Width = 90, Margin = new Thickness(16, 0, 0, 0), ToolTip = "Type an exact value" };
            exact.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) ApplyExact(index, exact.Text); };
            exact.LostFocus += (_, _) => ApplyExact(index, exact.Text);
            stageTools.Children.Add(exact);
            Button remove = Ui.DeleteButton("Remove", () => RemoveStage(index));
            remove.IsEnabled = profile.DpiStages.Count > 1;
            stageTools.Children.Add(remove.With(new Thickness(8, 0, 0, 0)));
        }
    }

    private void ApplyExact(int index, string text)
    {
        MouseProfile profile = service.ActiveProfile;
        if (!int.TryParse(text, out int value) || index >= profile.DpiStages.Count) return;
        value = Math.Clamp(value / DpiTrack.Step * DpiTrack.Step, DpiTrack.Min, DpiTrack.Max);
        int old = profile.DpiStages[index];
        if (old == value) return;
        profile.DpiStages[index] = value;
        profile.DpiStages = profile.DpiStages.Distinct().Order().ToList();
        if (profile.Dpi == old) { profile.Dpi = value; service.SendDpi(value); }
        service.Save();
        track.Set(profile.DpiStages, profile.Dpi, profile.ShiftDpi);
        track.Select(profile.DpiStages.IndexOf(value));
        RenderSide();
        RenderTools();
    }

    private void AddStage()
    {
        MouseProfile profile = service.ActiveProfile;
        if (profile.DpiStages.Count >= 5) return;
        int candidate = Math.Min(DpiTrack.Max, (profile.DpiStages.Count == 0 ? 800 : profile.DpiStages.Max() * 2));
        while (profile.DpiStages.Contains(candidate) && candidate > DpiTrack.Min) candidate -= 100;
        profile.DpiStages.Add(candidate);
        profile.DpiStages.Sort();
        service.Save();
        track.Set(profile.DpiStages, profile.Dpi, profile.ShiftDpi);
        track.Select(profile.DpiStages.IndexOf(candidate));
        RenderSide();
        RenderTools();
    }

    private void RemoveStage(int index)
    {
        MouseProfile profile = service.ActiveProfile;
        if (profile.DpiStages.Count <= 1) return;
        int removed = profile.DpiStages[index];
        profile.DpiStages.RemoveAt(index);
        if (profile.Dpi == removed)
        {
            profile.Dpi = profile.DpiStages.MinBy(x => Math.Abs(x - removed));
            service.SendDpi(profile.Dpi);
        }
        service.Save();
        track.Select(null);
        track.Set(profile.DpiStages, profile.Dpi, profile.ShiftDpi);
        RenderSide();
        RenderTools();
    }

    private void SetCurrent(int dpi)
    {
        MouseProfile profile = service.ActiveProfile;
        profile.Dpi = dpi;
        service.Save();
        service.SendDpi(dpi);
        track.Set(profile.DpiStages, profile.Dpi, profile.ShiftDpi);
        RenderSide();
    }

    private void RenderSide()
    {
        side.Children.Clear();
        MouseProfile profile = service.ActiveProfile;
        side.Children.Add(Ui.Text("Sensitivity", "H2"));
        side.Children.Add(Ui.Text("DPI is how far the pointer moves for each inch of mouse movement. Use the DPI buttons, or assign DPI Up/Down/Cycle, to switch speeds.", "Body").With(new Thickness(0, 8, 0, 20)));

        side.Children.Add(Ui.Text("DPI SPEEDS", "Overline"));
        WrapPanel speeds = new();
        for (int i = 0; i < profile.DpiStages.Count; i++)
        {
            int value = profile.DpiStages[i];
            TextBlock number = new()
            {
                Text = value.ToString(), FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 16, 4),
                Foreground = new SolidColorBrush(DpiTrack.StageColors[i % DpiTrack.StageColors.Length]),
                TextDecorations = value == profile.Dpi ? TextDecorations.Underline : null,
                Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = value == profile.Dpi ? "Current speed" : $"Switch to {value} DPI", Opacity = value == profile.Dpi ? 1 : 0.8
            };
            if (value != profile.Dpi)
            {
                number.MouseEnter += (_, _) => number.Opacity = 1;
                number.MouseLeave += (_, _) => number.Opacity = 0.8;
                number.MouseLeftButtonUp += (_, _) => SetCurrent(value);
            }
            speeds.Children.Add(number);
        }
        side.Children.Add(speeds);
        side.Children.Add(Ui.Text("The underlined speed is current. Click a speed to switch to it.", "Body", size: 12).With(new Thickness(0, 2, 0, 18)));

        side.Children.Add(Ui.Text("DPI SHIFT", "Overline"));
        side.Children.Add(Ui.Text($"{profile.ShiftDpi} DPI while held", size: 15, bold: true));
        side.Children.Add(Ui.Text("Assign DPI Shift to a button (normally the thumb sniper button, G6) and hold it for precise aiming.", "Body", size: 12).With(new Thickness(0, 4, 0, 20)));

        side.Children.Add(Ui.Text("REPORT RATE", "Overline"));
        StackPanel rates = new() { Orientation = Orientation.Horizontal };
        foreach (int hz in new[] { 1000, 500, 250, 125 })
        {
            RadioButton rate = new() { Content = hz.ToString(), Style = Ui.Style("Segment"), GroupName = "Rate", IsChecked = profile.ReportRate == hz, Padding = new Thickness(12, 7, 12, 7) };
            rate.Click += (_, _) =>
            {
                profile.ReportRate = hz;
                service.Save();
                service.SendReportRate(hz);
            };
            rates.Children.Add(rate);
        }
        side.Children.Add(new Border { Style = Ui.Style("SegmentHost"), Child = rates });
        side.Children.Add(Ui.Text("Reports per second. 1000 Hz is smoothest; lower rates save battery.", "Body", size: 12).With(new Thickness(0, 8, 0, 22)));

        Button reset = Ui.Button("Restore default speeds", () =>
        {
            MouseProfile defaults = new();
            profile.DpiStages = defaults.DpiStages;
            profile.Dpi = defaults.Dpi;
            profile.ShiftDpi = defaults.ShiftDpi;
            profile.ReportRate = defaults.ReportRate;
            service.Save();
            service.ApplyDeviceSettings();
            Refresh();
        }, "Btn");
        reset.HorizontalAlignment = HorizontalAlignment.Stretch;
        side.Children.Add(reset);
        side.Children.Add(Ui.Text(service.IsOnboard
            ? "Saved to this slot in the mouse's on-board memory. DPI Shift snaps to the closest speed, since the slot can only shift to one of its speeds."
            : "Settings are sent to the mouse's working memory only. On-board slots change only when you run one and edit it.", "Body", size: 11.5, color: "Faint").With(new Thickness(0, 12, 0, 0)));
    }
}
