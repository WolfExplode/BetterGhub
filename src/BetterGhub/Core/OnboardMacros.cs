namespace BetterGhub.Core;

/// <summary>
/// On-board macros (HID++ 0x8100 macro format 1): a byte-code list in a spare user sector, bound to a button as
/// 00 &lt;sector&gt; 00 &lt;offset&gt;. Key press (43), key release (44), delay (40, big-endian ms) and end (FF) are
/// confirmed from a macro G HUB wrote, and repeat-while-pressed (02) from libratbag and a test on the mouse. Mouse
/// button (41/42) and media key (45/46) presses are libratbag's codes with the same 16-bit big-endian argument as
/// the others (a button mask as in a binding, a consumer usage), which is inferred rather than confirmed.
/// </summary>
public static class OnboardMacros
{
    private const byte Delay = 0x40, ButtonDown = 0x41, ButtonUp = 0x42, KeyPress = 0x43, KeyRelease = 0x44, ConsumerDown = 0x45, ConsumerUp = 0x46,
        RepeatWhilePressed = 0x02, End = 0xFF;

    /// <summary>The last two bytes of every sector are left alone, in case the mouse ever checks a CRC there.</summary>
    public static int Capacity(int sectorSize) => sectorSize - 2;

    /// <summary>
    /// A BetterGhub macro as on-board byte code, or null with <paramref name="reason"/> when the mouse can't play it.
    /// It mirrors <see cref="MacroEngine"/>: "while held" repeats with the same pause between rounds, and keys
    /// still held at the end are released.
    /// </summary>
    public static byte[]? Encode(MacroDefinition macro, out string reason)
    {
        reason = "";
        if (macro.Mode is not (MacroMode.Once or MacroMode.WhileHeld)) { reason = $"{ModeLabel(macro.Mode)} macros can't be stored on the mouse"; return null; }
        if (macro.Steps.Count == 0) { reason = "it has no steps yet"; return null; }
        List<byte> code = [];
        List<(byte Modifiers, byte Key)> held = [];
        void Wait(int ms)
        {
            ms = Math.Clamp(ms, 0, 60000);
            if (ms > 0) code.AddRange([Delay, (byte)(ms >> 8), (byte)ms]);
        }
        bool first = true;
        foreach (MacroStep step in macro.Steps)
        {
            if (macro.StandardDelayMs is int standard)
            {
                if (step.Kind == ActionKind.Delay) continue;
                if (!first) Wait(standard);
            }
            first = false;
            if (Tap(step) is { } tap)
            {
                code.AddRange(tap);
                continue;
            }
            switch (step.Kind)
            {
                case ActionKind.Delay:
                    Wait(step.DelayMs);
                    break;
                case ActionKind.Key when OnboardProfiles.KeyCode(step.Value) is var (modifiers, key):
                    code.AddRange([KeyPress, modifiers, key, KeyRelease, modifiers, key]);
                    break;
                case ActionKind.KeyDown when SingleKey(step.Value) is { } down:
                    if (held.Contains(down)) break;
                    held.Add(down);
                    code.AddRange([KeyPress, down.Modifiers, down.Key]);
                    break;
                case ActionKind.KeyUp when SingleKey(step.Value) is { } up:
                    if (!held.Remove(up)) break;
                    code.AddRange([KeyRelease, up.Modifiers, up.Key]);
                    break;
                case ActionKind.Key or ActionKind.KeyDown or ActionKind.KeyUp:
                    reason = $"the mouse has no code for {step.Value}";
                    return null;
                default:
                    reason = "only keys, media keys, clicks and delays can be stored on the mouse";
                    return null;
            }
        }
        if (macro.Mode == MacroMode.WhileHeld)
        {
            Wait(Math.Max(20, macro.StandardDelayMs ?? 0));
            code.Add(RepeatWhilePressed);
        }
        for (int i = held.Count - 1; i >= 0; i--) code.AddRange([KeyRelease, held[i].Modifiers, held[i].Key]);
        code.Add(End);
        if (code.Count > Capacity(255)) { reason = "it's too long to fit on the mouse"; return null; }
        return [.. code];
    }

    /// <summary>A click or media key step as a press and release, or null for any other step.</summary>
    private static byte[]? Tap(MacroStep step)
    {
        int? button = step.Kind switch { ActionKind.LeftClick => 0x01, ActionKind.RightClick => 0x02, ActionKind.MiddleClick => 0x04, _ => null };
        if (button is int mask) return [ButtonDown, 0, (byte)mask, ButtonUp, 0, (byte)mask];
        string? media = step.Kind switch
        {
            ActionKind.VolumeUp => "VolumeUp",
            ActionKind.VolumeDown => "VolumeDown",
            ActionKind.Mute => "Mute",
            ActionKind.PlayPause => "PlayPause",
            ActionKind.NextTrack => "NextTrack",
            ActionKind.PreviousTrack => "PreviousTrack",
            ActionKind.Key => step.Value.Trim(), // A key step can hold a media key too.
            _ => null
        };
        if (media is null || OnboardProfiles.ConsumerUsage(media) is not int usage) return null;
        return [ConsumerDown, (byte)(usage >> 8), (byte)usage, ConsumerUp, (byte)(usage >> 8), (byte)usage];
    }

    private static string ModeLabel(MacroMode mode) => mode == MacroMode.Toggle ? "Toggle" : "Sequence";

