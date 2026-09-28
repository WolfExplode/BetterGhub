using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using BetterGhub.Core;
using BetterGhub.Services;

namespace BetterGhub.Views;

/// <summary>
/// The mouse's on-board profile slots: view them, edit one (name, enabled, report rate, DPI, or a copy of
/// a BetterGhub profile's bindings), restore a backup, or hand the mouse over to a slot.
/// </summary>
internal sealed class OnboardCard : ContentControl
{
    private static readonly int[] Rates = [125, 250, 500, 1000];

    private readonly MouseService service;
    private int selectedSlot = 1;
    /// <summary>Slot being edited and its unsaved values; null when just viewing.</summary>
    private Draft? draft;
    private object? rendered;

    private sealed class Draft(int slot, OnboardEdit edit)
    {
        public int Slot = slot;
        public string Name = edit.Name;
        public bool Enabled = edit.Enabled;
        public int ReportIntervalMs = edit.ReportIntervalMs;
        /// <summary>Five boxes; blank ones are dropped when saving.</summary>
        public string[] Dpis = [.. Enumerable.Range(0, 5).Select(i => i < edit.Dpis.Count ? edit.Dpis[i].ToString() : "")];
        public int DefaultIndex = edit.DefaultDpiIndex, ShiftIndex = edit.ShiftDpiIndex;
        public Dictionary<int, uint> Buttons = new(edit.Buttons), ShiftButtons = new(edit.ShiftButtons);
        public string? CopiedFrom;
        public List<string> Notes = [];
    }

    public OnboardCard(MouseService service)
    {
        this.service = service;
        Focusable = false;
        service.StateChanged += () => { if (IsLoaded) Render(); };
        service.OnboardWritten += (success, message) =>
        {
            if (success) draft = null;
            (Window.GetWindow(this) as MainWindow)?.ShowToast(success ? message : "Not saved: " + message);
            Render(force: true);
        };
        Render();
    }

    /// <summary>
    /// Rebuilds the card. StateChanged also fires for DPI and battery updates, so without <paramref name="force"/>
    /// this only redraws when something the card shows changed, and never throws away half-typed edits.
    /// </summary>
    public void Render(bool force = false)
    {
        object signature = (service.State, service.Onboard, service.OnboardBusy, service.OnboardSector);
        if (!force && Equals(signature, rendered)) return;
        rendered = signature;

        StackPanel body = new();
        DockPanel head = new();
        bool connected = service.State == ConnectionState.Connected;
        if (connected)
        {
            StackPanel actions = Ui.Row(8,
                Ui.Button("Restore backup…", Restore, "GhostBtn", "", "Write a saved backup of all slots back to the mouse"),
                Ui.Button("Read again", service.ReadOnboardMemory, "GhostBtn", ""));
            DockPanel.SetDock(actions, Dock.Right);
            head.Children.Add(actions);
        }
        head.Children.Add(Ui.Text("On-board memory", "H2"));
        body.Children.Add(head);
        body.Children.Add(Ui.Text("Profiles saved on the mouse itself, used when BetterGhub isn't handling the buttons: on another computer, when BetterGhub isn't running, "
            + "or when you run a slot here or from the profile menu. While a slot runs, the Assignments and Sensitivity pages edit it and save to the mouse.", "Body", size: 12, wrap: true).With(new Thickness(0, 6, 0, 18)));

        IReadOnlyList<OnboardSlot>? slots = service.OnboardSlots;
        if (slots is null || slots.Count == 0)
        {
            body.Children.Add(Ui.Text(connected ? "Reading the mouse's memory…" : "Connect the mouse to read its memory.", "Body"));
            Content = Ui.Card(body, new Thickness(24, 20, 24, 20));
            return;
        }
        if (slots.All(s => s.Number != selectedSlot)) selectedSlot = slots[0].Number;
        OnboardSlot slot = slots.First(s => s.Number == selectedSlot);
        if (draft is not null && draft.Slot != slot.Number) draft = null;

        Grid layout = new();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        StackPanel list = new() { Margin = new Thickness(0, 0, 28, 0) };
        foreach (OnboardSlot item in slots) list.Children.Add(SlotRow(item));
        layout.Children.Add(list);
        FrameworkElement detail = slot.Profile is null ? Ui.Text("This slot could not be read.", "Body")
            : draft is not null ? Editor(slot, draft) : Viewer(slot);
        Grid.SetColumn(detail, 1);
        layout.Children.Add(detail);
        body.Children.Add(layout);
        Content = Ui.Card(body, new Thickness(24, 20, 24, 20));
    }

