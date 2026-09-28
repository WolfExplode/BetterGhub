using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BetterGhub.App;

internal sealed class MacroEngine : IDisposable
{
    private readonly Dictionary<int, CancellationTokenSource> running = [];
    private readonly Dictionary<int, int> sequencePositions = [];
    private readonly object gate = new();
    public event Action<string>? Activity;

    public void Handle(int bit, bool down, MouseProfile profile, IReadOnlyList<MacroDefinition> macros, bool pulse = false, string? assignmentId = null)
    {
        string? id = assignmentId ?? profile.Assignments.GetValueOrDefault(bit);
        if (id is null) return;
        MacroDefinition? macro = macros.FirstOrDefault(x => x.Id == id);
        if (macro is null || macro.Steps.Count == 0) return;
        if (!down)
        {
            if (macro.Mode == MacroMode.WhileHeld && !pulse) Stop(bit);
            return;
        }
        if (macro.Mode == MacroMode.Toggle && IsRunning(bit)) { Stop(bit); return; }
        if (IsRunning(bit)) return;
        CancellationTokenSource cancellation = new();
        lock (gate) running[bit] = cancellation;
        Activity?.Invoke($"{macro.Name} · {macro.Mode}");
        _ = Task.Run(async () =>
        {
            HashSet<ushort> heldKeys = [];
            try
            {
                if (macro.Mode == MacroMode.Sequence)
                {
                    int index;
                    lock (gate)
                    {
                        index = sequencePositions.GetValueOrDefault(bit) % macro.Steps.Count;
                        sequencePositions[bit] = index + 1;
                    }
                    await Execute(macro.Steps[index], heldKeys, cancellation.Token);
                }
                else if (macro.Mode == MacroMode.Toggle || (macro.Mode == MacroMode.WhileHeld && !pulse))
                {
                    do
                    {
                        foreach (MacroStep step in macro.Steps)
                            await Execute(step, heldKeys, cancellation.Token);
                        await Task.Delay(20, cancellation.Token);
                    } while (!cancellation.IsCancellationRequested);
                }
                else
                    foreach (MacroStep step in macro.Steps)
                        await Execute(step, heldKeys, cancellation.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { Activity?.Invoke($"Macro error: {error.Message}"); }
            finally
            {
                foreach (ushort key in heldKeys.Reverse())
                {
                    try { SendInput([Keyboard(key, 0x0002)]); }
                    catch (Exception) { /* Continue releasing other held keys. */ }
                }
                lock (gate)
                {
                    if (running.GetValueOrDefault(bit) == cancellation) running.Remove(bit);
                }
                cancellation.Dispose();
            }
        });
    }

    private static async Task Execute(MacroStep step, HashSet<ushort> heldKeys, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        switch (step.Kind)
        {
            case ActionKind.Delay: await Task.Delay(Math.Clamp(step.DelayMs, 0, 60000), cancellation); break;
            case ActionKind.Key: SendCombo(step.Value); break;
            case ActionKind.KeyDown:
                ushort downKey = ParseKey(step.Value);
                if (heldKeys.Add(downKey)) SendInput([Keyboard(downKey, 0)]);
                break;
            case ActionKind.KeyUp:
                ushort upKey = ParseKey(step.Value);
                if (heldKeys.Remove(upKey)) SendInput([Keyboard(upKey, 0x0002)]);
                break;
            case ActionKind.Text: SendText(step.Value); break;
            case ActionKind.LeftClick: SendMouse(0x0002, 0x0004); break;
            case ActionKind.RightClick: SendMouse(0x0008, 0x0010); break;
            case ActionKind.MiddleClick: SendMouse(0x0020, 0x0040); break;
            case ActionKind.Wheel:
                if (!int.TryParse(step.Value, out int ticks)) ticks = 1;
                SendInput([Mouse(0x0800, ticks * 120)]);
                break;
            case ActionKind.VolumeUp: SendCombo("VolumeUp"); break;
            case ActionKind.VolumeDown: SendCombo("VolumeDown"); break;
            case ActionKind.Mute: SendCombo("VolumeMute"); break;
            case ActionKind.Launch:
                if (!File.Exists(step.Value)) throw new FileNotFoundException("Application not found", step.Value);
                Process.Start(new ProcessStartInfo(step.Value) { UseShellExecute = true });
                break;
        }
    }

    private static void SendCombo(string combo)
    {
        string[] names = combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        List<ushort> keys = [];
        foreach (string name in names)
        {
            keys.Add(ParseKey(name));
        }
        if (keys.Count == 0) return;
        try
        {
            foreach (ushort key in keys) SendInput([Keyboard(key, 0)]);
        }
        finally
        {
            for (int i = keys.Count - 1; i >= 0; i--) SendInput([Keyboard(keys[i], 0x0002)]);
        }
    }

    private static ushort ParseKey(string name)
    {
        string normalized = name.ToLowerInvariant() switch
        {
            "ctrl" or "control" => "ControlKey",
            "win" or "windows" => "LWin",
            "alt" => "Menu",
            "shift" => "ShiftKey",
            "enter" => "Return",
            "esc" => "Escape",
            "space" => "Space",
            _ => name
        };
        if (normalized.Length == 1 && char.IsAsciiDigit(normalized[0])) normalized = "D" + normalized;
        if (!Enum.TryParse(normalized, true, out Keys key) || key == Keys.None)
            throw new ArgumentException($"Unknown key: {name}");
        return (ushort)key;
    }

    private static void SendText(string value)
    {
        foreach (char letter in value)
            SendInput([Unicode(letter, 0), Unicode(letter, 0x0002)]);
    }

    private static void SendMouse(uint down, uint up) => SendInput([Mouse(down), Mouse(up)]);
    private bool IsRunning(int bit) { lock (gate) return running.ContainsKey(bit); }
    private void Stop(int bit) { lock (gate) running.GetValueOrDefault(bit)?.Cancel(); }
    public void StopAll() { lock (gate) foreach (CancellationTokenSource item in running.Values) item.Cancel(); }
    public void Dispose() => StopAll();

    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputData
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput
    {
        public int Dx, Dy;
        public uint MouseData, Flags, Time;
        public nint ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput
    {
        public ushort VirtualKey, Scan;
        public uint Flags, Time;
        public nint ExtraInfo;
    }
    private static Input Keyboard(ushort key, uint flags) => new() { Type = 1, Data = new InputData { Keyboard = new KeyboardInput { VirtualKey = key, Flags = flags } } };
    private static Input Unicode(char letter, uint flags) => new() { Type = 1, Data = new InputData { Keyboard = new KeyboardInput { Scan = letter, Flags = flags | 0x0004 } } };
    private static Input Mouse(uint flags, int data = 0) => new() { Type = 0, Data = new InputData { Mouse = new MouseInput { Flags = flags, MouseData = unchecked((uint)data) } } };
    private static void SendInput(Input[] inputs)
    {
        if (NativeSendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new InvalidOperationException("Windows rejected simulated input");
    }
    [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)]
    private static extern uint NativeSendInput(uint count, Input[] inputs, int size);
}
