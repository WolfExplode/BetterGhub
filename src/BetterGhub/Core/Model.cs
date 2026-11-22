using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterGhub.Core;

public enum MacroMode { Once, WhileHeld, Toggle, Sequence }

// Append new kinds at the end: settings store them by name, but keep the order stable anyway.
public enum ActionKind { Key, KeyDown, KeyUp, Text, Delay, LeftClick, RightClick, MiddleClick, Wheel, VolumeUp, VolumeDown, Mute, Launch, PlayPause, NextTrack, PreviousTrack, MouseDown, MouseUp }

public sealed class MacroStep
{
    public ActionKind Kind { get; set; } = ActionKind.Key;
    public string Value { get; set; } = "";
    public int DelayMs { get; set; } = 50;
    /// <summary>When above <see cref="DelayMs"/>, a Delay step waits a random time from DelayMs to this, rolled on every run.</summary>
    public int? DelayMaxMs { get; set; }

    /// <summary>The buttons a MouseDown or MouseUp step can hold, by the index <see cref="Input.InputSender.MouseButton"/> takes.</summary>
    public static readonly string[] MouseButtons = ["Left", "Right", "Middle", "Back", "Forward"];

    /// <summary>A MouseDown or MouseUp step's button as an index into <see cref="MouseButtons"/>; Left when unknown.</summary>
    public int MouseButton => Math.Max(0, Array.IndexOf(MouseButtons, Value));

    public MacroStep Clone() => new() { Kind = Kind, Value = Value, DelayMs = DelayMs, DelayMaxMs = DelayMaxMs };
    public override string ToString() => Kind switch
    {
        ActionKind.Delay => DelayMaxMs > DelayMs ? $"Delay {DelayMs}-{DelayMaxMs} ms" : $"Delay {DelayMs} ms",
        ActionKind.Key or ActionKind.KeyDown or ActionKind.KeyUp or ActionKind.Text or ActionKind.Launch or ActionKind.Wheel or ActionKind.MouseDown or ActionKind.MouseUp => $"{Kind}: {Value}",
        _ => Kind.ToString()
    };
}

public static class Delays
{
    public const int Max = 60000;

    /// <summary>A delay in milliseconds: <paramref name="min"/>, or a random time up to <paramref name="max"/> when that is larger.</summary>
    public static int Roll(int min, int? max)
    {
        min = Math.Clamp(min, 0, Max);
        return max is int top && top > min ? Random.Shared.Next(min, Math.Min(top, Max) + 1) : min;
    }

    /// <summary>The middle of a range, for the mouse's on-board memory, which can only store fixed delays.</summary>
    public static int Middle(int min, int? max)
    {
        min = Math.Clamp(min, 0, Max);
        return max is int top && top > min ? (min + Math.Min(top, Max)) / 2 : min;
    }
}

public sealed class MacroDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New macro";
    public MacroMode Mode { get; set; }
    public List<MacroStep> Steps { get; set; } = [];
    /// <summary>When set, recorded Delay steps are ignored and this delay separates every action.</summary>
    public int? StandardDelayMs { get; set; }
    /// <summary>When above <see cref="StandardDelayMs"/>, each gap is a random time from the standard delay to this.</summary>
    public int? StandardDelayMaxMs { get; set; }
    public override string ToString() => Name;
}

