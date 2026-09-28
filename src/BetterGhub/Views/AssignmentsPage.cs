using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterGhub.Core;
using BetterGhub.Input;
using BetterGhub.Services;

namespace BetterGhub.Views;

internal sealed class AssignmentsPage : UserControl, IPage
{
    private readonly MouseService service;
    private readonly MainWindow shell;
    private readonly MouseDiagram diagram;
    private readonly StackPanel panel = new();
    private readonly Border calibrationBanner = new();
    private readonly DockPanel toolbar = new() { LastChildFill = false };
    private readonly RadioButton topView = new() { Content = "TOP", GroupName = "View" };
    private readonly RadioButton sideView = new() { Content = "SIDE", GroupName = "View" };
    private MouseControl selected = MouseControls.ById("G1")!;
    private string tab = "Commands";
    private string tabKey = "";
    private string search = "";
    private bool calibrating;
    /// <summary>Left and right click stay locked until the warning is acknowledged, once per session.</summary>
    private readonly HashSet<string> unlockedClicks = [];

    public AssignmentsPage(MouseService service, MainWindow shell)
    {
        this.service = service;
        this.shell = shell;
        diagram = new MouseDiagram(service) { Selected = selected, Margin = new Thickness(0, 0, 0, 10) };
        diagram.ControlClicked += Select;
        diagram.AssignmentDropped += AssignDropped;
        diagram.ResetRequested += ResetControl;

        Grid layout = new() { Margin = new Thickness(36, 8, 36, 28) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(350) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Border left = Ui.Card(Ui.Scroll(panel), new Thickness(22, 20, 14, 20));
        layout.Children.Add(left);

        Grid stage = new() { Margin = new Thickness(24, 0, 0, 0) };
        stage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stage.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        stage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(stage, 1);
        layout.Children.Add(stage);

        stage.Children.Add(toolbar);

        Grid.SetRow(diagram, 1);
        stage.Children.Add(diagram);

        calibrationBanner.Visibility = Visibility.Collapsed;
        calibrationBanner.VerticalAlignment = VerticalAlignment.Top;
        calibrationBanner.HorizontalAlignment = HorizontalAlignment.Center;
        calibrationBanner.Margin = new Thickness(0, 14, 0, 0);
        calibrationBanner.Background = Ui.Brush("Surface2");
        calibrationBanner.BorderBrush = Ui.Brush("Warning");
        calibrationBanner.BorderThickness = new Thickness(1);
        calibrationBanner.CornerRadius = new CornerRadius(12);
        calibrationBanner.Padding = new Thickness(18, 12, 18, 12);
        calibrationBanner.Child = Ui.Text("Click a control, then press it on your mouse. Left click, right click and the scroll wheel are fixed.", size: 13, color: "Text");
        Grid.SetRow(calibrationBanner, 1);
        stage.Children.Add(calibrationBanner);

        topView.Style = Ui.Style("Segment");
        sideView.Style = Ui.Style("Segment");
        topView.IsChecked = true;
        topView.Checked += (_, _) => SetView(MouseView.Top);
        sideView.Checked += (_, _) => SetView(MouseView.Side);
        Border views = new() { Style = Ui.Style("SegmentHost"), HorizontalAlignment = HorizontalAlignment.Center, Child = Ui.Row(2, topView, sideView) };

        // Layer switch under the view toggle, like G HUB: DEFAULT ◯— G-SHIFT
        TextBlock defaultLabel = Ui.Text("DEFAULT", size: 12.5, bold: true);
        TextBlock shiftLabel = Ui.Text("G-SHIFT", size: 12.5, bold: true);
        CheckBox layerSwitch = new() { Style = Ui.Style("Switch"), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Edit what buttons do while G-Shift is held" };
        void ShowLayer()
        {
            defaultLabel.Foreground = Ui.Brush(diagram.ShiftLayer ? "Faint" : "Text");
            shiftLabel.Foreground = Ui.Brush(diagram.ShiftLayer ? "Text" : "Faint");
        }
        layerSwitch.Click += (_, _) => { diagram.ShiftLayer = layerSwitch.IsChecked == true; ShowLayer(); Refresh(); };
        ShowLayer();
        defaultLabel.VerticalAlignment = shiftLabel.VerticalAlignment = VerticalAlignment.Center;
        // The switch template leaves a 12px gap for content it doesn't have here.
        StackPanel layers = Ui.Row(10, defaultLabel, layerSwitch, shiftLabel.With(new Thickness(-12, 0, 0, 0)));
        layers.HorizontalAlignment = HorizontalAlignment.Center;
        layers.Margin = new Thickness(0, 14, 0, 0);

        StackPanel bottom = new() { Children = { views, layers } };
        Grid.SetRow(bottom, 2);
        stage.Children.Add(bottom);

        Content = layout;

        service.ButtonChanged += OnButton;
        service.Calibrated += (control, _) => { if (calibrating && IsVisible) shell.ShowToast($"{control.Label} calibrated"); Refresh(); };
        IsVisibleChanged += (_, _) => { if (!IsVisible && calibrating) SetCalibrating(false); };
        service.StateChanged += () => { if (IsLoaded) RenderPanel(); };
        Refresh();
    }

    public void Refresh()
    {
        diagram.CalibrationMode = calibrating;
        diagram.Rebuild();
        RenderToolbar();
        RenderPanel();
    }

    private void RenderToolbar()
    {
        toolbar.Children.Clear();
        Button toggle = calibrating
            ? Ui.Button("Done", () => SetCalibrating(false), "PrimaryBtn", "")
            : Ui.Button("Calibrate buttons", () => SetCalibrating(true), "Btn", "", "Click a control, then press it on the mouse");
        DockPanel.SetDock(toggle, Dock.Right);
        toolbar.Children.Add(toggle);
    }

    private void OnButton(int bit, bool down)
    {
        if (bit >= Input.RawMouseWheel.Up) { if (down) diagram.Pulse(bit); return; }
        diagram.SetPressed(bit, down);
        if (down && service.Learning is null && service.Settings.ControlFor(bit) is null && IsVisible)
            shell.ShowToast(calibrating
                ? $"Unmapped button 0x{bit:x4}. Click the control it belongs to, then press it again."
                : $"Unmapped button 0x{bit:x4}. Use Calibrate buttons to name it.");
    }

    private void Select(MouseControl control)
    {
        selected = control;
        diagram.Selected = control;
        if (calibrating && control.Calibratable) service.StartLearning(control);
        else if (service.Learning is not null) service.CancelLearning();
        if (calibrating && !control.Calibratable) shell.ShowToast($"{control.Label} is fixed and doesn't need calibrating");
        Refresh();
    }

    private void AssignDropped(MouseControl control, string id)
    {
        if (service.Settings.BitFor(control) is not int bit) return;
        if (id == BuiltinActions.GShift && diagram.ShiftLayer) { shell.ShowToast("G-Shift can't be assigned on the G-Shift layer"); return; }
        if (id == BuiltinActions.DpiShift && control.IsWheel) { shell.ShowToast("DPI Shift can't be assigned to the wheel"); return; }
        if (ClickLocked(control))
        {
            SelectControl(control.Id);
            shell.ShowToast($"Read the warning and choose Change anyway before reassigning {control.DefaultAction.ToLowerInvariant()}");
            return;
        }
        if (!service.Assign(diagram.ShiftLayer, bit, id)) return; // The service already explained why.
        shell.ShowToast($"{control.Label} → {service.Settings.DescribeAssignment(id)}");
        Refresh();
    }

    private void ResetControl(MouseControl control)
    {
        if (service.Settings.BitFor(control) is not int bit) return;
        service.Assign(diagram.ShiftLayer, bit, null);
        shell.ShowToast(diagram.ShiftLayer ? $"{control.Label} uses its Default layer action" : $"{control.Label} reset to {control.DefaultAction.ToLowerInvariant()}");
        Refresh();
    }

    /// <summary>
    /// Lets an option row be dragged onto a control in the diagram to assign it. Dragging is the only way to
    /// assign from the list, so a stray click can't overwrite a binding.
    /// </summary>
    private void MakeDraggable(RadioButton row, string id, bool isCurrent)
    {
        row.Cursor = System.Windows.Input.Cursors.Hand;
        row.Click += (_, _) =>
        {
            row.IsChecked = isCurrent;
            if (!isCurrent) shell.ShowToast($"Drag {service.Settings.DescribeAssignment(id)} onto a button, or right-click it, to assign it");
        };
        Point? start = null;
        row.PreviewMouseLeftButtonDown += (_, e) => start = e.GetPosition(row);
        row.PreviewMouseMove += (_, e) =>
        {
            if (start is not Point origin || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) { start = null; return; }
            Vector moved = e.GetPosition(row) - origin;
            if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            start = null;
            // Release the button's capture so the drop doesn't also count as a click on the row.
            row.ReleaseMouseCapture();

            // OLE drag-and-drop draws nothing, so float a chip beside the cursor. It sits off the
            // hotspot because the popup is its own window and would otherwise swallow the drop.
            System.Windows.Controls.Primitives.Popup ghost = new()
            {
                AllowsTransparency = true, IsHitTestVisible = false,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Absolute,
                Child = new Border
                {
                    Background = Ui.Brush("Surface2"), BorderBrush = Ui.Brush("Accent"), BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6, 12, 6), Opacity = 0.95,
                    Child = Ui.Row(8, Ui.Glyph("", 12, "Accent"), Ui.Text(service.Settings.DescribeAssignment(id), bold: true))
                }
            };
            Matrix toDip = PresentationSource.FromVisual(row)?.CompositionTarget.TransformFromDevice ?? Matrix.Identity;
            void Follow()
            {
                if (!GetCursorPos(out CursorPoint cursor)) return;
                Point at = toDip.Transform(new Point(cursor.X, cursor.Y));
                ghost.HorizontalOffset = at.X + 16;
                ghost.VerticalOffset = at.Y + 14;
            }
            GiveFeedbackEventHandler feedback = (_, _) => Follow();
            row.GiveFeedback += feedback;
            Follow();
            ghost.IsOpen = true;
            try { DragDrop.DoDragDrop(row, new DataObject(MouseDiagram.DragFormat, id), DragDropEffects.Copy); }
            finally
            {
                ghost.IsOpen = false;
                row.GiveFeedback -= feedback;
            }
        };
    }

    private struct CursorPoint { public int X, Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);

    internal void SelectControl(string id)
    {
        if (MouseControls.ById(id) is not { } control) return;
        ShowView(control.View);
        Select(control);
    }

    private void SetView(MouseView view)
    {
        if (diagram.View == view) return;
        diagram.View = view;
        if (selected.View != view) { selected = MouseControls.All.First(c => c.View == view); diagram.Selected = selected; }
        Refresh();
    }

    private void ShowView(MouseView view)
    {
        if (view == MouseView.Top) topView.IsChecked = true; else sideView.IsChecked = true;
    }

    // ── Left panel ────────────────────────────────────────────────────────────

    private void RenderPanel()
    {
        panel.Children.Clear();
        MouseProfile profile = service.ActiveProfile;
        int? bit = service.Settings.BitFor(selected);

        if (diagram.ShiftLayer)
            panel.Children.Add(Ui.Text(
                profile.Assignments.ContainsValue(BuiltinActions.GShift)
                    ? "Editing the G-Shift layer. These actions apply while the G-Shift button is held."
                    : "Editing the G-Shift layer. Assign G-Shift to a button on the Default layer to use it.",
                "Body", size: 12, color: "Accent", wrap: true).With(new Thickness(0, 0, 8, 16)));

        if (bit is null && service.Learning?.Id != selected.Id)
            panel.Children.Add(Ui.Text($"{selected.Label} isn't calibrated yet. Use Calibrate buttons to set it.", "Body", size: 12, color: "Warning", wrap: true).With(new Thickness(0, 0, 8, 12)));

        if (service.Learning?.Id == selected.Id)
        {
            panel.Children.Add(LearningBox().With(new Thickness(0, 0, 8, 22)));
        }
        else if (calibrating && service.Settings.ControlBits.ContainsKey(selected.Id))
        {
            Button reset = Ui.Button($"Reset {selected.Label} calibration", () => { service.ForgetCalibration(selected); Refresh(); }, "GhostBtn");
            reset.HorizontalAlignment = HorizontalAlignment.Left;
            panel.Children.Add(reset.With(new Thickness(0, 0, 0, 22)));
        }
        if (bit is null) return;
        if (ClickLocked(selected))
        {
            panel.Children.Add(ClickWarning().With(new Thickness(0, 0, 8, 22)));
            return;
        }

        // Assignment picker
        panel.Children.Add(Ui.Text("ASSIGN", "Overline").With(new Thickness(0, 0, 0, 4)));
        panel.Children.Add(Ui.Text($"Drag an action onto a button on the mouse, or right-click it to assign it to {selected.Label}.", "Body", size: 12, wrap: true).With(new Thickness(0, 0, 8, 8)));
        Dictionary<int, string> bindings = diagram.ShiftLayer ? profile.ShiftAssignments : profile.Assignments;
        string current = bindings.GetValueOrDefault(bit.Value) ?? "";
        string key = selected.Id + (diagram.ShiftLayer ? "/shift" : "");
        if (key != tabKey)
        {
            tabKey = key;
            search = "";
            if (TabFor(current) is { } assignedTab) tab = assignedTab;
        }

        WrapPanel tabs = new() { Margin = new Thickness(0, 0, 8, 8) };
        foreach (string name in Tabs)
        {
            RadioButton item = new() { Content = name.ToUpperInvariant(), Style = Ui.Style("TextTab"), GroupName = "AssignTab", IsChecked = tab == name };
            item.Checked += (_, _) => { if (tab == name) return; tab = name; RenderPanel(); };
            tabs.Children.Add(item);
        }
        panel.Children.Add(tabs);

        StackPanel options = new();
        TextBox filter = new() { Text = search, Margin = new Thickness(0, 0, 8, 10) };
        filter.TextChanged += (_, _) => { search = filter.Text; RenderOptions(options, bit.Value, current, bindings); };
        panel.Children.Add(Ui.Placeholder(filter, tab switch
        {
            "Commands" => "Search for a command",
            "Keys" => "Search for a key",
            "Macros" => "Search for a macro",
            _ => "Search for a system control"
        }));

        panel.Children.Add(options);
        RenderOptions(options, bit.Value, current, bindings);
    }

    private static readonly string[] Tabs = ["Commands", "Keys", "Macros", "System"];

    /// <summary>The tab that lists <paramref name="id"/>, or null when nothing is assigned.</summary>
    private string? TabFor(string id)
    {
        if (id.Length == 0) return null;
        if (service.Settings.IsMacro(id)) return "Macros";
        if (Assignments.KeyCombo(id) is { } combo)
        {
            if (Assignments.Commands.Any(c => c.Combo == combo)) return "Commands";
            if (BuiltinActions.All.Any(a => a.Id == id)) return "System";
            return combo.Contains('+') ? "Commands" : "Keys";
        }
        return "System";
    }

    private bool Matches(params string[] fields) =>
        search.Trim().Length == 0 || fields.Any(f => f.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase));

