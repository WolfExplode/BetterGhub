using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using BetterGhub.Core;
using BetterGhub.Device;
using BetterGhub.Input;

namespace BetterGhub.Services;

public enum ConnectionState { Disconnected, Connecting, Connected, GHubRunning }

public sealed record LogEntry(DateTime Time, string Text, bool IsButton = false);

/// <summary>
/// Everything between the receiver and the UI: connection, button handling, macro dispatch,
/// profile switching, DPI and calibration. All members must be used on the UI thread; bridge
/// callbacks are marshalled onto it.
/// </summary>
internal sealed class MouseService : IDisposable
{
    private readonly SynchronizationContext ui;
    private readonly DeviceBridge bridge = new();
    private readonly MacroEngine engine = new();
    private readonly Dictionary<int, string> pressedAssignments = [];
    private readonly HashSet<int> pressed = [];
    private readonly HashSet<int> swallowRelease = [];
    private MouseHook? hook;
    private bool dpiShiftHeld;
    private bool disposed;

    public Settings Settings { get; }
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string StateDetail { get; private set; } = "";
    public int DeviceDpi { get; private set; }
    public int DeviceIntervalMs { get; private set; }
    public bool GShiftHeld { get; private set; }
    public MouseControl? Learning { get; private set; }
    public IReadOnlyCollection<int> Pressed => pressed;
    public List<LogEntry> Log { get; } = [];
    public MouseProfile ActiveProfile => Settings.ActiveProfile;
    public int DeviceReportRate => DeviceIntervalMs > 0 ? 1000 / DeviceIntervalMs : 0;

    /// <summary>Connection, DPI, profile or layer changed.</summary>
    public event Action? StateChanged;
    public event Action<int, bool>? ButtonChanged;
    public event Action<LogEntry>? Logged;
    /// <summary>Settings changed outside the page being edited (profile switch, DPI button, calibration).</summary>
    public event Action? SettingsChanged;
    public event Action<MouseControl, int>? Calibrated;

    public MouseService(SynchronizationContext ui)
    {
        this.ui = ui;
        Settings = Settings.Load();
        if (Settings.LoadError is { } loadError) Write(loadError);
        bridge.Message += message => Post(() => HandleMessage(message));
        bridge.Error += text => Post(() => Write(text));
        bridge.Exited += () => Post(() =>
        {
            if (State is ConnectionState.Connected or ConnectionState.Connecting) SetDisconnected("Device bridge stopped");
        });
        engine.Activity += text => Post(() => Write(text));
    }

    private void Post(Action action) => ui.Post(_ => { if (!disposed) action(); }, null);

    public void Write(string text, bool isButton = false)
    {
        LogEntry entry = new(DateTime.Now, text, isButton);
        Log.Add(entry);
        if (Log.Count > 400) Log.RemoveAt(0);
        Logged?.Invoke(entry);
    }

    public void Save()
    {
        try { Settings.Save(); }
        catch (Exception error) { Write("Could not save settings: " + error.Message); }
    }

    // ── Connection ────────────────────────────────────────────────────────────

    public static bool IsGHubRunning() =>
        Process.GetProcessesByName("lghub_agent").Length > 0 || Process.GetProcessesByName("lghub").Length > 0;

    public void Connect()
    {
        if (bridge.Running) return;
        if (IsGHubRunning())
        {
            State = ConnectionState.GHubRunning;
            StateDetail = "Exit G HUB (including its tray icon) to connect";
            Write("G HUB is running. Exit it completely before connecting.");
            StateChanged?.Invoke();
            return;
        }
        try
        {
            bridge.Start();
            State = ConnectionState.Connecting;
            StateDetail = "Looking for the LIGHTSPEED receiver…";
            Write("Starting device bridge");
        }
        catch (Exception error)
        {
            State = ConnectionState.Disconnected;
            StateDetail = error.Message;
            Write(error.Message);
        }
        StateChanged?.Invoke();
    }

    public void Disconnect()
    {
        bridge.Stop();
        SetDisconnected("Disconnected by you");
    }

    private void SetDisconnected(string reason)
    {
        State = bridge.Running ? ConnectionState.Connecting : ConnectionState.Disconnected;
        StateDetail = reason;
        ReleaseAll();
        StateChanged?.Invoke();
    }

    private void ReleaseAll()
    {
        dpiShiftHeld = false;
        GShiftHeld = false;
        pressedAssignments.Clear();
        foreach (int bit in pressed.ToArray()) { pressed.Remove(bit); ButtonChanged?.Invoke(bit, false); }
        engine.StopAll();
    }

