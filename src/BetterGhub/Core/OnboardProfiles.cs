using System.Text;

namespace BetterGhub.Core;

/// <summary>One of the mouse's on-board profile slots, from the 0x8100 directory in sector 0.</summary>
public sealed record OnboardSlot(int Number, int Sector, bool Enabled, OnboardProfile? Profile)
{
    /// <summary>The stored name, or G HUB's "Profile N" for a slot saved without one.</summary>
    public string DisplayName => Profile is { Name.Length: > 0 } profile ? profile.Name : $"Profile {Number}";
}

/// <summary>What the mouse stores for a slot: used in onboard mode, when BetterGhub isn't driving it.</summary>
public sealed record OnboardProfile(
    string Name,
    int ReportIntervalMs,
    IReadOnlyList<int> Dpis,
    int DefaultDpiIndex,
    int ShiftDpiIndex,
    IReadOnlyList<OnboardBinding> Buttons,
    IReadOnlyList<OnboardBinding> ShiftButtons,
    bool ChecksumOk,
    byte[] Sector);

/// <summary>A decoded button binding; <see cref="Raw"/> is the 4 stored bytes in hex.</summary>
public sealed record OnboardBinding(int Index, string Description, string Raw);

/// <summary>Everything read from on-board memory, with the raw directory kept for writing it back.</summary>
/// <remarks>
/// <see cref="SpareSectors"/> holds every other user sector (where macros live), read in full, or all 0xFF when
/// its first 16 bytes were erased.
/// </remarks>
public sealed record OnboardMemory(IReadOnlyList<OnboardSlot> Slots, byte[] Directory, int SectorSize, int ButtonCount, IReadOnlyDictionary<int, byte[]> SpareSectors)
{
    /// <summary>A user sector's contents as last read: the directory, a slot, or a spare sector.</summary>
    public byte[]? SectorData(int sector) =>
        sector == 0 ? Directory : Slots.FirstOrDefault(s => s.Sector == sector)?.Profile?.Sector ?? SpareSectors.GetValueOrDefault(sector);
}

/// <summary>A change to one slot. Bindings map binding index to the 4 stored bytes (big-endian).</summary>
public sealed record OnboardEdit(
    string Name,
    bool Enabled,
    int ReportIntervalMs,
    IReadOnlyList<int> Dpis,
    int DefaultDpiIndex,
    int ShiftDpiIndex,
    IReadOnlyDictionary<int, uint> Buttons,
    IReadOnlyDictionary<int, uint> ShiftButtons,
    IReadOnlyDictionary<int, byte[]>? MacroButtons = null,
    IReadOnlyDictionary<int, byte[]>? MacroShiftButtons = null);
// MacroButtons: on-board macros (see OnboardMacros) to store and bind, by binding index; they override Buttons.

/// <summary>
/// HID++ 0x8100 profile format 3 (G502 X): report rate, default and shift DPI index, five DPI levels,
/// 16 button and 16 G-shift bindings, a UTF-16 name, and a CRC-CCITT over the rest of the sector.
/// </summary>
public static class OnboardProfiles
{
    private const int ButtonsOffset = 32, ShiftButtonsOffset = 96, NameOffset = 160, NameBytes = 48;
    public const int MaxNameLength = NameBytes / 2;

    /// <summary>User slots live in sectors 1–0xFF; 0x01xx are the read-only factory profiles.</summary>
    public static bool IsUserSector(int sector) => sector is > 0 and < 0x100;

