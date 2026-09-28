using Microsoft.Win32;

namespace BetterGhub.Services;

/// <summary>"Start with Windows" for the portable exe: an HKCU Run entry pointing at wherever the exe currently lives.</summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "BetterGhub";

    public static string CurrentExe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "BetterGhub.exe");
    public static string Version => typeof(AutoStart).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    private static string Command => $"\"{CurrentExe}\" --minimized";

    public static bool IsEnabled
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(Name, Command);
        else key.DeleteValue(Name, false);
    }

    /// <summary>If the exe was moved since autostart was enabled, point the entry at the new location.</summary>
    public static void RefreshPath()
    {
        // Developer builds (managed dll beside the exe) must not repoint the user's real autostart entry.
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "BetterGhub.dll"))) return;
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(Name) is string existing && !string.Equals(existing, Command, StringComparison.OrdinalIgnoreCase))
                key.SetValue(Name, Command);
        }
        catch (Exception) { /* Leave the entry as it is. */ }
    }
}
