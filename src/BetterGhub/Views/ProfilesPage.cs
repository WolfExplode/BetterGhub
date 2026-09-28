using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BetterGhub.Core;
using BetterGhub.Input;
using BetterGhub.Services;

namespace BetterGhub.Views;

internal sealed class ProfilesPage : UserControl, IPage
{
    private readonly MouseService service;
    private readonly WrapPanel tiles = new();
    private readonly ContentControl details = new() { Focusable = false };
    private readonly CheckBox autoSwitch = new();
    private readonly ContentControl onboard = new() { Focusable = false };
    private MouseProfile? selected;
    private int selectedSlot = 1;

    public ProfilesPage(MouseService service)
    {
        this.service = service;
        StackPanel page = new() { Margin = new Thickness(36, 8, 36, 28) };

        DockPanel head = new() { Margin = new Thickness(0, 0, 0, 16) };
        Button add = Ui.Button("Add application", AddApplication, "PrimaryBtn", "");
        DockPanel.SetDock(add, Dock.Right);
        head.Children.Add(add);
        Button pick = Ui.Button("", PickWindow, "Btn", "", "Pick an application by clicking its window");
        DockPanel.SetDock(pick, Dock.Right);
        pick.Margin = new Thickness(0, 0, 8, 0);
        head.Children.Add(pick);
        autoSwitch.Style = Ui.Style("Switch");
        autoSwitch.Content = Ui.Text("Switch profiles automatically when an application gets focus", color: "Text");
        autoSwitch.VerticalAlignment = VerticalAlignment.Center;
        autoSwitch.Click += (_, _) => { service.Settings.AutoSwitchProfiles = autoSwitch.IsChecked == true; service.Save(); };
        head.Children.Add(autoSwitch);
        page.Children.Add(head);

        page.Children.Add(tiles);
        page.Children.Add(details.With(new Thickness(0, 12, 0, 0)));
        page.Children.Add(onboard.With(new Thickness(0, 16, 0, 0)));
        Content = Ui.Scroll(page);
        service.StateChanged += () => { if (IsLoaded) RenderOnboard(); };
        Refresh();
    }

    public void Refresh()
    {
        autoSwitch.IsChecked = service.Settings.AutoSwitchProfiles;
        if (selected is null || !service.Settings.Profiles.Contains(selected)) selected = service.ActiveProfile;
        tiles.Children.Clear();
        foreach (MouseProfile profile in service.Settings.Profiles) tiles.Children.Add(Tile(profile));
        tiles.Children.Add(AddTile());
        RenderDetails();
        RenderOnboard();
    }

    // ── On-board memory (read-only) ───────────────────────────────────────────

    /// <summary>For --snapshot: scrolls the on-board memory card into view.</summary>
    internal void ShowOnboard() => ((ScrollViewer)Content).ScrollToEnd();

    private void RenderOnboard()
    {
        StackPanel body = new();
        DockPanel head = new();
        if (service.State == ConnectionState.Connected)
        {
            Button read = Ui.Button("Read again", service.ReadOnboardMemory, "GhostBtn", "");
            DockPanel.SetDock(read, Dock.Right);
            head.Children.Add(read);
        }
        head.Children.Add(Ui.Text("On-board memory", "H2"));
        body.Children.Add(head);
        body.Children.Add(Ui.Text("Profiles saved on the mouse itself. The mouse uses them in onboard mode: when BetterGhub isn't running, or on another computer. "
            + "While BetterGhub is connected the mouse is in host mode and ignores them. Read-only for now.", "Body", size: 12, wrap: true).With(new Thickness(0, 6, 0, 18)));

        IReadOnlyList<OnboardSlot>? slots = service.OnboardSlots;
        if (slots is null || slots.Count == 0)
        {
            body.Children.Add(Ui.Text(service.State == ConnectionState.Connected ? "Reading the mouse's memory…" : "Connect the mouse to read its memory.", "Body"));
            onboard.Content = Ui.Card(body, new Thickness(24, 20, 24, 20));
            return;
        }
        if (slots.All(s => s.Number != selectedSlot)) selectedSlot = slots[0].Number;

        Grid layout = new();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        StackPanel list = new() { Margin = new Thickness(0, 0, 28, 0) };
        foreach (OnboardSlot slot in slots) list.Children.Add(SlotRow(slot));
        layout.Children.Add(list);
        FrameworkElement detail = SlotDetail(slots.First(s => s.Number == selectedSlot));
        Grid.SetColumn(detail, 1);
        layout.Children.Add(detail);
        body.Children.Add(layout);
        onboard.Content = Ui.Card(body, new Thickness(24, 20, 24, 20));
    }

