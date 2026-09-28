using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using BetterGhub.Core;
using BetterGhub.Services;
using BetterGhub.Views;

namespace BetterGhub;

public partial class App : Application
{
    private const string InstanceName = "BetterGhub.SingleInstance", ActivateName = "BetterGhub.Activate", QuitName = "BetterGhub.Quit";
    private Mutex? singleInstance;
    private EventWaitHandle? activateSignal, quitSignal;
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

        if (args.Contains("--uninstall"))
        {
            RunUninstall(quiet: args.Contains("--quiet"));
            Shutdown();
            return;
        }

        if (!Installer.IsDeveloperBuild && !Installer.IsRunningInstalled && !args.Contains("--portable") && OfferInstall())
        {
            Shutdown();
            return;
        }

        singleInstance = new Mutex(true, InstanceName, out bool created);
        if (!created)
        {
            // Already running (maybe hidden in the tray): bring that window forward instead.
            if (EventWaitHandle.TryOpenExisting(ActivateName, out EventWaitHandle? existing)) { existing.Set(); existing.Dispose(); }
            singleInstance.Dispose();
            singleInstance = null;
            Shutdown();
            return;
        }
        activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateName);
        quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, QuitName);
        ThreadPool.RegisterWaitForSingleObject(activateSignal, (_, _) => Dispatcher.BeginInvoke(ShowMainWindow), null, -1, false);
        ThreadPool.RegisterWaitForSingleObject(quitSignal, (_, _) => Dispatcher.BeginInvoke(Quit), null, -1, false);

        service = new MouseService(new DispatcherSynchronizationContext(Dispatcher));
        window = new MainWindow(service);
        MainWindow = window;
        tray = new TrayIcon { MenuItems = TrayMenu };
        tray.Activated += ShowMainWindow;
        service.StateChanged += UpdateTray;
        UpdateTray();

        if (args.Contains("--minimized")) window.PrepareHidden();
        else window.Show();
    }

    // ── Install / uninstall ───────────────────────────────────────────────────

    /// <summary>Returns true when this process should exit (installed and relaunched, or cancelled).</summary>
    private bool OfferInstall()
    {
        Settings peek = Settings.Load();
        if (peek.SkipInstallPrompt && !Installer.IsInstalled) return false;
        InstallWindow dialog = new();
        dialog.ShowDialog();
        if (dialog.Choice is null) return true;
        if (dialog.Choice == false)
        {
            if (!Installer.IsInstalled)
            {
                peek.SkipInstallPrompt = true;
                try { peek.Save(); } catch (Exception) { /* Only a preference. */ }
            }
            return false;
        }
        try
        {
            CloseRunningInstance();
            Installer.Install(dialog.DesktopShortcut, dialog.StartWithWindows);
            Process.Start(new ProcessStartInfo(Installer.InstalledExe) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            MessageBox.Show($"BetterGhub could not be installed.\n\n{error.Message}", "Install BetterGhub", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        return true;
    }

    private static void RunUninstall(bool quiet)
    {
        if (!quiet && MessageBox.Show("Uninstall BetterGhub?", "Uninstall BetterGhub", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        bool deleteSettings = !quiet && MessageBox.Show("Also delete your macros, profiles and calibration?\n\nChoose No to keep them for a future install.",
            "Uninstall BetterGhub", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        CloseRunningInstance();
        try
        {
            Installer.Uninstall(deleteSettings);
            if (!quiet) MessageBox.Show("BetterGhub was uninstalled. Power-cycle the mouse to return it to its onboard profile.", "Uninstall BetterGhub", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error)
        {
            if (!quiet) MessageBox.Show($"Uninstall did not finish.\n\n{error.Message}", "Uninstall BetterGhub", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Asks a running BetterGhub to quit and waits up to five seconds for it to exit.</summary>
    private static void CloseRunningInstance()
    {
        if (!EventWaitHandle.TryOpenExisting(QuitName, out EventWaitHandle? quit)) return;
        using (quit) quit.Set();
        Stopwatch clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(5) && Mutex.TryOpenExisting(InstanceName, out Mutex? running))
        {
            running.Dispose();
            Thread.Sleep(150);
        }
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

    protected override void OnExit(ExitEventArgs e)
    {
        tray?.Dispose();
        service?.Dispose();
        activateSignal?.Dispose();
        quitSignal?.Dispose();
        singleInstance?.Dispose();
        base.OnExit(e);
    }
}