    private void HandleMessage(JsonElement message)
    {
        switch (message.GetProperty("type").GetString())
        {
            case "connected":
                State = ConnectionState.Connected;
                DeviceDpi = GetInt(message, "dpi");
                DeviceIntervalMs = GetInt(message, "report_interval_ms");
                StateDetail = "";
                Write($"Connected · {DeviceDpi} DPI · {DeviceReportRate} Hz");
                ApplyDeviceSettings();
                StateChanged?.Invoke();
                break;
            case "disconnected":
                string reason = message.TryGetProperty("message", out JsonElement text) ? text.GetString() ?? "" : "";
                if (State != ConnectionState.Connecting || StateDetail != FriendlyReason(reason)) Write("Waiting for mouse: " + reason);
                SetDisconnected(FriendlyReason(reason));
                break;
            case "button":
                OnSpyButton(message.GetProperty("bit").GetInt32(), message.GetProperty("down").GetBoolean());
                break;
            case "dpi":
                DeviceDpi = GetInt(message, "value");
                StateChanged?.Invoke();
                break;
            case "report_interval":
                DeviceIntervalMs = GetInt(message, "value");
                StateChanged?.Invoke();
                break;
            case "error":
                Write("Device error: " + message.GetProperty("message").GetString());
                break;
        }
    }

    private static string FriendlyReason(string reason) =>
        reason.Contains("Expected one vendor HID usage", StringComparison.Ordinal) ? "Receiver not found. Is it plugged in?"
        : reason.Contains("No reply", StringComparison.Ordinal) ? "Mouse is asleep or switched off"
        : reason;

    private static int GetInt(JsonElement item, string property) =>
        item.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

    // ── Input ─────────────────────────────────────────────────────────────────

    public void InstallHook()
    {
        try
        {
            hook = new MouseHook(control =>
                State == ConnectionState.Connected && Settings.SuppressStandardActions
                && MouseControls.ById(control) is { } physical && Settings.BitFor(physical) is int bit
                && !string.IsNullOrEmpty(EffectiveAssignment(bit)));
        }
        catch (Exception error) { Write("Default-action blocking unavailable: " + error.Message); }
    }

    /// <summary>Called by the window's Raw Input handler with a wheel pseudo-bit.</summary>
    public void OnWheelPulse(int bit)
    {
        if (Learning is { } control && control.Id is "TiltLeft" or "TiltRight" && bit is RawMouseWheel.Left or RawMouseWheel.Right)
        {
            FinishLearning(control, bit);
            return;
        }
        ButtonChanged?.Invoke(bit, true);
        ButtonChanged?.Invoke(bit, false);
        HandleAssignment(bit, true, pulse: true);
    }

    private void OnSpyButton(int bit, bool down)
    {
        if (down) pressed.Add(bit); else pressed.Remove(bit);
        MouseControl? control = Settings.ControlFor(bit);
        Write($"{(down ? "▼" : "▲")} 0x{bit:x4}  {control?.Label ?? "unmapped"}", isButton: true);
        ButtonChanged?.Invoke(bit, down);
        // Primary click is ignored while learning other controls so the UI stays clickable.
        if (down && Learning is { } learning && !learning.IsWheel && (bit != 0x0001 || learning.Id == "G1"))
        {
            FinishLearning(learning, bit);
            swallowRelease.Add(bit); // The calibration press must not also trigger its new assignment.
            return;
        }
        if (!down && swallowRelease.Remove(bit)) return;
        HandleAssignment(bit, down);
    }

    // ── Calibration ───────────────────────────────────────────────────────────

    public void StartLearning(MouseControl control)
    {
        Learning = control;
        StateChanged?.Invoke();
    }

    public void CancelLearning()
    {
        Learning = null;
        StateChanged?.Invoke();
    }

    private void FinishLearning(MouseControl control, int bit)
    {
        foreach (string other in Settings.ControlBits.Where(x => x.Value == bit && x.Key != control.Id).Select(x => x.Key).ToArray())
            Settings.ControlBits.Remove(other);
        if (control.DefaultBit == bit) Settings.ControlBits.Remove(control.Id);
        else Settings.ControlBits[control.Id] = bit;
        Learning = null;
        Save();
        Write($"Calibrated {control.Label} → 0x{bit:x4}");
        Calibrated?.Invoke(control, bit);
        SettingsChanged?.Invoke();
        StateChanged?.Invoke();
    }

    public void ForgetCalibration(MouseControl control)
    {
        Settings.ControlBits.Remove(control.Id);
        Save();
        SettingsChanged?.Invoke();
    }

    // ── Assignments ───────────────────────────────────────────────────────────

    public string EffectiveAssignment(int bit)
    {
        MouseProfile profile = ActiveProfile;
        return GShiftHeld && profile.ShiftAssignments.TryGetValue(bit, out string? shifted)
            ? shifted : profile.Assignments.GetValueOrDefault(bit) ?? "";
    }

    public void Assign(bool shiftLayer, int bit, string? id)
    {
        Dictionary<int, string> bindings = shiftLayer ? ActiveProfile.ShiftAssignments : ActiveProfile.Assignments;
        if (string.IsNullOrEmpty(id)) bindings.Remove(bit); else bindings[bit] = id;
        Save();
    }