    /// <summary>
    /// A copy of <paramref name="original"/> with the edit applied and a fresh checksum. Bytes the edit
    /// doesn't cover (lighting, power timeouts, reserved) are kept as the mouse had them.
    /// </summary>
    public static byte[] Build(byte[] original, OnboardEdit edit)
    {
        if (edit.Dpis.Count is < 1 or > 5) throw new ArgumentException("Use between one and five DPI speeds");
        if (edit.DefaultDpiIndex >= edit.Dpis.Count || edit.ShiftDpiIndex >= edit.Dpis.Count) throw new ArgumentException("Default and DPI Shift must be one of the speeds");
        if (edit.ReportIntervalMs is not (1 or 2 or 4 or 8)) throw new ArgumentException("Report rate must be 125, 250, 500 or 1000 Hz");
        byte[] sector = (byte[])original.Clone();
        sector[0] = (byte)edit.ReportIntervalMs;
        sector[1] = (byte)edit.DefaultDpiIndex;
        sector[2] = (byte)edit.ShiftDpiIndex;
        for (int i = 0; i < 5; i++)
        {
            int dpi = i < edit.Dpis.Count ? edit.Dpis[i] : 0;
            if (i < edit.Dpis.Count && dpi is < 100 or > 25600) throw new ArgumentException("DPI speeds must be 100–25600");
            sector[3 + i * 2] = (byte)dpi;
            sector[4 + i * 2] = (byte)(dpi >> 8);
        }
        foreach ((int index, uint value) in edit.Buttons) PutBinding(sector, ButtonsOffset, index, value);
        foreach ((int index, uint value) in edit.ShiftButtons) PutBinding(sector, ShiftButtonsOffset, index, value);
        string name = edit.Name.Trim();
        if (name.Length > MaxNameLength) name = name[..MaxNameLength];
        Array.Clear(sector, NameOffset, NameBytes);
        Encoding.Unicode.GetBytes(name, 0, name.Length, sector, NameOffset);
        return WithChecksum(sector);
    }

    /// <summary>The directory with one slot's enabled flag changed, and a fresh checksum.</summary>
    public static byte[] SetEnabled(byte[] directory, int sector, bool enabled)
    {
        byte[] copy = (byte[])directory.Clone();
        for (int offset = 0; offset + 3 < copy.Length; offset += 4)
        {
            int entry = (copy[offset] << 8) | copy[offset + 1];
            if (entry == 0xFFFF) break;
            if (entry != sector) continue;
            copy[offset + 2] = (byte)(enabled ? 1 : 0);
            return WithChecksum(copy);
        }
        throw new ArgumentException($"Sector {sector} is not in the profile directory");
    }

    public static byte[] WithChecksum(byte[] sector)
    {
        ushort crc = Checksum(sector);
        sector[^2] = (byte)(crc >> 8);
        sector[^1] = (byte)crc;
        return sector;
    }

    private static void PutBinding(byte[] sector, int offset, int index, uint value)
    {
        if (index is < 0 or >= 16) throw new ArgumentOutOfRangeException(nameof(index));
        int at = offset + index * 4;
        for (int b = 0; b < 4; b++) sector[at + b] = (byte)(value >> (24 - b * 8));
    }

    /// <summary>Binding index for a BetterGhub control id; the scroll wheel is left out since its slots are only inferred.</summary>
    public static int? IndexFor(string controlId) => controlId switch
    {
        "G1" => 0,
        "G2" => 1,
        "G3" => 2,
        "G4" => 3,
        "G6" => 4,
        "G5" => 5,
        "TiltLeft" => 6,
        "TiltRight" => 7,
        "G9" => 8,
        "G8" => 9,
        "G7" => 10,
        _ => null
    };

    /// <summary>What a control does on the factory profile, for BetterGhub buttons left unassigned.</summary>
    public static string NativeAction(MouseControl control) => control.SoftwareDefault ?? control.Id switch
    {
        "G1" => BuiltinActions.LeftClick,
        "G2" => BuiltinActions.RightClick,
        "G3" => BuiltinActions.MiddleClick,
        "G4" => BuiltinActions.Back,
        "G5" => BuiltinActions.Forward,
        "TiltLeft" => BuiltinActions.ScrollLeft,
        "TiltRight" => BuiltinActions.ScrollRight,
        _ => BuiltinActions.Disabled
    };

    /// <summary>The stored binding for a button (or its G-shift layer), or null when the entry is unused.</summary>
    public static uint? BindingAt(byte[] sector, int index, bool shift)
    {
        int at = (shift ? ShiftButtonsOffset : ButtonsOffset) + index * 4;
        if (sector[at] == 0xFF && sector[at + 1] == 0xFF) return null;
        return (uint)((sector[at] << 24) | (sector[at + 1] << 16) | (sector[at + 2] << 8) | sector[at + 3]);
    }

