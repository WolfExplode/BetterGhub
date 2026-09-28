using System.Windows;
using System.Windows.Threading;
using BetterGhub.Services;
using BetterGhub.Views;

namespace BetterGhub;

public partial class App : Application
{
    private Mutex? singleInstance;
    private MouseService? service;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // --snapshot <folder>: render every page to PNG and exit (design review without the device).
        int snapshot = Array.IndexOf(e.Args, "--snapshot");
        if (snapshot >= 0 && snapshot + 1 < e.Args.Length)
        {
            // Never show dialogs in snapshot mode: write the error next to the images and quit.
            string folder = e.Args[snapshot + 1];
            DispatcherUnhandledException += (_, args) =>
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "error.txt"), args.Exception.ToString());
                args.Handled = true;
                Shutdown(1);
            };
            service = new MouseService(new DispatcherSynchronizationContext(Dispatcher));
            Snapshot.Run(service, e.Args[snapshot + 1], e.Args.Contains("--small"));
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "BetterGhub error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        singleInstance = new Mutex(true, "BetterGhub.SingleInstance", out bool created);
        if (!created)
        {
            MessageBox.Show("BetterGhub is already running.", "BetterGhub", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        service = new MouseService(new DispatcherSynchronizationContext(Dispatcher));
        MainWindow window = new(service);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        service?.Dispose();
        singleInstance?.Dispose();
        base.OnExit(e);
    }
}
