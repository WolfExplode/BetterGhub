using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace BetterGhub.Services;

/// <summary>
/// "Start with Windows" for the portable exe: an HKCU Run entry pointing at wherever the exe currently lives, or, when
/// running as administrator, a scheduled logon task (Windows silently skips Run entries that need elevation).
/// </summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "BetterGhub";

    public static string CurrentExe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "BetterGhub.exe");
    public static string Version => typeof(AutoStart).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    private static string Command => $"\"{CurrentExe}\" --minimized";

    public static bool IsEnabled => RunEntry is not null || TaskExists;

    private static string? RunEntry
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) as string;
        }
    }

    private static bool TaskExists => Schtasks("/Query", "/TN", Name).ExitCode == 0;

    /// <summary>Turns autostart on or off; <paramref name="asAdmin"/> uses the elevated task when this process can create it.</summary>
    public static void Set(bool enabled, bool asAdmin)
    {
        if (enabled && asAdmin && Elevation.IsElevated)
        {
            CreateTask();
            SetRunEntry(false);
        }
        else
        {
            SetRunEntry(enabled);
            DeleteTask();
        }
    }

    private static void SetRunEntry(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(Name, Command);
        else key.DeleteValue(Name, false);
    }

    private static void CreateTask()
    {
        string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts BetterGhub as administrator when you sign in.</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{SecurityElement.Escape(CurrentExe)}</Command><Arguments>--minimized</Arguments></Exec></Actions>
            </Task>
            """;
        string file = Path.Combine(Path.GetTempPath(), $"BetterGhub-task-{Environment.ProcessId}.xml");
        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode);
            (int exitCode, string error) = Schtasks("/Create", "/TN", Name, "/XML", file, "/F");
            if (exitCode != 0) throw new InvalidOperationException(error.Trim());
        }
        finally { File.Delete(file); }
    }

    private static void DeleteTask()
    {
        if (!TaskExists) return;
        (int exitCode, string error) = Schtasks("/Delete", "/TN", Name, "/F");
        if (exitCode != 0) throw new InvalidOperationException(error.Trim());
    }

    private static (int ExitCode, string Error) Schtasks(params string[] args)
    {
        ProcessStartInfo info = new("schtasks.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        using Process process = Process.Start(info)!;
        process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, error);
    }

    /// <summary>
    /// If the exe was moved since autostart was enabled, point the entry at the new location; when elevated, also
    /// switch between the Run entry and the logon task to match <paramref name="asAdmin"/>.
    /// </summary>
    public static void RefreshPath(bool asAdmin)
    {
        // Developer builds (managed dll beside the exe) must not repoint the user's real autostart entry.
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "BetterGhub.dll"))) return;
        try
        {
            if (Elevation.IsElevated)
            {
                if (IsEnabled) Set(true, asAdmin);
                return;
            }
            if (RunEntry is string existing && !string.Equals(existing, Command, StringComparison.OrdinalIgnoreCase))
                SetRunEntry(true);
        }
        catch (Exception) { /* Leave the entry as it is. */ }
    }
}