    /// <summary>
    /// A stored binding as a BetterGhub assignment id: the inverse of <see cref="Encode"/>. Anything BetterGhub
    /// has no action for (profile cycling, battery check, macros) comes back as "onboard:XXXXXXXX" so it round-trips.
    /// </summary>
    public static string Decode(uint binding)
    {
        byte type = (byte)(binding >> 24), kind = (byte)(binding >> 16), high = (byte)(binding >> 8), low = (byte)binding;
        string? id = (type, kind) switch
        {
            (0x80, 0x01) => ((high << 8) | low) switch
            {
                0x0001 => BuiltinActions.LeftClick,
                0x0002 => BuiltinActions.RightClick,
                0x0004 => BuiltinActions.MiddleClick,
                0x0008 => BuiltinActions.Back,
                0x0010 => BuiltinActions.Forward,
                _ => null
            },
            (0x80, 0x02) => KeyCombo(high, low) is { } combo ? Assignments.KeyId(combo) : null,
            (0x80, 0x03) => (high == 0x0C ? low : (high << 8) | low) switch
            {
                0xE9 => Assignments.KeyId("VolumeUp"),
                0xEA => Assignments.KeyId("VolumeDown"),
                0xE2 => Assignments.KeyId("Mute"),
                0xCD => Assignments.KeyId("PlayPause"),
                0xB5 => Assignments.KeyId("NextTrack"),
                0xB6 => Assignments.KeyId("PreviousTrack"),
                0xB7 => Assignments.KeyId("StopMedia"),
                _ => null
            },
            (0x90, 0x00) or (0x80, 0x00) or (0xFF, _) => BuiltinActions.Disabled,
            (0x90, 0x01) => BuiltinActions.ScrollLeft,
            (0x90, 0x02) => BuiltinActions.ScrollRight,
            (0x90, 0x03) => BuiltinActions.DpiUp,
            (0x90, 0x04) => BuiltinActions.DpiDown,
            (0x90, 0x05) => BuiltinActions.DpiCycle,
            (0x90, 0x07) => BuiltinActions.DpiShift,
            (0x90, 0x0B) => BuiltinActions.GShift,
            _ => null
        };
        return id ?? Assignments.OnboardPrefix + binding.ToString("X8");
    }

    /// <summary>Modifier bits and a HID usage as a BetterGhub combo ("Ctrl+Shift+T"), or null for an unknown key.</summary>
    private static string? KeyCombo(byte modifiers, byte usage)
    {
        string[] names = ["Ctrl", "Shift", "Alt", "Win", "RCtrl", "RShift", "RAlt", "RWin"];
        List<string> parts = [];
        for (int bit = 0; bit < 8; bit++)
            if ((modifiers & (1 << bit)) != 0) parts.Add(names[bit]);
        if (usage != 0)
        {
            if (KeyNameForUsage(usage) is not { } key) return null;
            parts.Add(key);
        }
        return parts.Count > 0 ? string.Join("+", parts) : null;
    }

    /// <summary>A HID usage as the key name BetterGhub uses (see <c>VirtualKeys</c>).</summary>
    private static string? KeyNameForUsage(byte usage) => usage switch
    {
        >= 0x04 and <= 0x1D => ((char)('A' + usage - 0x04)).ToString(),
        >= 0x1E and <= 0x26 => ((char)('1' + usage - 0x1E)).ToString(),
        0x27 => "0",
        >= 0x3A and <= 0x45 => $"F{usage - 0x39}",
        >= 0x68 and <= 0x73 => $"F{usage - 0x68 + 13}",
        0x28 => "Enter",
        0x29 => "Esc",
        0x2A => "Backspace",
        0x2B => "Tab",
        0x2C => "Space",
        0x2D => "-",
        0x2E => "=",
        0x2F => "[",
        0x30 => "]",
        0x31 => "\\",
        0x33 => ";",
        0x34 => "'",
        0x35 => "`",
        0x36 => ",",
        0x37 => ".",
        0x38 => "/",
        0x39 => "CapsLock",
        0x46 => "PrintScreen",
        0x47 => "ScrollLock",
        0x48 => "Pause",
        0x49 => "Insert",
        0x4A => "Home",
        0x4B => "PageUp",
        0x4C => "Delete",
        0x4D => "End",
        0x4E => "PageDown",
        0x4F => "Right",
        0x50 => "Left",
        0x51 => "Down",
        0x52 => "Up",
        0x65 => "Apps",
        _ => null
    };

