using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using System.Windows.Threading;
using BetterGhub.Core;
using BetterGhub.Input;
using BetterGhub.Services;

namespace BetterGhub.Views;

internal sealed class MacrosPage : UserControl, IPage
{
    private readonly MouseService service;
    private readonly MainWindow shell;
    private readonly StackPanel list = new();
    private readonly ContentControl editorHost = new() { Focusable = false };
    private readonly TextBox search = new() { Margin = new Thickness(0, 0, 8, 12) };
    private MacroDefinition? macro;
    private int selectedStep = -1;
    private WrapPanel? timeline;
    private StackPanel? inspector;
    private Button? testButton;
    private DispatcherTimer? countdown;

    public MacrosPage(MouseService service, MainWindow shell)
    {
        this.service = service;
        this.shell = shell;

        Grid layout = new() { Margin = new Thickness(36, 8, 36, 28) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        DockPanel side = new();
        DockPanel head = new() { Margin = new Thickness(0, 0, 8, 14) };
        Button add = Ui.Button("New", CreateMacro, "PrimaryBtn", "");
        add.Padding = new Thickness(12, 6, 12, 6);
        DockPanel.SetDock(add, Dock.Right);
        head.Children.Add(add);
        head.Children.Add(Ui.Text("Your macros", "H2"));
        DockPanel.SetDock(head, Dock.Top);
        side.Children.Add(head);
        search.TextChanged += (_, _) => RenderList();
        Grid searchBox = Ui.Placeholder(search, "Search macros");
        DockPanel.SetDock(searchBox, Dock.Top);
        side.Children.Add(searchBox);
        side.Children.Add(Ui.Scroll(list));
        layout.Children.Add(Ui.Card(side, new Thickness(18, 18, 10, 18)));

        Grid.SetColumn(editorHost, 1);
        editorHost.Margin = new Thickness(24, 0, 0, 0);
        layout.Children.Add(editorHost);
        Content = layout;

        macro = service.Settings.Macros.FirstOrDefault();
        Refresh();
    }

    public void Refresh()
    {
        if (macro is not null && !service.Settings.Macros.Contains(macro)) macro = service.Settings.Macros.FirstOrDefault();
        RenderList();
        RenderEditor();
    }

    public void Select(MacroDefinition target)
    {
        macro = target;
        selectedStep = -1;
        Refresh();
    }

    internal void SelectStep(int index)
    {
        selectedStep = index;
        RenderTimeline();
    }

    private void CreateMacro()
    {
        MacroDefinition created = new() { Name = UniqueName("New macro") };
        service.Settings.Macros.Add(created);
        service.Save();
        Select(created);
        FocusTitle();
    }

    private string UniqueName(string name)
    {
        string candidate = name;
        for (int i = 2; service.Settings.Macros.Any(m => m.Name == candidate); i++) candidate = $"{name} {i}";
        return candidate;
    }

    // ── List ──────────────────────────────────────────────────────────────────

    private void RenderList()
    {
        list.Children.Clear();
        string filter = search.Text.Trim();
        foreach (MacroDefinition item in service.Settings.Macros.Where(m => filter.Length == 0 || m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            List<string> uses = UsesOf(item);
            StackPanel text = new();
            text.Children.Add(new TextBlock { Text = item.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(Ui.Text(uses.Count > 0 ? string.Join(", ", uses) : "Not assigned", "Body", size: 11.5));
            ((TextBlock)text.Children[1]).TextWrapping = TextWrapping.NoWrap;
            ((TextBlock)text.Children[1]).TextTrimming = TextTrimming.CharacterEllipsis;
            DockPanel row = new();
            FrameworkElement icon = ModeIcon(item.Mode, 16);
            icon.Margin = new Thickness(0, 0, 12, 0);
            row.Children.Add(icon);
            row.Children.Add(text);
            RadioButton button = new() { Style = Ui.Style("Row"), Content = row, IsChecked = item == macro, GroupName = "MacroList", Margin = new Thickness(0, 0, 8, 2) };
            button.Click += (_, _) => Select(item);
            list.Children.Add(button);
        }
        if (service.Settings.Macros.Count == 0)
            list.Children.Add(Ui.Text("Nothing here yet.", "Body").With(new Thickness(4)));
    }

    private List<string> UsesOf(MacroDefinition item)
    {
        List<string> uses = [];
        foreach (MouseProfile profile in service.Settings.Profiles)
        {
            foreach ((int bit, string id) in profile.Assignments.Where(x => x.Value == item.Id))
                uses.Add(ControlName(bit) + (service.Settings.Profiles.Count > 1 ? $" · {profile.Name}" : ""));
            foreach ((int bit, string id) in profile.ShiftAssignments.Where(x => x.Value == item.Id))
                uses.Add("G-Shift+" + ControlName(bit) + (service.Settings.Profiles.Count > 1 ? $" · {profile.Name}" : ""));
        }
        return uses;
    }

    private string ControlName(int bit) => service.Settings.ControlFor(bit)?.Label ?? $"0x{bit:x4}";

    // ── Editor ────────────────────────────────────────────────────────────────

    private void RenderEditor()
    {
        StopCountdown();
        if (macro is null)
        {
            StackPanel empty = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            empty.Children.Add(Ui.Glyph("", 40, "Faint").With(new Thickness(0, 0, 0, 14)));
            ((FrameworkElement)empty.Children[0]).HorizontalAlignment = HorizontalAlignment.Center;
            empty.Children.Add(Ui.Text("Create your first macro", "H2").With(new Thickness(0, 0, 0, 6)));
            ((FrameworkElement)empty.Children[1]).HorizontalAlignment = HorizontalAlignment.Center;
            empty.Children.Add(Ui.Text("Macros type text, press shortcuts, click, change volume or launch apps. Assign them to any button.", "Body"));
            ((TextBlock)empty.Children[2]).TextAlignment = TextAlignment.Center;
            ((TextBlock)empty.Children[2]).MaxWidth = 380;
            Button create = Ui.Button("New macro", CreateMacro, "PrimaryBtn", "").With(new Thickness(0, 18, 0, 0));
            create.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Children.Add(create);
            editorHost.Content = Ui.Card(empty);
            return;
        }
        MacroDefinition current = macro;
        StackPanel editor = new();

        // Title row
        DockPanel titleRow = new() { Margin = new Thickness(-6, 0, 0, 4) };
        Button delete = DeleteButton(current);
        DockPanel.SetDock(delete, Dock.Right);
        testButton = Ui.Button("Test in 3 s", () => StartTest(current), "Btn", "", "Plays the macro once after a 3 second countdown, so you can click into another window first");
        DockPanel.SetDock(testButton, Dock.Right);
        testButton.Margin = new Thickness(8, 0, 8, 0);
        titleRow.Children.Add(delete);
        titleRow.Children.Add(testButton);
        TextBox title = new() { Text = current.Name, Style = Ui.Style("TitleBox"), Tag = "Title" };
        title.TextChanged += (_, _) =>
        {
            current.Name = title.Text.Trim().Length == 0 ? "Untitled macro" : title.Text.Trim();
            service.Save();
            RenderList();
        };
        titleRow.Children.Add(title);
        editor.Children.Add(titleRow);
        List<string> uses = UsesOf(current);
        StackPanel usedBy = Ui.Row(8, Ui.Text(uses.Count > 0 ? "Assigned to " + string.Join(", ", uses) : "Not assigned to a button yet", "Body", size: 12));
        Button assign = Ui.Button(uses.Count > 0 ? "Change" : "Assign to a button", () => shell.Navigate("Assignments"), "GhostBtn");
        assign.Padding = new Thickness(8, 2, 8, 2);
        assign.Foreground = Ui.Brush("Accent");
        usedBy.Children.Add(assign);
        ((FrameworkElement)usedBy.Children[0]).VerticalAlignment = VerticalAlignment.Center;
        editor.Children.Add(usedBy);

        // Mode + options side by side
        StackPanel settingsRow = new() { Margin = new Thickness(0, 22, 0, 0) };
        StackPanel modes = new();
        modes.Children.Add(Ui.Text("MACRO TYPE", "Overline"));
        WrapPanel tiles = new();
        TextBlock modeHelp = Ui.Text(ModeHelp(current.Mode), "Body", size: 12);
        modeHelp.MaxWidth = 440;
        modeHelp.HorizontalAlignment = HorizontalAlignment.Left;
        foreach (MacroMode mode in Enum.GetValues<MacroMode>())
        {
            StackPanel face = new() { Width = 92 };
            FrameworkElement icon = ModeIcon(mode, 26);
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            face.Children.Add(icon);
            face.Children.Add(new TextBlock { Text = AssignmentsPage.ModeName(mode), FontSize = 11.5, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
            RadioButton tile = new() { Style = Ui.Style("Tile"), Content = face, IsChecked = current.Mode == mode, GroupName = "Mode", Padding = new Thickness(6, 14, 6, 10), Margin = new Thickness(0, 0, 10, 10) };
            tile.Click += (_, _) => { current.Mode = mode; modeHelp.Text = ModeHelp(mode); service.Save(); RenderList(); };
            tiles.Children.Add(tile);
        }
        modes.Children.Add(tiles);
        modes.Children.Add(modeHelp.With(new Thickness(0, 10, 0, 0)));
        settingsRow.Children.Add(modes);

        StackPanel options = new() { Margin = new Thickness(0, 22, 0, 0), MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left };
        options.Children.Add(Ui.Text("TIMING", "Overline"));
        CheckBox standard = new() { Style = Ui.Style("Switch"), IsChecked = current.StandardDelayMs.HasValue };
        TextBox delayBox = new() { Text = (current.StandardDelayMs ?? 50).ToString(), Width = 64, Padding = new Thickness(16, 8, 16, 8), IsEnabled = current.StandardDelayMs.HasValue };
        StackPanel standardLabel = new()
        {
            Children =
            {
                Ui.Row(8, Ui.Text("Use a standard delay", bold: true), delayBox, Ui.Text("ms", color: "Muted")),
                Ui.Text(current.StandardDelayMs.HasValue ? "Recorded delays are ignored; every action is separated by this delay." : "Currently using the delays shown in the timeline.", "Body", size: 12).With(new Thickness(0, 4, 0, 0))
            }
        };
        foreach (FrameworkElement child in ((StackPanel)standardLabel.Children[0]).Children) child.VerticalAlignment = VerticalAlignment.Center;
        standard.Content = standardLabel;
        standard.Click += (_, _) =>
        {
            current.StandardDelayMs = standard.IsChecked == true ? ParseDelay(delayBox.Text, 50) : null;
            service.Save();
            RenderEditor();
        };
        delayBox.LostFocus += (_, _) =>
        {
            if (current.StandardDelayMs is null) return;
            current.StandardDelayMs = ParseDelay(delayBox.Text, current.StandardDelayMs.Value);
            delayBox.Text = current.StandardDelayMs.ToString();
            service.Save();
        };
        delayBox.PreviewMouseLeftButtonDown += (_, e) => e.Handled = !delayBox.IsEnabled;
        options.Children.Add(standard);
        settingsRow.Children.Add(options);
        editor.Children.Add(settingsRow);

        // Timeline
        editor.Children.Add(new Border { Height = 1, Background = Ui.Brush("Line"), Margin = new Thickness(0, 24, 0, 18) });
        DockPanel actionsHead = new() { Margin = new Thickness(0, 0, 0, 10) };
        Button record = Ui.Button("Record keystrokes", () => Record(current), "Btn", "");
        ((StackPanel)record.Content).Children[0].SetValue(TextBlock.ForegroundProperty, Ui.Brush("Danger"));
        DockPanel.SetDock(record, Dock.Right);
        actionsHead.Children.Add(record);
        actionsHead.Children.Add(Ui.Text("ACTIONS", "Overline").With(new Thickness(0, 10, 0, 0)));
        editor.Children.Add(actionsHead);

        Border lane = new() { Background = Ui.Brush("Bg"), CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12, 14, 6), MinHeight = 110 };
        timeline = new WrapPanel();
        lane.Child = timeline;
        editor.Children.Add(lane);
        editor.Children.Add(Ui.Text("Click an action to edit it. New actions are added after the selected one.", "Body", size: 12).With(new Thickness(2, 8, 0, 0)));

        inspector = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        editor.Children.Add(inspector);

        editorHost.Content = Ui.Card(Ui.Scroll(editor), new Thickness(28, 22, 20, 22));
        RenderTimeline();
    }

    private static int ParseDelay(string text, int fallback) => int.TryParse(text, out int value) ? Math.Clamp(value, 0, 60000) : fallback;

    private void FocusTitle() => Dispatcher.BeginInvoke(() =>
    {
        if (FindTitle(editorHost) is TextBox box) { box.Focus(); box.SelectAll(); }
    }, DispatcherPriority.Input);

    private static TextBox? FindTitle(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBox { Tag: "Title" } box) return box;
            if (FindTitle(child) is TextBox found) return found;
        }
        return null;
    }

    private Button DeleteButton(MacroDefinition current)
    {
        Button delete = Ui.DeleteButton("", () =>
        {
            foreach (MouseProfile profile in service.Settings.Profiles)
            {
                foreach (int bit in profile.Assignments.Where(x => x.Value == current.Id).Select(x => x.Key).ToArray()) profile.Assignments.Remove(bit);
                foreach (int bit in profile.ShiftAssignments.Where(x => x.Value == current.Id).Select(x => x.Key).ToArray()) profile.ShiftAssignments.Remove(bit);
            }
            int index = service.Settings.Macros.IndexOf(current);
            service.Settings.Macros.Remove(current);
            service.Save();
            macro = service.Settings.Macros.Count == 0 ? null : service.Settings.Macros[Math.Min(index, service.Settings.Macros.Count - 1)];
            selectedStep = -1;
            shell.ShowToast($"Deleted {current.Name}");
            Refresh();
        }, "Delete macro");
        delete.Padding = new Thickness(10, 8, 10, 8);
        return delete;
    }

    // ── Timeline ──────────────────────────────────────────────────────────────

    private void RenderTimeline()
    {
        if (macro is null || timeline is null) return;
        timeline.Children.Clear();
        bool standard = macro.StandardDelayMs.HasValue;
        for (int i = 0; i < macro.Steps.Count; i++)
        {
            int index = i;
            MacroStep step = macro.Steps[i];
            timeline.Children.Add(StepChips.Build(step, () => { selectedStep = index; RenderTimeline(); inspector?.BringIntoView(); }, index == selectedStep,
                faded: standard && step.Kind == ActionKind.Delay));
        }
        timeline.Children.Add(AddTile());
        RenderInspector();
    }

    private FrameworkElement AddTile()
    {
        Button plus = new()
        {
            Style = Ui.Style("Btn"), Width = 42, Height = 42, Padding = new Thickness(0),
            Content = Ui.Glyph("", 15), ToolTip = "Add an action", Margin = new Thickness(3, 12, 3, 20)
        };
        Popup menu = new() { PlacementTarget = plus, Placement = PlacementMode.Right, HorizontalOffset = 8, StaysOpen = false, AllowsTransparency = true };
        StackPanel items = new();
        void Item(StepChips.Family family, string text, Action action)
        {
            (Color background, Color foreground, string glyph) = StepChips.Palette(family);
            Button item = new()
            {
                Style = Ui.Style("Btn"), Background = new SolidColorBrush(background), Foreground = new SolidColorBrush(foreground),
                HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 5), Padding = new Thickness(12, 9, 16, 9),
                Content = Ui.Row(10, Ui.Glyph(glyph, 13), new TextBlock { Text = text, FontWeight = FontWeights.SemiBold })
            };
            item.Click += (_, _) => { menu.IsOpen = false; action(); };
            items.Children.Add(item);
        }
        Button recordItem = new()
        {
            Style = Ui.Style("Btn"), Background = new SolidColorBrush(Color.FromRgb(0x6A, 0x14, 0x2A)), Foreground = Brushes.White,
            HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 5), Padding = new Thickness(12, 9, 16, 9),
            Content = Ui.Row(10, new Ellipse { Width = 12, Height = 12, Fill = Ui.Brush("Danger") }, new TextBlock { Text = "Record keystrokes", FontWeight = FontWeights.SemiBold })
        };
        recordItem.Click += (_, _) => { menu.IsOpen = false; Record(macro!); };
        items.Children.Add(recordItem);
        Item(StepChips.Family.Key, "Keystroke or shortcut", () => Insert(new MacroStep { Kind = ActionKind.Key, Value = "" }));
        Item(StepChips.Family.Text, "Text", () => Insert(new MacroStep { Kind = ActionKind.Text, Value = "" }));
        Item(StepChips.Family.Mouse, "Mouse action", () => Insert(new MacroStep { Kind = ActionKind.LeftClick }));
        Item(StepChips.Family.Media, "Volume & media", () => Insert(new MacroStep { Kind = ActionKind.VolumeUp }));
        Item(StepChips.Family.Launch, "Launch application", () => { if (PickApp() is string path) Insert(new MacroStep { Kind = ActionKind.Launch, Value = path }); });
        Item(StepChips.Family.Delay, "Delay", () => Insert(new MacroStep { Kind = ActionKind.Delay, DelayMs = 50 }));
        menu.Child = new Border
        {
            Background = Ui.Brush("Surface"), BorderBrush = Ui.Brush("LineStrong"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(8, 8, 8, 3), Child = items
        };
        plus.Click += (_, _) => menu.IsOpen = true;
        Grid wrapper = new();
        wrapper.Children.Add(plus);
        wrapper.Children.Add(menu);
        return wrapper;
    }

    private void Insert(MacroStep step) => Insert([step]);

    private void Insert(List<MacroStep> steps)
    {
        if (macro is null || steps.Count == 0) return;
        int at = selectedStep >= 0 && selectedStep < macro.Steps.Count ? selectedStep + 1 : macro.Steps.Count;
        macro.Steps.InsertRange(at, steps);
        selectedStep = at + steps.Count - 1;
        service.Save();
        RenderTimeline();
        RenderList();
        if (steps.Count == 1) FocusInspector();
    }

    private void FocusInspector() => Dispatcher.BeginInvoke(() =>
    {
        if (inspector is not null && FindFirst<TextBox>(inspector) is TextBox box) { box.Focus(); box.SelectAll(); }
    }, DispatcherPriority.Input);

    private static T? FindFirst<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindFirst<T>(child) is T found) return found;
        }
        return null;
    }

    private string? PickApp()
    {
        Microsoft.Win32.OpenFileDialog dialog = new() { Filter = "Applications (*.exe)|*.exe|All files (*.*)|*.*", Title = "Choose an application" };
        return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null;
    }

    private void Record(MacroDefinition target)
    {
        RecordKeysWindow recorder = new() { Owner = Window.GetWindow(this) };
        if (recorder.ShowDialog() != true) return;
        macro = target;
        Insert(recorder.Result());
    }

    // ── Inspector ─────────────────────────────────────────────────────────────

    private void RenderInspector()
    {
        if (inspector is null || macro is null) return;
        inspector.Children.Clear();
        if (selectedStep < 0 || selectedStep >= macro.Steps.Count) return;
        MacroStep step = macro.Steps[selectedStep];
        StepChips.Family family = StepChips.FamilyOf(step.Kind);

        Border card = new() { Background = Ui.Brush("Surface"), CornerRadius = new CornerRadius(12), Padding = new Thickness(18, 16, 18, 16) };
        StackPanel body = new();
        card.Child = body;

        DockPanel head = new() { Margin = new Thickness(0, 0, 0, 12) };
        StackPanel tools = Ui.Row(4,
            IconButton("", "Move earlier", () => Move(-1), selectedStep > 0),
            IconButton("", "Move later", () => Move(1), selectedStep < macro.Steps.Count - 1),
            IconButton("", "Duplicate", Duplicate, true),
            StepDeleteButton());
        DockPanel.SetDock(tools, Dock.Right);
        head.Children.Add(tools);
        head.Children.Add(Ui.Text($"ACTION {selectedStep + 1} OF {macro.Steps.Count} · {KindName(step.Kind).ToUpperInvariant()}", "Overline").With(new Thickness(0, 8, 0, 0)));
        body.Children.Add(head);

        switch (family)
        {
            case StepChips.Family.Key:
                body.Children.Add(KeyEditor(step));
                break;
            case StepChips.Family.Text:
                TextBox text = new() { Text = step.Value, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64, VerticalContentAlignment = VerticalAlignment.Top };
                text.TextChanged += (_, _) => { step.Value = text.Text; service.Save(); UpdateSelectedChip(); };
                body.Children.Add(text);
                body.Children.Add(Ui.Text("Typed as Unicode characters, so it works with any keyboard layout. New lines press Enter.", "Body", size: 12).With(new Thickness(0, 6, 0, 0)));
                break;
            case StepChips.Family.Mouse:
                body.Children.Add(Choices(step, [(ActionKind.LeftClick, "Left click", ""), (ActionKind.RightClick, "Right click", ""), (ActionKind.MiddleClick, "Middle click", ""), (ActionKind.Wheel, "Scroll up", "1"), (ActionKind.Wheel, "Scroll down", "-1")]));
                break;
            case StepChips.Family.Media:
                body.Children.Add(Choices(step, [(ActionKind.VolumeUp, "Volume up", ""), (ActionKind.VolumeDown, "Volume down", ""), (ActionKind.Mute, "Mute", ""), (ActionKind.PlayPause, "Play / pause", ""), (ActionKind.NextTrack, "Next track", ""), (ActionKind.PreviousTrack, "Previous track", "")]));
                break;
            case StepChips.Family.Launch:
                DockPanel launch = new();
                Button browse = Ui.Button("Browse…", () => { if (PickApp() is string path) { step.Value = path; service.Save(); RenderTimeline(); } });
                DockPanel.SetDock(browse, Dock.Right);
                browse.Margin = new Thickness(8, 0, 0, 0);
                launch.Children.Add(browse);
                TextBox pathBox = new() { Text = step.Value };
                pathBox.LostFocus += (_, _) => { step.Value = pathBox.Text.Trim('"', ' '); service.Save(); UpdateSelectedChip(); };
                launch.Children.Add(pathBox);
                body.Children.Add(launch);
                break;
            case StepChips.Family.Delay:
                TextBox ms = new() { Text = step.DelayMs.ToString(), Width = 100 };
                ms.TextChanged += (_, _) => { if (int.TryParse(ms.Text, out int value)) { step.DelayMs = Math.Clamp(value, 0, 60000); service.Save(); UpdateSelectedChip(); } };
                StackPanel presets = Ui.Row(6, ms, Ui.Text("milliseconds", color: "Muted"));
                ((FrameworkElement)presets.Children[1]).VerticalAlignment = VerticalAlignment.Center;
                foreach (int preset in new[] { 10, 50, 100, 250, 1000 })
                {
                    Button quick = Ui.Button(StepChips.FormatDelay(preset) is var (v, u) ? $"{v} {u}" : "", () => { ms.Text = preset.ToString(); }, "GhostBtn");
                    quick.Padding = new Thickness(9, 5, 9, 5);
                    presets.Children.Add(quick.With(new Thickness(preset == 10 ? 14 : 2, 0, 0, 0)));
                }
                body.Children.Add(presets);
                if (macro.StandardDelayMs.HasValue)
                    body.Children.Add(Ui.Text("Ignored while this macro uses a standard delay.", "Body", size: 12, color: "Warning").With(new Thickness(0, 8, 0, 0)));
                break;
        }
        inspector.Children.Add(card);
    }

    private static string KindName(ActionKind kind) => kind switch
    {
        ActionKind.Key => "Keystroke",
        ActionKind.KeyDown => "Key down",
        ActionKind.KeyUp => "Key up",
        ActionKind.Text => "Text",
        ActionKind.Delay => "Delay",
        ActionKind.Launch => "Launch application",
        ActionKind.LeftClick or ActionKind.RightClick or ActionKind.MiddleClick or ActionKind.Wheel => "Mouse",
        _ => "Media"
    };

    private FrameworkElement KeyEditor(MacroStep step)
    {
        StackPanel panel = new();
        StackPanel edges = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        foreach ((ActionKind kind, string name) in new[] { (ActionKind.Key, "Press & release"), (ActionKind.KeyDown, "Key down"), (ActionKind.KeyUp, "Key up") })
        {
            RadioButton option = new() { Content = name, Style = Ui.Style("Segment"), GroupName = "KeyEdge", IsChecked = step.Kind == kind };
            option.Click += (_, _) =>
            {
                step.Kind = kind;
                if (kind != ActionKind.Key && step.Value.Contains('+')) step.Value = step.Value.Split('+')[^1];
                service.Save();
                RenderTimeline();
            };
            edges.Children.Add(option);
        }
        panel.Children.Add(new Border { Style = Ui.Style("SegmentHost"), Child = edges, Margin = new Thickness(0, 0, 0, 12) });

        TextBox capture = new() { Text = step.Value, IsReadOnly = true, Width = 260, HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand, FontSize = 15, FontWeight = FontWeights.SemiBold };
        TextBlock hint = Ui.Text(step.Kind == ActionKind.Key ? "Click the box and press the key or shortcut, e.g. Ctrl+Alt+T." : "Click the box and press a single key.", "Body", size: 12);
        capture.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            string? value = step.Kind == ActionKind.Key ? KeyCapture.Combo(e) : VirtualKeys.NameOf(KeyCapture.VirtualKey(e));
            if (value is null) return;
            step.Value = value;
            capture.Text = value;
            service.Save();
            UpdateSelectedChip();
        };
        capture.GotKeyboardFocus += (_, _) => hint.Text = "Listening… press the key now.";
        capture.LostKeyboardFocus += (_, _) => hint.Text = step.Kind == ActionKind.Key ? "Click the box and press the key or shortcut, e.g. Ctrl+Alt+T." : "Click the box and press a single key.";
        panel.Children.Add(capture);
        panel.Children.Add(hint.With(new Thickness(0, 6, 0, 0)));
        return panel;
    }

    private FrameworkElement Choices(MacroStep step, (ActionKind Kind, string Name, string Value)[] choices)
    {
        WrapPanel panel = new();
        foreach ((ActionKind kind, string name, string value) in choices)
        {
            bool isCurrent = step.Kind == kind && (kind != ActionKind.Wheel || (value.StartsWith('-') == step.Value.StartsWith('-')));
            RadioButton option = new() { Content = name, Style = Ui.Style("Tile"), GroupName = "Choice", IsChecked = isCurrent, Padding = new Thickness(14, 9, 14, 9), Margin = new Thickness(0, 0, 8, 8) };
            option.Click += (_, _) => { step.Kind = kind; step.Value = value; service.Save(); RenderTimeline(); };
            panel.Children.Add(option);
        }
        return panel;
    }

    private Button IconButton(string glyph, string tip, Action click, bool enabled)
    {
        Button button = Ui.Button("", click, "GhostBtn", glyph, tip);
        button.Padding = new Thickness(9, 7, 9, 7);
        button.IsEnabled = enabled;
        return button;
    }

    private Button StepDeleteButton()
    {
        Button button = Ui.DeleteButton("", Remove, "Remove");
        button.Padding = new Thickness(9, 7, 9, 7);
        return button;
    }

    private void UpdateSelectedChip()
    {
        if (macro is null || timeline is null || selectedStep < 0 || selectedStep >= timeline.Children.Count - 1) return;
        int index = selectedStep;
        MacroStep step = macro.Steps[index];
        // UIElementCollection's indexer setter throws if the slot is occupied, so swap via remove + insert.
        timeline.Children.RemoveAt(index);
        timeline.Children.Insert(index, StepChips.Build(step, () => { selectedStep = index; RenderTimeline(); inspector?.BringIntoView(); }, true,
            faded: macro.StandardDelayMs.HasValue && step.Kind == ActionKind.Delay));
    }

    private void Move(int delta)
    {
        if (macro is null) return;
        int target = selectedStep + delta;
        if (target < 0 || target >= macro.Steps.Count) return;
        (macro.Steps[selectedStep], macro.Steps[target]) = (macro.Steps[target], macro.Steps[selectedStep]);
        selectedStep = target;
        service.Save();
        RenderTimeline();
    }

    private void Duplicate()
    {
        if (macro is null) return;
        macro.Steps.Insert(selectedStep + 1, macro.Steps[selectedStep].Clone());
        selectedStep++;
        service.Save();
        RenderTimeline();
    }

    private void Remove()
    {
        if (macro is null) return;
        macro.Steps.RemoveAt(selectedStep);
        selectedStep = Math.Min(selectedStep, macro.Steps.Count - 1);
        service.Save();
        RenderTimeline();
        RenderList();
    }

    // ── Test playback ─────────────────────────────────────────────────────────

    private void StartTest(MacroDefinition target)
    {
        if (countdown is not null || service.IsTestRunning)
        {
            StopCountdown();
            service.StopTest();
            return;
        }
        if (target.Steps.Count == 0) { shell.ShowToast("Add an action first"); return; }
        int remaining = 3;
        countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        SetTestLabel($"Starting in {remaining}… (click to cancel)");
        countdown.Tick += (_, _) =>
        {
            remaining--;
            if (remaining > 0) { SetTestLabel($"Starting in {remaining}… (click to cancel)"); return; }
            StopCountdown();
            service.TestMacro(target);
            shell.ShowToast($"Played {target.Name}");
        };
        countdown.Start();
    }

    private void SetTestLabel(string text)
    {
        if (testButton is null) return;
        testButton.Content = Ui.Row(8, Ui.Glyph("", 12.5), new TextBlock { Text = text });
    }

    private void StopCountdown()
    {
        countdown?.Stop();
        countdown = null;
        if (testButton is not null) testButton.Content = Ui.Row(8, Ui.Glyph("", 12.5), new TextBlock { Text = "Test in 3 s" });
    }

    // ── Icons ─────────────────────────────────────────────────────────────────

    private static string ModeHelp(MacroMode mode) => mode switch
    {
        MacroMode.Once => "Plays once each time you press the button.",
        MacroMode.WhileHeld => "Repeats for as long as you hold the button, and stops when you let go.",
        MacroMode.Toggle => "Press once to start repeating, press again to stop.",
        _ => "Each press plays the next action in the list, then wraps around."
    };

    /// <summary>Simple line icons for the four macro types.</summary>
    internal static FrameworkElement ModeIcon(MacroMode mode, double size)
    {
        Brush stroke = Ui.Brush("Muted");
        Canvas canvas = new() { Width = 24, Height = 24 };
        switch (mode)
        {
            case MacroMode.Once:
                canvas.Children.Add(new Path { Data = Geometry.Parse("M3,12 L20,12 M14,6 L20,12 L14,18"), StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round });
                break;
            case MacroMode.WhileHeld:
                canvas.Children.Add(new Path { Data = Geometry.Parse("M19,12 A7,7 0 1 1 15.5,5.9 M15,2.5 L15.8,6 L12.3,6.8"), StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round });
                break;
            case MacroMode.Toggle:
                canvas.Children.Add(new Rectangle { Width = 22, Height = 12, RadiusX = 6, RadiusY = 6, StrokeThickness = 2 });
                Canvas.SetLeft(canvas.Children[0], 1); Canvas.SetTop(canvas.Children[0], 6);
                Ellipse knob = new() { Width = 7, Height = 7 };
                knob.SetBinding(Shape.FillProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Control), 1) });
                Canvas.SetLeft(knob, 13.5); Canvas.SetTop(knob, 8.5);
                canvas.Children.Add(knob);
                break;
            default:
                canvas.Children.Add(new Rectangle { Width = 10, Height = 10, RadiusX = 2, RadiusY = 2, StrokeThickness = 2 });
                Canvas.SetLeft(canvas.Children[0], 2); Canvas.SetTop(canvas.Children[0], 5);
                canvas.Children.Add(new Rectangle { Width = 10, Height = 10, RadiusX = 2, RadiusY = 2, StrokeThickness = 2 });
                Canvas.SetLeft(canvas.Children[1], 8); Canvas.SetTop(canvas.Children[1], 8);
                canvas.Children.Add(new Rectangle { Width = 10, Height = 10, RadiusX = 2, RadiusY = 2, StrokeThickness = 2 });
                Canvas.SetLeft(canvas.Children[2], 14); Canvas.SetTop(canvas.Children[2], 11);
                break;
        }
        foreach (Shape shape in canvas.Children.OfType<Shape>())
        {
            if (shape is Ellipse) continue;
            // Follow the hosting control's foreground so selected tiles turn accent-coloured.
            shape.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Control), 1), FallbackValue = stroke });
            if (mode == MacroMode.Sequence) shape.SetValue(Shape.FillProperty, Ui.Brush("Surface"));
        }
        return new Viewbox { Width = size, Height = size, Child = canvas };
    }
}
