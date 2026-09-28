using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterGhub.Services;

namespace BetterGhub.Views;

/// <summary>First-run prompt when the exe runs from outside the install folder (e.g. Downloads).</summary>
internal sealed class InstallWindow : Window
{
    private readonly CheckBox startup = new() { IsChecked = true };
    private readonly CheckBox desktop = new() { IsChecked = false };
    public bool DesktopShortcut => desktop.IsChecked == true;
    public bool StartWithWindows => startup.IsChecked == true;
    /// <summary>True: install. False: run once without installing. Null: window closed.</summary>
    public bool? Choice { get; private set; }

    public InstallWindow()
    {
        string? installed = Installer.InstalledVersion;
        bool update = installed is not null;
        Title = update ? "Update BetterGhub" : "Install BetterGhub";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Ui.Brush("Panel");
        Foreground = Ui.Brush("Text");
        FontFamily = Ui.Font("BodyFont");
        FontSize = 13;
        Ui.DarkTitleBar(this);
        Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/icon-256.png"));

        StackPanel root = new() { Margin = new Thickness(32, 28, 32, 26) };
        StackPanel head = Ui.Row(16,
            new Image { Source = Icon, Width = 56, Height = 56 },
            new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    Ui.Text(update ? "Update BetterGhub" : "Install BetterGhub", "H1"),
                    Ui.Text(update ? $"Version {installed} is installed. This is version {Installer.Version}." : $"Version {Installer.Version}", "Body")
                }
            });
        RenderOptions.SetBitmapScalingMode((Image)head.Children[0], BitmapScalingMode.HighQuality);
        root.Children.Add(head);

        root.Children.Add(Ui.Text(update
            ? "Your macros, profiles and calibration are kept. A running BetterGhub will be closed and restarted."
            : "Installs for your Windows account only, so no administrator rights are needed. You get a Start menu shortcut and can uninstall from Settings › Apps like any other app.",
            "Body").With(new Thickness(0, 20, 0, 18)));

        startup.Style = Ui.Style("Switch");
        startup.Content = Ui.Text("Start with Windows (in the tray) so macros are always ready", color: "Text");
        desktop.Style = Ui.Style("Switch");
        desktop.Content = Ui.Text("Add a desktop shortcut", color: "Text");
        startup.IsChecked = !update || Installer.StartsWithWindows();
        root.Children.Add(startup);
        root.Children.Add(desktop.With(new Thickness(0, 12, 0, 0)));

        Button install = Ui.Button(update ? "Update" : "Install", () => { Choice = true; Close(); }, "PrimaryBtn");
        install.Padding = new Thickness(22, 9, 22, 9);
        Button portable = Ui.Button(update ? "Run this copy without updating" : "Just run it", () => { Choice = false; Close(); }, "GhostBtn");
        StackPanel buttons = Ui.Row(8, portable, install);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Margin = new Thickness(0, 26, 0, 0);
        root.Children.Add(buttons);
        Content = root;
        Loaded += (_, _) => install.Focus();
    }
}
