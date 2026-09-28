using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BetterGhub.Core;
using BetterGhub.Input;
using BetterGhub.Services;

namespace BetterGhub.Views;

/// <summary>Implemented by pages so the shell can refresh them after outside changes.</summary>
internal interface IPage
{
    void Refresh();
}

public partial class MainWindow : Window
{
    private readonly MouseService service;
    private readonly RawMouseWheel wheel = new();
    private readonly Dictionary<string, FrameworkElement> pages = [];
    private readonly DispatcherTimer profileTimer = new() { Interval = TimeSpan.FromMilliseconds(1000) };
    private readonly DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(2.6) };
    private nint handle;

    internal MainWindow(MouseService service)
    {
        this.service = service;
        InitializeComponent();
        service.StateChanged += UpdateChrome;
        service.SettingsChanged += () => (PageHost.Content as IPage)?.Refresh();
        service.Calibrated += (control, bit) => ShowToast($"{control.Label} calibrated · 0x{bit:x4}");
        StateChanged += (_, _) => UpdateMaximized();
        SizeChanged += (_, _) => UpdateCompact();
        SourceInitialized += (_, _) => AttachInput();
        Loaded += (_, _) => { if (service.Settings.LoadError is { } error) ShowToast(error); };
        profileTimer.Tick += (_, _) =>
        {
            service.AutoSelectProfile(handle);
            if (service.State == ConnectionState.GHubRunning && !MouseService.IsGHubRunning()) service.Connect();
        };
        toastTimer.Tick += (_, _) => { toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };
        NavAssignments.IsChecked = true;
        UpdateChrome();
    }

    internal MouseService Service => service;

    private void AttachInput()
    {
        handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WndProc);
        try { wheel.Register(handle); }
        catch (Exception error) { service.Write("Wheel capture unavailable: " + error.Message); }
        service.InstallHook();
        profileTimer.Start();
        AutoConnect(); // Here rather than Loaded so it also runs when started hidden in the tray.
    }

    private void AutoConnect()
    {
        if (service.Settings.AutoConnect) service.Connect();
    }

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x00FF)
        {
            try
            {
                int bit = wheel.Read(lParam);
                if (bit != 0) service.OnWheelPulse(bit);
            }
            catch (Exception error) { service.Write("Wheel input error: " + error.Message); }
        }
        return nint.Zero;
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    private void NavChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton item) Navigate(AutomationProperties.GetName(item));
    }

    internal void Navigate(string name)
    {
        PageTitle.Text = name;
        if (!pages.TryGetValue(name, out FrameworkElement? page))
        {
            page = name switch
            {
                "Assignments" => new AssignmentsPage(service, this),
                "Macros" => new MacrosPage(service, this),
                "Sensitivity" => new SensitivityPage(service),
                "Profiles" => new ProfilesPage(service),
                _ => new DevicePage(service)
            };
            pages[name] = page;
        }
        else (page as IPage)?.Refresh();
        PageHost.Content = page;
        foreach (RadioButton item in Nav.Children.OfType<RadioButton>())
            if (AutomationProperties.GetName(item) == name && item.IsChecked != true) item.IsChecked = true;
    }

    internal T? FindPage<T>() where T : FrameworkElement => pages.Values.OfType<T>().FirstOrDefault();

    internal void OpenMacro(MacroDefinition macro)
    {
        Navigate("Macros");
        ((MacrosPage)pages["Macros"]).Select(macro);
    }

    // ── Chrome ────────────────────────────────────────────────────────────────

    private void UpdateChrome()
    {
        (string title, Brush dot, string detail, string button) = service.State switch
        {
            ConnectionState.Connected => ("Connected", (Brush)FindResource("Success"), $"{service.DeviceDpi} DPI · {service.DeviceReportRate} Hz", "Disconnect"),
            ConnectionState.Connecting => ("Connecting", (Brush)FindResource("Warning"), Fallback(service.StateDetail, "Looking for the receiver…"), "Cancel"),
            ConnectionState.GHubRunning => ("G HUB is running", (Brush)FindResource("Warning"), "Exit G HUB completely. BetterGhub connects when it closes.", "Try again"),
            _ => ("Disconnected", (Brush)FindResource("Faint"), Fallback(service.StateDetail, "Mouse not connected"), "Connect mouse")
        };
        StatusTitle.Text = title;
        StatusDot.Fill = dot;
        CompactDot.Fill = dot;
        CompactStatus.ToolTip = $"{title} · {detail}";
        StatusDetail.Text = detail;
        ConnectButton.Content = button;
        ConnectButton.Style = (Style)FindResource(service.State == ConnectionState.Connected ? "Btn" : "PrimaryBtn");
        GShiftBadge.Visibility = service.GShiftHeld ? Visibility.Visible : Visibility.Collapsed;

        MouseProfile profile = service.ActiveProfile;
        ProfileKind.Text = profile.IsDesktop ? "DESKTOP" : "APPLICATION";
        ProfileName.Text = profile.Name;
        ProfileIcon.Content = ShellIcons.Element(profile, 18);
    }

    private static string Fallback(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private void ConnectClick(object sender, RoutedEventArgs e)
    {
        if (service.State is ConnectionState.Connected or ConnectionState.Connecting) service.Disconnect();
        else service.Connect();
    }

    // The profile menu is opened and closed here rather than with StaysOpen="False": that closes the
    // popup on mouse-down, so the same click on the toggle reopened it, and it can't animate closing.
    private bool profileMenuClosing;

    private void ProfileToggleClick(object sender, RoutedEventArgs e)
    {
        if (ProfileToggle.IsChecked == true) OpenProfileMenu();
        else CloseProfileMenu();
    }

    private void OpenProfileMenu()
    {
        BuildProfileList();
        profileMenuClosing = false;
        ProfileToggle.IsChecked = true;
        if (!ProfilePopup.IsOpen)
        {
            ProfileMenu.BeginAnimation(OpacityProperty, null);
            ProfileMenuSlide.BeginAnimation(TranslateTransform.YProperty, null);
            ProfileMenu.Opacity = 0;
            ProfileMenuSlide.Y = -8;
            ProfilePopup.IsOpen = true;
        }
        AnimateProfileMenu(1, 0, TimeSpan.FromMilliseconds(170), new CubicEase { EasingMode = EasingMode.EaseOut }, null);
    }

    private void CloseProfileMenu()
    {
        ProfileToggle.IsChecked = false;
        if (!ProfilePopup.IsOpen || profileMenuClosing) return;
        profileMenuClosing = true;
        AnimateProfileMenu(0, -8, TimeSpan.FromMilliseconds(120), new CubicEase { EasingMode = EasingMode.EaseIn }, () =>
        {
            if (!profileMenuClosing) return; // Reopened mid-animation.
            profileMenuClosing = false;
            ProfilePopup.IsOpen = false;
        });
    }

    private void AnimateProfileMenu(double opacity, double offset, TimeSpan duration, IEasingFunction ease, Action? completed)
    {
        DoubleAnimation fade = new(opacity, duration) { EasingFunction = ease };
        if (completed is not null) fade.Completed += (_, _) => completed();
        ProfileMenu.BeginAnimation(OpacityProperty, fade);
        ProfileMenuSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offset, duration) { EasingFunction = ease });
    }

    protected override void OnPreviewMouseDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        // Clicks inside the popup land in its own window, so anything seen here is outside it.
        if (ProfilePopup.IsOpen && !ProfileToggle.IsMouseOver) CloseProfileMenu();
        base.OnPreviewMouseDown(e);
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape && ProfilePopup.IsOpen) { CloseProfileMenu(); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        CloseProfileMenu();
        base.OnDeactivated(e);
    }

    private void BuildProfileList()
    {
        ProfileList.Children.Clear();
        foreach (MouseProfile profile in service.Settings.Profiles)
        {
            RadioButton row = new()
            {
                Style = (Style)FindResource("Row"),
                IsChecked = profile == service.ActiveProfile,
                Content = ProfileRow(profile),
                GroupName = "ProfilePick"
            };
            row.Click += (_, _) => { service.SelectProfile(profile); CloseProfileMenu(); };
            ProfileList.Children.Add(row);
        }
        Button manage = new() { Style = (Style)FindResource("GhostBtn"), Content = "Manage profiles…", HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        manage.Click += (_, _) => { CloseProfileMenu(); Navigate("Profiles"); };
        ProfileList.Children.Add(manage);
    }

    private static FrameworkElement ProfileRow(MouseProfile profile)
    {
        DockPanel row = new();
        row.Children.Add(ShellIcons.Element(profile, 16).With(new Thickness(0, 0, 10, 0)));
        row.Children.Add(new TextBlock { Text = profile.Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    internal void ShowToast(string text)
    {
        ToastText.Text = text;
        Toast.Visibility = Visibility.Visible;
        toastTimer.Stop();
        toastTimer.Start();
    }

    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>Below ~1220px the rail collapses to icons so the pages keep their room.</summary>
    private void UpdateCompact()
    {
        bool compact = ActualWidth < 1220;
        if (compact == (RailColumn.Width.Value < 100)) return;
        RailColumn.Width = new GridLength(compact ? 76 : 232);
        RailPanel.Margin = compact ? new Thickness(12, 0, 12, 12) : new Thickness(16, 0, 16, 16);
        Wordmark.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ConnectionCard.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CompactStatus.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        foreach (RadioButton item in Nav.Children.OfType<RadioButton>())
        {
            string name = AutomationProperties.GetName(item);
            item.Content = compact ? null : name;
            item.ToolTip = compact ? name : null;
        }
    }

    private void CompactStatusClick(object sender, RoutedEventArgs e) => Navigate("Settings");

    private void UpdateMaximized()
    {
        // A chromeless maximized window overhangs the screen by the resize border.
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
    }

    /// <summary>Creates the window handle without showing it (for --minimized) so input capture still starts.</summary>
    internal void PrepareHidden() => new WindowInteropHelper(this).EnsureHandle();

    internal void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true; // Reliably bring to front when activated from another process or the tray.
        Topmost = false;
        Focus();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!App.Current.Quitting && service.Settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            App.Current.OnWindowClosedToTray();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        profileTimer.Stop();
        App.Current.Quit();
        base.OnClosed(e);
    }
}