    private RadioButton SlotRow(OnboardSlot slot)
    {
        DockPanel content = new();
        bool running = service.IsOnboard && service.OnboardSector == slot.Sector;
        Border state = Ui.Badge(running ? "RUNNING" : slot.Enabled ? "ON" : "OFF", running ? "OnAccent" : slot.Enabled ? "Success" : "Faint", running ? "Success" : "Surface2");
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
        row.Click += (_, _) =>
        {
            if (slot.Number == selectedSlot) return;
            selectedSlot = slot.Number;
            draft = null;
            Render(force: true);
        };
        return row;
    }

    /// <summary>For --snapshot: opens the editor on the selected slot.</summary>
    internal void StartEditing(MouseProfile? copyFrom = null)
    {
        if (service.OnboardSlots?.FirstOrDefault(s => s.Number == selectedSlot) is not { Profile: not null } slot) return;
        List<string> notes = [];
        draft = copyFrom is null ? new Draft(slot.Number, MouseService.EditFor(slot))
            : new Draft(slot.Number, service.EditFromProfile(copyFrom, slot.Enabled, notes)) { CopiedFrom = copyFrom.Name, Notes = notes };
        Render(force: true);
    }

    // ── Viewing ───────────────────────────────────────────────────────────────

    private FrameworkElement Viewer(OnboardSlot slot)
    {
        OnboardProfile profile = slot.Profile!;
        StackPanel panel = new();
        DockPanel head = new();
        if (service.State == ConnectionState.Connected)
        {
            bool running = service.IsOnboard && service.OnboardSector == slot.Sector;
            Button use = running
                ? Ui.Button("Use BetterGhub profiles", service.UseHostMode, "Btn", "", "Hand the buttons back to BetterGhub")
                : Ui.Button("Run this slot", () => SwitchTo(slot), "Btn", "",
                    slot.Enabled ? "Run the mouse from this slot. The Assignments and Sensitivity pages then edit it, saving to the mouse." : "Enable this slot first");
            use.IsEnabled = (running || slot.Enabled) && !service.OnboardBusy;
            Button edit = Ui.Button("Edit", () => { draft = new Draft(slot.Number, MouseService.EditFor(slot)); Render(force: true); }, "Btn", "");
            edit.IsEnabled = !service.OnboardBusy;
            StackPanel actions = Ui.Row(8, use, edit);
            DockPanel.SetDock(actions, Dock.Right);
            head.Children.Add(actions);
        }
        bool active = service.IsOnboard && service.OnboardSector == slot.Sector;
        TextBlock state = Ui.Text(active ? "Running now" : slot.Enabled ? "Enabled" : "Disabled, so the mouse skips it", "Body", size: 12, color: active ? "Success" : null);
        state.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(Ui.Row(12, Ui.Text(slot.DisplayName, size: 17, bold: true, color: "Text"), state));
        panel.Children.Add(head);
        if (!profile.ChecksumOk)
            panel.Children.Add(Notice("The checksum doesn't match, so the mouse uses its factory profile instead of this slot.", "Warning"));

        WrapPanel facts = new() { Margin = new Thickness(0, 16, 0, 0) };
        facts.Children.Add(Fact("REPORT RATE", profile.ReportIntervalMs > 0 ? $"{1000 / profile.ReportIntervalMs} Hz" : "Unknown"));
        StackPanel dpis = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        for (int i = 0; i < profile.Dpis.Count; i++) dpis.Children.Add(DpiChip(profile.Dpis[i], i == profile.DefaultDpiIndex, i == profile.ShiftDpiIndex));
        facts.Children.Add(new StackPanel { Margin = new Thickness(0, 0, 24, 6), Children = { Ui.Text("DPI SPEEDS", "Overline"), dpis } });
        facts.Children.Add(Legend());
        panel.Children.Add(facts);
        panel.Children.Add(BindingTable(profile, null));
        return panel;
    }

