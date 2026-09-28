using System.Diagnostics;
using Microsoft.Win32;

namespace BetterGhub.Services;

/// <summary>
/// Per-user install without admin rights: copies the single-file exe to %LOCALAPPDATA%\Programs\BetterGhub,
/// adds Start menu (and optional desktop) shortcuts and an "Installed apps" entry, and manages autostart.
/// </summary>
internal static class Installer
{
    private const string AppName = "BetterGhub";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\BetterGhub";

    public static string InstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);
    public static string InstalledExe => Path.Combine(InstallDirectory, "BetterGhub.exe");
    public static string CurrentExe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "BetterGhub.exe");
    private static string StartMenuShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "BetterGhub.lnk");
    private static string DesktopShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "BetterGhub.lnk");

    public static string Version => typeof(Installer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Framework-dependent developer builds (bin\Debug etc.) have the managed dll next to the exe.</summary>
    public static bool IsDeveloperBuild => File.Exists(Path.Combine(AppContext.BaseDirectory, "BetterGhub.dll"));

    public static bool IsInstalled => File.Exists(InstalledExe);

    public static bool IsRunningInstalled =>
        string.Equals(Path.GetFullPath(CurrentExe), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    public static string? InstalledVersion =>
        IsInstalled ? FileVersionInfo.GetVersionInfo(InstalledExe).ProductVersion?.Split('+')[0] : null;

    public static void Install(bool desktopShortcut, bool startWithWindows)
    {
        Directory.CreateDirectory(InstallDirectory);
        CopyWithRetry(CurrentExe, InstalledExe);
        CreateShortcut(StartMenuShortcut, InstalledExe);
        if (desktopShortcut) CreateShortcut(DesktopShortcut, InstalledExe);
        else if (File.Exists(DesktopShortcut)) File.Delete(DesktopShortcut);

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", Version);
        key.SetValue("DisplayIcon", $"{InstalledExe},0");
        key.SetValue("Publisher", AppName);
        key.SetValue("InstallLocation", InstallDirectory);
        key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{InstalledExe}\" --uninstall --quiet");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);

        SetStartWithWindows(startWithWindows, InstalledExe);
    }

    private static void CopyWithRetry(string source, string target)
    {
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) return;
        for (int attempt = 0; ; attempt++)
        {
            try { File.Copy(source, target, true); return; }
            catch (IOException) when (attempt < 20) { Thread.Sleep(250); } // An old instance may still be exiting.
        }
    }

    /// <summary>Removes shortcuts, autostart and the Installed apps entry, then deletes the folder after exit.</summary>
    public static void Uninstall(bool deleteSettings)
    {
        SetStartWithWindows(false, InstalledExe);
        foreach (string shortcut in new[] { StartMenuShortcut, DesktopShortcut })
            if (File.Exists(shortcut)) File.Delete(shortcut);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        if (deleteSettings)
        {
            string? folder = Path.GetDirectoryName(Core.Settings.FilePath);
            if (folder is not null && Directory.Exists(folder)) Directory.Delete(folder, true);
        }
        // The running exe can't delete itself: remove the folder from a short-lived hidden shell after we exit.
        if (Directory.Exists(InstallDirectory))
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{InstallDirectory}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath()
            });
    }

    public static bool StartsWithWindows()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(AppName) is string;
    }

    public static void SetStartWithWindows(bool enabled, string? exe = null)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(AppName, $"\"{exe ?? CurrentExe}\" --minimized");
        else key.DeleteValue(AppName, false);
    }

    private static void CreateShortcut(string path, string target)
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host is unavailable");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = target;
            link.WorkingDirectory = Path.GetDirectoryName(target);
            link.IconLocation = $"{target},0";
            link.Description = "Macros, DPI and button assignments for the G502 X LIGHTSPEED";
            link.Save();
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }
}