    /// <summary>One key for a hold or release step; a modifier is sent as its own usage (Left Ctrl is E0) plus its bit, like G HUB.</summary>
    private static (byte Modifiers, byte Key)? SingleKey(string name)
    {
        if (OnboardProfiles.ModifierBit(name.Trim()) is int bit) return ((byte)bit, (byte)(0xE0 + System.Numerics.BitOperations.Log2((uint)bit)));
        return OnboardProfiles.KeyCode(name) is (0, var key) ? (0, key) : null;
    }

    /// <summary>The binding that plays the macro at <paramref name="offset"/> in <paramref name="sector"/>.</summary>
    public static uint Binding(int sector, int offset) => ((uint)(byte)sector << 16) | (byte)offset;

    /// <summary>Where a macro binding points, or null for any other binding.</summary>
    public static (int Sector, int Offset)? Target(uint binding) =>
        binding >> 24 == 0x00 ? ((int)(binding >> 16) & 0xFF, (int)binding & 0xFF) : null;

    /// <summary>
    /// How many bytes the macro at <paramref name="offset"/> uses, through its end marker. An instruction this
    /// doesn't know (G HUB may write mouse moves or jumps) claims the rest of the sector, so it's never overwritten.
    /// </summary>
    public static int Length(byte[] sector, int offset)
    {
        int limit = Capacity(sector.Length);
        int at = offset;
        while (at < limit)
        {
            byte op = sector[at];
            if (op == End) return at + 1 - offset;
            int size = op switch
            {
                <= 0x03 => 1,
                >= 0x40 and <= 0x46 => 3,
                _ => 0
            };
            if (size == 0) break;
            at += size;
        }
        return sector.Length - offset;
    }

    /// <summary>The macro's bytes at <paramref name="offset"/>, through its end marker.</summary>
    public static byte[] Read(byte[] sector, int offset) =>
        offset < sector.Length ? sector.AsSpan(offset, Math.Min(Length(sector, offset), sector.Length - offset)).ToArray() : [];
}

/// <summary>
/// Places macros into spare sectors without disturbing macros other bindings still use. A macro already on the
/// mouse byte for byte is reused; otherwise it goes into the first gap that fits. Sectors that already hold bound
/// macros come first, then ones holding only leftovers nothing points at (e.g. an unassigned G HUB macro, which
/// the write clears), then erased ones.
/// </summary>
public sealed class OnboardMacroPlacer
{
    private readonly OnboardMemory memory;
    /// <summary>Bytes in use per sector, and where each macro in use starts: macros bound by any slot, plus the ones placed here.</summary>
    private readonly Dictionary<int, bool[]> used = [];
    private readonly Dictionary<int, HashSet<int>> starts = [];
    private readonly Dictionary<int, byte[]> placed = [];

    /// <param name="bindings">Every binding that will be stored after the write, across all slots.</param>
    public OnboardMacroPlacer(OnboardMemory memory, IEnumerable<uint> bindings)
    {
        this.memory = memory;
        foreach (uint binding in bindings)
            if (OnboardMacros.Target(binding) is var (sector, offset) && memory.SpareSectors.TryGetValue(sector, out byte[]? data) && offset < data.Length)
                Mark(sector, offset, OnboardMacros.Length(data, offset));
    }

    /// <summary>The sectors macros were placed in, to write before the slot that binds them.</summary>
    public IEnumerable<(int Sector, byte[] Data)> Changed =>
        placed.Where(x => !x.Value.AsSpan().SequenceEqual(memory.SpareSectors[x.Key])).Select(x => (x.Key, x.Value));

    public uint Place(byte[] macro)
    {
        foreach ((int sector, HashSet<int> offsets) in starts)
        {
            byte[] data = placed.GetValueOrDefault(sector) ?? memory.SpareSectors[sector];
            foreach (int offset in offsets)
                if (offset + macro.Length <= data.Length && data.AsSpan(offset, macro.Length).SequenceEqual(macro))
                    return OnboardMacros.Binding(sector, offset);
        }
        int capacity = OnboardMacros.Capacity(memory.SectorSize);
        foreach (int sector in memory.SpareSectors.Keys.Order().OrderBy(Priority))
        {
            bool[] marks = Marks(sector);
            for (int offset = 0, run = 0; offset < capacity; offset++)
            {
                run = marks[offset] ? 0 : run + 1;
                if (run < macro.Length) continue;
                int start = offset - macro.Length + 1;
                macro.CopyTo(Cleaned(sector), start);
                Mark(sector, start, macro.Length);
                return OnboardMacros.Binding(sector, start);
            }
        }
        throw new InvalidOperationException("The mouse's macro memory is full");
    }

    private int Priority(int sector) =>
        starts.ContainsKey(sector) ? 0 : memory.SpareSectors[sector].Any(b => b != 0xFF) ? 1 : 2;

    /// <summary>The sector as it will be written: macros in use kept, everything else erased.</summary>
    private byte[] Cleaned(int sector)
    {
        if (placed.TryGetValue(sector, out byte[]? data)) return data;
        byte[] original = memory.SpareSectors[sector];
        bool[] marks = Marks(sector);
        data = new byte[original.Length];
        for (int i = 0; i < data.Length; i++) data[i] = marks[i] ? original[i] : (byte)0xFF;
        return placed[sector] = data;
    }

    private bool[] Marks(int sector)
    {
        if (!used.TryGetValue(sector, out bool[]? marks)) used[sector] = marks = new bool[memory.SectorSize];
        return marks;
    }

    private void Mark(int sector, int offset, int length)
    {
        bool[] marks = Marks(sector);
        for (int i = offset; i < Math.Min(offset + length, marks.Length); i++) marks[i] = true;
        if (!starts.TryGetValue(sector, out HashSet<int>? offsets)) starts[sector] = offsets = [];
        offsets.Add(offset);
    }
}