    private void RenderOptions(StackPanel options, int bit, string current, Dictionary<int, string> bindings)
    {
        options.Children.Clear();
        // Which controls on this layer already use each assignment, like G HUB's right-hand column.
        Dictionary<string, string> usage = bindings
            .Select(x => (x.Value, Control: service.Settings.ControlFor(x.Key)))
            .Where(x => x.Control is not null)
            .GroupBy(x => x.Value)
            .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(x => ShortLabel(x.Control!))));
        string Used(string id) => usage.GetValueOrDefault(id) ?? "";
        int before = 0;

        switch (tab)
        {
            case "Commands":
                options.Children.Add(ShortcutRecorder(bit));
                before = options.Children.Count;
                string? custom = Assignments.KeyCombo(current);
                if (custom is not null && custom.Contains('+') && !Assignments.Commands.Any(c => c.Combo == custom)
                    && !BuiltinActions.All.Any(a => a.Id == current) && Matches(custom, Assignments.DisplayCombo(custom), "Custom"))
                {
                    options.Children.Add(Section("Custom"));
                    options.Children.Add(ListRow(current, true, Assignments.DisplayCombo(custom), "Custom shortcut", Used(current), bit, comboColumn: true));
                }
                foreach (IGrouping<string, (string Category, string Combo, string Name)> group in Assignments.Commands.GroupBy(c => c.Category))
                {
                    List<(string Category, string Combo, string Name)> items = group
                        .Where(c => Matches(c.Name, c.Combo, Assignments.DisplayCombo(c.Combo), c.Category)).ToList();
                    if (items.Count == 0) continue;
                    options.Children.Add(Section(group.Key));
                    foreach ((_, string combo, string name) in items)
                    {
                        string id = Assignments.KeyId(combo);
                        options.Children.Add(ListRow(id, current == id, Assignments.DisplayCombo(combo), name, Used(id), bit, comboColumn: true));
                    }
                }
                break;

            case "Keys":
                options.Children.Add(Ui.Text("The key stays down for as long as you hold the button.", "Body", size: 12, wrap: true).With(new Thickness(2, 0, 8, 8)));
                before = options.Children.Count;
                foreach ((string keyName, string name) in Assignments.Keys)
                {
                    if (!Matches(name, keyName)) continue;
                    string id = Assignments.KeyId(keyName);
                    options.Children.Add(ListRow(id, current == id, name, null, Used(id), bit));
                }
                break;

            case "Macros":
                StackPanel list = new();
                options.Children.Add(list);
                foreach (MacroDefinition macro in service.Settings.Macros.Where(m => Matches(m.Name)))
                    list.Children.Add(OptionRow(macro.Id, current == macro.Id, macro.Name, ModeName(macro.Mode) + $" · {macro.Steps.Count} step{(macro.Steps.Count == 1 ? "" : "s")}", bit, macro));
                if (service.Settings.Macros.Count == 0)
                    list.Children.Add(Ui.Text("No macros yet. Create one and it will be assigned to this button.", "Body", size: 12).With(new Thickness(2, 4, 8, 4)));
                else if (list.Children.Count == 0)
                    list.Children.Add(NoMatches());
                Button create = Ui.Button("New macro", () => CreateMacroFor(bit), "GhostBtn", "");
                create.HorizontalAlignment = HorizontalAlignment.Left;
                options.Children.Add(create.With(new Thickness(0, 6, 0, 0)));
                return;

            default:
                if (Matches("Launch Application", "application", "program"))
                {
                    options.Children.Add(Section("Launch Application"));
                    IEnumerable<string> launches = service.ActiveProfile.Assignments.Values
                        .Concat(service.ActiveProfile.ShiftAssignments.Values)
                        .Append(current)
                        .Where(x => Assignments.LaunchPath(x) is not null)
                        .Distinct();
                    foreach (string id in launches)
                        options.Children.Add(ListRow(id, current == id, Path.GetFileNameWithoutExtension(Assignments.LaunchPath(id)!), null, Used(id), bit, tooltip: Assignments.LaunchPath(id)));
                    Button add = Ui.Button("ADD APPLICATION", () => PickApplication(bit), "GhostBtn", "");
                    add.HorizontalAlignment = HorizontalAlignment.Left;
                    options.Children.Add(add.With(new Thickness(0, 0, 0, 4)));
                }
                before = options.Children.Count;
                foreach (IGrouping<string, (string Id, string Name, string Description, string Category)> group in BuiltinActions.All.GroupBy(a => a.Category))
                {
                    List<(string Id, string Name, string Description, string Category)> items = group
                        .Where(a => !(a.Id == BuiltinActions.GShift && diagram.ShiftLayer))
                        .Where(a => !(a.Id == BuiltinActions.DpiShift && selected.IsWheel))
                        .Where(a => Matches(a.Name, a.Category, a.Description)).ToList();
                    if (items.Count == 0) continue;
                    options.Children.Add(Section(group.Key));
                    foreach ((string id, string name, string description, _) in items)
                        options.Children.Add(ListRow(id, current == id, name, null, Used(id), bit, tooltip: description.Length > 0 ? description : null));
                }
                break;
        }
        if (options.Children.Count == before) options.Children.Add(NoMatches());
    }

    private static TextBlock NoMatches() => Ui.Text("Nothing matches your search.", "Body", size: 12).With(new Thickness(2, 6, 8, 4));

    private static TextBlock Section(string title) =>
        Ui.Text(title, size: 14, bold: true).With(new Thickness(2, 14, 8, 6));

    private static string ShortLabel(MouseControl control) =>
        control.Id.StartsWith('G') ? control.Id : control.Label;

    /// <summary>A one-line option: name (or shortcut and name for commands) plus the controls already using it.</summary>
    private RadioButton ListRow(string id, bool isCurrent, string primary, string? secondary, string used, int bit, bool comboColumn = false, string? tooltip = null)
    {
        Grid content = new();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = comboColumn ? new GridLength(118) : new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = comboColumn ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        TextBlock first = Ui.Text(primary, size: 12.5, bold: comboColumn, wrap: true);
        first.VerticalAlignment = VerticalAlignment.Center;
        content.Children.Add(first);
        if (secondary is not null)
        {
            TextBlock second = Ui.Text(secondary, size: 12.5, color: "Muted", wrap: true).With(new Thickness(10, 0, 0, 0));
            second.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(second, 1);
            content.Children.Add(second);
        }
        if (used.Length > 0)
        {
            TextBlock badge = Ui.Text(used, size: 11.5, color: "Accent", bold: true).With(new Thickness(10, 0, 0, 0));
            badge.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(badge, 2);
            content.Children.Add(badge);
        }
        RadioButton row = new()
        {
            Style = Ui.Style("Row"), Content = content, IsChecked = isCurrent, GroupName = "Assignment",
            Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 8, 1), ToolTip = tooltip
        };
        MakeDraggable(row, id, isCurrent);
        row.ContextMenu = AssignMenu(id, isCurrent, bit);
        return row;
    }

    /// <summary>Right-click menu on an option row: the other deliberate way to assign it, to the selected control.</summary>
    private ContextMenu AssignMenu(string id, bool isCurrent, int bit, MacroDefinition? macro = null)
    {
        MenuItem assign = new()
        {
            Header = isCurrent ? $"Already assigned to {selected.Label}" : $"Assign to {selected.Label}",
            Icon = Ui.Glyph("", 12),
            IsEnabled = !isCurrent
        };
        assign.Click += (_, _) =>
        {
            if (!service.Assign(diagram.ShiftLayer, bit, id)) return; // The service already explained why.
            shell.ShowToast($"{selected.Label} → {service.Settings.DescribeAssignment(id)}");
            Refresh();
        };
        ContextMenu menu = new() { Items = { assign } };
        if (macro is not null) menu.Items.Add(DeleteMacroItem(macro, menu));
        return menu;
    }

    /// <summary>"Delete macro": the first click arms it ("Click again to delete"), the second deletes. Closing the menu disarms it.</summary>
    private MenuItem DeleteMacroItem(MacroDefinition macro, ContextMenu menu)
    {
        MenuItem delete = new() { Header = "Delete macro", Icon = Ui.Glyph("", 12), StaysOpenOnClick = true };
        bool armed = false;
        delete.Click += (_, _) =>
        {
            if (!armed)
            {
                armed = true;
                delete.Header = "Click again to delete";
                return;
            }
            menu.IsOpen = false;
            service.DeleteMacro(macro);
            shell.ShowToast($"Deleted {macro.Name}");
            Refresh();
        };
        menu.Closed += (_, _) =>
        {
            armed = false;
            delete.Header = "Delete macro";
        };
        return delete;
    }

    private RadioButton OptionRow(string id, bool isCurrent, string name, string subtitle, int bit, MacroDefinition? macro = null)
    {
        Grid content = new();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        FrameworkElement icon = macro is not null ? Ui.MacroMark()
            : Ui.Glyph("\uE8AB", 13, "Accent");
        icon.Margin = new Thickness(0, 0, 12, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Width = 18;
        content.Children.Add(icon);
        StackPanel text = new() { Children = { Ui.Text(name, bold: true), Ui.Text(subtitle, "Body", size: 11.5) } };
        ((TextBlock)text.Children[0]).TextTrimming = TextTrimming.CharacterEllipsis;
        ((TextBlock)text.Children[1]).TextWrapping = TextWrapping.NoWrap;
        ((TextBlock)text.Children[1]).TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(text, 1);
        content.Children.Add(text);
        if (macro is not null)
        {
            Button edit = Ui.Button("", () => shell.OpenMacro(macro), "GhostBtn", "", "Edit macro");
            edit.Padding = new Thickness(8, 6, 8, 6);
            edit.Visibility = isCurrent ? Visibility.Visible : Visibility.Hidden;
            Grid.SetColumn(edit, 2);
            content.Children.Add(edit);
        }
        RadioButton row = new() { Style = Ui.Style("Row"), Content = content, IsChecked = isCurrent, GroupName = "Assignment", Margin = new Thickness(0, 0, 8, 2) };
        MakeDraggable(row, id, isCurrent);
        row.ContextMenu = AssignMenu(id, isCurrent, bit, macro);
        return row;
    }

    private void PickApplication(int bit)
    {
        Microsoft.Win32.OpenFileDialog dialog = new() { Filter = "Applications (*.exe)|*.exe|All files (*.*)|*.*", Title = "Choose an application" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        service.Assign(diagram.ShiftLayer, bit, Assignments.LaunchId(dialog.FileName));
        Refresh();
    }

    /// <summary>
    /// "Record a shortcut": the next key combination pressed (including Win and Alt shortcuts, which the
    /// keyboard hook swallows) becomes this button's assignment. Esc on its own cancels.
    /// </summary>
    private Button ShortcutRecorder(int bit)
    {
        Button button = Ui.Button("Record a shortcut", () => { }, "Btn", "", "Press any key combination to assign it");
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.Margin = new Thickness(0, 0, 0, 4);
        object idle = button.Content;
        KeyboardHook? hook = null;
        HashSet<ushort> swallowed = [];

        void Stop()
        {
            hook?.Dispose();
            hook = null;
            button.Content = idle;
        }

        button.Click += (_, _) =>
        {
            if (hook is not null) { Stop(); return; }
            try { hook = new KeyboardHook(OnKey); }
            catch (System.ComponentModel.Win32Exception) { shell.ShowToast("Shortcut recording isn't available"); return; }
            button.Content = Ui.Row(8, Ui.Glyph("", 12.5, "Warning"), Ui.Text("Press a shortcut… (Esc cancels)", color: "Warning"));
        };
        button.Unloaded += (_, _) => Stop();

        bool OnKey(ushort vk, bool down)
        {
            // Key releases for keys swallowed while recording are swallowed too, so nothing sees a stray release.
            if (!down) return swallowed.Remove(vk);
            if (hook is null || Window.GetWindow(button) is not { IsActive: true })
            {
                Dispatcher.BeginInvoke(Stop);
                return false;
            }
            swallowed.Add(vk);
            if (VirtualKeys.IsModifier(vk)) return true;
            List<string> parts = [];
            if (IsDown(0x11)) parts.Add("Ctrl");
            if (IsDown(0x10)) parts.Add("Shift");
            if (IsDown(0x12)) parts.Add("Alt");
            if (IsDown(0x5B) || IsDown(0x5C)) parts.Add("Win");
            parts.Add(VirtualKeys.NameOf(vk));
            bool cancel = vk == 0x1B && parts.Count == 1;
            Dispatcher.BeginInvoke(() =>
            {
                Stop();
                if (cancel) return;
                service.Assign(diagram.ShiftLayer, bit, Assignments.KeyId(string.Join("+", parts)));
                Refresh();
            });
            return true;
        }
        return button;
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);

    private void CreateMacroFor(int bit)
    {
        MacroDefinition macro = new() { Name = $"{selected.Label} macro" };
        service.Settings.Macros.Add(macro);
        service.Assign(diagram.ShiftLayer, bit, macro.Id);
        shell.OpenMacro(macro);
    }

    internal static string ModeName(MacroMode mode) => mode switch
    {
        MacroMode.Once => "No repeat",
        MacroMode.WhileHeld => "Repeat while holding",
        MacroMode.Toggle => "Toggle",
        _ => "Sequence"
    };

    // ── Click lock ────────────────────────────────────────────────────────────

    /// <summary>
    /// Left or right click on the Default layer that hasn't been unlocked yet. The G-Shift layer isn't locked:
    /// letting go of G-Shift always brings the normal click back.
    /// </summary>
    private bool ClickLocked(MouseControl control) =>
        control.Id is "G1" or "G2" && !diagram.ShiftLayer && !unlockedClicks.Contains(control.Id);

    private Border ClickWarning()
    {
        string click = selected.DefaultAction.ToLowerInvariant();
        string undo = selected.Id == "G1"
            ? "right-click Primary click on the diagram and choose Reset to default"
            : "select Secondary click and pick Secondary Click under System";
        StackPanel content = new();
        content.Children.Add(Ui.Row(10, Ui.Glyph("", 16, "Warning"), Ui.Text($"Reassign {click}?", bold: true, color: "Warning")));
        content.Children.Add(Ui.Text(
            $"{selected.Label} stops sending {click} as soon as you pick something else, everywhere in Windows. "
            + $"To get it back, {undo}.",
            "Body", size: 12, wrap: true).With(new Thickness(26, 4, 0, 10)));
        Button unlock = Ui.Button("Change anyway", () => { unlockedClicks.Add(selected.Id); RenderPanel(); }, "Btn");
        unlock.HorizontalAlignment = HorizontalAlignment.Left;
        content.Children.Add(unlock.With(new Thickness(26, 0, 0, 0)));
        return new Border { Background = Ui.Brush("WarningDim"), CornerRadius = new CornerRadius(10), Padding = new Thickness(14), Child = content };
    }

    // ── Calibration ───────────────────────────────────────────────────────────

    private Border LearningBox()
    {
        StackPanel content = new();
        content.Children.Add(Ui.Row(10, Ui.Glyph("", 16, "Warning"), Ui.Text($"Press {selected.Label} on your mouse", bold: true, color: "Warning")));
        content.Children.Add(Ui.Text(selected.Id is "TiltLeft" or "TiltRight"
            ? "Push the wheel sideways once."
            : "Press and release it once. Other buttons are ignored except the one you press.", "Body", size: 12).With(new Thickness(26, 4, 0, 10)));
        content.Children.Add(Ui.Button("Cancel", () => { service.CancelLearning(); Refresh(); }, "Btn")
            .With(new Thickness(26, 0, 0, 0)));
        ((Button)content.Children[^1]).HorizontalAlignment = HorizontalAlignment.Left;
        return new Border { Background = Ui.Brush("WarningDim"), CornerRadius = new CornerRadius(10), Padding = new Thickness(14), Margin = new Thickness(0, 0, 8, 0), Child = content };
    }

    private void SetCalibrating(bool on)
    {
        if (on && service.State != ConnectionState.Connected)
        {
            shell.ShowToast("Connect the mouse first");
            return;
        }
        calibrating = on;
        if (service.Learning is not null) service.CancelLearning();
        calibrationBanner.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Refresh();
    }
}
