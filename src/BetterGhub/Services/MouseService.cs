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
    private readonly HidppBridge bridge = new();
    private readonly MacroEngine engine = new();
    private readonly Dictionary<int, string> pressedAssignments = [];
    private readonly HashSet<int> pressed = [];
    private readonly HashSet<int> swallowRelease = [];
    /// <summary>What to do when a button holding a key or mouse button is released, by trigger bit.</summary>
    private readonly Dictionary<int, Action> heldOutputs = [];
    private MouseHook? hook;
    private bool disposed;
    /// <summary>The running on-board slot as a profile the pages edit, and as it was last read (to diff against).</summary>
    private MouseProfile? onboardProfile, onboardBaseline;
    private bool onboardDirty, onboardBackedUp;
    private Timer? onboardTimer;
    /// <summary>The profile the pages last rendered, to notice when <see cref="ActiveProfile"/> becomes another one.</summary>
    private MouseProfile? shownProfile;

    public Settings Settings { get; }
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string StateDetail { get; private set; } = "";
    public int DeviceDpi { get; private set; }
    public int DeviceIntervalMs { get; private set; }
    public bool GShiftHeld { get; private set; }
    public bool DpiShiftHeld { get; private set; }
    public MouseControl? Learning { get; private set; }
    public IReadOnlyCollection<int> Pressed => pressed;
    public List<LogEntry> Log { get; } = [];
    /// <summary>The profile the pages show and edit: the running on-board slot in on-board mode, else the BetterGhub profile.</summary>
    public MouseProfile ActiveProfile => IsOnboard && onboardProfile is { } slot ? slot : Settings.ActiveProfile;
    public int DeviceReportRate => DeviceIntervalMs > 0 ? 1000 / DeviceIntervalMs : 0;
    /// <summary>Charge in percent, or null until the mouse reports it.</summary>
    public int? BatteryPercent { get; private set; }
    public bool BatteryCharging { get; private set; }
    /// <summary>Firmware versions, kept after a disconnect since they don't change without an update.</summary>
    public string? MouseFirmware { get; private set; }
    public string? ReceiverFirmware { get; private set; }
    /// <summary>On-board memory as last read from the mouse; kept after a disconnect.</summary>
    public OnboardMemory? Onboard { get; private set; }
    public IReadOnlyList<OnboardSlot>? OnboardSlots => Onboard?.Slots;
    /// <summary>A write to on-board memory is in flight.</summary>
    public bool OnboardBusy { get; private set; }
    /// <summary>Sector of the on-board slot the mouse runs from, or null in host mode (BetterGhub handles the buttons).</summary>
    public int? OnboardSector { get; private set; }
    /// <summary>
    /// Connected with the mouse running an on-board slot: the mouse carries out its own buttons, and the
    /// Assignments and Sensitivity pages edit that slot, saving each change to the mouse.
    /// </summary>
    public bool IsOnboard => State == ConnectionState.Connected && OnboardSector is not null;
    public OnboardSlot? ActiveOnboardSlot => OnboardSector is int sector ? OnboardSlots?.FirstOrDefault(s => s.Sector == sector) : null;
    /// <summary>Report interval the battery estimate uses; 1 ms until the mouse reports its rate.</summary>
    public int PowerIntervalMs => DeviceIntervalMs > 0 ? DeviceIntervalMs : 1;

    /// <summary>Rough hours left at the current charge and report rate, like G HUB's; null while charging or unknown.</summary>
    public int? BatteryHoursLeft => BatteryPercent is int percent && !BatteryCharging ? (int)Math.Round(PowerModel.HoursLeft(percent, PowerIntervalMs)) : null;

    /// <summary>Connection, DPI, profile or layer changed.</summary>
    public event Action? StateChanged;
    public event Action<int, bool>? ButtonChanged;
    public event Action<LogEntry>? Logged;
    /// <summary>Settings changed outside the page being edited (profile switch, DPI button, calibration).</summary>
    public event Action? SettingsChanged;
    public event Action<MouseControl, int>? Calibrated;
    /// <summary>An on-board memory write finished: success and a message for the user.</summary>
    public event Action<bool, string>? OnboardWritten;
    /// <summary>Something the user should see as a toast, e.g. an assignment the mouse can't store.</summary>
    public event Action<string>? Notice;

    public MouseService(SynchronizationContext ui)
    {
        this.ui = ui;
        Settings = Settings.Load();
        if (Settings.LoadError is { } loadError) Write(loadError);
        bridge.Event += item => Post(() => HandleEvent(item));
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

    /// <summary>Saves settings; in on-board mode also writes any change to the running slot, shortly after edits stop.</summary>
    public void Save()
    {
        try { Settings.Save(); }
        catch (Exception error) { Write("Could not save settings: " + error.Message); }
        if (IsOnboard)
        {
            SnapToOnboardSpeeds(ActiveProfile);
            ScheduleOnboardWrite();
        }
    }

    /// <summary>
    /// An on-board slot stores DPI Shift and the current speed as positions in its speed list, so both snap
    /// to the closest speed (e.g. Restore default speeds' 100 DPI shift becomes 200).
    /// </summary>
    private static void SnapToOnboardSpeeds(MouseProfile profile)
    {
        List<int> stages = profile.DpiStages.Where(d => d is >= 100 and <= 25600).Distinct().Take(5).ToList();
        if (stages.Count == 0) return;
        profile.ShiftDpi = stages.MinBy(s => Math.Abs(s - profile.ShiftDpi));
        profile.Dpi = stages.MinBy(s => Math.Abs(s - profile.Dpi));
    }

    public void ExportSettings(string path)
    {
        Settings.Export(path);
        Write("Settings exported to " + path);
    }

    /// <summary>Replaces all profiles, macros and calibration with the file's. Throws when it can't be read.</summary>
    public void ImportSettings(string path)
    {
        Settings imported = Settings.Import(path);
        ReleaseAll();
        engine.ResetSequences();
        Settings.ReplaceWith(imported);
        Save();
        ApplyDeviceSettings();
        Write("Settings imported from " + path);
        StateChanged?.Invoke();
        SettingsChanged?.Invoke();
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
            bridge.OnboardSector = Settings.OnboardSector ?? 0;
            bridge.Start();
            State = ConnectionState.Connecting;
            StateDetail = "Looking for the LIGHTSPEED receiver…";
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
        OnboardBusy = false;
        onboardDirty = false;
        onboardTimer?.Dispose();
        onboardTimer = null;
        State = bridge.Running ? ConnectionState.Connecting : ConnectionState.Disconnected;
        StateDetail = reason;
        BatteryPercent = null;
        BatteryCharging = false;
        ReleaseAll();
        StateChanged?.Invoke();
        NotifyIfProfileChanged();
    }

    private void ReleaseAll()
    {
        DpiShiftHeld = false;
        GShiftHeld = false;
        pressedAssignments.Clear();
        foreach (int bit in heldOutputs.Keys.ToArray()) ReleaseOutput(bit);
        foreach (int bit in pressed.ToArray()) { pressed.Remove(bit); ButtonChanged?.Invoke(bit, false); }
        engine.StopAll();
    }

    private void HandleEvent(DeviceEvent item)
    {
        HandleEventCore(item);
        NotifyIfProfileChanged();
    }

    /// <summary>
    /// Refreshes the pages when the profile they show is a different one: the on-board slot is read, the
    /// connection completes (only then does <see cref="IsOnboard"/> hold), the mode switches, or it disconnects.
    /// </summary>
    private void NotifyIfProfileChanged()
    {
        if (ReferenceEquals(ActiveProfile, shownProfile)) return;
        shownProfile = ActiveProfile;
        SettingsChanged?.Invoke();
    }

    private void HandleEventCore(DeviceEvent item)
    {
        switch (item)
        {
            case ConnectedEvent connected:
                State = ConnectionState.Connected;
                DeviceDpi = connected.Dpi;
                DeviceIntervalMs = connected.ReportIntervalMs;
                StateDetail = "";
                Write($"Connected · {DeviceDpi} DPI · {DeviceReportRate} Hz");
                ApplyDeviceSettings();
                StateChanged?.Invoke();
                break;
            case DisconnectedEvent disconnected:
                if (State != ConnectionState.Connecting || StateDetail != disconnected.Reason) Write("Waiting for mouse: " + disconnected.Reason);
                SetDisconnected(disconnected.Reason);
                break;
            case ButtonEvent button:
                OnSpyButton(button.Bit, button.Down);
                break;
            case DpiEvent dpi:
                DeviceDpi = dpi.Dpi;
                StateChanged?.Invoke();
                break;
            case ReportIntervalEvent rate:
                DeviceIntervalMs = rate.ReportIntervalMs;
                StateChanged?.Invoke();
                break;
            case FirmwareEvent firmware:
                MouseFirmware = firmware.Mouse ?? MouseFirmware;
                ReceiverFirmware = firmware.Receiver ?? ReceiverFirmware;
                StateChanged?.Invoke();
                break;
            case OnboardMemoryEvent onboard:
                Onboard = onboard.Memory;
                RebuildOnboardProfile();
                if (onboardDirty && !OnboardBusy) FlushOnboard(); // Edits made while the last write was in flight.
                StateChanged?.Invoke();
                break;
            case OnboardWriteEvent written:
                OnboardBusy = false;
                Write((written.Success ? "On-board memory: " : "On-board write failed: ") + written.Message);
                OnboardWritten?.Invoke(written.Success, written.Message);
                StateChanged?.Invoke();
                break;
            case ModeEvent mode:
                bool changed = OnboardSector != (mode.OnboardSector == 0 ? null : mode.OnboardSector);
                OnboardSector = mode.OnboardSector == 0 ? null : mode.OnboardSector;
                if (Settings.OnboardSector != OnboardSector)
                {
                    Settings.OnboardSector = OnboardSector;
                    try { Settings.Save(); } catch (Exception error) { Write("Could not save settings: " + error.Message); }
                }
                ReleaseAll();
                onboardDirty = false;
                onboardBackedUp = false;
                RebuildOnboardProfile();
                if (changed) Write(OnboardSector is null ? "BetterGhub handles the mouse (host mode)" : $"Mouse runs {ActiveOnboardSlot?.DisplayName ?? "its on-board slot"} from its own memory");
                if (OnboardSector is null && State == ConnectionState.Connected) ApplyDeviceSettings();
                StateChanged?.Invoke();
                SettingsChanged?.Invoke();
                break;
            case BatteryEvent battery:
                if (BatteryPercent is null) Write($"Battery {battery.Percent}%{(battery.Charging ? ", charging" : "")}");
                else if (battery.Charging != BatteryCharging) Write(battery.Charging ? "Charging" : "Running on battery");
                BatteryPercent = battery.Percent;
                BatteryCharging = battery.Charging;
                StateChanged?.Invoke();
                break;
            case DeviceErrorEvent error:
                Write("Device error: " + error.Message);
                break;
        }
    }

    // ── Input ─────────────────────────────────────────────────────────────────

    public void InstallHook()
    {
        try
        {
            hook = new MouseHook(control =>
                State == ConnectionState.Connected && !IsOnboard
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
        if (IsOnboard) return; // The mouse carries out its own on-board actions.
        HandleAssignment(bit, true, pulse: true);
    }

    private void OnSpyButton(int bit, bool down)
    {
        if (down) pressed.Add(bit); else pressed.Remove(bit);
        MouseControl? control = Settings.ControlFor(bit);
        Write($"{(down ? "▼" : "▲")} 0x{bit:x4}  {control?.Label ?? "unmapped"}", isButton: true);
        ButtonChanged?.Invoke(bit, down);
        // Left and right click can't be learned by another control, so the UI stays clickable while learning.
        if (down && Learning is { } learning && !learning.IsWheel && !MouseControls.IsFixedBit(bit))
        {
            FinishLearning(learning, bit);
            swallowRelease.Add(bit); // The calibration press must not also trigger its new assignment.
            return;
        }
        if (!down && swallowRelease.Remove(bit)) return;
        if (IsOnboard) return;
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
        return GShiftHeld && profile.ShiftAssignments.TryGetValue(bit, out string? shifted) ? shifted
            : profile.Assignments.GetValueOrDefault(bit) ?? Settings.ControlFor(bit)?.SoftwareDefault ?? "";
    }

    /// <summary>Deletes a macro and clears every button, in every profile and layer, that was assigned to it.</summary>
    public void DeleteMacro(MacroDefinition macro)
    {
        foreach (MouseProfile profile in Settings.Profiles)
        {
            foreach (int bit in profile.Assignments.Where(x => x.Value == macro.Id).Select(x => x.Key).ToArray()) profile.Assignments.Remove(bit);
            foreach (int bit in profile.ShiftAssignments.Where(x => x.Value == macro.Id).Select(x => x.Key).ToArray()) profile.ShiftAssignments.Remove(bit);
        }
        Settings.Macros.Remove(macro);
        Save();
    }

    /// <summary>Assigns an action; false (with a <see cref="Notice"/>) when the on-board slot can't store it.</summary>
    public bool Assign(bool shiftLayer, int bit, string? id)
    {
        if (IsOnboard && OnboardRefusal(bit, id) is { } refusal)
        {
            Write(refusal);
            Notice?.Invoke(refusal);
            return false;
        }
        Dictionary<int, string> bindings = shiftLayer ? ActiveProfile.ShiftAssignments : ActiveProfile.Assignments;
        if (string.IsNullOrEmpty(id)) bindings.Remove(bit); else bindings[bit] = id;
        Save();
        return true;
    }

    private string? OnboardRefusal(int bit, string? id)
    {
        MouseControl? control = Settings.ControlFor(bit);
        if (control is null || OnboardProfiles.IndexFor(control.Id) is null)
            return $"{control?.Label ?? "This control"} isn't available for on-board profiles";
        if (!string.IsNullOrEmpty(id) && OnboardProfiles.Encode(id) is null)
            return $"{Settings.DescribeAssignment(id)} isn't available for on-board profiles";
        return null;
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
                if (pulse || down == DpiShiftHeld) return;
                DpiShiftHeld = down;
                SendDpi(down ? profile.ShiftDpi : profile.Dpi);
                StateChanged?.Invoke();
                return;
            case BuiltinActions.DpiUp or BuiltinActions.DpiDown or BuiltinActions.DpiCycle:
                if (down) StepDpi(assignment);
                return;
            case BuiltinActions.GShift:
                return; // G-Shift on the shift layer itself has no meaning.
        }
        try
        {
            if (RunDirect(bit, down, pulse, assignment)) return;
        }
        catch (Exception error)
        {
            Write($"{Settings.DescribeAssignment(assignment)} failed: {error.Message}");
            return;
        }
        if (Settings.Macros.FirstOrDefault(m => m.Id == assignment) is { } macro)
        {
            try { engine.Handle(bit, down, macro, pulse); }
            catch (Exception error) { Write("Macro error: " + error.Message); }
        }
    }

    /// <summary>Keys, mouse buttons, scrolling and launches. Returns false when the assignment is something else.</summary>
    private bool RunDirect(int bit, bool down, bool pulse, string assignment)
    {
        if (Assignments.KeyCombo(assignment) is { } combo)
        {
            List<ushort> keys = InputSender.ParseCombo(combo);
            Hold(bit, down, pulse, () => InputSender.PressCombo(keys), () => InputSender.ReleaseCombo(keys));
            return true;
        }
        if (Assignments.LaunchPath(assignment) is { } path)
        {
            if (!down) return true;
            if (!File.Exists(path)) throw new FileNotFoundException("Application not found", path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) ?? "" });
            return true;
        }
        int button = assignment switch
        {
            BuiltinActions.LeftClick => 0,
            BuiltinActions.RightClick => 1,
            BuiltinActions.MiddleClick => 2,
            BuiltinActions.Back => 3,
            BuiltinActions.Forward => 4,
            _ => -1
        };
        if (button >= 0)
        {
            Hold(bit, down, pulse, () => InputSender.MouseButton(button, up: false), () => InputSender.MouseButton(button, up: true));
            return true;
        }
        if (!down) return assignment is BuiltinActions.DoubleClick or BuiltinActions.ScrollUp or BuiltinActions.ScrollDown
            or BuiltinActions.ScrollLeft or BuiltinActions.ScrollRight or BuiltinActions.LockScreen;
        switch (assignment)
        {
            case BuiltinActions.DoubleClick: InputSender.LeftClick(); InputSender.LeftClick(); return true;
            case BuiltinActions.ScrollUp: InputSender.Wheel(1); return true;
            case BuiltinActions.ScrollDown: InputSender.Wheel(-1); return true;
            case BuiltinActions.ScrollLeft: InputSender.HorizontalWheel(-1); return true;
            case BuiltinActions.ScrollRight: InputSender.HorizontalWheel(1); return true;
            case BuiltinActions.LockScreen: _ = LockWorkStation(); return true;
        }
        return false;
    }

    /// <summary>Holds an output for as long as the button is held, like G HUB; wheel pulses tap it.</summary>
    private void Hold(int bit, bool down, bool pulse, Action press, Action release)
    {
        if (pulse)
        {
            press();
            release();
        }
        else if (down)
        {
            if (heldOutputs.ContainsKey(bit)) return;
            press();
            heldOutputs[bit] = release;
        }
        else ReleaseOutput(bit);
    }

    private void ReleaseOutput(int bit)
    {
        if (!heldOutputs.Remove(bit, out Action? release)) return;
        try { release(); }
        catch (Exception error) { Write("Could not release input: " + error.Message); }
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
        if (State != ConnectionState.Connected) return; // Also applies on-board, straight away; the slot write follows.
        try { bridge.SetDpi(dpi); }
        catch (Exception error) { Write(error.Message); }
    }

    /// <summary>Re-reads the on-board profile slots (read-only).</summary>
    public void ReadOnboardMemory()
    {
        if (State != ConnectionState.Connected) return;
        bridge.ReadOnboardMemory();
    }

    // ── On-board memory ───────────────────────────────────────────────────────

    public static string OnboardBackupFolder => Path.Combine(Path.GetDirectoryName(Settings.FilePath) ?? ".", "onboard-backups");

    /// <summary>Starting point for editing a slot: what the mouse holds now.</summary>
    public static OnboardEdit EditFor(OnboardSlot slot)
    {
        OnboardProfile profile = slot.Profile ?? throw new InvalidOperationException("This slot could not be read");
        return new OnboardEdit(profile.Name, slot.Enabled, Math.Max(1, profile.ReportIntervalMs), profile.Dpis,
            Math.Min(profile.DefaultDpiIndex, profile.Dpis.Count - 1), Math.Min(profile.ShiftDpiIndex, profile.Dpis.Count - 1),
            new Dictionary<int, uint>(), new Dictionary<int, uint>());
    }

    /// <summary>
    /// Converts a BetterGhub profile into an edit: its name, DPI speeds, report rate and every assignment the
    /// mouse can store. <paramref name="notes"/> lists what couldn't be carried over.
    /// </summary>
    public OnboardEdit EditFromProfile(MouseProfile profile, bool enabled, List<string> notes)
    {
        List<int> stages = profile.DpiStages.Where(d => d is >= 100 and <= 25600).Distinct().Order().ToList();
        if (stages.Count == 0) stages = [profile.Dpi];
        if (stages.Count > 5)
        {
            notes.Add($"Only 5 DPI speeds fit; kept {string.Join(", ", stages.Take(5))}");
            stages = stages.Take(5).ToList();
        }
        int Nearest(int dpi) => stages.IndexOf(stages.MinBy(s => Math.Abs(s - dpi)));
        int defaultIndex = Nearest(profile.Dpi), shiftIndex = Nearest(profile.ShiftDpi);
        if (stages[shiftIndex] != profile.ShiftDpi) notes.Add($"DPI Shift {profile.ShiftDpi} isn't one of the speeds; the mouse will use {stages[shiftIndex]}");
        Dictionary<int, uint> buttons = [], shifted = [];
        foreach (MouseControl control in MouseControls.All)
        {
            if (OnboardProfiles.IndexFor(control.Id) is not int index) continue;
            int? bit = Settings.BitFor(control);
            string normal = bit is int b && profile.Assignments.GetValueOrDefault(b) is { Length: > 0 } assigned ? assigned : OnboardProfiles.NativeAction(control);
            string shift = normal == BuiltinActions.GShift ? normal
                : bit is int s && profile.ShiftAssignments.GetValueOrDefault(s) is { Length: > 0 } shiftAssigned ? shiftAssigned : normal;
            if (OnboardProfiles.Encode(normal) is uint code) buttons[index] = code;
            else notes.Add($"{control.Label}: {Settings.DescribeAssignment(normal)} can't be stored on the mouse; left as it was");
            if (OnboardProfiles.Encode(shift) is uint shiftCode) shifted[index] = shiftCode;
            else if (shift != normal) notes.Add($"{control.Label} with G-Shift: {Settings.DescribeAssignment(shift)} can't be stored on the mouse; left as it was");
        }
        return new OnboardEdit(profile.Name, enabled, 1000 / Math.Clamp(profile.ReportRate, 125, 1000), stages, defaultIndex, shiftIndex, buttons, shifted);
    }

    /// <summary>Backs up every slot (unless <paramref name="backup"/> is false), then writes the edited slot and the directory if enabling changed.</summary>
    public void SaveOnboardSlot(OnboardSlot slot, OnboardEdit edit, bool backup = true)
    {
        OnboardMemory memory = RequireOnboard();
        OnboardProfile profile = slot.Profile ?? throw new InvalidOperationException("This slot could not be read");
        List<SectorWrite> writes = [new(slot.Sector, profile.Sector, OnboardProfiles.Build(profile.Sector, edit))];
        if (edit.Enabled != slot.Enabled)
        {
            if (!edit.Enabled && memory.Slots.Count(s => s.Enabled) <= 1) throw new InvalidOperationException("Keep at least one slot enabled");
            writes.Add(new(0, memory.Directory, OnboardProfiles.SetEnabled(memory.Directory, slot.Sector, edit.Enabled)));
        }
        if (!edit.Enabled && slot.Sector == OnboardSector) throw new InvalidOperationException("The mouse is running this slot; switch to another one first");
        if (backup) BackupOnboard(memory);
        SendWrite(writes, $"Saving {(edit.Name.Length > 0 ? edit.Name : $"Profile {slot.Number}")} to slot {slot.Number}");
    }

    /// <summary>Writes a backup file's sectors back, after backing up what's there now. Returns the new backup's path.</summary>
    public string RestoreOnboard(string path)
    {
        OnboardMemory memory = RequireOnboard();
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement sectors = document.RootElement.GetProperty("sectors");
        List<SectorWrite> writes = [];
        foreach (JsonProperty entry in sectors.EnumerateObject())
        {
            int sector = int.Parse(entry.Name);
            byte[] data = Convert.FromHexString(entry.Value.GetString() ?? "");
            byte[]? current = sector == 0 ? memory.Directory : memory.Slots.FirstOrDefault(s => s.Sector == sector)?.Profile?.Sector;
            if (current is null) throw new InvalidDataException($"Sector {sector} in the backup isn't a slot on this mouse");
            if (data.Length != memory.SectorSize) throw new InvalidDataException("The backup is from a mouse with a different memory layout");
            writes.Add(new(sector, current, data));
        }
        if (writes.Count == 0) throw new InvalidDataException("The backup has no sectors");
        string backup = BackupOnboard(memory);
        SendWrite(writes, "Restoring on-board memory from " + Path.GetFileName(path));
        return backup;
    }

    /// <summary>
    /// Runs the mouse from <paramref name="slot"/> in its own memory. BetterGhub stays connected: the pages then
    /// edit that slot. Remembered, so BetterGhub reconnects in on-board mode.
    /// </summary>
    public void SwitchToOnboard(OnboardSlot slot)
    {
        if (State != ConnectionState.Connected) throw new InvalidOperationException("Connect the mouse first");
        if (!slot.Enabled) throw new InvalidOperationException("Enable this slot first");
        FlushOnboard();
        bridge.SetMode(slot.Sector);
    }

    /// <summary>Back to host mode: BetterGhub handles the buttons with its own profiles.</summary>
    public void UseHostMode()
    {
        FlushOnboard();
        Settings.OnboardSector = null;
        try { Settings.Save(); } catch (Exception error) { Write("Could not save settings: " + error.Message); }
        if (State == ConnectionState.Connected) bridge.SetMode(0);
    }

    /// <summary>
    /// Refreshes the slot profile from the latest read. While edits are waiting to be written the edited
    /// profile is kept and only the baseline moves. Returns whether the pages' profile was replaced.
    /// </summary>
    private bool RebuildOnboardProfile()
    {
        if (ActiveOnboardSlot is not { Profile: not null } slot)
        {
            onboardProfile = onboardBaseline = null;
            return true;
        }
        onboardBaseline = ProfileFromSlot(slot);
        if (onboardDirty || OnboardBusy) return false;
        onboardProfile = ProfileFromSlot(slot);
        return true;
    }

    /// <summary>A slot as a BetterGhub profile: its DPI speeds, report rate, and each binding decoded to an action.</summary>
    private MouseProfile ProfileFromSlot(OnboardSlot slot)
    {
        OnboardProfile stored = slot.Profile!;
        List<int> dpis = stored.Dpis.Count > 0 ? [.. stored.Dpis] : [800];
        MouseProfile profile = new()
        {
            Id = $"onboard-{slot.Sector}",
            Name = slot.DisplayName,
            DpiStages = dpis,
            Dpi = dpis[Math.Clamp(stored.DefaultDpiIndex, 0, dpis.Count - 1)],
            ShiftDpi = dpis[Math.Clamp(stored.ShiftDpiIndex, 0, dpis.Count - 1)],
            ReportRate = 1000 / Math.Clamp(stored.ReportIntervalMs, 1, 8)
        };
        foreach (MouseControl control in MouseControls.All)
        {
            if (OnboardProfiles.IndexFor(control.Id) is not int index || Settings.BitFor(control) is not int bit) continue;
            string normal = OnboardProfiles.BindingAt(stored.Sector, index, shift: false) is uint code ? OnboardProfiles.Decode(code) : OnboardProfiles.NativeAction(control);
            profile.Assignments[bit] = normal;
            if (OnboardProfiles.BindingAt(stored.Sector, index, shift: true) is uint shiftCode && OnboardProfiles.Decode(shiftCode) is var shifted && shifted != normal)
                profile.ShiftAssignments[bit] = shifted;
        }
        return profile;
    }

    private void ScheduleOnboardWrite()
    {
        onboardDirty = true;
        onboardTimer?.Dispose();
        onboardTimer = new Timer(_ => Post(FlushOnboard), null, 600, Timeout.Infinite);
    }

    /// <summary>
    /// Writes what changed in the slot profile since it was read. Only changed buttons are written, so bindings
    /// BetterGhub can't show (or controls that aren't calibrated) stay as the mouse had them.
    /// </summary>
    private void FlushOnboard()
    {
        onboardTimer?.Dispose();
        onboardTimer = null;
        if (!onboardDirty) return;
        if (!IsOnboard || onboardProfile is not { } edited || onboardBaseline is not { } baseline || ActiveOnboardSlot is not { Profile: { } stored } slot)
        {
            onboardDirty = false;
            return;
        }
        if (OnboardBusy) return; // Runs again when the write's re-read arrives.
        onboardDirty = false;

        Dictionary<int, uint> buttons = [], shifted = [];
        foreach (MouseControl control in MouseControls.All)
        {
            if (OnboardProfiles.IndexFor(control.Id) is not int index || Settings.BitFor(control) is not int bit) continue;
            string Normal(MouseProfile p) => p.Assignments.GetValueOrDefault(bit) is { Length: > 0 } a ? a : OnboardProfiles.NativeAction(control);
            string Shift(MouseProfile p, string normal) => normal == BuiltinActions.GShift ? normal
                : p.ShiftAssignments.GetValueOrDefault(bit) is { Length: > 0 } a ? a : normal;
            string normal = Normal(edited), before = Normal(baseline);
            if (normal != before && OnboardProfiles.Encode(normal) is uint code) buttons[index] = code;
            string shift = Shift(edited, normal), shiftBefore = Shift(baseline, before);
            if (shift != shiftBefore && OnboardProfiles.Encode(shift) is uint shiftCode) shifted[index] = shiftCode;
        }
        List<int> stages = edited.DpiStages.Where(d => d is >= 100 and <= 25600).Distinct().Take(5).ToList();
        if (stages.Count == 0) stages = [edited.Dpi];
        int Nearest(int dpi) => stages.IndexOf(stages.MinBy(s => Math.Abs(s - dpi)));
        int shiftIndex = Nearest(edited.ShiftDpi);
        OnboardEdit edit = new(stored.Name, slot.Enabled, 1000 / Math.Clamp(edited.ReportRate, 125, 1000), stages, Nearest(edited.Dpi), shiftIndex, buttons, shifted);
        try
        {
            if (OnboardProfiles.Build(stored.Sector, edit).AsSpan().SequenceEqual(stored.Sector)) return;
            SaveOnboardSlot(slot, edit, backup: !onboardBackedUp);
            onboardBackedUp = true; // One backup per on-board session, not one per change.
        }
        catch (Exception error)
        {
            Write("Could not save to the mouse: " + error.Message);
            Notice?.Invoke("Could not save to the mouse: " + error.Message);
        }
    }

    private OnboardMemory RequireOnboard()
    {
        if (State != ConnectionState.Connected) throw new InvalidOperationException("Connect the mouse first");
        if (OnboardBusy) throw new InvalidOperationException("A write is already in progress");
        return Onboard ?? throw new InvalidOperationException("On-board memory hasn't been read yet");
    }

    private void SendWrite(List<SectorWrite> writes, string description)
    {
        OnboardBusy = true;
        Write(description + "…");
        bridge.WriteOnboard(writes);
        StateChanged?.Invoke();
    }

    /// <summary>Saves the directory and every slot as hex, so any write can be undone with Restore.</summary>
    private string BackupOnboard(OnboardMemory memory)
    {
        Directory.CreateDirectory(OnboardBackupFolder);
        Dictionary<string, string> sectors = new() { ["0"] = Convert.ToHexString(memory.Directory) };
        foreach (OnboardSlot slot in memory.Slots)
            if (slot.Profile is { } profile) sectors[slot.Sector.ToString()] = Convert.ToHexString(profile.Sector);
        string path = Path.Combine(OnboardBackupFolder, $"onboard-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            created = DateTime.Now,
            mouse = "G502 X LIGHTSPEED",
            firmware = MouseFirmware,
            sectorSize = memory.SectorSize,
            sectors
        }, new JsonSerializerOptions { WriteIndented = true }));
        Write("On-board memory backed up to " + path);
        return path;
    }

    public void SendReportRate(int hz)
    {
        if (State != ConnectionState.Connected) return;
        try { bridge.SetReportInterval(1000 / hz); }
        catch (Exception error) { Write(error.Message); }
    }

    /// <summary>Sends the BetterGhub profile's DPI and report rate; on-board, the running slot keeps its own.</summary>
    public void ApplyDeviceSettings()
    {
        if (IsOnboard) return;
        SendDpi(DpiShiftHeld ? ActiveProfile.ShiftDpi : ActiveProfile.Dpi);
        SendReportRate(ActiveProfile.ReportRate);
    }

    // ── Profiles ──────────────────────────────────────────────────────────────

    public void SelectProfile(MouseProfile profile)
    {
        if (IsOnboard) UseHostMode(); // Picking a BetterGhub profile hands the buttons back to BetterGhub.
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
        if (!Settings.AutoSwitchProfiles || IsOnboard) return;
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
        BatteryPercent = 77;
        MouseFirmware = "30.0.14";
        ReceiverFirmware = "4.2.9";
        Onboard = SampleOnboard();
        StateChanged?.Invoke();
    }

    /// <summary>Fakes running on-board slot 1 for --snapshot renders.</summary>
    internal void SimulateOnboard()
    {
        OnboardSector = 1;
        RebuildOnboardProfile();
        StateChanged?.Invoke();
        SettingsChanged?.Invoke();
    }

    /// <summary>Slots shaped like a real read (G HUB's factory layout), for --snapshot renders.</summary>
    private static OnboardMemory SampleOnboard()
    {
        byte[] sector = new byte[255];
        Array.Fill(sector, (byte)0xFF);
        byte[] head = [1, 2, 1, 0x20, 0x03, 0xB0, 0x04, 0x40, 0x06, 0x60, 0x09, 0x80, 0x0C];
        head.CopyTo(sector, 0);
        uint[] buttons = [0x80010001, 0x80010002, 0x80010004, 0x80010008, 0x90070000, 0x80010010, 0x90010000, 0x90020000, 0x900A0000, 0x90030000, 0x90040000];
        for (int i = 0; i < buttons.Length; i++)
            for (int b = 0; b < 4; b++) sector[32 + i * 4 + b] = (byte)(buttons[i] >> (24 - b * 8));
        ushort crc = OnboardProfiles.Checksum(sector);
        sector[^2] = (byte)(crc >> 8);
        sector[^1] = (byte)crc;
        OnboardProfile profile = OnboardProfiles.Parse(sector, buttons.Length);
        byte[] directory = new byte[255];
        Array.Fill(directory, (byte)0xFF);
        for (int n = 1; n <= 5; n++) new byte[] { 0, (byte)n, (byte)(n <= 2 ? 1 : 0), 0xFF }.CopyTo(directory, (n - 1) * 4);
        return new OnboardMemory([.. Enumerable.Range(1, 5).Select(n => new OnboardSlot(n, n, n <= 2, profile))], OnboardProfiles.WithChecksum(directory), 255, buttons.Length);
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
    [DllImport("user32.dll")] private static extern bool LockWorkStation();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
