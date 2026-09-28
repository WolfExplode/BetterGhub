namespace BetterGhub.Input;

/// <summary>Friendly key names for macro steps, independent of any UI framework's key enum.</summary>
public static class VirtualKeys
{
    private static readonly Dictionary<string, ushort> ByName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<ushort, string> ByCode = [];
    private static readonly HashSet<ushort> Extended = [0xA3, 0xA5, 0x2D, 0x2E, 0x24, 0x23, 0x21, 0x22, 0x25, 0x26, 0x27, 0x28, 0x90, 0x6F, 0x2C, 0x5B, 0x5C, 0x5D];
    private static readonly HashSet<ushort> Modifiers = [0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C];

    static VirtualKeys()
    {
        // The first name is canonical; aliases include the WinForms Keys names older settings used.
        Add(0x11, "Ctrl", "Control", "ControlKey");
        Add(0x10, "Shift", "ShiftKey");
        Add(0x12, "Alt", "Menu");
        Add(0xA2, "LCtrl", "LControlKey", "LeftCtrl");
        Add(0xA3, "RCtrl", "RControlKey", "RightCtrl");
        Add(0xA0, "LShift", "LShiftKey", "LeftShift");
        Add(0xA1, "RShift", "RShiftKey", "RightShift");
        Add(0xA4, "LAlt", "LMenu", "LeftAlt");
        Add(0xA5, "RAlt", "RMenu", "RightAlt", "AltGr");
        Add(0x5B, "Win", "LWin", "Windows");
        Add(0x5C, "RWin");
        Add(0x5D, "Apps", "ContextMenu");
        Add(0x08, "Backspace", "Back");
        Add(0x09, "Tab");
        Add(0x0D, "Enter", "Return");
        Add(0x13, "Pause");
        Add(0x14, "CapsLock", "Capital");
        Add(0x1B, "Esc", "Escape");
        Add(0x20, "Space", "Spacebar");
        Add(0x21, "PageUp", "Prior", "PgUp");
        Add(0x22, "PageDown", "Next", "PgDn");
        Add(0x23, "End");
        Add(0x24, "Home");
        Add(0x25, "Left");
        Add(0x26, "Up");
        Add(0x27, "Right");
        Add(0x28, "Down");
        Add(0x2C, "PrintScreen", "Snapshot", "PrtSc");
        Add(0x2D, "Insert", "Ins");
        Add(0x2E, "Delete", "Del");
        for (int i = 0; i <= 9; i++) Add((ushort)(0x30 + i), i.ToString(), "D" + i);
        for (char c = 'A'; c <= 'Z'; c++) Add(c, c.ToString());
        for (int i = 0; i <= 9; i++) Add((ushort)(0x60 + i), "Num" + i, "NumPad" + i);
        Add(0x6A, "NumMultiply", "Multiply");
        Add(0x6B, "NumAdd", "Add");
        Add(0x6D, "NumSubtract", "Subtract");
        Add(0x6E, "NumDecimal", "Decimal");
        Add(0x6F, "NumDivide", "Divide");
        for (int i = 1; i <= 24; i++) Add((ushort)(0x6F + i), "F" + i);
        Add(0x90, "NumLock");
        Add(0x91, "ScrollLock", "Scroll");
        Add(0xAD, "Mute", "VolumeMute");
        Add(0xAE, "VolumeDown");
        Add(0xAF, "VolumeUp");
        Add(0xB0, "NextTrack", "MediaNextTrack");
        Add(0xB1, "PreviousTrack", "MediaPreviousTrack", "PrevTrack");
        Add(0xB2, "StopMedia", "MediaStop");
        Add(0xB3, "PlayPause", "MediaPlayPause");
        Add(0xBA, ";", "Oem1", "OemSemicolon", "Semicolon");
        Add(0xBB, "=", "OemPlus", "Equals");
        Add(0xBC, ",", "OemComma", "Comma");
        Add(0xBD, "-", "OemMinus", "Minus");
        Add(0xBE, ".", "OemPeriod", "Period");
        Add(0xBF, "/", "Oem2", "OemQuestion", "Slash");
        Add(0xC0, "`", "Oem3", "OemTilde", "Grave");
        Add(0xDB, "[", "Oem4", "OemOpenBrackets");
        Add(0xDC, "\\", "Oem5", "OemPipe", "Backslash");
        Add(0xDD, "]", "Oem6", "OemCloseBrackets");
        Add(0xDE, "'", "Oem7", "OemQuotes", "Quote");
        Add(0xE2, "Oem102", "OemBackslash");
    }

    private static void Add(ushort code, string name, params string[] aliases)
    {
        ByCode.TryAdd(code, name);
        ByName[name] = code;
        foreach (string alias in aliases) ByName.TryAdd(alias, code);
    }

    public static bool TryParse(string name, out ushort code) => ByName.TryGetValue(name.Trim(), out code);

    public static ushort Parse(string name) =>
        TryParse(name, out ushort code) ? code : throw new ArgumentException($"Unknown key: {name}");

    public static string NameOf(ushort code) => ByCode.GetValueOrDefault(code) ?? $"VK{code:X2}";
    public static bool IsExtended(ushort code) => Extended.Contains(code);
    public static bool IsModifier(ushort code) => Modifiers.Contains(code);

    /// <summary>Validates a combo such as "Ctrl+Alt+T". Returns the canonical form or null.</summary>
    public static string? NormalizeCombo(string combo)
    {
        string[] parts = combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        List<string> names = [];
        foreach (string part in parts)
        {
            if (!TryParse(part, out ushort code)) return null;
            names.Add(NameOf(code));
        }
        return string.Join("+", names);
    }
}
