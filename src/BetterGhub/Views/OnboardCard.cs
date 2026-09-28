using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using BetterGhub.Core;
using BetterGhub.Services;

namespace BetterGhub.Views;

/// <summary>
/// The mouse's on-board profile slots: view them, rename (double-click) or enable, disable and run one
/// (right-click), fill one from a BetterGhub profile, or restore a backup. Buttons and DPI of the running slot are edited on
/// the Assignments and Sensitivity pages.
/// </summary>
internal sealed class OnboardCard : ContentControl
{
    private readonly MouseService service;
    private int selectedSlot = 1;
    private object? rendered;
    /// <summary>A slot name is being typed; background refreshes wait so they don't throw it away.</summary>
    private bool renaming;

    public OnboardCard(MouseService service)
    {
        this.service = service;
        Focusable = false;
        service.StateChanged += () => { if (IsLoaded) Render(); };
        service.OnboardWritten += (success, message) =>
        {
            Toast(success ? message : "Not saved: " + message);
            Render(force: true);
        };
        Render();
    }

    /// <summary>
    /// Rebuilds the card. StateChanged also fires for DPI and battery updates, so without <paramref name="force"/>
    /// this only redraws when something the card shows changed, and never while a name is being typed.
    /// </summary>
    public void Render(bool force = false)
    {
        object signature = (service.State, service.Onboard, service.OnboardBusy, service.OnboardSector);
        if (!force && (renaming || Equals(signature, rendered))) return;
        rendered = signature;
        renaming = false;

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
            + "or when you run a slot here or from the profile menu. While a slot runs, the Assignments and Sensitivity pages edit it and save to the mouse. "
            + "Double-click a slot to rename it, or right-click it for more.", "Body", size: 12, wrap: true).With(new Thickness(0, 6, 0, 18)));

        IReadOnlyList<OnboardSlot>? slots = service.OnboardSlots;
        if (slots is null || slots.Count == 0)
        {
            body.Children.Add(Ui.Text(connected ? "Reading the mouse's memory…" : "Connect the mouse to read its memory.", "Body"));
            Content = Ui.Card(body, new Thickness(24, 20, 24, 20));
            return;
        }
        if (slots.All(s => s.Number != selectedSlot)) selectedSlot = slots[0].Number;
        OnboardSlot slot = slots.First(s => s.Number == selectedSlot);

        Grid layout = new();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        StackPanel list = new() { Margin = new Thickness(0, 0, 28, 0) };
        foreach (OnboardSlot item in slots) list.Children.Add(SlotRow(item));
        layout.Children.Add(list);
        FrameworkElement detail = slot.Profile is null ? Ui.Text("This slot could not be read.", "Body") : Viewer(slot);
        Grid.SetColumn(detail, 1);
        layout.Children.Add(detail);
        body.Children.Add(layout);
        Content = Ui.Card(body, new Thickness(24, 20, 24, 20));
    }

    private bool CanWrite => service.State == ConnectionState.Connected && !service.OnboardBusy;

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
        RadioButton row = new()
        {
            Style = Ui.Style("Row"), Content = content, GroupName = "OnboardSlots", IsChecked = slot.Number == selectedSlot,
            ToolTip = slot.Profile is not null ? "Double-click to rename · right-click for more" : null
        };
        if (slot.Profile is not null && service.State == ConnectionState.Connected) row.ContextMenu = SlotMenu(slot, running, () => StartRename(slot, content, name));
        row.Click += (_, _) =>
        {
            if (slot.Number == selectedSlot || renaming) return;
            selectedSlot = slot.Number;
            Render(force: true);
        };
        row.MouseDoubleClick += (_, e) =>
        {
            e.Handled = true;
            if (slot.Profile is not null && CanWrite && !renaming) StartRename(slot, content, name);
        };
        return row;
    }

    private ContextMenu SlotMenu(OnboardSlot slot, bool running, Action rename)
    {
        MenuItem Item(string header, string glyph, Action click, bool enabled = true)
        {
            MenuItem item = new()
            {
                Header = header, IsEnabled = enabled,
                Icon = new TextBlock { Text = glyph, FontFamily = Ui.Font("Icons"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center }
            };
            item.Click += (_, _) => click();
            return item;
        }
        return new ContextMenu
        {
            Items =
            {
                Item("Rename", "\uE8AC", rename, CanWrite),
                running ? Item("Use BetterGhub profiles", "\uE8AB", service.UseHostMode, !service.OnboardBusy)
                    : Item("Run this slot", "\uE8AB", () => SwitchTo(slot), slot.Enabled && !service.OnboardBusy),
                slot.Enabled
                    ? Item("Disable slot", "\uE711", () => Apply(slot, edit => edit with { Enabled = false }), CanWrite && !running)
                    : Item("Enable slot", "\uE73E", () => Apply(slot, edit => edit with { Enabled = true }), CanWrite)
            }
        };
    }

    /// <summary>Swaps the slot's name for a text box: Enter or leaving it saves to the mouse, Esc cancels.</summary>
    private void StartRename(OnboardSlot slot, DockPanel content, TextBlock name)
    {
        renaming = true;
        TextBox box = new()
        {
            Text = slot.Profile!.Name, MaxLength = OnboardProfiles.MaxNameLength, Padding = new Thickness(6, 2, 6, 2),
            FontSize = 13.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(-7, -3, 8, -3), VerticalAlignment = VerticalAlignment.Center
        };
        int index = content.Children.IndexOf(name);
        content.Children.RemoveAt(index);
        content.Children.Insert(index, box);
        bool done = false;
        void Finish(bool save)
        {
            if (done) return;
            done = true;
            string text = box.Text.Trim();
            if (save && text != slot.Profile.Name) Apply(slot, edit => edit with { Name = text });
            else Render(force: true);
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Finish(save: true); }
            else if (e.Key == Key.Escape) { e.Handled = true; Finish(save: false); }
        };
        box.LostKeyboardFocus += (_, _) => Finish(save: true);
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
    }

    // ── Slot detail ───────────────────────────────────────────────────────────

    private FrameworkElement Viewer(OnboardSlot slot)
    {
        OnboardProfile profile = slot.Profile!;
        bool running = service.IsOnboard && service.OnboardSector == slot.Sector;
        StackPanel panel = new();
        DockPanel head = new();
        if (service.State == ConnectionState.Connected)
        {
            Button use = running
                ? Ui.Button("Use BetterGhub profiles", service.UseHostMode, "Btn", "", "Hand the buttons back to BetterGhub")
                : Ui.Button("Run this slot", () => SwitchTo(slot), "Btn", "",
                    slot.Enabled ? "Run the mouse from this slot. The Assignments and Sensitivity pages then edit it, saving to the mouse." : "Enable this slot first");
            use.IsEnabled = (running || slot.Enabled) && !service.OnboardBusy;
            StackPanel actions = Ui.Row(8, CopyFromButton(slot), use);
            DockPanel.SetDock(actions, Dock.Right);
            head.Children.Add(actions);
        }
        TextBlock title = Ui.Text(slot.DisplayName, size: 17, bold: true, color: "Text");
        title.VerticalAlignment = VerticalAlignment.Center;
        TextBlock state = Ui.Text(running ? "Running now" : slot.Enabled ? "Enabled" : "Disabled, so the mouse skips it", "Body", size: 12, color: running ? "Success" : null);
        state.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(Ui.Row(12, title, state));
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
        if (!running)
            panel.Children.Add(Ui.Text("Run this slot to change its buttons, DPI speeds and report rate on the Assignments and Sensitivity pages.",
                "Body", size: 11.5, color: "Faint", wrap: true).With(new Thickness(0, 4, 0, 0)));
        panel.Children.Add(BindingTable(profile));
        return panel;
    }

    private Button CopyFromButton(OnboardSlot slot)
    {
        Button button = Ui.Button("Copy from profile…", () => { }, "Btn", "", "Fill this slot from a BetterGhub profile: name, DPI, report rate and every button action the mouse can store");
        Popup popup = new() { PlacementTarget = button, Placement = PlacementMode.Bottom, VerticalOffset = 6, StaysOpen = false, AllowsTransparency = true };
        StackPanel items = new();
        foreach (MouseProfile profile in service.Settings.Profiles)
        {
            Button item = Ui.Button(profile.Name, () =>
            {
                popup.IsOpen = false;
                List<string> notes = [];
                OnboardEdit copied = service.EditFromProfile(profile, slot.Enabled, notes);
                if (Apply(slot, _ => copied) && notes.Count > 0)
                    Toast($"Copied {profile.Name}, except: " + string.Join("; ", notes));
            }, "GhostBtn");
            item.HorizontalContentAlignment = HorizontalAlignment.Left;
            items.Children.Add(item);
        }
        popup.Child = new Border { Background = Ui.Brush("Surface"), BorderBrush = Ui.Brush("LineStrong"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(6), MinWidth = 200, Child = items };
        button.Click += (_, _) => popup.IsOpen = true;
        button.IsEnabled = CanWrite;
        return button;
    }

    /// <summary>Writes a change to a slot straight away (a backup of every slot is saved first).</summary>
    private bool Apply(OnboardSlot slot, Func<OnboardEdit, OnboardEdit> change)
    {
        try
        {
            service.SaveOnboardSlot(slot, change(MouseService.EditFor(slot)));
            Render(force: true);
            return true;
        }
        catch (Exception error)
        {
            Toast(error.Message);
            Render(force: true);
            return false;
        }
    }

    // ── Restore and switching ─────────────────────────────────────────────────

    private void Restore()
    {
        Window owner = Window.GetWindow(this);
        Directory.CreateDirectory(MouseService.OnboardBackupFolder);
        Microsoft.Win32.OpenFileDialog dialog = new() { Filter = "On-board memory backup (*.json)|*.json", InitialDirectory = MouseService.OnboardBackupFolder };
        if (dialog.ShowDialog(owner) != true) return;
        try { service.RestoreOnboard(dialog.FileName); }
        catch (Exception error) { MessageBox.Show(owner, error.Message, "Could not restore the backup", MessageBoxButton.OK, MessageBoxImage.Error); }
        Render(force: true);
    }

    private void SwitchTo(OnboardSlot slot)
    {
        try { service.SwitchToOnboard(slot); }
        catch (Exception error) { Toast(error.Message); }
    }

    private void Toast(string text) => (Window.GetWindow(this) as MainWindow)?.ShowToast(text);

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

    /// <summary>Each button's action and its G-shift action, as stored on the mouse.</summary>
    private static Grid BindingTable(OnboardProfile profile)
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
            Cell(BindingText(binding), line, 1);
            Cell(shifted.GetValueOrDefault(binding.Index) is { } shift ? BindingText(shift) : Ui.Text("Same", size: 12.5, color: "Faint").With(new Thickness(0, 6, 8, 6)), line, 2);
            line++;
        }
        return table;
    }

    private static TextBlock BindingText(OnboardBinding binding)
    {
        bool quiet = binding.Description is "No action" or "Disabled";
        TextBlock text = Ui.Text(binding.Description, size: 12.5, color: quiet ? "Faint" : "Text").With(new Thickness(0, 6, 8, 6));
        text.ToolTip = $"Stored as {binding.Raw}";
        return text;
    }
}
