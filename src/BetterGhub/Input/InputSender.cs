using System.Runtime.InteropServices;

namespace BetterGhub.Input;

/// <summary>Injects keyboard and mouse input with SendInput.</summary>
internal static class InputSender
{
    private const uint KeyUp = 0x0002, ExtendedKey = 0x0001, Unicode = 0x0004;

    public static void Key(ushort key, bool up)
    {
        // Include the hardware scan code as well as the virtual key: many games read scan codes only.
        uint flags = (up ? KeyUp : 0) | (VirtualKeys.IsExtended(key) ? ExtendedKey : 0);
        ushort scan = (ushort)MapVirtualKey(key, 0);
        Send([new Input { Type = 1, Data = new InputData { Keyboard = new KeyboardInput { VirtualKey = key, Scan = scan, Flags = flags } } }]);
    }

    public static void Combo(string combo)
    {
        List<ushort> keys = combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(VirtualKeys.Parse).ToList();
        int pressed = 0;
        try
        {
            foreach (ushort key in keys) { Key(key, false); pressed++; }
        }
        finally
        {
            for (int i = pressed - 1; i >= 0; i--) Key(keys[i], true);
        }
    }

    /// <summary>Presses a combo's keys in order and leaves them held. Pair with <see cref="ReleaseCombo"/>.</summary>
    public static void PressCombo(IReadOnlyList<ushort> keys)
    {
        foreach (ushort key in keys) Key(key, false);
    }

    public static void ReleaseCombo(IReadOnlyList<ushort> keys)
    {
        for (int i = keys.Count - 1; i >= 0; i--) Key(keys[i], true);
    }

    public static List<ushort> ParseCombo(string combo) =>
        combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(VirtualKeys.Parse).ToList();

    public static void Text(string value)
    {
        foreach (char letter in value)
        {
            if (letter == '\n') { Key(0x0D, false); Key(0x0D, true); continue; }
            if (letter == '\r') continue;
            Send([UnicodeInput(letter, 0), UnicodeInput(letter, KeyUp)]);
        }
    }

    public static void Click(uint downFlag, uint upFlag) => Send([MouseInput(downFlag), MouseInput(upFlag)]);
    public static void LeftClick() => Click(0x0002, 0x0004);
    public static void RightClick() => Click(0x0008, 0x0010);
    public static void MiddleClick() => Click(0x0020, 0x0040);
    public static void Wheel(int ticks) => Send([MouseInput(0x0800, ticks * 120)]);
    public static void HorizontalWheel(int ticks) => Send([MouseInput(0x1000, ticks * 120)]);

    /// <summary>Presses or releases one mouse button: 0 left, 1 right, 2 middle, 3 back, 4 forward.</summary>
    public static void MouseButton(int button, bool up)
    {
        (uint down, uint release, int data) = button switch
        {
            0 => (0x0002u, 0x0004u, 0),
            1 => (0x0008u, 0x0010u, 0),
            2 => (0x0020u, 0x0040u, 0),
            3 => (0x0080u, 0x0100u, 1),
            4 => (0x0080u, 0x0100u, 2),
            _ => throw new ArgumentOutOfRangeException(nameof(button))
        };
        Send([MouseInput(up ? release : down, data)]);
    }

    private static Input UnicodeInput(char letter, uint flags) =>
        new() { Type = 1, Data = new InputData { Keyboard = new KeyboardInput { Scan = letter, Flags = flags | Unicode } } };
    private static Input MouseInput(uint flags, int data = 0) =>
        new() { Type = 0, Data = new InputData { Mouse = new MouseData { Flags = flags, MouseDataValue = unchecked((uint)data) } } };

    private static void Send(Input[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new InvalidOperationException("Windows rejected simulated input (an elevated window may have focus)");
    }

    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputData
    {
        [FieldOffset(0)] public MouseData Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseData
    {
        public int Dx, Dy;
        public uint MouseDataValue, Flags, Time;
        public nint ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput
    {
        public ushort VirtualKey, Scan;
        public uint Flags, Time;
        public nint ExtraInfo;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
}
