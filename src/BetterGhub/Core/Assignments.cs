using BetterGhub.Input;

namespace BetterGhub.Core;

/// <summary>
/// Assignment ids beyond macros and <see cref="BuiltinActions"/>: "key:Ctrl+Z" holds a key or shortcut
/// while the button is held, "launch:C:\path\app.exe" starts an application.
/// </summary>
public static class Assignments
{
    public const string KeyPrefix = "key:";
    public const string LaunchPrefix = "launch:";
    /// <summary>"onboard:900A0000": an on-board binding BetterGhub has no action for, kept as its stored bytes.</summary>
    public const string OnboardPrefix = "onboard:";

    public static string KeyId(string combo) => KeyPrefix + combo;
    public static string LaunchId(string path) => LaunchPrefix + path;

    public static string? KeyCombo(string? id) =>
        id is not null && id.StartsWith(KeyPrefix, StringComparison.Ordinal) ? id[KeyPrefix.Length..] : null;

    public static string? LaunchPath(string? id) =>
        id is not null && id.StartsWith(LaunchPrefix, StringComparison.Ordinal) ? id[LaunchPrefix.Length..] : null;

    /// <summary>Friendly name for a key or launch assignment, or null for anything else.</summary>
    public static string? NameFor(string id)
    {
        if (KeyCombo(id) is { } combo)
            return Commands.FirstOrDefault(c => c.Combo == combo).Name
                ?? SystemCommandName(combo)
                ?? DisplayCombo(combo);
        if (LaunchPath(id) is { } path) return "Launch " + Path.GetFileNameWithoutExtension(path);
        if (id.StartsWith(OnboardPrefix, StringComparison.Ordinal) && uint.TryParse(id[OnboardPrefix.Length..], System.Globalization.NumberStyles.HexNumber, null, out uint binding))
            return OnboardProfiles.Describe(binding);
        return null;
    }

    private static string? SystemCommandName(string combo) =>
        BuiltinActions.All.FirstOrDefault(a => a.Id == KeyId(combo)).Name;

    // ── Commands ──────────────────────────────────────────────────────────────

    public static readonly (string Category, string Combo, string Name)[] Commands =
    [
        ("Editing", "Ctrl+Y", "Redo"),
        ("Editing", "Ctrl+Z", "Undo"),
        ("Editing", "Ctrl+N", "New"),
        ("Editing", "Ctrl+W", "Close tab"),
        ("Editing", "Ctrl+-", "Zoom Out"),
        ("Editing", "Ctrl+A", "Select All"),
        ("Editing", "Ctrl+X", "Cut"),
        ("Editing", "Ctrl+V", "Paste"),
        ("Editing", "Ctrl+0", "Zoom Reset"),
        ("Editing", "Ctrl+C", "Copy"),
        ("Editing", "Ctrl+S", "Save"),
        ("Editing", "Ctrl+=", "Zoom In"),
        ("Editing", "Ctrl+O", "Open"),
        ("Editing", "Ctrl+T", "New tab"),
        ("Editing", "Ctrl+Shift+T", "Reopen closed tab"),
        ("Editing", "Ctrl+F", "Find"),
        ("Editing", "Ctrl+P", "Print"),
        ("Windows", "Win+S", "Open Search"),
        ("Windows", "Win+K", "Open Connect Quick Action"),
        ("Windows", "Win+X", "Open Quick Links"),
        ("Windows", "Win+=", "Open Magnifier"),
        ("Windows", "Win+I", "Open Windows Settings"),
        ("Windows", "Win+U", "Open Ease of Access Center"),
        ("Windows", "Win+D", "Hide/Show Desktop"),
        ("Windows", "Win+Tab", "Open Task View"),
        ("Windows", "Win+B", "Set Focus in Notification Area"),
        ("Windows", "Win+R", "Run dialog"),
        ("Windows", "Win+A", "Open Action Center"),
        ("Windows", "Win+E", "Open File Explorer"),
        ("Windows", "Win+V", "Clipboard History"),
        ("Windows", "Win+Shift+S", "Screen Snip"),
        ("Windows", "Win+C", "Open Copilot"),
        ("Navigation", "Alt+Right", "Go Forward"),
        ("Navigation", "Alt+Left", "Go Back"),
        ("Navigation", "Ctrl+Tab", "Next tab"),
        ("Navigation", "Ctrl+Shift+Tab", "Previous tab"),
        ("Productivity", "Alt+F4", "Exit Active App"),
        ("Productivity", "Alt+Esc", "Cycle Through Apps"),
        ("Productivity", "Alt+Tab", "Switch Between Apps"),
        ("Productivity", "Ctrl+Shift+Esc", "Open Task Manager"),
        ("Productivity", "Ctrl+Esc", "Open Start"),
    ];

