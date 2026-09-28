using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterGhub.App;

public enum MacroMode { Once, WhileHeld, Toggle, Sequence }
public enum ActionKind { Key, KeyDown, KeyUp, Text, Delay, LeftClick, RightClick, MiddleClick, Wheel, VolumeUp, VolumeDown, Mute, Launch }

public sealed class MacroStep
{
    public ActionKind Kind { get; set; } = ActionKind.Key;
    public string Value { get; set; } = "";
    public int DelayMs { get; set; } = 50;
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
    public Dictionary<int, string> Assignments { get; set; } = [];
    public Dictionary<int, string> ShiftAssignments { get; set; } = [];
    public override string ToString() => string.IsNullOrWhiteSpace(ApplicationPath) ? Name : $"{Name}  ·  {Path.GetFileName(ApplicationPath)}";
}

public sealed class Settings
{
    public List<MouseProfile> Profiles { get; set; } = [new()];
    public List<MacroDefinition> Macros { get; set; } = [];
    public Dictionary<int, string> ButtonNames { get; set; } = [];
    public string ActiveProfileId { get; set; } = "";
    public bool SuppressStandardActions { get; set; }

    [JsonIgnore]
    public MouseProfile ActiveProfile => Profiles.FirstOrDefault(p => p.Id == ActiveProfileId) ?? Profiles[0];

    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BetterGhub", "settings.json");
    public static Settings Load()
    {
        try
        {
            Settings? settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });
            if (settings is not null && settings.Profiles.Count > 0)
                return settings;
        }
        catch (Exception) { /* A fresh profile is safer than crashing on corrupt settings. */ }
        return new Settings();
    }
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } }));
        File.Move(temporary, FilePath, true);
    }
}

internal static class BuiltinActions
{
    public const string DpiShift = "__dpi_shift__";
    public const string DpiUp = "__dpi_up__";
    public const string DpiDown = "__dpi_down__";
    public const string DpiCycle = "__dpi_cycle__";
    public const string GShift = "__g_shift__";
    public static readonly (string Id, string Name)[] All =
    [
        (DpiShift, "DPI Shift (hold)"),
        (DpiUp, "DPI up"),
        (DpiDown, "DPI down"),
        (DpiCycle, "Cycle DPI"),
        (GShift, "G-Shift (hold)")
    ];
    public static string? NameFor(string? id) => All.FirstOrDefault(x => x.Id == id).Name;
}
