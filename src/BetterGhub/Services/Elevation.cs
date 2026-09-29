using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace BetterGhub.Services;

/// <summary>Whether BetterGhub runs as administrator, and starting an elevated copy of it.</summary>
internal static class Elevation
{
    public static bool IsElevated { get; } = CheckElevated();

    private static bool CheckElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Starts BetterGhub as administrator (Windows asks first); false when that was declined or failed.</summary>
    public static bool TryStartElevated(IEnumerable<string> args)
    {
        ProcessStartInfo info = new(AutoStart.CurrentExe)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))
        };
        try
        {
            Process.Start(info)?.Dispose();
            return true;
        }
        catch (Win32Exception) { return false; }
    }
}
