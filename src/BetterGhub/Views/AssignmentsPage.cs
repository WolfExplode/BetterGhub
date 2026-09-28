using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterGhub.Core;
using BetterGhub.Services;

namespace BetterGhub.Views;

internal sealed class AssignmentsPage : UserControl, IPage
{
    private readonly MouseService service;
    private readonly MainWindow shell;
    private readonly MouseDiagram diagram;
    private readonly StackPanel panel = new();
    private readonly Border wizardBanner = new();
    private readonly RadioButton topView = new() { Content = "TOP", GroupName = "View" };
    private readonly RadioButton sideView = new() { Content = "SIDE", GroupName = "View" };
    private MouseControl selected = MouseControls.ById("G1")!;
    private string tab = "Macros";
    private string search = "";
    private Queue<MouseControl>? wizard;
    private int wizardTotal;

    public AssignmentsPage(MouseService service, MainWindow shell)
    {
        this.service = service;
        this.shell = shell;
        diagram = new MouseDiagram(service) { Selected = selected, Margin = new Thickness(0, 0, 0, 10) };
        diagram.ControlClicked += Select;

        Grid layout = new() { Margin = new Thickness(36, 8, 36, 28) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Border left = Ui.Card(Ui.Scroll(panel), new Thickness(22, 20, 14, 20));
        layout.Children.Add(left);

        Grid stage = new() { Margin = new Thickness(24, 0, 0, 0) };
        stage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stage.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        stage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(stage, 1);
        layout.Children.Add(stage);

        DockPanel toolbar = new() { LastChildFill = false };
        Button calibrateAll = Ui.Button("Calibrate buttons", StartWizard, "Btn", "", "Walk through each control and press it once");
        DockPanel.SetDock(calibrateAll, Dock.Right);
        toolbar.Children.Add(calibrateAll);
        toolbar.Children.Add(Ui.Text("Click a control to change what it does. Pressed buttons light up.", "Body").With(new Thickness(2, 8, 0, 0)));
        stage.Children.Add(toolbar);

        Grid.SetRow(diagram, 1);
        stage.Children.Add(diagram);

        wizardBanner.Visibility = Visibility.Collapsed;
        wizardBanner.VerticalAlignment = VerticalAlignment.Top;
        wizardBanner.HorizontalAlignment = HorizontalAlignment.Center;
        wizardBanner.Margin = new Thickness(0, 14, 0, 0);
        Grid.SetRow(wizardBanner, 1);
        stage.Children.Add(wizardBanner);

        topView.Style = Ui.Style("Segment");
        sideView.Style = Ui.Style("Segment");
        topView.IsChecked = true;
        topView.Checked += (_, _) => SetView(MouseView.Top);
        sideView.Checked += (_, _) => SetView(MouseView.Side);
        Border views = new() { Style = Ui.Style("SegmentHost"), HorizontalAlignment = HorizontalAlignment.Center, Child = Ui.Row(2, topView, sideView) };
        Grid.SetRow(views, 2);
        stage.Children.Add(views);

        Content = layout;

        service.ButtonChanged += OnButton;
        service.Calibrated += (_, _) => AdvanceWizard();
        service.StateChanged += () => { if (IsLoaded) RenderPanel(); };
        Refresh();
    }

    public void Refresh()
    {
        diagram.Rebuild();
        RenderPanel();
    }

    private void OnButton(int bit, bool down)
    {
        if (bit >= Input.RawMouseWheel.Up) { if (down) diagram.Pulse(bit); return; }
        diagram.SetPressed(bit, down);
        if (down && service.Learning is null && service.Settings.ControlFor(bit) is null && IsVisible)
            shell.ShowToast($"Unmapped button 0x{bit:x4}. Use Calibrate buttons to name it.");
    }

    private void Select(MouseControl control)
    {
        selected = control;
        diagram.Selected = control;
        if (service.Learning is not null && wizard is null) service.CancelLearning();
        Refresh();
    }

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

        // Layer
        panel.Children.Add(Ui.Text("LAYER", "Overline"));
        RadioButton normal = new() { Content = "Default", Style = Ui.Style("Segment"), GroupName = "Layer", IsChecked = !diagram.ShiftLayer };
        RadioButton shifted = new() { Content = "G-Shift", Style = Ui.Style("Segment"), GroupName = "Layer", IsChecked = diagram.ShiftLayer };
        normal.Checked += (_, _) => { diagram.ShiftLayer = false; Refresh(); };
        shifted.Checked += (_, _) => { diagram.ShiftLayer = true; Refresh(); };
        panel.Children.Add(new Border { Style = Ui.Style("SegmentHost"), Child = Ui.Row(2, normal, shifted), Margin = new Thickness(0, 0, 0, 6) });
        if (diagram.ShiftLayer)
            panel.Children.Add(Ui.Text(
                profile.Assignments.ContainsValue(BuiltinActions.GShift)
                    ? "These actions apply while the G-Shift button is held."
                    : "Assign G-Shift to a button on the Default layer to use this layer.", "Body", size: 12));

        // Selected control
        panel.Children.Add(new Border { Height = 1, Background = Ui.Brush("Line"), Margin = new Thickness(0, 16, 8, 16) });
        panel.Children.Add(Ui.Text(selected.Label, "H2"));
        string detail = bit is int b
            ? (selected.IsWheel ? $"Wheel · default {selected.DefaultAction.ToLowerInvariant()}" : $"HID 0x{b:x4} · default {selected.DefaultAction.ToLowerInvariant()}")
            : "Not calibrated yet";
        panel.Children.Add(Ui.Text(detail, "Body", size: 12, color: bit is null ? "Warning" : "Muted").With(new Thickness(0, 3, 0, 12)));

        if (service.Learning?.Id == selected.Id)
        {
            panel.Children.Add(LearningBox());
        }
        else if (selected.Calibratable)
        {
            StackPanel calibration = Ui.Row(8,
                Ui.Button(bit is null ? "Learn button" : "Re-learn", () => service.StartLearning(selected), bit is null ? "PrimaryBtn" : "Btn", ""));
            if (service.Settings.ControlBits.ContainsKey(selected.Id))
                calibration.Children.Add(Ui.Button("Reset", () => { service.ForgetCalibration(selected); Refresh(); }, "GhostBtn").With(new Thickness(8, 0, 0, 0)));
            panel.Children.Add(calibration);
        }
        if (bit is null) return;

        // Assignment picker
        panel.Children.Add(Ui.Text("ASSIGN", "Overline").With(new Thickness(0, 22, 0, 8)));
        Dictionary<int, string> bindings = diagram.ShiftLayer ? profile.ShiftAssignments : profile.Assignments;
        string current = bindings.GetValueOrDefault(bit.Value) ?? "";
        bool builtinSelected = BuiltinActions.IsBuiltin(current);
        if (current.Length > 0) tab = builtinSelected ? "Functions" : "Macros";

        StackPanel options = new();
        RadioButton macrosTab = new() { Content = "Macros", Style = Ui.Style("Segment"), GroupName = "AssignTab", IsChecked = tab == "Macros" };
        RadioButton functionsTab = new() { Content = "Mouse functions", Style = Ui.Style("Segment"), GroupName = "AssignTab", IsChecked = tab == "Functions" };
        macrosTab.Checked += (_, _) => { tab = "Macros"; RenderOptions(options, bit.Value, current); };
        functionsTab.Checked += (_, _) => { tab = "Functions"; RenderOptions(options, bit.Value, current); };
        panel.Children.Add(new Border { Style = Ui.Style("SegmentHost"), Child = Ui.Row(2, macrosTab, functionsTab), Margin = new Thickness(0, 0, 0, 10) });

        // Default row is always first so "undo" is obvious.
        panel.Children.Add(OptionRow(null, current.Length == 0,
            diagram.ShiftLayer ? "Same as Default layer" : "Default action",
            diagram.ShiftLayer ? "No G-Shift override" : selected.DefaultAction, bit.Value));

        panel.Children.Add(options);
        RenderOptions(options, bit.Value, current);

        if (selected.CanBlockDefault && !diagram.ShiftLayer)
        {
            panel.Children.Add(new Border { Height = 1, Background = Ui.Brush("Line"), Margin = new Thickness(0, 18, 8, 16) });
            CheckBox block = new()
            {
                Style = Ui.Style("Switch"), IsChecked = service.Settings.SuppressStandardActions,
                Content = new StackPanel
                {
                    Children =
                    {
                        Ui.Text("Block Windows' default action", bold: true),
                        Ui.Text("When a standard button (right, middle, back, forward, wheel) has an assignment, Windows won't also see the original click. Applies to every mouse.", "Body", size: 12).With(new Thickness(0, 3, 0, 0))
                    }
                }
            };
            block.Click += (_, _) => { service.Settings.SuppressStandardActions = block.IsChecked == true; service.Save(); };
            panel.Children.Add(block);
        }
    }

