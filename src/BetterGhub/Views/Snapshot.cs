using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BetterGhub.Services;

namespace BetterGhub.Views;

/// <summary>
/// Design-review helper: <c>BetterGhub.exe --snapshot &lt;folder&gt; [--small]</c> renders every page to PNG and exits.
/// Point BETTERGHUB_SETTINGS at a scratch settings file so real settings are untouched.
/// </summary>
internal static class Snapshot
{
    public static void Run(MouseService service, string folder, bool small)
    {
        Directory.CreateDirectory(folder);
        service.Settings.AutoConnect = false;
        MainWindow window = new(service)
        {
            Width = small ? 1040 : 1320,
            Height = small ? 680 : 840,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -30000,
            Top = 0,
            ShowActivated = false,
            ShowInTaskbar = false
        };
        Application.Current.MainWindow = window;
        window.Show();
        service.SimulateConnected(1600, 1);
        _ = RenderAll(window, folder, small ? "-small" : "");
    }

    private static async Task RenderAll(MainWindow window, string folder, string suffix)
    {
        foreach (string page in new[] { "Assignments", "Macros", "Sensitivity", "Profiles", "Settings" })
        {
            window.Navigate(page);
            await Settle();
            Save(window, Path.Combine(folder, $"{page.ToLowerInvariant()}{suffix}.png"));
        }
        if (window.Service.Settings.Macros.Count > 0)
        {
            window.OpenMacro(window.Service.Settings.Macros[^1]);
            if (window.FindPage<MacrosPage>() is MacrosPage macros) macros.SelectStep(0);
            await Settle();
            Save(window, Path.Combine(folder, $"macros-step{suffix}.png"));
        }
        window.Navigate("Assignments");
        window.FindPage<AssignmentsPage>()?.SelectControl("G4");
        await Settle();
        Save(window, Path.Combine(folder, $"assignments-side{suffix}.png"));

        InstallWindow install = new() { WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
        install.Show();
        await Settle();
        Save(install, Path.Combine(folder, $"install{suffix}.png"));
        install.Close();
        Application.Current.Shutdown();
    }

    private static async Task Settle()
    {
        for (int i = 0; i < 3; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(150);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static void Save(Window window, string path)
    {
        FrameworkElement root = (FrameworkElement)window.Content;
        RenderTargetBitmap bitmap = new((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        DrawingVisual visual = new();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(window.Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            context.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }
        bitmap.Render(visual);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }
}