public sealed class MouseProfile
{
    private readonly List<int> defaultDpiStages = [200, 800, 1600, 3000, 6400];

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Default";
    public string ApplicationPath { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentId { get; set; }
    [JsonIgnore] public MouseProfile? Parent { get; private set; }

    [JsonPropertyName("Dpi"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LocalDpi { get; set; }
    [JsonPropertyName("DpiStages"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<int>? LocalDpiStages { get; set; }
    [JsonPropertyName("ShiftDpi"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LocalShiftDpi { get; set; }
    [JsonPropertyName("ReportRate"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LocalReportRate { get; set; }
    [JsonIgnore] public int Dpi { get => LocalDpi ?? Parent?.Dpi ?? 1600; set => LocalDpi = value; }
    [JsonIgnore] public List<int> DpiStages { get => LocalDpiStages ?? Parent?.DpiStages ?? defaultDpiStages; set => LocalDpiStages = value; }
    [JsonIgnore] public int ShiftDpi { get => LocalShiftDpi ?? Parent?.ShiftDpi ?? 100; set => LocalShiftDpi = value; }
    [JsonIgnore] public int ReportRate { get => LocalReportRate ?? Parent?.ReportRate ?? 1000; set => LocalReportRate = value; }

    /// <summary>Local bindings by HID bit. An empty value in a child masks the inherited binding.</summary>
    public Dictionary<int, string> Assignments { get; set; } = [];
    public Dictionary<int, string> ShiftAssignments { get; set; } = [];
    public List<ComboSlot> ComboSlots { get; set; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? RemovedCombos { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ComboOrder { get; set; }
    /// <summary>Buttons that act when tapped (on release) rather than when pressed, so holding them for a combo doesn't also run their action.</summary>
    public List<int> TapBits { get; set; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<int>? PressBits { get; set; }

    [JsonIgnore] public IEnumerable<ComboSlot> Combos => Parent is null ? ComboSlots : MergeCombos(Parent.Combos);
    [JsonIgnore] public IEnumerable<ComboSlot> CombosInDisplayOrder
    {
        get
        {
            IEnumerable<ComboSlot> ordered = Parent is null
                ? ComboSlots.OrderBy(combo => combo.DisplayOrder ?? int.MaxValue)
                : MergeCombos(Parent.CombosInDisplayOrder);
            if (ComboOrder is not { } order) return ordered;
            return ordered.OrderBy(combo => order.IndexOf(combo.Id) is int index && index >= 0 ? index : int.MaxValue);
        }
    }

    private IEnumerable<ComboSlot> MergeCombos(IEnumerable<ComboSlot> inherited)
    {
        HashSet<string> inheritedIds = [];
        foreach (ComboSlot combo in inherited)
        {
            inheritedIds.Add(combo.Id);
            if (RemovedCombos?.Contains(combo.Id) != true)
                yield return ComboSlots.Find(local => local.Id == combo.Id) ?? combo;
        }
        foreach (ComboSlot combo in ComboSlots)
            if (!inheritedIds.Contains(combo.Id)) yield return combo;
    }

    internal ComboSlot EditCombo(ComboSlot combo)
    {
        if (ComboSlots.Find(local => local.Id == combo.Id) is { } local) return local;
        local = combo.Clone();
        ComboSlots.Add(local);
        return local;
    }

    internal void RemoveCombo(ComboSlot combo)
    {
        ComboSlots.RemoveAll(local => local.Id == combo.Id);
        ComboOrder?.Remove(combo.Id);
        if (Parent?.Combos.Any(inherited => inherited.Id == combo.Id) == true)
            (RemovedCombos ??= []).Add(combo.Id);
    }

    internal Dictionary<int, string> LocalBindings(bool shift) => shift ? ShiftAssignments : Assignments;

    internal string? Binding(bool shift, int bit)
    {
        if (!LocalBindings(shift).TryGetValue(bit, out string? local)) return Parent?.Binding(shift, bit);
        return Parent is not null && local.Length == 0 ? null : local;
    }

    internal Dictionary<int, string> Bindings(bool shift)
    {
        Dictionary<int, string> local = LocalBindings(shift);
        if (Parent is null) return new(local);
        Dictionary<int, string> effective = Parent.Bindings(shift);
        foreach ((int bit, string action) in local)
            if (action.Length == 0) effective.Remove(bit); else effective[bit] = action;
        return effective;
    }

    internal bool IsTap(int bit) => TapBits.Contains(bit) || (PressBits?.Contains(bit) != true && Parent?.IsTap(bit) == true);

    internal void SetTap(int bit, bool tap)
    {
        if (tap)
        {
            PressBits?.Remove(bit);
            TapBits.Add(bit);
        }
        else
        {
            TapBits.Remove(bit);
            if (Parent is not null) (PressBits ??= []).Add(bit);
        }
    }

    private IEnumerable<int> EffectiveTapBits() => Parent is null
        ? TapBits
        : Parent.EffectiveTapBits().Where(bit => PressBits?.Contains(bit) != true).Union(TapBits);

    internal void SetParent(MouseProfile? parent)
    {
        Parent = parent;
        ParentId = parent?.Id;
    }

    internal void ClearOverrides()
    {
        LocalDpi = LocalShiftDpi = LocalReportRate = null;
        LocalDpiStages = null;
        Assignments.Clear();
        ShiftAssignments.Clear();
        ComboSlots.Clear();
        TapBits.Clear();
        RemovedCombos = ComboOrder = null;
        PressBits = null;
    }

    /// <summary>Copies the effective settings and makes this profile independent.</summary>
    internal void CopySettingsFrom(MouseProfile source)
    {
        Assignments = source.Bindings(false);
        ShiftAssignments = source.Bindings(true);
        List<ComboSlot> displayOrder = [.. source.CombosInDisplayOrder];
        ComboSlots = [.. source.Combos.Select(combo => combo.Clone())];
        foreach (ComboSlot combo in ComboSlots)
            combo.DisplayOrder = displayOrder.FindIndex(item => item.Id == combo.Id);
        TapBits = [.. source.EffectiveTapBits()];
        Dpi = source.Dpi;
        DpiStages = [.. source.DpiStages];
        ShiftDpi = source.ShiftDpi;
        ReportRate = source.ReportRate;
        SetParent(null);
        RemovedCombos = ComboOrder = null;
        PressBits = null;
    }

    [JsonIgnore] public bool IsDesktop => string.IsNullOrWhiteSpace(ApplicationPath);
    public override string ToString() => Name;
}

/// <summary>
/// Two or more buttons pressed together. The action runs when the last of them goes down, in place of that
/// button's own action, and is held (keys, clicks, macros) until that button is released.
/// </summary>
public sealed class ComboSlot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>Position in the editor. Unordered combos appear last, in their original order.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DisplayOrder { get; set; }
    /// <summary>HID bits of the buttons: the last one is pressed, the others are held down first.</summary>
    public List<int> Bits { get; set; } = [];
    /// <summary>Every button is held together (for <see cref="Services.MouseService.ComboHoldMs"/>) instead of the last one being pressed.</summary>
    public bool HoldAll { get; set; }
    [JsonIgnore] public int PressBit => HoldAll || Bits.Count == 0 ? -1 : Bits[^1];
    [JsonIgnore] public IEnumerable<int> HoldBits => HoldAll ? Bits : Bits.Take(Math.Max(0, Bits.Count - 1));
    /// <summary>An assignment id like a button's: macro, key, launch or built-in action; empty until chosen.</summary>
    public string Action { get; set; } = "";

    internal ComboSlot Clone() => new() { Id = Id, DisplayOrder = DisplayOrder, Bits = [.. Bits], HoldAll = HoldAll, Action = Action };
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
    public bool AutoConnect { get; set; } = true;
    public bool AutoSwitchProfiles { get; set; } = true;
    /// <summary>On-board slot (its sector) the mouse runs from, edited through the normal pages; null for host mode.</summary>
    public int? OnboardSector { get; set; }
    /// <summary>On-board slot the mouse last ran from; it's handed back to this one when BetterGhub disconnects or quits.</summary>
    public int? LastOnboardSector { get; set; }
    /// <summary>Closing the window keeps BetterGhub running in the notification area.</summary>
    public bool CloseToTray { get; set; } = true;
    public bool TrayHintShown { get; set; }
    /// <summary>Start elevated so macros and buttons also reach apps and games that run as administrator.</summary>
    public bool RunAsAdmin { get; set; }
    /// <summary>The main window opens maximized, as it was last left; otherwise at its last normal size.</summary>
    public bool WindowMaximized { get; set; } = true;
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }

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

    /// <summary>Just the <see cref="RunAsAdmin"/> choice, read before anything else starts; false when unreadable.</summary>
    public static bool PeekRunAsAdmin()
    {
        try { return File.Exists(FilePath) && Read(FilePath).RunAsAdmin; }
        catch (Exception) { return false; }
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
            if (profile.LocalDpiStages is not { } stages) continue;
            profile.DpiStages = stages.Where(x => x is >= 100 and <= 25600).Distinct().Order().ToList();
        }
        RestoreProfileLinks();
        foreach (MouseProfile profile in Profiles.Where(p => p.LocalDpiStages is { Count: 0 }))
            profile.DpiStages = [Math.Clamp(profile.Dpi, 100, 25600)];
    }

    internal void RestoreProfileLinks()
    {
        Dictionary<string, MouseProfile> byId = [];
        foreach (MouseProfile profile in Profiles) byId.TryAdd(profile.Id, profile);
        foreach (MouseProfile profile in Profiles)
        {
            MouseProfile? parent = profile.ParentId is { } id ? byId.GetValueOrDefault(id) : null;
            HashSet<MouseProfile> visited = [profile];
            for (MouseProfile? ancestor = parent; ancestor is not null; ancestor = ancestor.ParentId is { } next ? byId.GetValueOrDefault(next) : null)
            {
                if (visited.Add(ancestor)) continue;
                parent = null;
                break;
            }
            profile.SetParent(parent);
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
        string.IsNullOrEmpty(id) ? ""
            : id.Contains(Assignments.Separator) ? string.Join(" + ", Assignments.Split(id).Select(DescribeAssignment))
            : Macros.FirstOrDefault(m => m.Id == id)?.Name ?? BuiltinActions.NameFor(id) ?? Assignments.NameFor(id) ?? "Missing macro";

    public bool IsMacro(string? id) => id is not null && Macros.Any(m => m.Id == id);
}

public static class BuiltinActions
{
    public const string DpiShift = "__dpi_shift__";
    public const string DpiUp = "__dpi_up__";
    public const string DpiDown = "__dpi_down__";
    public const string DpiCycle = "__dpi_cycle__";
    public const string GShift = "__g_shift__";
    public const string Disabled = "__disabled__";
    public const string LeftClick = "__left_click__";
    public const string RightClick = "__right_click__";
    public const string MiddleClick = "__middle_click__";
    public const string Back = "__back__";
    public const string Forward = "__forward__";
    public const string DoubleClick = "__double_click__";
    public const string ScrollUp = "__scroll_up__";
    public const string ScrollDown = "__scroll_down__";
    public const string ScrollLeft = "__scroll_left__";
    public const string ScrollRight = "__scroll_right__";
    public const string LockScreen = "__lock_screen__";

    /// <summary>The System tab, in display order. Media and OS entries are plain key assignments with friendly names.</summary>
    public static readonly (string Id, string Name, string Description, string Category)[] All =
    [
        (LeftClick, "Primary Click", "Left mouse button", "Mouse"),
        (RightClick, "Secondary Click", "Right mouse button", "Mouse"),
        (MiddleClick, "Middle Click", "Middle mouse button", "Mouse"),
        (Back, "Back", "Mouse back button", "Mouse"),
        (Forward, "Forward", "Mouse forward button", "Mouse"),
        (DoubleClick, "Double Click", "Two left clicks", "Mouse"),
        (ScrollUp, "Scroll Up", "One wheel notch up", "Mouse"),
        (ScrollDown, "Scroll Down", "One wheel notch down", "Mouse"),
        (ScrollLeft, "Scroll Left", "One notch left", "Mouse"),
        (ScrollRight, "Scroll Right", "One notch right", "Mouse"),
        (DpiUp, "DPI Up", "Next higher DPI speed", "Mouse"),
        (DpiDown, "DPI Down", "Next lower DPI speed", "Mouse"),
        (DpiCycle, "DPI Cycle", "Step through DPI speeds", "Mouse"),
        (DpiShift, "DPI Shift", "Hold for the DPI Shift speed", "Mouse"),
        (GShift, "G-Shift", "Hold to use the G-Shift layer", "Device"),
        (Disabled, "Do nothing", "Disable the button", "Device"),
        (Assignments.KeyId("VolumeUp"), "Volume Up", "", "Media"),
        (Assignments.KeyId("VolumeDown"), "Volume Down", "", "Media"),
        (Assignments.KeyId("NextTrack"), "Next Track", "", "Media"),
        (Assignments.KeyId("PreviousTrack"), "Previous Track", "", "Media"),
        (Assignments.KeyId("PlayPause"), "Play/Pause", "", "Media"),
        (Assignments.KeyId("StopMedia"), "Stop", "", "Media"),
        (Assignments.KeyId("Mute"), "Volume Mute", "", "Media"),
        (LockScreen, "Lock screen", "Lock the PC", "OS"),
        (Assignments.KeyId("Ctrl+Z"), "Undo", "Ctrl + Z", "OS"),
        (Assignments.KeyId("Ctrl+X"), "Cut", "Ctrl + X", "OS"),
        (Assignments.KeyId("Ctrl+C"), "Copy", "Ctrl + C", "OS"),
        (Assignments.KeyId("Ctrl+V"), "Paste", "Ctrl + V", "OS"),
        (Assignments.KeyId("Win+Shift+S"), "Screenshot", "Win + Shift + S", "OS"),
    ];

    public static string? NameFor(string? id) => All.FirstOrDefault(x => x.Id == id).Name;
    /// <summary>Actions BetterGhub carries out itself, as opposed to macros, keys and launches.</summary>
    public static bool IsBuiltin(string? id) => id is not null && id.StartsWith("__", StringComparison.Ordinal) && All.Any(x => x.Id == id);
}