    private void RenderOptions(StackPanel options, int bit, string current)
    {
        options.Children.Clear();
        if (tab == "Macros")
        {
            StackPanel list = new();
            if (service.Settings.Macros.Count > 5)
            {
                TextBox filter = new() { Text = search, Margin = new Thickness(0, 0, 8, 8) };
                filter.TextChanged += (_, _) => { search = filter.Text; RenderMacroRows(list, bit, current); };
                options.Children.Add(Ui.Placeholder(filter, "Search macros"));
            }
            options.Children.Add(list);
            RenderMacroRows(list, bit, current);
            options.Children.Add(Ui.Button("New macro", () => CreateMacroFor(bit), "GhostBtn", "").With(new Thickness(0, 6, 0, 0)));
            ((Button)options.Children[^1]).HorizontalAlignment = HorizontalAlignment.Left;
        }
        else
        {
            foreach ((string id, string name, string description) in BuiltinActions.All)
            {
                if (id == BuiltinActions.GShift && diagram.ShiftLayer) continue;
                if (id == BuiltinActions.DpiShift && selected.IsWheel) continue;
                options.Children.Add(OptionRow(id, current == id, name, description, bit));
            }
        }
    }

    private void RenderMacroRows(StackPanel list, int bit, string current)
    {
        list.Children.Clear();
        IEnumerable<MacroDefinition> macros = service.Settings.Macros
            .Where(m => search.Length == 0 || m.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
        foreach (MacroDefinition macro in macros)
            list.Children.Add(OptionRow(macro.Id, current == macro.Id, macro.Name, ModeName(macro.Mode) + $" · {macro.Steps.Count} step{(macro.Steps.Count == 1 ? "" : "s")}", bit, macro));
        if (service.Settings.Macros.Count == 0)
            list.Children.Add(Ui.Text("No macros yet. Create one and it will be assigned to this button.", "Body", size: 12).With(new Thickness(2, 4, 8, 4)));
    }

    private RadioButton OptionRow(string? id, bool isCurrent, string name, string subtitle, int bit, MacroDefinition? macro = null)
    {
        Grid content = new();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        FrameworkElement icon = macro is not null ? Ui.MacroMark()
            : id is null ? Ui.Glyph("\uE7A7", 14, "Muted") : Ui.Glyph("", 13, "Accent");
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
        row.Click += (_, _) =>
        {
            service.Assign(diagram.ShiftLayer, bit, id);
            Refresh();
        };
        return row;
    }

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

    // ── Calibration ───────────────────────────────────────────────────────────

    private Border LearningBox()
    {
        StackPanel content = new();
        content.Children.Add(Ui.Row(10, Ui.Glyph("", 16, "Warning"), Ui.Text($"Press {selected.Label} on your mouse", bold: true, color: "Warning")));
        content.Children.Add(Ui.Text(selected.Id is "TiltLeft" or "TiltRight"
            ? "Push the wheel sideways once."
            : "Press and release it once. Other buttons are ignored except the one you press.", "Body", size: 12).With(new Thickness(26, 4, 0, 10)));
        content.Children.Add(Ui.Button("Cancel", () => { wizard = null; wizardBanner.Visibility = Visibility.Collapsed; service.CancelLearning(); Refresh(); }, "Btn")
            .With(new Thickness(26, 0, 0, 0)));
        ((Button)content.Children[^1]).HorizontalAlignment = HorizontalAlignment.Left;
        return new Border { Background = Ui.Brush("WarningDim"), CornerRadius = new CornerRadius(10), Padding = new Thickness(14), Margin = new Thickness(0, 0, 8, 0), Child = content };
    }

    private void StartWizard()
    {
        if (service.State != ConnectionState.Connected)
        {
            shell.ShowToast("Connect the mouse first");
            return;
        }
        wizard = new Queue<MouseControl>(MouseControls.CalibrationOrder.Where(c => c.Id != "G1"));
        wizardTotal = wizard.Count;
        AdvanceWizard();
    }

    private void AdvanceWizard()
    {
        if (wizard is null) { Refresh(); return; }
        if (wizard.Count == 0)
        {
            wizard = null;
            wizardBanner.Visibility = Visibility.Collapsed;
            shell.ShowToast("All buttons calibrated");
            Refresh();
            return;
        }
        MouseControl next = wizard.Dequeue();
        selected = next;
        diagram.Selected = next;
        ShowView(next.View);
        service.StartLearning(next);
        int step = wizardTotal - wizard.Count;

        Button skip = Ui.Button("Skip", AdvanceWizard, "Btn");
        Button stop = Ui.Button("Stop", () => { wizard = null; wizardBanner.Visibility = Visibility.Collapsed; service.CancelLearning(); Refresh(); }, "GhostBtn");
        wizardBanner.Child = Ui.Row(14,
            Ui.Text($"{step} / {wizardTotal}", size: 12, color: "Muted", bold: true),
            Ui.Text($"Press  {next.Label}", size: 15, bold: true),
            skip, stop);
        foreach (FrameworkElement child in ((StackPanel)wizardBanner.Child).Children) child.VerticalAlignment = VerticalAlignment.Center;
        wizardBanner.Background = Ui.Brush("Surface2");
        wizardBanner.BorderBrush = Ui.Brush("Warning");
        wizardBanner.BorderThickness = new Thickness(1);
        wizardBanner.CornerRadius = new CornerRadius(12);
        wizardBanner.Padding = new Thickness(18, 10, 12, 10);
        wizardBanner.Visibility = Visibility.Visible;
        Refresh();
    }
}