    /// <summary>
    /// Encodes a BetterGhub assignment as an on-board binding, or null when the mouse can't store it
    /// (macros, launching apps, double click, lock screen, wheel notches, keys it has no code for).
    /// </summary>
    public static uint? Encode(string assignment)
    {
        switch (assignment)
        {
            case BuiltinActions.LeftClick: return 0x80010001;
            case BuiltinActions.RightClick: return 0x80010002;
            case BuiltinActions.MiddleClick: return 0x80010004;
            case BuiltinActions.Back: return 0x80010008;
            case BuiltinActions.Forward: return 0x80010010;
            case BuiltinActions.ScrollLeft: return 0x90010000;
            case BuiltinActions.ScrollRight: return 0x90020000;
            case BuiltinActions.DpiUp: return 0x90030000;
            case BuiltinActions.DpiDown: return 0x90040000;
            case BuiltinActions.DpiCycle: return 0x90050000;
            case BuiltinActions.DpiShift: return 0x90070000;
            case BuiltinActions.GShift: return 0x900B0000;
            case BuiltinActions.Disabled or "": return 0x90000000;
        }
        if (assignment.StartsWith(Assignments.OnboardPrefix, StringComparison.Ordinal))
            return uint.TryParse(assignment[Assignments.OnboardPrefix.Length..], System.Globalization.NumberStyles.HexNumber, null, out uint raw) ? raw : null;
        if (Assignments.KeyCombo(assignment) is not { } combo) return null;
        string[] parts = combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1 && ConsumerUsage(parts[0]) is int consumer) return 0x80030000u | (uint)consumer;
        return KeyCode(combo) is var (modifiers, key) ? 0x80020000u | ((uint)modifiers << 8) | key : null;
    }

    /// <summary>A combo ("Ctrl+Shift+T") as modifier bits and one HID usage (0 for modifiers alone), or null when it can't be stored.</summary>
    internal static (byte Modifiers, byte Key)? KeyCode(string combo)
    {
        byte modifiers = 0;
        byte? key = null;
        foreach (string part in combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (ModifierBit(part) is int bit) modifiers |= (byte)bit;
            else if (key is null && KeyUsage(part) is byte usage) key = usage;
            else return null; // A second key or one without a HID code.
        }
        return modifiers == 0 && key is null ? null : (modifiers, key ?? 0);
    }

    internal static int? ModifierBit(string name) => name.ToLowerInvariant() switch
    {
        "ctrl" or "control" or "lctrl" or "leftctrl" => 0x01,
        "shift" or "lshift" or "leftshift" => 0x02,
        "alt" or "lalt" or "leftalt" => 0x04,
        "win" or "lwin" or "windows" => 0x08,
        "rctrl" or "rightctrl" => 0x10,
        "rshift" or "rightshift" => 0x20,
        "ralt" or "rightalt" or "altgr" => 0x40,
        "rwin" => 0x80,
        _ => null
    };

    internal static int? ConsumerUsage(string name) => name.ToLowerInvariant() switch
    {
        "volumeup" => 0xE9,
        "volumedown" => 0xEA,
        "mute" or "volumemute" => 0xE2,
        "playpause" or "mediaplaypause" => 0xCD,
        "nexttrack" or "medianexttrack" => 0xB5,
        "previoustrack" or "mediaprevioustrack" or "prevtrack" => 0xB6,
        "stopmedia" or "mediastop" => 0xB7,
        _ => null
    };

    /// <summary>A BetterGhub key name (see <c>VirtualKeys</c>) as a HID keyboard usage.</summary>
    private static byte? KeyUsage(string name)
    {
        if (name.Length == 1 && char.ToUpperInvariant(name[0]) is >= 'A' and <= 'Z' and char letter) return (byte)(0x04 + letter - 'A');
        if (name.Length == 1 && name[0] is >= '1' and <= '9') return (byte)(0x1E + name[0] - '1');
        if (name.Length > 1 && name[0] is 'F' or 'f' && int.TryParse(name[1..], out int f) && f is >= 1 and <= 24)
            return (byte)(f <= 12 ? 0x3A + f - 1 : 0x68 + f - 13);
        return name.ToLowerInvariant() switch
        {
            "0" => 0x27,
            "enter" or "return" => 0x28,
            "esc" or "escape" => 0x29,
            "backspace" or "back" => 0x2A,
            "tab" => 0x2B,
            "space" or "spacebar" => 0x2C,
            "-" or "minus" or "oemminus" => 0x2D,
            "=" or "equals" or "oemplus" => 0x2E,
            "[" or "oem4" or "oemopenbrackets" => 0x2F,
            "]" or "oem6" or "oemclosebrackets" => 0x30,
            "\\" or "oem5" or "oempipe" or "backslash" => 0x31,
            ";" or "oem1" or "oemsemicolon" or "semicolon" => 0x33,
            "'" or "oem7" or "oemquotes" => 0x34,
            "`" or "oem3" or "oemtilde" or "grave" => 0x35,
            "," or "comma" or "oemcomma" => 0x36,
            "." or "period" or "oemperiod" => 0x37,
            "/" or "slash" or "oem2" or "oemquestion" => 0x38,
            "capslock" or "capital" => 0x39,
            "printscreen" or "snapshot" or "prtsc" => 0x46,
            "scrolllock" or "scroll" => 0x47,
            "pause" => 0x48,
            "insert" or "ins" => 0x49,
            "home" => 0x4A,
            "pageup" or "prior" or "pgup" => 0x4B,
            "delete" or "del" => 0x4C,
            "end" => 0x4D,
            "pagedown" or "next" or "pgdn" => 0x4E,
            "right" => 0x4F,
            "left" => 0x50,
            "down" => 0x51,
            "up" => 0x52,
            "apps" or "contextmenu" => 0x65,
            _ => null
        };
    }

    /// <summary>Directory entries: sector (big-endian), enabled flag, reserved; 0xFFFF ends the list.</summary>
    public static List<(int Sector, bool Enabled)> ParseDirectory(byte[] data, int maxSlots)
    {
        List<(int, bool)> slots = [];
        for (int offset = 0; offset + 3 < data.Length && slots.Count < maxSlots; offset += 4)
        {
            int sector = (data[offset] << 8) | data[offset + 1];
            if (sector == 0xFFFF) break;
            slots.Add((sector, data[offset + 2] != 0));
        }
        return slots;
    }

    public static OnboardProfile Parse(byte[] sector, int buttonCount)
    {
        List<int> dpis = [];
        for (int i = 0; i < 5; i++)
        {
            int dpi = sector[3 + i * 2] | (sector[4 + i * 2] << 8);
            if (dpi is not (0 or 0xFFFF)) dpis.Add(dpi);
        }
        string name = Encoding.Unicode.GetString(sector, NameOffset, NameBytes);
        int end = name.IndexOfAny(['\0', (char)0xFFFF]); // Erased flash reads as 0xFFFF.
        if (end >= 0) name = name[..end];
        return new OnboardProfile(
            name.Trim(),
            sector[0],
            dpis,
            sector[1],
            sector[2],
            Bindings(sector, ButtonsOffset, buttonCount),
            Bindings(sector, ShiftButtonsOffset, buttonCount),
            ChecksumOk(sector),
            (byte[])sector.Clone());
    }

    private static List<OnboardBinding> Bindings(byte[] sector, int offset, int buttonCount)
    {
        List<OnboardBinding> bindings = [];
        for (int i = 0; i < 16; i++)
        {
            int at = offset + i * 4;
            if (sector[at] == 0xFF && sector[at + 1] == 0xFF) continue; // Unused entry.
            if (i >= buttonCount && sector[at] == 0x90 && sector[at + 1] == 0x00) continue;
            bindings.Add(new OnboardBinding(i, Describe(sector[at], sector[at + 1], sector[at + 2], sector[at + 3]), Convert.ToHexString(sector, at, 4)));
        }
        return bindings;
    }

    /// <summary>
    /// Which control a binding slot belongs to on the G502 X, from the order of G HUB's factory profile
    /// (left, right, middle, back, DPI Shift, forward, tilts, G9, G8, G7). 12 and 13 are inferred as the scroll wheel.
    /// </summary>
    public static string ControlName(int index) => index switch
    {
        0 => "Primary click",
        1 => "Secondary click",
        2 => "Middle click",
        3 => "G4 · Back",
        4 => "G6 · DPI Shift",
        5 => "G5 · Forward",
        6 => "Wheel tilt left",
        7 => "Wheel tilt right",
        8 => "G9",
        9 => "G8",
        10 => "G7",
        11 => "Scroll down",
        12 => "Scroll up",
        _ => $"Button {index + 1}"
    };

    /// <summary>The last two bytes of the sector hold a CRC-CCITT (0xFFFF seed) of everything before them.</summary>
    private static bool ChecksumOk(byte[] sector)
    {
        int length = sector.Length - 2;
        return Checksum(sector) == ((sector[length] << 8) | sector[length + 1]);
    }

    /// <summary>CRC-CCITT of everything but the last two bytes, which is what belongs there.</summary>
    public static ushort Checksum(byte[] sector)
    {
        ushort crc = 0xFFFF;
        for (int i = 0; i < sector.Length - 2; i++)
        {
            crc ^= (ushort)(sector[i] << 8);
            for (int bit = 0; bit < 8; bit++) crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }

    public static string Describe(uint binding) => Describe((byte)(binding >> 24), (byte)(binding >> 16), (byte)(binding >> 8), (byte)binding);

    public static string Describe(byte type, byte kind, byte high, byte low)
    {
        int value = (high << 8) | low;
        return type switch
        {
            0x80 when kind == 0x01 => MouseButtons(value),
            0x80 when kind == 0x02 => Key(high, low),
            0x80 when kind == 0x03 => Consumer(high == 0x0C ? low : value),
            0x80 when kind == 0x00 => "Disabled",
            0x90 => kind switch
            {
                0x00 => "No action",
                0x01 => "Scroll left",
                0x02 => "Scroll right",
                0x03 => "DPI up",
                0x04 => "DPI down",
                0x05 => "DPI cycle",
                0x06 => "DPI default",
                0x07 => "DPI Shift",
                0x08 => "Next profile",
                0x09 => "Previous profile",
                0x0A => "Cycle profiles",
                0x0B => "G-Shift",
                0x0C => "Battery level",
                0x10 or 0x11 => "Scroll",
                _ => $"Special 0x{kind:X2}"
            },
            0x00 => "Macro",
            0xFF => "Disabled",
            _ => "Unknown"
        };
    }

    private static string MouseButtons(int mask) => mask switch
    {
        0x0001 => "Left click",
        0x0002 => "Right click",
        0x0004 => "Middle click",
        0x0008 => "Back",
        0x0010 => "Forward",
        _ => $"Mouse buttons 0x{mask:X4}"
    };

    private static string Consumer(int usage) => usage switch
    {
        0xB5 => "Next track",
        0xB6 => "Previous track",
        0xB7 => "Stop",
        0xCD => "Play/Pause",
        0xE2 => "Mute",
        0xE9 => "Volume up",
        0xEA => "Volume down",
        _ => $"Media key 0x{usage:X}"
    };

    private static readonly string[] ModifierNames = ["Ctrl", "Shift", "Alt", "Win", "Right Ctrl", "Right Shift", "Right Alt", "Right Win"];

    private static string Key(byte modifiers, byte usage)
    {
        List<string> parts = [];
        for (int bit = 0; bit < 8; bit++)
            if ((modifiers & (1 << bit)) != 0) parts.Add(ModifierNames[bit]);
        if (usage != 0) parts.Add(KeyName(usage));
        return parts.Count > 0 ? string.Join(" + ", parts) : "No key";
    }

    /// <summary>HID keyboard usage (page 0x07) to a readable key name.</summary>
    private static string KeyName(byte usage) => usage switch
    {
        >= 0x04 and <= 0x1D => ((char)('A' + usage - 0x04)).ToString(),
        >= 0x1E and <= 0x26 => ((char)('1' + usage - 0x1E)).ToString(),
        0x27 => "0",
        0x28 => "Enter",
        0x29 => "Esc",
        0x2A => "Backspace",
        0x2B => "Tab",
        0x2C => "Space",
        0x2D => "-",
        0x2E => "=",
        0x2F => "[",
        0x30 => "]",
        0x31 => "\\",
        0x33 => ";",
        0x34 => "'",
        0x35 => "`",
        0x36 => ",",
        0x37 => ".",
        0x38 => "/",
        0x39 => "Caps Lock",
        >= 0x3A and <= 0x45 => $"F{usage - 0x39}",
        0x46 => "Print Screen",
        0x47 => "Scroll Lock",
        0x48 => "Pause",
        0x49 => "Insert",
        0x4A => "Home",
        0x4B => "Page Up",
        0x4C => "Delete",
        0x4D => "End",
        0x4E => "Page Down",
        0x4F => "Right",
        0x50 => "Left",
        0x51 => "Down",
        0x52 => "Up",
        >= 0x68 and <= 0x73 => $"F{usage - 0x68 + 13}",
        _ => $"Key 0x{usage:X2}"
    };
}