    // ── Editing ───────────────────────────────────────────────────────────────

    private FrameworkElement Editor(OnboardSlot slot, Draft edit)
    {
        OnboardProfile profile = slot.Profile!;
        StackPanel panel = new();
        DockPanel head = new();
        Button save = Ui.Button(service.OnboardBusy ? "Saving…" : "Save to mouse", () => Save(slot, edit), "PrimaryBtn", "");
        save.IsEnabled = !service.OnboardBusy && service.State == ConnectionState.Connected;
        Button cancel = Ui.Button("Cancel", () => { draft = null; Render(force: true); }, "Btn");
        cancel.IsEnabled = !service.OnboardBusy;
        StackPanel actions = Ui.Row(8, CopyFromButton(edit), cancel, save);
        DockPanel.SetDock(actions, Dock.Right);
        head.Children.Add(actions);
        head.Children.Add(Ui.Text($"Editing slot {slot.Number}", size: 17, bold: true, color: "Text"));
        panel.Children.Add(head);
        if (edit.CopiedFrom is { } source)
            panel.Children.Add(Notice($"Copied from BetterGhub profile \"{source}\"." + (edit.Notes.Count > 0 ? "\n• " + string.Join("\n• ", edit.Notes) : ""),
                edit.Notes.Count > 0 ? "Warning" : "Accent"));

        Grid form = new() { Margin = new Thickness(0, 16, 0, 0) };
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Row(string label, UIElement control)
        {
            int row = form.RowDefinitions.Count;
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            TextBlock text = Ui.Text(label, size: 12.5, color: "Muted");
            text.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(text, row);
            form.Children.Add(text);
            if (control is FrameworkElement element) element.Margin = new Thickness(0, 0, 0, 12);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, 1);
            form.Children.Add(control);
        }

        TextBox name = new() { Text = edit.Name, MaxLength = OnboardProfiles.MaxNameLength, Width = 280, Padding = new Thickness(12, 8, 12, 8), HorizontalAlignment = HorizontalAlignment.Left };
        name.TextChanged += (_, _) => edit.Name = name.Text;
        Row("Name", name);

        CheckBox enabled = new() { Style = Ui.Style("Switch"), IsChecked = edit.Enabled, Content = Ui.Text("The mouse can switch to this slot", color: "Text") };
        enabled.Click += (_, _) => edit.Enabled = enabled.IsChecked == true;
        Row("Enabled", enabled);

        StackPanel rates = new() { Orientation = Orientation.Horizontal };
        foreach (int hz in Rates)
        {
            RadioButton rate = new() { Content = $"{hz} Hz", Style = Ui.Style("Segment"), GroupName = "OnboardRate", IsChecked = 1000 / edit.ReportIntervalMs == hz, Padding = new Thickness(12, 7, 12, 7) };
            rate.Checked += (_, _) => edit.ReportIntervalMs = 1000 / hz;
            rates.Children.Add(rate);
        }
        Row("Report rate", new Border { Style = Ui.Style("SegmentHost"), Child = rates, HorizontalAlignment = HorizontalAlignment.Left });

