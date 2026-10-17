using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using BetterGhub.Core;
using BetterGhub.Services;
using BetterGhub.Views;

namespace BetterGhub;

public partial class App : Application
{
    private const string InstanceName = "BetterGhub.SingleInstance", ActivateName = "BetterGhub.Activate";
    private Mutex? singleInstance;
    private EventWaitHandle? activateSignal;
    private MouseService? service;
    private MainWindow? window;
    private TrayIcon? tray;

    public static new App Current => (App)Application.Current;
    public bool Quitting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string[] args = e.Args;

        // --probe <file>: read-only receiver diagnostic (lists HID interfaces, reads mode/DPI/rate), then exit.
        int probe = Array.IndexOf(args, "--probe");
        if (probe >= 0 && probe + 1 < args.Length)
        {
            string report;
            try { report = Device.HidppBridge.Probe(); }
            catch (Exception error) { report = error.ToString(); }
            File.WriteAllText(args[probe + 1], report);
            Shutdown();
            return;
        }

        // --g304 renders the G304 layout without the receiver (with --snapshot).
        Device.HidppBridge.Detect(args.Contains("--g304"));

        // --snapshot <folder>: render every page to PNG and exit (design review without the device).
        int snapshot = Array.IndexOf(args, "--snapshot");
        if (snapshot >= 0 && snapshot + 1 < args.Length)
        {
            // Never show dialogs in snapshot mode: write the error next to the images and quit.
            string folder = args[snapshot + 1];
            DispatcherUnhandledException += (_, error) =>
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "error.txt"), error.Exception.ToString());
                error.Handled = true;
                Shutdown(1);
            };
            service = new MouseService(new DispatcherSynchronizationContext(Dispatcher));
            Snapshot.Run(service, folder, args.Contains("--small"));
            return;
        }

        DispatcherUnhandledException += (_, error) =>
        {
            MessageBox.Show(error.Exception.Message, "BetterGhub error", MessageBoxButton.OK, MessageBoxImage.Error);
            error.Handled = true;
        };

        // --after <pid>: an instance handing over to this one (to run as administrator); let it finish exiting first.
        int after = Array.IndexOf(args, "--after");
        if (after >= 0 && after + 1 < args.Length && int.TryParse(args[after + 1], out int previousId))
        {
            try { using Process previous = Process.GetProcessById(previousId); previous.WaitForExit(10_000); }
            catch (Exception) { /* Already gone. */ }
        }
        else if (!Elevation.IsElevated && Settings.PeekRunAsAdmin() && Elevation.TryStartElevated(args))
        {
            Shutdown();
            return;
        }

        bool created;
        try { singleInstance = new Mutex(true, InstanceName, out created); }
        catch (UnauthorizedAccessException) { created = false; } // Held by an instance running as administrator.
        if (!created)
        {
            // Already running (maybe hidden in the tray): bring that window forward instead.
            if (EventWaitHandle.TryOpenExisting(ActivateName, out EventWaitHandle? existing)) { existing.Set(); existing.Dispose(); }
            singleInstance?.Dispose();
            singleInstance = null;
            Shutdown();
            return;
        }
        activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateName);
        ThreadPool.RegisterWaitForSingleObject(activateSignal, (_, _) => Dispatcher.BeginInvoke(ShowMainWindow), null, -1, false);

        service = new MouseService(new DispatcherSynchronizationContext(Dispatcher));
        bool asAdmin = service.Settings.RunAsAdmin;
        Task.Run(() => AutoStart.RefreshPath(asAdmin));
        window = new MainWindow(service);
        MainWindow = window;
        tray = new TrayIcon { MenuItems = TrayMenu };
        tray.Activated += ShowMainWindow;
        service.StateChanged += UpdateTray;
        UpdateTray();

        if (args.Contains("--minimized")) window.PrepareHidden();
        else window.Show();
    }

    // ── Tray ──────────────────────────────────────────────────────────────────

    public void ShowMainWindow()
    {
        if (window is null || Quitting) return;
        window.ShowFromTray();
    }

    /// <summary>Called when the main window is closed by the user.</summary>
    public void OnWindowClosedToTray()
    {
        if (service is null || tray is null || service.Settings.TrayHintShown) return;
        tray.ShowBalloon("BetterGhub is still running", "Your macros keep working. Right-click the tray icon to quit.");
        service.Settings.TrayHintShown = true;
        service.Save();
    }

    private void UpdateTray()
    {
        if (service is null || tray is null) return;
        string state = service.State switch
        {
            ConnectionState.Connected => "Connected",
            ConnectionState.Connecting => "Waiting for mouse",
            ConnectionState.GHubRunning => "G HUB is running",
            _ => "Disconnected"
        };
        tray.SetTooltip($"BetterGhub · {state} · {service.ActiveProfile.Name}");
    }

    private IEnumerable<TrayIcon.MenuItem?> TrayMenu()
    {
        if (service is null) yield break;
        MouseService current = service;
        yield return new TrayIcon.MenuItem("Open BetterGhub", ShowMainWindow);
        yield return null;
        yield return new TrayIcon.MenuItem("Profile", null, Children: current.Settings.Profiles
            .Select(p => (TrayIcon.MenuItem?)new TrayIcon.MenuItem(p.Name, () => current.SelectProfile(p), Checked: p == current.ActiveProfile)).ToList());
        yield return current.State is ConnectionState.Connected or ConnectionState.Connecting
            ? new TrayIcon.MenuItem("Disconnect mouse", current.Disconnect)
            : new TrayIcon.MenuItem("Connect mouse", current.Connect);
        yield return null;
        yield return new TrayIcon.MenuItem("Quit BetterGhub", Quit);
    }

    public void Quit()
    {
        if (Quitting) return;
        Quitting = true;
        tray?.Dispose();
        tray = null;
        window?.Close();
        Shutdown();
    }

    /// <summary>Hands over to a copy running as administrator; false when Windows' prompt was declined.</summary>
    public bool RestartElevated()
    {
        if (!Elevation.TryStartElevated(["--after", Environment.ProcessId.ToString()])) return false;
        Quit();
        return true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        tray?.Dispose();
        service?.Dispose();
        activateSignal?.Dispose();
        singleInstance?.Dispose();
        base.OnExit(e);
    }
}