    // ── Keys ──────────────────────────────────────────────────────────────────

    /// <summary>Every single key the Keys tab offers, in G HUB order: symbols, digits, letters, then named keys A–Z.</summary>
    public static readonly IReadOnlyList<(string Key, string Name)> Keys = BuildKeys();

    private static List<(string, string)> BuildKeys()
    {
        string[] symbols = ["-", ",", ";", ".", "'", "[", "]", "/", "\\", "`", "="];
        List<(string, string)> keys = [.. symbols.Select(s => (s, s))];
        for (char c = '0'; c <= '9'; c++) keys.Add((c.ToString(), c.ToString()));
        for (char c = 'A'; c <= 'Z'; c++) keys.Add((c.ToString(), c.ToString()));
        List<string> named =
        [
            "Alt", "LAlt", "RAlt", "Ctrl", "LCtrl", "RCtrl", "Shift", "LShift", "RShift", "Win", "RWin", "Apps",
            "Backspace", "CapsLock", "Delete", "Down", "End", "Enter", "Esc", "Home", "Insert", "Left", "Right", "Up",
            "PageDown", "PageUp", "Pause", "PrintScreen", "ScrollLock", "NumLock", "Space", "Tab",
            "NumMultiply", "NumAdd", "NumSubtract", "NumDecimal", "NumDivide", "Oem102",
        ];
        for (int i = 1; i <= 24; i++) named.Add("F" + i);
        for (int i = 0; i <= 9; i++) named.Add("Num" + i);
        keys.AddRange(named.Select(k => (k, KeyName(k))).OrderBy(k => k.Item2, NaturalOrder.Instance));
        return keys;
    }

    /// <summary>G HUB style name for one key: "Right Alt", "Num 8", "Page Down".</summary>
    public static string KeyName(string key) => key switch
    {
        "LAlt" => "Left Alt", "RAlt" => "Right Alt",
        "LCtrl" => "Left Ctrl", "RCtrl" => "Right Ctrl",
        "LShift" => "Left Shift", "RShift" => "Right Shift",
        "Win" => "Left Windows", "RWin" => "Right Windows",
        "Apps" => "Menu",
        "CapsLock" => "Caps Lock", "ScrollLock" => "Scroll Lock", "NumLock" => "Num Lock",
        "PageDown" => "Page Down", "PageUp" => "Page Up", "PrintScreen" => "Print Screen",
        "NumMultiply" => "Num *", "NumAdd" => "Num +", "NumSubtract" => "Num -", "NumDecimal" => "Num Del", "NumDivide" => "Num /",
        "Oem102" => "\\ (ISO)",
        "VolumeUp" => "Volume Up", "VolumeDown" => "Volume Down", "Mute" => "Volume Mute",
        "NextTrack" => "Next Track", "PreviousTrack" => "Previous Track", "PlayPause" => "Play/Pause", "StopMedia" => "Stop",
        _ when key.Length > 3 && key.StartsWith("Num", StringComparison.Ordinal) && char.IsDigit(key[3]) => "Num " + key[3..],
        _ => key
    };

    /// <summary>"Ctrl + Shift + T" style display of a stored combo.</summary>
    public static string DisplayCombo(string combo) =>
        string.Join(" + ", combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(k => VirtualKeys.TryParse(k, out ushort code) ? KeyName(VirtualKeys.NameOf(code)) : k));

    /// <summary>Orders "F2" before "F10" and "Num 2" before "Num 10".</summary>
    private sealed class NaturalOrder : IComparer<string>
    {
        public static readonly NaturalOrder Instance = new();
        public int Compare(string? x, string? y)
        {
            (string xs, int xn) = Split(x ?? "");
            (string ys, int yn) = Split(y ?? "");
            int text = string.Compare(xs, ys, StringComparison.OrdinalIgnoreCase);
            return text != 0 ? text : xn.CompareTo(yn);
        }
        private static (string, int) Split(string value)
        {
            int end = value.Length;
            while (end > 0 && char.IsDigit(value[end - 1])) end--;
            return end < value.Length && end > 0 ? (value[..end], int.Parse(value[end..])) : (value, -1);
        }
    }
}