        StackPanel dpiBoxes = new() { Orientation = Orientation.Horizontal };
        for (int i = 0; i < 5; i++)
        {
            int index = i;
            TextBox box = new() { Text = edit.Dpis[i], Width = 72, Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 8, 0), MaxLength = 5, HorizontalContentAlignment = HorizontalAlignment.Center };
            box.TextChanged += (_, _) => edit.Dpis[index] = box.Text.Trim();
            box.LostFocus += (_, _) => Render(force: true); // Refresh the default and shift pickers' labels.
            dpiBoxes.Children.Add(box);
        }
        Row("DPI speeds", new StackPanel { Children = { dpiBoxes, Ui.Text("Leave a box empty to use fewer speeds.", "Body", size: 11.5, color: "Faint").With(new Thickness(0, 6, 0, 0)) } });
        Row("Default speed", DpiPicker(edit, shift: false));
        Row("DPI Shift speed", DpiPicker(edit, shift: true));
        panel.Children.Add(form);
        panel.Children.Add(Ui.Text("To change button actions, set them up in a BetterGhub profile and use Copy from profile. "
            + "Macros, launching apps and double clicks only work in BetterGhub, so the mouse can't store them.", "Body", size: 11.5, color: "Faint", wrap: true).With(new Thickness(0, 2, 0, 0)));
        panel.Children.Add(BindingTable(profile, edit));
        return panel;
    }

    private static Border DpiPicker(Draft edit, bool shift)
    {
        StackPanel options = new() { Orientation = Orientation.Horizontal };
        for (int i = 0; i < 5; i++)
        {
            if (!int.TryParse(edit.Dpis[i], out int dpi)) continue;
            int index = i;
            RadioButton option = new()
            {
                Content = dpi.ToString(), Style = Ui.Style("Segment"), GroupName = shift ? "OnboardShift" : "OnboardDefault",
                IsChecked = (shift ? edit.ShiftIndex : edit.DefaultIndex) == i, Padding = new Thickness(12, 7, 12, 7)
            };
            option.Checked += (_, _) => { if (shift) edit.ShiftIndex = index; else edit.DefaultIndex = index; };
            options.Children.Add(option);
        }
        return new Border { Style = Ui.Style("SegmentHost"), Child = options, HorizontalAlignment = HorizontalAlignment.Left };
    }

    private Button CopyFromButton(Draft edit)
    {
        Button button = Ui.Button("Copy from profile…", () => { }, "Btn", "", "Fill this slot from a BetterGhub profile: name, DPI, report rate and button actions");
        Popup popup = new() { PlacementTarget = button, Placement = PlacementMode.Bottom, VerticalOffset = 6, StaysOpen = false, AllowsTransparency = true };
        StackPanel items = new();
        foreach (MouseProfile profile in service.Settings.Profiles)
        {
            Button item = Ui.Button(profile.Name, () =>
            {
                popup.IsOpen = false;
                List<string> notes = [];
                OnboardEdit copied = service.EditFromProfile(profile, edit.Enabled, notes);
                draft = new Draft(edit.Slot, copied) { CopiedFrom = profile.Name, Notes = notes };
                Render(force: true);
            }, "GhostBtn");
            item.HorizontalContentAlignment = HorizontalAlignment.Left;
            items.Children.Add(item);
        }
        popup.Child = new Border { Background = Ui.Brush("Surface"), BorderBrush = Ui.Brush("LineStrong"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(6), MinWidth = 200, Child = items };
        button.Click += (_, _) => popup.IsOpen = true;
        button.IsEnabled = !service.OnboardBusy;
        return button;
    }

    private void Save(OnboardSlot slot, Draft edit)
    {
        Window owner = Window.GetWindow(this);
        List<int> dpis = [];
        int defaultIndex = -1, shiftIndex = -1;
        for (int i = 0; i < 5; i++)
        {
            if (edit.Dpis[i].Length == 0) continue;
            if (!int.TryParse(edit.Dpis[i], out int dpi) || dpi is < 100 or > 25600)
            {
                MessageBox.Show(owner, $"\"{edit.Dpis[i]}\" isn't a DPI speed. Use 100–25600.", "Check the DPI speeds", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (i == edit.DefaultIndex) defaultIndex = dpis.Count;
            if (i == edit.ShiftIndex) shiftIndex = dpis.Count;
            dpis.Add(dpi);
        }
        if (dpis.Count == 0 || defaultIndex < 0 || shiftIndex < 0)
        {
            MessageBox.Show(owner, "Enter at least one DPI speed, and pick a default and a DPI Shift speed from them.", "Check the DPI speeds", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        OnboardEdit final = new(edit.Name, edit.Enabled, edit.ReportIntervalMs, dpis, defaultIndex, shiftIndex, edit.Buttons, edit.ShiftButtons);
        try { service.SaveOnboardSlot(slot, final); }
        catch (Exception error) { MessageBox.Show(owner, error.Message, "Could not save to the mouse", MessageBoxButton.OK, MessageBoxImage.Error); }
        Render(force: true);
    }

    // ── Restore and switching ─────────────────────────────────────────────────

    private void Restore()
    {
        Window owner = Window.GetWindow(this);
        Directory.CreateDirectory(MouseService.OnboardBackupFolder);
        Microsoft.Win32.OpenFileDialog dialog = new() { Filter = "On-board memory backup (*.json)|*.json", InitialDirectory = MouseService.OnboardBackupFolder };
        if (dialog.ShowDialog(owner) != true) return;
        try
        {
            service.RestoreOnboard(dialog.FileName);
            draft = null;
        }
        catch (Exception error) { MessageBox.Show(owner, error.Message, "Could not restore the backup", MessageBoxButton.OK, MessageBoxImage.Error); }
        Render(force: true);
    }

    private void SwitchTo(OnboardSlot slot)
    {
        Window owner = Window.GetWindow(this);
        try { service.SwitchToOnboard(slot); }
        catch (Exception error) { MessageBox.Show(owner, error.Message, "Could not switch", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    // ── Shared pieces ─────────────────────────────────────────────────────────

    private static Border Notice(string text, string color) => new()
    {
        Background = Ui.Brush(color == "Warning" ? "WarningDim" : "AccentDim"), CornerRadius = new CornerRadius(9), Padding = new Thickness(12), Margin = new Thickness(0, 12, 0, 0),
        Child = Ui.Text(text, "Body", size: 12, color: color, wrap: true)
    };

    private static FrameworkElement Fact(string title, string value) =>
        new StackPanel { Margin = new Thickness(0, 0, 40, 6), Children = { Ui.Text(title, "Overline"), Ui.Text(value, size: 15, bold: true, color: "Text") } };

    private static Border DpiChip(int dpi, bool isDefault, bool isShift) => new()
    {
        CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 0),
        Background = Ui.Brush(isDefault ? "AccentDim" : "Surface2"),
        BorderBrush = Ui.Brush(isDefault ? "Accent" : isShift ? "Warning" : "Surface2"), BorderThickness = new Thickness(1),
        ToolTip = isDefault && isShift ? "Default and DPI Shift speed" : isDefault ? "Default speed" : isShift ? "DPI Shift speed" : null,
        Child = Ui.Text(dpi.ToString(), size: 13, bold: true, color: isDefault ? "Accent" : "Text")
    };

    private static StackPanel Legend()
    {
        static StackPanel Key(string color, string label) =>
            Ui.Row(6, new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(3), BorderBrush = Ui.Brush(color), BorderThickness = new Thickness(1.5), VerticalAlignment = VerticalAlignment.Center },
                Ui.Text(label, "Body", size: 11.5));
        return new StackPanel { Margin = new Thickness(0, 22, 0, 6), VerticalAlignment = VerticalAlignment.Center, Children = { Ui.Row(14, Key("Accent", "Default"), Key("Warning", "DPI Shift")) } };
    }

    /// <summary>Button and G-shift actions; with a draft, pending changes show in the accent colour.</summary>
    private static Grid BindingTable(OnboardProfile profile, Draft? edit)
    {
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
            uint? pending = edit?.Buttons.TryGetValue(binding.Index, out uint code) == true ? code : null;
            Cell(BindingText(binding, pending), line, 1);
            OnboardBinding? shift = shifted.GetValueOrDefault(binding.Index);
            uint? pendingShift = edit?.ShiftButtons.TryGetValue(binding.Index, out uint shiftCode) == true ? shiftCode : null;
            Cell(shift is null && pendingShift is null ? Ui.Text("Same", size: 12.5, color: "Faint").With(new Thickness(0, 6, 8, 6)) : BindingText(shift, pendingShift), line, 2);
            line++;
        }
        return table;
    }

    private static TextBlock BindingText(OnboardBinding? stored, uint? pending)
    {
        string raw = pending is uint code ? code.ToString("X8") : stored?.Raw ?? "";
        string description = pending is uint value ? OnboardProfiles.Describe(value) : stored?.Description ?? "";
        bool changed = pending is not null && raw != stored?.Raw;
        bool quiet = description is "No action" or "Disabled";
        TextBlock text = Ui.Text(description, size: 12.5, bold: changed, color: changed ? "Accent" : quiet ? "Faint" : "Text").With(new Thickness(0, 6, 8, 6));
        text.ToolTip = changed ? $"Will be stored as {raw} (now {stored?.Raw ?? "empty"})" : $"Stored as {raw}";
        return text;
    }
}