    private RadioButton SlotRow(OnboardSlot slot)
    {
        DockPanel content = new();
        Border state = Ui.Badge(slot.Enabled ? "ON" : "OFF", slot.Enabled ? "Success" : "Faint", "Surface2");
        DockPanel.SetDock(state, Dock.Right);
        content.Children.Add(state);
        TextBlock number = Ui.Text($"SLOT {slot.Number}", "Overline");
        number.Width = 52;
        number.Margin = new Thickness(0);
        number.VerticalAlignment = VerticalAlignment.Center;
        content.Children.Add(number);
        TextBlock name = Ui.Text(slot.DisplayName, size: 13.5, bold: slot.Enabled, color: slot.Enabled ? "Text" : "Muted");
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.VerticalAlignment = VerticalAlignment.Center;
        content.Children.Add(name);
        RadioButton row = new() { Style = Ui.Style("Row"), Content = content, GroupName = "OnboardSlots", IsChecked = slot.Number == selectedSlot };
        row.Click += (_, _) => { selectedSlot = slot.Number; RenderOnboard(); };
        return row;
    }

    private static FrameworkElement SlotDetail(OnboardSlot slot)
    {
        StackPanel panel = new();
        if (slot.Profile is not { } profile)
        {
            panel.Children.Add(Ui.Text("This slot could not be read.", "Body"));
            return panel;
        }
        TextBlock state = Ui.Text(slot.Enabled ? "Enabled" : "Disabled, so the mouse skips it", "Body", size: 12);
        state.VerticalAlignment = VerticalAlignment.Center;
        panel.Children.Add(Ui.Row(12, Ui.Text(slot.DisplayName, size: 17, bold: true, color: "Text"), state));
        if (!profile.ChecksumOk)
            panel.Children.Add(new Border
            {
                Background = Ui.Brush("WarningDim"), CornerRadius = new CornerRadius(9), Padding = new Thickness(12), Margin = new Thickness(0, 12, 0, 0),
                Child = Ui.Text("The checksum doesn't match, so the mouse uses its factory profile instead of this slot.", "Body", size: 12, color: "Warning")
            });

        WrapPanel facts = new() { Margin = new Thickness(0, 16, 0, 0) };
        facts.Children.Add(Fact("REPORT RATE", profile.ReportIntervalMs > 0 ? $"{1000 / profile.ReportIntervalMs} Hz" : "Unknown"));
        StackPanel dpis = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        for (int i = 0; i < profile.Dpis.Count; i++)
        {
            bool isDefault = i == profile.DefaultDpiIndex, isShift = i == profile.ShiftDpiIndex;
            dpis.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 0),
                Background = Ui.Brush(isDefault ? "AccentDim" : "Surface2"),
                BorderBrush = Ui.Brush(isDefault ? "Accent" : isShift ? "Warning" : "Surface2"), BorderThickness = new Thickness(1),
                ToolTip = isDefault && isShift ? "Default and DPI Shift speed" : isDefault ? "Default speed" : isShift ? "DPI Shift speed" : null,
                Child = Ui.Text(profile.Dpis[i].ToString(), size: 13, bold: true, color: isDefault ? "Accent" : "Text")
            });
        }
        facts.Children.Add(new StackPanel { Margin = new Thickness(0, 0, 24, 6), Children = { Ui.Text("DPI SPEEDS", "Overline"), dpis } });
        facts.Children.Add(new StackPanel
        {
            Margin = new Thickness(0, 22, 0, 6), VerticalAlignment = VerticalAlignment.Center,
            Children = { Ui.Row(14, Legend("Accent", "Default"), Legend("Warning", "DPI Shift")) }
        });
        panel.Children.Add(facts);

        Grid table = new() { Margin = new Thickness(0, 14, 0, 0) };
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Cell(UIElement element, int row, int column)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            table.Children.Add(element);
        }
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Cell(Ui.Text("BUTTON", "Overline").With(new Thickness(0, 0, 0, 8)), 0, 0);
        Cell(Ui.Text("ACTION", "Overline").With(new Thickness(0, 0, 0, 8)), 0, 1);
        Cell(Ui.Text("WITH G-SHIFT", "Overline").With(new Thickness(0, 0, 0, 8)), 0, 2);
        Dictionary<int, OnboardBinding> shifted = profile.ShiftButtons.ToDictionary(b => b.Index);
        int line = 1;
        foreach (OnboardBinding binding in profile.Buttons)
        {
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (line % 2 == 1)
            {
                Border stripe = new() { Background = Ui.Brush("Surface"), CornerRadius = new CornerRadius(6), Margin = new Thickness(-8, 0, -8, 0) };
                Grid.SetColumnSpan(stripe, 3);
                Cell(stripe, line, 0);
            }
            Cell(Ui.Text(OnboardProfiles.ControlName(binding.Index), size: 12.5, color: "Muted").With(new Thickness(0, 6, 8, 6)), line, 0);
            Cell(BindingText(binding), line, 1);
            Cell(shifted.TryGetValue(binding.Index, out OnboardBinding? shift) ? BindingText(shift) : Ui.Text("Same", size: 12.5, color: "Faint").With(new Thickness(0, 6, 8, 6)), line, 2);
            line++;
        }
        panel.Children.Add(table);
        return panel;
    }

    private static StackPanel Legend(string color, string label) =>
        Ui.Row(6, new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(3), BorderBrush = Ui.Brush(color), BorderThickness = new Thickness(1.5), VerticalAlignment = VerticalAlignment.Center },
            Ui.Text(label, "Body", size: 11.5));

    private static TextBlock BindingText(OnboardBinding binding)
    {
        bool quiet = binding.Description is "No action" or "Disabled";
        TextBlock text = Ui.Text(binding.Description, size: 12.5, color: quiet ? "Faint" : "Text").With(new Thickness(0, 6, 8, 6));
        text.ToolTip = $"Stored as {binding.Raw}";
        return text;
    }

    private FrameworkElement Tile(MouseProfile profile)
    {
        bool active = profile == service.ActiveProfile;
        StackPanel face = new() { Width = 150 };
        Border iconBox = new()
        {
            Width = 72, Height = 72, CornerRadius = new CornerRadius(16), Background = Ui.Brush("Surface2"), HorizontalAlignment = HorizontalAlignment.Center,
            Child = ShellIcons.Element(profile, 38)
        };
        ((FrameworkElement)iconBox.Child).HorizontalAlignment = HorizontalAlignment.Center;
        ((FrameworkElement)iconBox.Child).VerticalAlignment = VerticalAlignment.Center;
        face.Children.Add(iconBox);
        face.Children.Add(new TextBlock { Text = profile.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 14, 0, 2), Foreground = Ui.Brush("Text") });
        face.Children.Add(new TextBlock { Text = profile.IsDesktop ? "Everything else" : Path.GetFileName(profile.ApplicationPath), FontSize = 11.5, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Ui.Brush("Muted") });
        Border badge = Ui.Badge(active ? "ACTIVE" : profile.IsDesktop ? "DESKTOP" : "APP", active ? "OnAccent" : "Muted", active ? "Accent" : "Surface2");
        badge.HorizontalAlignment = HorizontalAlignment.Center;
        badge.Margin = new Thickness(0, 12, 0, 0);
        face.Children.Add(badge);
        RadioButton tile = new() { Style = Ui.Style("Tile"), Content = face, GroupName = "Profiles", IsChecked = profile == selected, Padding = new Thickness(14, 20, 14, 16), Margin = new Thickness(0, 0, 14, 14) };
        tile.Click += (_, _) => { selected = profile; Refresh(); };
        return tile;
    }

    private FrameworkElement AddTile()
    {
        StackPanel face = new() { Width = 150, VerticalAlignment = VerticalAlignment.Center };
        face.Children.Add(new TextBlock { Text = "", FontFamily = Ui.Font("Icons"), FontSize = 26, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Ui.Brush("Muted") });
        face.Children.Add(new TextBlock { Text = "Add game or app", FontSize = 12.5, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0), Foreground = Ui.Brush("Muted") });
        Button button = new() { Style = Ui.Style("GhostBtn"), Content = face, Padding = new Thickness(14), Margin = new Thickness(0, 0, 14, 14), BorderBrush = Ui.Brush("LineStrong"), BorderThickness = new Thickness(1.5), MinHeight = 206 };
        button.Click += (_, _) => AddApplication();
        return button;
    }

    private void AddApplication()
    {
        Microsoft.Win32.OpenFileDialog dialog = new() { Filter = "Applications (*.exe)|*.exe", Title = "Choose a game or application" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        AddProfile(dialog.FileName);
    }

    private void PickWindow()
    {
        MainWindow window = (MainWindow)Window.GetWindow(this);
        WindowState previous = window.WindowState;
        PickHighlight highlight = new();
        try
        {
            WindowPicker picker = new(path =>
            {
                highlight.Close();
                window.WindowState = previous;
                window.Activate();
                if (path is null) window.ShowToast("No application picked");
                else AddProfile(path);
            });
            picker.Hover += highlight.Track;
        }
        catch (Exception error) { highlight.Close(); window.ShowToast(error.Message); return; }
        window.WindowState = WindowState.Minimized; // Get out of the way of the window being picked.
    }

    private void AddProfile(string path)
    {
        MouseProfile? existing = service.Settings.Profiles.FirstOrDefault(p => string.Equals(p.ApplicationPath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) { selected = existing; Refresh(); return; }
        MouseProfile template = service.ActiveProfile;
        MouseProfile profile = new()
        {
            Name = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 and < 40 } description ? description : Path.GetFileNameWithoutExtension(path),
            ApplicationPath = path,
            Dpi = template.Dpi, DpiStages = [.. template.DpiStages], ShiftDpi = template.ShiftDpi, ReportRate = template.ReportRate
        };
        service.Settings.Profiles.Add(profile);
        service.Save();
        selected = profile;
        Refresh();
    }

    private void RenderDetails()
    {
        if (selected is null) { details.Content = null; return; }
        MouseProfile profile = selected;
        StackPanel body = new();
        DockPanel head = new();
        StackPanel actions = Ui.Row(8);
        if (profile != service.ActiveProfile)
            actions.Children.Add(Ui.Button("Use now", () => { service.SelectProfile(profile); Refresh(); }, "Btn", ""));
        actions.Children.Add(CopyFromButton(profile).With(new Thickness(actions.Children.Count > 0 ? 8 : 0, 0, 0, 0)));
        if (!profile.IsDesktop)
            actions.Children.Add(Ui.DeleteButton("Remove", () => Remove(profile)).With(new Thickness(8, 0, 0, 0)));
        DockPanel.SetDock(actions, Dock.Right);
        head.Children.Add(actions);
        TextBox name = new() { Text = profile.Name, Style = Ui.Style("TitleBox"), FontSize = 20, Margin = new Thickness(-6, 0, 16, 0) };
        name.LostFocus += (_, _) =>
        {
            profile.Name = name.Text.Trim().Length == 0 ? profile.Name : name.Text.Trim();
            service.Save();
            Refresh();
        };
        head.Children.Add(name);
        body.Children.Add(head);
        body.Children.Add(Ui.Text(profile.IsDesktop
            ? "Used whenever no application profile matches the focused window."
            : $"Active while {profile.ApplicationPath} has focus.", "Body").With(new Thickness(0, 4, 0, 18)));

        int assigned = profile.Assignments.Count + profile.ShiftAssignments.Count;
        WrapPanel facts = new();
        facts.Children.Add(Fact("ASSIGNMENTS", assigned == 0 ? "None" : $"{assigned} button{(assigned == 1 ? "" : "s")}"));
        facts.Children.Add(Fact("DPI SPEEDS", string.Join(" · ", profile.DpiStages)));
        facts.Children.Add(Fact("CURRENT DPI", profile.Dpi.ToString()));
        facts.Children.Add(Fact("REPORT RATE", $"{profile.ReportRate} Hz"));
        body.Children.Add(facts);
        body.Children.Add(Ui.Text("Each profile keeps its own assignments, DPI speeds and report rate. Macros are shared, so any profile can use them.", "Body", size: 12, color: "Faint").With(new Thickness(0, 12, 0, 0)));
        details.Content = Ui.Card(body, new Thickness(24, 20, 24, 20));
    }

    private static FrameworkElement Fact(string title, string value) =>
        new StackPanel { Margin = new Thickness(0, 0, 40, 6), Children = { Ui.Text(title, "Overline"), Ui.Text(value, size: 15, bold: true, color: "Text") } };

    private Button CopyFromButton(MouseProfile target)
    {
        Button button = Ui.Button("Copy settings from…", () => { }, "Btn", "");
        Popup popup = new() { PlacementTarget = button, Placement = PlacementMode.Bottom, VerticalOffset = 6, StaysOpen = false, AllowsTransparency = true };
        StackPanel items = new();
        foreach (MouseProfile source in service.Settings.Profiles.Where(p => p != target))
        {
            Button item = Ui.Button(source.Name, () =>
            {
                popup.IsOpen = false;
                target.Assignments = new Dictionary<int, string>(source.Assignments);
                target.ShiftAssignments = new Dictionary<int, string>(source.ShiftAssignments);
                target.DpiStages = [.. source.DpiStages];
                target.Dpi = source.Dpi;
                target.ShiftDpi = source.ShiftDpi;
                target.ReportRate = source.ReportRate;
                service.Save();
                if (target == service.ActiveProfile) service.ApplyDeviceSettings();
                ((MainWindow)Window.GetWindow(this)).ShowToast($"Copied {source.Name} into {target.Name}");
                Refresh();
            }, "GhostBtn");
            item.HorizontalContentAlignment = HorizontalAlignment.Left;
            items.Children.Add(item);
        }
        popup.Child = new Border { Background = Ui.Brush("Surface"), BorderBrush = Ui.Brush("LineStrong"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(6), MinWidth = 200, Child = items };
        button.IsEnabled = items.Children.Count > 0;
        button.Click += (_, _) => popup.IsOpen = true;
        return button;
    }

    private void Remove(MouseProfile profile)
    {
        bool wasActive = profile == service.ActiveProfile;
        service.Settings.Profiles.Remove(profile);
        if (wasActive) service.SelectProfile(service.Settings.Profiles.FirstOrDefault(p => p.IsDesktop) ?? service.Settings.Profiles[0]);
        service.Save();
        selected = null;
        Refresh();
    }
}