    private void HandleAssignment(int bit, bool down, bool pulse = false)
    {
        MouseProfile profile = ActiveProfile;
        if (profile.Assignments.GetValueOrDefault(bit) == BuiltinActions.GShift)
        {
            if (GShiftHeld == down) return;
            GShiftHeld = down;
            StateChanged?.Invoke();
            return;
        }
        string assignment;
        if (pulse) assignment = EffectiveAssignment(bit);
        else if (down) pressedAssignments[bit] = assignment = EffectiveAssignment(bit);
        else
        {
            assignment = pressedAssignments.GetValueOrDefault(bit) ?? EffectiveAssignment(bit);
            pressedAssignments.Remove(bit);
        }
        switch (assignment)
        {
            case "" or BuiltinActions.Disabled:
                return;
            case BuiltinActions.DpiShift:
                if (pulse || down == dpiShiftHeld) return;
                dpiShiftHeld = down;
                SendDpi(down ? profile.ShiftDpi : profile.Dpi);
                return;
            case BuiltinActions.DpiUp or BuiltinActions.DpiDown or BuiltinActions.DpiCycle:
                if (down) StepDpi(assignment);
                return;
            case BuiltinActions.GShift:
                return; // G-Shift on the shift layer itself has no meaning.
        }
        if (Settings.Macros.FirstOrDefault(m => m.Id == assignment) is { } macro)
        {
            try { engine.Handle(bit, down, macro, pulse); }
            catch (Exception error) { Write("Macro error: " + error.Message); }
        }
    }

    public void TestMacro(MacroDefinition macro) => engine.PlayOnce(macro);
    public void StopTest() => engine.Stop(MacroEngine.TestTrigger);
    public bool IsTestRunning => engine.IsRunning(MacroEngine.TestTrigger);

    // ── DPI & report rate ─────────────────────────────────────────────────────

    private void StepDpi(string action)
    {
        MouseProfile profile = ActiveProfile;
        List<int> stages = profile.DpiStages.Distinct().Order().ToList();
        if (stages.Count == 0) return;
        int current = Math.Max(0, stages.IndexOf(profile.Dpi));
        int next = action switch
        {
            BuiltinActions.DpiDown => Math.Max(0, current - 1),
            BuiltinActions.DpiUp => Math.Min(stages.Count - 1, current + 1),
            _ => (current + 1) % stages.Count
        };
        profile.Dpi = stages[next];
        Save();
        SendDpi(profile.Dpi);
        Write($"DPI {profile.Dpi}");
        SettingsChanged?.Invoke();
    }

    public void SendDpi(int dpi)
    {
        if (State != ConnectionState.Connected) return;
        try { bridge.Send("set_dpi", dpi); }
        catch (Exception error) { Write(error.Message); }
    }

    public void SendReportRate(int hz)
    {
        if (State != ConnectionState.Connected) return;
        try { bridge.Send("set_report_interval", 1000 / hz); }
        catch (Exception error) { Write(error.Message); }
    }

    public void ApplyDeviceSettings()
    {
        SendDpi(dpiShiftHeld ? ActiveProfile.ShiftDpi : ActiveProfile.Dpi);
        SendReportRate(ActiveProfile.ReportRate);
    }

    // ── Profiles ──────────────────────────────────────────────────────────────

    public void SelectProfile(MouseProfile profile)
    {
        if (Settings.ActiveProfileId == profile.Id && Settings.ActiveProfile == profile) return;
        Settings.ActiveProfileId = profile.Id;
        Save();
        ReleaseAll();
        engine.ResetSequences();
        ApplyDeviceSettings();
        Write($"Profile: {profile.Name}");
        StateChanged?.Invoke();
        SettingsChanged?.Invoke();
    }

    /// <summary>Picks the profile for the foreground application. Called on a UI timer.</summary>
    public void AutoSelectProfile(nint ownWindow)
    {
        if (!Settings.AutoSwitchProfiles) return;
        try
        {
            nint foreground = GetForegroundWindow();
            if (foreground == ownWindow || foreground == nint.Zero) return;
            _ = GetWindowThreadProcessId(foreground, out uint pid);
            if (pid == 0 || pid == Environment.ProcessId) return;
            using Process process = Process.GetProcessById((int)pid);
            string name = process.ProcessName;
            MouseProfile target = Settings.Profiles.FirstOrDefault(p => !p.IsDesktop
                && string.Equals(Path.GetFileNameWithoutExtension(p.ApplicationPath), name, StringComparison.OrdinalIgnoreCase))
                ?? Settings.Profiles.FirstOrDefault(p => p.IsDesktop) ?? Settings.Profiles[0];
            if (Settings.ActiveProfileId != target.Id) SelectProfile(target);
        }
        catch (Exception) { /* Some protected foreground processes reject inspection. */ }
    }

    /// <summary>Fakes a connected mouse for --snapshot renders.</summary>
    internal void SimulateConnected(int dpi, int intervalMs)
    {
        State = ConnectionState.Connected;
        DeviceDpi = dpi;
        DeviceIntervalMs = intervalMs;
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        disposed = true;
        hook?.Dispose();
        engine.Dispose();
        bridge.Dispose();
        Save();
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
