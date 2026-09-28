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
    private readonly OnboardCard onboard;
    private MouseProfile? selected;

    public ProfilesPage(MouseService service)
    {
        this.service = service;
        onboard = new OnboardCard(service);
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
        Refresh();
    }

    public void Refresh()
    {
        autoSwitch.IsChecked = service.Settings.AutoSwitchProfiles;
        if (selected is null || !service.Settings.Profiles.Contains(selected))
            selected = service.Settings.Profiles.FirstOrDefault(p => p.Id == selected?.Id) ?? service.ActiveProfile; // Undo puts back copies.
        tiles.Children.Clear();
        foreach (MouseProfile profile in service.Settings.Profiles) tiles.Children.Add(Tile(profile));
        tiles.Children.Add(AddTile());
        RenderDetails();
        onboard.Render(force: true);
    }

    /// <summary>For --snapshot: scrolls the on-board memory card into view.</summary>
    internal void ShowOnboard()
    {
        UpdateLayout();
        ((ScrollViewer)Content).ScrollToEnd();
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
