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
    bool ChecksumOk);

/// <summary>A decoded button binding; <see cref="Raw"/> is the 4 stored bytes in hex.</summary>
public sealed record OnboardBinding(int Index, string Description, string Raw);

/// <summary>
/// Decodes HID++ 0x8100 profile format 3 (G502 X): report rate, default and shift DPI index, five DPI
/// levels, 16 button and 16 G-shift bindings, a UTF-16 name, and a CRC-CCITT over the rest of the sector.
/// Read-only: nothing here writes to the mouse.
/// </summary>
public static class OnboardProfiles
{
    private const int ButtonsOffset = 32, ShiftButtonsOffset = 96, NameOffset = 160, NameBytes = 48;

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
        int end = name.IndexOfAny(['\0', '￿']); // Erased flash reads as 0xFFFF.
        if (end >= 0) name = name[..end];
        return new OnboardProfile(
            name.Trim(),
            sector[0],
            dpis,
            sector[1],
            sector[2],
            Bindings(sector, ButtonsOffset, buttonCount),
            Bindings(sector, ShiftButtonsOffset, buttonCount),
            ChecksumOk(sector));
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
