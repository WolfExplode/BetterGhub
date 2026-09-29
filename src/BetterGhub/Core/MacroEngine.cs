using System.Diagnostics;
using BetterGhub.Input;

namespace BetterGhub.Core;

/// <summary>Plays macros on background tasks, one playback slot per trigger (HID bit).</summary>
internal sealed class MacroEngine : IDisposable
{
    public const int TestTrigger = -1;
    private readonly Dictionary<int, CancellationTokenSource> running = [];
    private readonly Dictionary<int, int> sequencePositions = [];
    private readonly object gate = new();
    public event Action<string>? Activity;

    public void Handle(int trigger, bool down, MacroDefinition macro, bool pulse = false)
    {
        if (macro.Steps.Count == 0) return;
        if (!down)
        {
            if (macro.Mode == MacroMode.WhileHeld && !pulse) Stop(trigger);
            return;
        }
        if (macro.Mode == MacroMode.Toggle && IsRunning(trigger)) { Stop(trigger); return; }
        if (IsRunning(trigger)) return;
        Start(trigger, macro, repeat: macro.Mode == MacroMode.Toggle || (macro.Mode == MacroMode.WhileHeld && !pulse));
    }

    /// <summary>Plays every step once, whatever the macro's mode. Used by the editor's Test button.</summary>
    public void PlayOnce(MacroDefinition macro)
    {
        Stop(TestTrigger);
        if (macro.Steps.Count > 0) Start(TestTrigger, macro, repeat: false, ignoreSequence: true);
    }

    private void Start(int trigger, MacroDefinition macro, bool repeat, bool ignoreSequence = false)
    {
        CancellationTokenSource cancellation = new();
        lock (gate) running[trigger] = cancellation;
        Activity?.Invoke($"▶ {macro.Name}");
        // Snapshot so edits in the UI cannot change a playback in progress.
        List<MacroStep> steps = macro.Steps.Select(s => s.Clone()).ToList();
        int? standardDelay = macro.StandardDelayMs;
        int? standardDelayMax = macro.StandardDelayMaxMs;
        bool sequence = macro.Mode == MacroMode.Sequence && !ignoreSequence;
        _ = Task.Run(async () =>
        {
            HashSet<ushort> heldKeys = [];
            HashSet<int> heldButtons = [];
            try
            {
                if (sequence)
                {
                    int index;
                    lock (gate)
                    {
                        index = sequencePositions.GetValueOrDefault(trigger) % steps.Count;
                        sequencePositions[trigger] = index + 1;
                    }
                    await Execute(steps[index], heldKeys, heldButtons, cancellation.Token);
                }
                else
                {
                    do
                    {
                        await RunAll(steps, standardDelay, standardDelayMax, heldKeys, heldButtons, cancellation.Token);
                        await Task.Delay(repeat ? Math.Max(20, standardDelay is int gap ? Delays.Roll(gap, standardDelayMax) : 0) : 0, cancellation.Token);
                    } while (repeat && !cancellation.IsCancellationRequested);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { Activity?.Invoke($"Macro error: {error.Message}"); }
            finally
            {
                foreach (ushort key in heldKeys.Reverse())
                {
                    try { InputSender.Key(key, up: true); }
                    catch (Exception) { /* Continue releasing other held keys. */ }
                }
                foreach (int button in heldButtons)
                {
                    try { InputSender.MouseButton(button, up: true); }
                    catch (Exception) { /* Continue releasing other held buttons. */ }
                }
                lock (gate)
                {
                    if (running.GetValueOrDefault(trigger) == cancellation) running.Remove(trigger);
                }
                cancellation.Dispose();
            }
        });
    }

    private static async Task RunAll(List<MacroStep> steps, int? standardDelay, int? standardDelayMax, HashSet<ushort> heldKeys, HashSet<int> heldButtons, CancellationToken cancellation)
    {
        bool first = true;
        foreach (MacroStep step in steps)
        {
            if (standardDelay.HasValue)
            {
                if (step.Kind == ActionKind.Delay) continue;
                if (!first) await Task.Delay(Delays.Roll(standardDelay.Value, standardDelayMax), cancellation);
            }
            await Execute(step, heldKeys, heldButtons, cancellation);
            first = false;
        }
    }

    private static async Task Execute(MacroStep step, HashSet<ushort> heldKeys, HashSet<int> heldButtons, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        switch (step.Kind)
        {
            case ActionKind.Delay: await Task.Delay(Delays.Roll(step.DelayMs, step.DelayMaxMs), cancellation); break;
            case ActionKind.Key: InputSender.Combo(step.Value); break;
            case ActionKind.KeyDown:
                ushort downKey = VirtualKeys.Parse(step.Value);
                if (heldKeys.Add(downKey)) InputSender.Key(downKey, up: false);
                break;
            case ActionKind.KeyUp:
                ushort upKey = VirtualKeys.Parse(step.Value);
                if (heldKeys.Remove(upKey)) InputSender.Key(upKey, up: true);
                break;
            case ActionKind.Text: InputSender.Text(step.Value); break;
            case ActionKind.LeftClick: InputSender.LeftClick(); break;
            case ActionKind.RightClick: InputSender.RightClick(); break;
            case ActionKind.MiddleClick: InputSender.MiddleClick(); break;
            case ActionKind.MouseDown:
                if (heldButtons.Add(step.MouseButton)) InputSender.MouseButton(step.MouseButton, up: false);
                break;
            case ActionKind.MouseUp:
                if (heldButtons.Remove(step.MouseButton)) InputSender.MouseButton(step.MouseButton, up: true);
                break;
            case ActionKind.Wheel: InputSender.Wheel(int.TryParse(step.Value, out int ticks) ? ticks : 1); break;
            case ActionKind.VolumeUp: InputSender.Combo("VolumeUp"); break;
            case ActionKind.VolumeDown: InputSender.Combo("VolumeDown"); break;
            case ActionKind.Mute: InputSender.Combo("Mute"); break;
            case ActionKind.PlayPause: InputSender.Combo("PlayPause"); break;
            case ActionKind.NextTrack: InputSender.Combo("NextTrack"); break;
            case ActionKind.PreviousTrack: InputSender.Combo("PreviousTrack"); break;
            case ActionKind.Launch:
                if (!File.Exists(step.Value)) throw new FileNotFoundException("Application not found", step.Value);
                Process.Start(new ProcessStartInfo(step.Value) { UseShellExecute = true });
                break;
        }
    }

    public bool IsRunning(int trigger) { lock (gate) return running.ContainsKey(trigger); }
    public void Stop(int trigger) { lock (gate) running.GetValueOrDefault(trigger)?.Cancel(); }
    public void StopAll() { lock (gate) foreach (CancellationTokenSource item in running.Values) item.Cancel(); }
    public void ResetSequences() { lock (gate) sequencePositions.Clear(); }
    public void Dispose() => StopAll();
}
