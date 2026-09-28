using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterGhub.Core;

public enum MacroMode { Once, WhileHeld, Toggle, Sequence }

// Append new kinds at the end: settings store them by name, but keep the order stable anyway.
public enum ActionKind { Key, KeyDown, KeyUp, Text, Delay, LeftClick, RightClick, MiddleClick, Wheel, VolumeUp, VolumeDown, Mute, Launch, PlayPause, NextTrack, PreviousTrack }

public sealed class MacroStep
{
    public ActionKind Kind { get; set; } = ActionKind.Key;
    public string Value { get; set; } = "";
    public int DelayMs { get; set; } = 50;

    public MacroStep Clone() => new() { Kind = Kind, Value = Value, DelayMs = DelayMs };
    public override string ToString() => Kind switch
    {
        ActionKind.Delay => $"Delay {DelayMs} ms",
        ActionKind.Key or ActionKind.KeyDown or ActionKind.KeyUp or ActionKind.Text or ActionKind.Launch or ActionKind.Wheel => $"{Kind}: {Value}",
        _ => Kind.ToString()
    };
}

public sealed class MacroDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New macro";
    public MacroMode Mode { get; set; }
    public List<MacroStep> Steps { get; set; } = [];
    /// <summary>When set, recorded Delay steps are ignored and this delay separates every action.</summary>
    public int? StandardDelayMs { get; set; }
    public override string ToString() => Name;
}

public sealed class MouseProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Default";
    public string ApplicationPath { get; set; } = "";
    public int Dpi { get; set; } = 1600;
    public List<int> DpiStages { get; set; } = [100, 1000, 1600, 2400, 18600];
    public int ShiftDpi { get; set; } = 100;
    public int ReportRate { get; set; } = 1000;
    /// <summary>HID bit (or wheel pseudo-bit) to macro id or built-in action id.</summary>
    public Dictionary<int, string> Assignments { get; set; } = [];
    public Dictionary<int, string> ShiftAssignments { get; set; } = [];
    [JsonIgnore] public bool IsDesktop => string.IsNullOrWhiteSpace(ApplicationPath);
    public override string ToString() => Name;
}

public sealed class Settings
{
    public List<MouseProfile> Profiles { get; set; } = [new()];
    public List<MacroDefinition> Macros { get; set; } = [];
    /// <summary>Physical control id (see <see cref="MouseControls"/>) to the HID bit it reports.</summary>
    public Dictionary<string, int> ControlBits { get; set; } = [];
    /// <summary>Legacy bit-to-"G7" labels from the first prototype; migrated into <see cref="ControlBits"/>.</summary>
    public Dictionary<int, string>? ButtonNames { get; set; }
    public string ActiveProfileId { get; set; } = "";
    public bool SuppressStandardActions { get; set; }
    public bool AutoConnect { get; set; } = true;
    public bool AutoSwitchProfiles { get; set; } = true;
    /// <summary>Closing the window keeps BetterGhub running in the notification area.</summary>
    public bool CloseToTray { get; set; } = true;
    public bool TrayHintShown { get; set; }

    [JsonIgnore] public string? LoadError { get; private set; }

    [JsonIgnore]
    public MouseProfile ActiveProfile => Profiles.FirstOrDefault(p => p.Id == ActiveProfileId) ?? Profiles[0];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static string FilePath =>
        Environment.GetEnvironmentVariable("BETTERGHUB_SETTINGS") is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BetterGhub", "settings.json");

    public static Settings Load()
    {
        Settings settings;
        try
        {
            settings = File.Exists(FilePath) ? Read(FilePath) : new Settings();
        }
        catch (Exception error)
        {
            // Start fresh rather than crash, but keep the unreadable file so nothing is lost.
            settings = new Settings();
            string backup = $"{FilePath}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
            try { File.Copy(FilePath, backup, true); } catch (Exception) { backup = ""; }
            settings.LoadError = $"Settings could not be read ({error.Message}).{(backup.Length > 0 ? $" A copy was kept at {backup}." : "")}";
        }
        settings.Normalize();
        return settings;
    }

    private static Settings Read(string path) =>
        JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), JsonOptions) ?? new Settings();

    /// <summary>Reads settings exported by <see cref="Export"/>. Throws when the file is not a settings file.</summary>
    public static Settings Import(string path)
    {
        Settings settings = Read(path);
        settings.Normalize();
        return settings;
    }

    public void Export(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));

    /// <summary>Replaces this configuration with <paramref name="other"/>'s, keeping local-only state like hints.</summary>
    public void ReplaceWith(Settings other)
    {
        Profiles = other.Profiles;
        Macros = other.Macros;
        ControlBits = other.ControlBits;
        ActiveProfileId = other.ActiveProfileId;
        SuppressStandardActions = other.SuppressStandardActions;
        AutoConnect = other.AutoConnect;
        AutoSwitchProfiles = other.AutoSwitchProfiles;
        CloseToTray = other.CloseToTray;
    }

    private void Normalize()
    {
        if (Profiles.Count == 0) Profiles.Add(new MouseProfile());
        if (ButtonNames is { Count: > 0 })
        {
            foreach ((int bit, string name) in ButtonNames)
                if (MouseControls.ById(name) is { Calibratable: true } control)
                    ControlBits.TryAdd(control.Id, bit);
        }
        ButtonNames = null;
        foreach (MouseProfile profile in Profiles)
        {
            profile.DpiStages = profile.DpiStages.Where(x => x is >= 100 and <= 25600).Distinct().Order().ToList();
            if (profile.DpiStages.Count == 0) profile.DpiStages.Add(Math.Clamp(profile.Dpi, 100, 25600));
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temporary, FilePath, true);
    }

    public int? BitFor(MouseControl control) =>
        control.Calibratable && ControlBits.TryGetValue(control.Id, out int bit) ? bit : control.DefaultBit;

    public MouseControl? ControlFor(int bit) =>
        MouseControls.All.FirstOrDefault(c => BitFor(c) == bit);

    public string DescribeAssignment(string? id) =>
        string.IsNullOrEmpty(id) ? "" : Macros.FirstOrDefault(m => m.Id == id)?.Name ?? BuiltinActions.NameFor(id) ?? "Missing macro";
}

public static class BuiltinActions
{
    public const string DpiShift = "__dpi_shift__";
    public const string DpiUp = "__dpi_up__";
    public const string DpiDown = "__dpi_down__";
    public const string DpiCycle = "__dpi_cycle__";
    public const string GShift = "__g_shift__";
    public const string Disabled = "__disabled__";

    public static readonly (string Id, string Name, string Description)[] All =
    [
        (DpiShift, "DPI Shift", "Hold for the DPI Shift speed"),
        (DpiUp, "DPI Up", "Next higher DPI speed"),
        (DpiDown, "DPI Down", "Next lower DPI speed"),
        (DpiCycle, "DPI Cycle", "Step through DPI speeds"),
        (GShift, "G-Shift", "Hold to use the G-Shift layer"),
        (Disabled, "Do nothing", "Run no macro (pair with blocking to disable a button)")
    ];

    public static string? NameFor(string? id) => All.FirstOrDefault(x => x.Id == id).Name;
    public static bool IsBuiltin(string? id) => All.Any(x => x.Id == id);
}
