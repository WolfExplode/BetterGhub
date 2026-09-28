using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace BetterGhub.App;

internal sealed class MainForm : Form
{
    private static readonly Color Background = Color.FromArgb(11, 14, 19);
    private static readonly Color Surface = Color.FromArgb(24, 29, 37);
    private static readonly Color Surface2 = Color.FromArgb(35, 42, 52);
    private static readonly Color Accent = Color.FromArgb(36, 181, 216);
    private static readonly Color Muted = Color.FromArgb(157, 166, 181);
    private readonly Settings settings = Settings.Load();
    private readonly DeviceBridge bridge = new();
    private readonly MacroEngine engine = new();
    private readonly RawMouseWheel wheelInput = new();
    private MouseHook? mouseHook;
    private readonly Panel body = new() { Dock = DockStyle.Fill, BackColor = Background, Padding = new Padding(28) };
    private Panel pageHost = new();
    private Panel pageHeader = new();
    private readonly Label status = new() { AutoSize = true, ForeColor = Muted, Text = "Disconnected" };
    private readonly ComboBox profilePicker = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
    private readonly System.Windows.Forms.Timer profileTimer = new() { Interval = 1200 };
    private ListBox? eventLog;
    private readonly List<string> logLines = [];
    private string page = "Assignments";
    private bool connected;
    private int? learningButton;
    private int? learningCapturedBit;
    private int? selectedBit;
    private int deviceDpi;
    private int deviceInterval;
    private bool dpiShiftHeld;
    private bool gShiftHeld;
    private bool editingShiftLayer;
    private readonly Dictionary<int, string> pressedAssignments = [];

    public MainForm()
    {
        Text = "BetterGhub · G502 X LIGHTSPEED";
        ClientSize = new Size(1220, 790);
        MinimumSize = new Size(980, 650);
        BackColor = Background;
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10);
        StartPosition = FormStartPosition.CenterScreen;

        TableLayoutPanel top = new() { Dock = DockStyle.Top, Height = 76, BackColor = Surface, ColumnCount = 4, RowCount = 1, Padding = new Padding(24, 13, 24, 12) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 245));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
        Label title = new() { Text = "BETTERGHUB", Font = new Font("Segoe UI", 19, FontStyle.Bold), ForeColor = Color.White, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        Label device = new() { Text = "G502 X LIGHTSPEED", ForeColor = Muted, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false };
        Button connect = Button("Connect mouse", 145);
        connect.Dock = DockStyle.Fill;
        connect.Click += (_, _) => Connect();
        profilePicker.BackColor = Surface2; profilePicker.ForeColor = Color.White; profilePicker.FlatStyle = FlatStyle.Flat;
        profilePicker.Dock = DockStyle.Fill;
        top.Controls.Add(title, 0, 0); top.Controls.Add(device, 1, 0);
        top.Controls.Add(profilePicker, 2, 0); top.Controls.Add(connect, 3, 0);

        Panel nav = new() { Dock = DockStyle.Left, Width = 205, BackColor = Surface, Padding = new Padding(15, 22, 15, 12) };
        FlowLayoutPanel navItems = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        foreach (string item in new[] { "Assignments", "Macros", "DPI & rate", "Profiles", "Device" })
        {
            Button button = Button(item, 170);
            button.Height = 43;
            button.TextAlign = ContentAlignment.MiddleLeft;
            button.Click += (_, _) => { page = item; Render(); };
            navItems.Controls.Add(button);
        }
        nav.Controls.Add(navItems);

        Panel footer = new() { Dock = DockStyle.Bottom, Height = 38, BackColor = Surface, Padding = new Padding(20, 9, 0, 0) };
        footer.Controls.Add(status);
        Controls.Add(body); Controls.Add(nav); Controls.Add(footer); Controls.Add(top);

        bridge.Message += message => OnUi(() => HandleMessage(message));
        bridge.Error += message => OnUi(() => Status(message));
        engine.Activity += message => OnUi(() => Status(message));
        profileTimer.Tick += (_, _) => AutoSelectProfile();
        profileTimer.Start();
        Shown += (_, _) =>
        {
            try { wheelInput.Register(Handle); }
            catch (Exception error) { Status("Wheel capture unavailable: " + error.Message); }
            try { mouseHook = new MouseHook(bit => connected && settings.SuppressStandardActions && !string.IsNullOrEmpty(EffectiveAssignment(settings.ActiveProfile, bit))); }
            catch (Exception error) { Status("Default-action suppression unavailable: " + error.Message); }
        };
        RefreshProfiles();
        Render();
        FormClosing += (_, _) => { profileTimer.Stop(); mouseHook?.Dispose(); engine.Dispose(); bridge.Dispose(); settings.Save(); };
    }

    private void Connect()
    {
        if (bridge.Running) { Status("Mouse bridge already running"); return; }
        if (Process.GetProcessesByName("lghub_agent").Length > 0)
        {
            MessageBox.Show(this, "Exit G HUB completely, including its tray agent, then click Connect mouse. Two programs controlling the same HID++ receiver can interfere with each other.", "G HUB is running", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try { bridge.Start(); Status("Connecting to receiver…"); }
        catch (Exception error) { MessageBox.Show(this, error.Message, "Connection failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x00FF && wheelInput is not null)
        {
            try
            {
                int bit = wheelInput.Read(message.LParam);
                if (bit != 0)
                {
                    Log($"{DateTime.Now:HH:mm:ss.fff}  {LabelFor(bit),-14} PULSE");
                    HandleAssignment(bit, true, pulse: true);
                }
            }
            catch (Exception error) { Status("Wheel input error: " + error.Message); }
        }
        base.WndProc(ref message);
    }

    private void HandleMessage(JsonElement message)
    {
        string type = message.GetProperty("type").GetString() ?? "";
        switch (type)
        {
            case "connected":
                connected = true;
                deviceDpi = GetInt(message, "dpi");
                deviceInterval = GetInt(message, "report_interval_ms");
                Status($"Connected · {deviceDpi} DPI · {(deviceInterval > 0 ? 1000 / deviceInterval : 0)} Hz");
                ApplyDeviceSettings(settings.ActiveProfile);
                if (page == "DPI & rate" || page == "Device") Render();
                break;
            case "disconnected":
                connected = false;
                dpiShiftHeld = false;
                gShiftHeld = false;
                pressedAssignments.Clear();
                engine.StopAll();
                Status("Disconnected · " + message.GetProperty("message").GetString());
                break;
            case "button":
                int bit = message.GetProperty("bit").GetInt32();
                bool down = message.GetProperty("down").GetBoolean();
                if (down && learningButton.HasValue)
                {
                    string name = $"G{learningButton.Value}";
                    foreach (int oldBit in settings.ButtonNames.Where(x => x.Value == name).Select(x => x.Key).ToArray()) settings.ButtonNames.Remove(oldBit);
                    settings.ButtonNames[bit] = name;
                    selectedBit = bit;
                    learningCapturedBit = bit;
                    learningButton = null;
                    settings.Save();
                    Status($"Mapped {name} to 0x{bit:x4}");
                    Render();
                }
                Log($"{DateTime.Now:HH:mm:ss.fff}  0x{bit:x4}  {LabelFor(bit),-16} {(down ? "DOWN" : "UP")}");
                if (learningCapturedBit == bit)
                {
                    if (!down) learningCapturedBit = null;
                }
                else if (!learningButton.HasValue) HandleAssignment(bit, down);
                break;
            case "dpi": deviceDpi = GetInt(message, "value"); Status($"DPI set to {deviceDpi}"); break;
            case "report_interval": deviceInterval = GetInt(message, "value"); Status($"Report rate set to {1000 / Math.Max(1, deviceInterval)} Hz"); break;
            case "error": Status("Device error: " + message.GetProperty("message").GetString()); break;
        }
    }
    private static int GetInt(JsonElement item, string property) => item.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
    private void OnUi(Action action) { if (!IsDisposed && IsHandleCreated) BeginInvoke(action); }
    private void Status(string text) { status.Text = text; Log(text); }
    private void Log(string text)
    {
        logLines.Add(text);
        if (logLines.Count > 200) logLines.RemoveAt(0);
        if (eventLog is { IsDisposed: false } visible)
        {
            visible.Items.Add(text);
            if (visible.Items.Count > 200) visible.Items.RemoveAt(0);
            visible.TopIndex = visible.Items.Count - 1;
        }
    }

    private void RefreshProfiles()
    {
        profilePicker.SelectedIndexChanged -= ProfileSelectionChanged;
        profilePicker.Items.Clear();
        foreach (MouseProfile profile in settings.Profiles) profilePicker.Items.Add(profile);
        profilePicker.SelectedItem = settings.ActiveProfile;
        profilePicker.SelectedIndexChanged += ProfileSelectionChanged;
    }
    private void ProfileSelectionChanged(object? sender, EventArgs args) => SelectProfile();
    private void SelectProfile()
    {
        if (profilePicker.SelectedItem is not MouseProfile profile || settings.ActiveProfileId == profile.Id) return;
        settings.ActiveProfileId = profile.Id;
        settings.Save();
        engine.StopAll();
        gShiftHeld = false; dpiShiftHeld = false; pressedAssignments.Clear();
        ApplyDeviceSettings(profile);
        Render();
    }
    private void AutoSelectProfile()
    {
        try
        {
            nint foreground = GetForegroundWindow();
            if (foreground == Handle || foreground == nint.Zero) return;
            _ = GetWindowThreadProcessId(foreground, out uint pid);
            if (pid == 0) return;
            string process = Process.GetProcessById((int)pid).ProcessName;
            if (string.Equals(process, Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase)) return;
            MouseProfile? match = settings.Profiles.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.ApplicationPath) && string.Equals(Path.GetFileNameWithoutExtension(p.ApplicationPath), process, StringComparison.OrdinalIgnoreCase));
            MouseProfile target = match ?? settings.Profiles[0];
            if (settings.ActiveProfileId != target.Id) { profilePicker.SelectedItem = target; SelectProfile(); }
        }
        catch (Exception) { /* Some protected foreground processes reject inspection. */ }
    }
    private void ApplyDeviceSettings(MouseProfile profile)
    {
        if (!connected) return;
        try
        {
            bridge.Send("set_dpi", profile.Dpi);
            bridge.Send("set_report_interval", 1000 / profile.ReportRate);
        }
        catch (Exception error) { Status(error.Message); }
    }
    private void HandleAssignment(int bit, bool down, bool pulse = false)
    {
        MouseProfile profile = settings.ActiveProfile;
        string defaultAssignment = profile.Assignments.GetValueOrDefault(bit) ?? "";
        if (defaultAssignment == BuiltinActions.GShift)
        {
            gShiftHeld = down;
            Status(down ? "G-Shift active" : "G-Shift released");
            return;
        }
        string assignment;
        if (pulse) assignment = EffectiveAssignment(profile, bit);
        else if (down)
        {
            assignment = EffectiveAssignment(profile, bit);
            pressedAssignments[bit] = assignment;
        }
        else
        {
            assignment = pressedAssignments.GetValueOrDefault(bit) ?? EffectiveAssignment(profile, bit);
            pressedAssignments.Remove(bit);
        }
        if (assignment == BuiltinActions.DpiShift)
        {
            if (down == dpiShiftHeld) return;
            dpiShiftHeld = down;
            if (connected) bridge.Send("set_dpi", down ? profile.ShiftDpi : profile.Dpi);
            return;
        }
        if (down && assignment is BuiltinActions.DpiUp or BuiltinActions.DpiDown or BuiltinActions.DpiCycle)
        {
            List<int> stages = profile.DpiStages.Distinct().Order().ToList();
            if (stages.Count == 0) return;
            int current = stages.IndexOf(profile.Dpi);
            if (current < 0) current = 0;
            int next = assignment == BuiltinActions.DpiDown ? Math.Max(0, current - 1) : assignment == BuiltinActions.DpiUp ? Math.Min(stages.Count - 1, current + 1) : (current + 1) % stages.Count;
            profile.Dpi = stages[next];
            settings.Save();
            if (connected) bridge.Send("set_dpi", profile.Dpi);
            Status($"DPI stage: {profile.Dpi}");
            return;
        }
        engine.Handle(bit, down, profile, settings.Macros, pulse, assignment);
    }
    private string EffectiveAssignment(MouseProfile profile, int bit) =>
        gShiftHeld && profile.ShiftAssignments.TryGetValue(bit, out string? shifted)
            ? shifted : profile.Assignments.GetValueOrDefault(bit) ?? "";
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    private void Render()
    {
        body.SuspendLayout();
        eventLog = null;
        foreach (Control control in body.Controls) control.Dispose();
        body.Controls.Clear();
        TableLayoutPanel canvas = new() { Dock = DockStyle.Fill, BackColor = Background, ColumnCount = 1, RowCount = 2 };
        canvas.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        canvas.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        canvas.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        pageHeader = new Panel { Dock = DockStyle.Fill, BackColor = Background };
        pageHost = new Panel { Dock = DockStyle.Fill, BackColor = Background };
        canvas.Controls.Add(pageHeader, 0, 0);
        canvas.Controls.Add(pageHost, 0, 1);
        body.Controls.Add(canvas);
        switch (page)
        {
            case "Assignments": RenderAssignments(); break;
            case "Macros": RenderMacros(); break;
            case "DPI & rate": RenderDpi(); break;
            case "Profiles": RenderProfiles(); break;
            default: RenderDevice(); break;
        }
        body.ResumeLayout();
    }

    private void RenderAssignments()
    {
        Header("Button assignments", "Press a button to see its HID bit. Select a row to assign a macro or calibrate its G-number.");
        SplitContainer split = new() { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterDistance = 390, BackColor = Background, Panel1MinSize = 300 };
        pageHost.Controls.Add(split);
        FlowLayoutPanel left = Column(); split.Panel1.Controls.Add(left);
        ComboBox layer = new() { Width = 350, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Surface2, ForeColor = Color.White };
        layer.Items.AddRange(["Default layer", "G-Shift layer"]);
        layer.SelectedIndex = editingShiftLayer ? 1 : 0;
        layer.SelectedIndexChanged += (_, _) => { editingShiftLayer = layer.SelectedIndex == 1; Render(); };
        left.Controls.Add(layer);
        int[] bits = [0x0001, 0x0002, 0x0004, 0x0008, 0x0010, 0x0020, 0x0040, 0x0080, 0x0100, 0x0200, 0x0400, RawMouseWheel.Up, RawMouseWheel.Down, RawMouseWheel.Left, RawMouseWheel.Right];
        ListBox buttons = List(left, 350, 315);
        foreach (int bit in bits)
            buttons.Items.Add(new ButtonRow(bit, $"{LabelFor(bit),-16}  0x{bit:x4}  {AssignmentFor(bit)}"));
        buttons.SelectedIndexChanged += (_, _) =>
        {
            if (buttons.SelectedItem is ButtonRow row) { selectedBit = row.Bit; RenderAssignmentEditor(left, row.Bit, buttons); }
        };
        Label tip = MakeLabel("Calibrate each extra control one at a time using its G HUB label. The names are saved locally.", 340, Muted);
        left.Controls.Add(tip);
        CheckBox suppression = new() { Text = "Block default actions for assigned right, middle, side, and wheel controls", Width = 350, Height = 52, ForeColor = Muted, Checked = settings.SuppressStandardActions };
        suppression.CheckedChanged += (_, _) => { settings.SuppressStandardActions = suppression.Checked; settings.Save(); };
        left.Controls.Add(suppression);
        int index = Array.FindIndex(bits, x => x == selectedBit);
        if (index >= 0) buttons.SelectedIndex = index;

        Panel right = new() { Dock = DockStyle.Fill, BackColor = Background };
        split.Panel2.Controls.Add(right);
        PictureBox photo = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Background };
        string sideImage = Path.Combine(AppContext.BaseDirectory, "images", "g502-x-lightspeed-mouse-profile-angle.png");
        string topImage = Path.Combine(AppContext.BaseDirectory, "images", "g502-x-lightspeed-mouse-top-angle.png");
        if (File.Exists(sideImage)) photo.Image = Image.FromFile(sideImage);
        right.Controls.Add(photo);
        Button view = Button("Show top view", 145);
        view.Dock = DockStyle.Top;
        view.Click += (_, _) =>
        {
            bool showingSide = view.Text == "Show top view";
            string path = showingSide ? topImage : sideImage;
            if (!File.Exists(path)) return;
            Image? previous = photo.Image;
            photo.Image = Image.FromFile(path);
            previous?.Dispose();
            view.Text = showingSide ? "Show side view" : "Show top view";
        };
        right.Controls.Add(view);
        Label imageLabel = MakeLabel("G502 X LIGHTSPEED  ·  mouse controls", 520, Muted);
        imageLabel.Dock = DockStyle.Bottom; imageLabel.TextAlign = ContentAlignment.MiddleCenter; imageLabel.Height = 40;
        right.Controls.Add(imageLabel);
    }

    private void RenderAssignmentEditor(FlowLayoutPanel left, int bit, ListBox buttons)
    {
        while (left.Controls.Count > 4) { Control last = left.Controls[^1]; left.Controls.Remove(last); last.Dispose(); }
        left.Controls.Add(MakeLabel($"Selected: {LabelFor(bit)}  ·  0x{bit:x4}", 350, Color.White));
        Dictionary<int, string> bindings = editingShiftLayer ? settings.ActiveProfile.ShiftAssignments : settings.ActiveProfile.Assignments;
        ComboBox assignment = new() { Width = 330, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Surface2, ForeColor = Color.White };
        assignment.Items.Add("Default / no macro");
        foreach ((string id, string name) in BuiltinActions.All)
            if ((bit < RawMouseWheel.Up || id != BuiltinActions.DpiShift) && (!editingShiftLayer || id != BuiltinActions.GShift)) assignment.Items.Add(new AssignmentChoice(id, name));
        foreach (MacroDefinition macro in settings.Macros) assignment.Items.Add(macro);
        string existing = bindings.GetValueOrDefault(bit) ?? "";
        assignment.SelectedItem = settings.Macros.FirstOrDefault(m => m.Id == existing) ?? assignment.Items.OfType<AssignmentChoice>().FirstOrDefault(x => x.Id == existing) ?? (object)"Default / no macro";
        assignment.SelectedIndexChanged += (_, _) =>
        {
            if (assignment.SelectedItem is MacroDefinition selected) bindings[bit] = selected.Id;
            else if (assignment.SelectedItem is AssignmentChoice builtIn) bindings[bit] = builtIn.Id;
            else bindings.Remove(bit);
            settings.Save();
            if (buttons.SelectedItem is ButtonRow row) { row.Caption = $"{LabelFor(bit),-16}  0x{bit:x4}  {AssignmentFor(bit)}"; buttons.Refresh(); }
        };
        left.Controls.Add(assignment);
        if (bit >= RawMouseWheel.Up) return;
        FlowLayoutPanel calibration = new() { Width = 350, Height = 48, FlowDirection = FlowDirection.LeftToRight };
        NumericUpDown number = new() { Minimum = 1, Maximum = 20, Value = 6, Width = 65, BackColor = Surface2, ForeColor = Color.White };
        Button learn = Button("Learn G-number", 160);
        learn.Click += (_, _) => { learningButton = (int)number.Value; Status($"Press G{learningButton} once now"); };
        calibration.Controls.Add(number); calibration.Controls.Add(learn);
        left.Controls.Add(calibration);
    }

    private string LabelFor(int bit) => settings.ButtonNames.GetValueOrDefault(bit) ?? bit switch
    {
        0x0001 => "Primary click", 0x0002 => "Right click", 0x0004 => "Middle click",
        0x0200 => "Side X2", 0x0400 => "Side X1",
        RawMouseWheel.Up => "Scroll up", RawMouseWheel.Down => "Scroll down",
        RawMouseWheel.Left => "Wheel left", RawMouseWheel.Right => "Wheel right",
        _ => "Unmapped button"
    };
    private string AssignmentFor(int bit)
    {
        string? id = (editingShiftLayer ? settings.ActiveProfile.ShiftAssignments : settings.ActiveProfile.Assignments).GetValueOrDefault(bit);
        return settings.Macros.FirstOrDefault(m => m.Id == id)?.Name ?? BuiltinActions.NameFor(id) ?? "Default";
    }
    private sealed class AssignmentChoice(string id, string name)
    {
        public string Id { get; } = id;
        public override string ToString() => name;
    }
    private sealed class ButtonRow(int bit, string caption)
    {
        public int Bit { get; } = bit;
        public string Caption { get; set; } = caption;
        public override string ToString() => Caption;
    }

    private void RenderMacros()
    {
        Header("Macro manager", "Create actions and bind a macro to any detected button in Assignments.");
        SplitContainer split = new() { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterDistance = 300, Panel1MinSize = 230 };
        pageHost.Controls.Add(split);
        FlowLayoutPanel left = Column(); split.Panel1.Controls.Add(left);
        ListBox macros = List(left, 260, 490);
        foreach (MacroDefinition macro in settings.Macros) macros.Items.Add(macro);
        Button add = Button("+ New macro", 260);
        add.Click += (_, _) => { MacroDefinition macro = new(); settings.Macros.Add(macro); settings.Save(); macros.Items.Add(macro); macros.SelectedItem = macro; };
        left.Controls.Add(add);
        FlowLayoutPanel editor = Column(); split.Panel2.Controls.Add(editor);
        macros.SelectedIndexChanged += (_, _) => RenderMacroEditor(editor, macros.SelectedItem as MacroDefinition, macros);
        if (macros.Items.Count > 0) macros.SelectedIndex = 0;
    }

    private void RenderMacroEditor(FlowLayoutPanel editor, MacroDefinition? macro, ListBox macroList)
    {
        editor.Controls.Clear();
        if (macro is null) { editor.Controls.Add(MakeLabel("Create or select a macro to edit it.", 600, Muted)); return; }
        TextBox name = Input(macro.Name, 550); editor.Controls.Add(MakeLabel("NAME", 550, Muted)); editor.Controls.Add(name);
        name.TextChanged += (_, _) => { macro.Name = name.Text; macroList.Refresh(); settings.Save(); };
        editor.Controls.Add(MakeLabel("PLAYBACK", 550, Muted));
        ComboBox mode = new() { Width = 550, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Surface2, ForeColor = Color.White };
        mode.Items.AddRange(Enum.GetNames<MacroMode>()); mode.SelectedItem = macro.Mode.ToString();
        mode.SelectedIndexChanged += (_, _) => { macro.Mode = Enum.Parse<MacroMode>((string)mode.SelectedItem!); settings.Save(); };
        editor.Controls.Add(mode);
        editor.Controls.Add(MakeLabel("A sequence runs one step per button press. Hold and toggle repeat the whole action list.", 550, Muted));
        ListBox steps = List(editor, 550, 250);
        void RefreshSteps() { steps.Items.Clear(); foreach (MacroStep step in macro.Steps) steps.Items.Add(step); settings.Save(); }
        RefreshSteps();
        FlowLayoutPanel controls = new() { Width = 590, Height = 45, FlowDirection = FlowDirection.LeftToRight };
        ComboBox kind = new() { Width = 145, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Surface2, ForeColor = Color.White };
        kind.Items.AddRange(Enum.GetNames<ActionKind>()); kind.SelectedIndex = 0;
        TextBox value = Input("Ctrl+Alt+T", 215);
        Button add = Button("Add step", 100);
        add.Click += (_, _) =>
        {
            ActionKind action = Enum.Parse<ActionKind>((string)kind.SelectedItem!);
            MacroStep step = new() { Kind = action, Value = value.Text };
            if (action == ActionKind.Delay && int.TryParse(value.Text, out int delay)) step.DelayMs = delay;
            macro.Steps.Add(step); RefreshSteps();
        };
        controls.Controls.Add(kind); controls.Controls.Add(value); controls.Controls.Add(add); editor.Controls.Add(controls);
        editor.Controls.Add(MakeLabel("Key example: Ctrl+Alt+T · Delay: milliseconds · Wheel: ticks · Launch: full exe path", 590, Muted));
        Button record = Button("● Record keystrokes", 190);
        record.Click += (_, _) =>
        {
            using RecordKeysDialog dialog = new();
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Recorded.Count == 0) return;
            macro.Steps.AddRange(dialog.Recorded);
            RefreshSteps();
        };
        editor.Controls.Add(record);
        FlowLayoutPanel actions = new() { Width = 590, Height = 45 };
        Button remove = Button("Remove step", 130);
        remove.Click += (_, _) => { if (steps.SelectedIndex >= 0) { macro.Steps.RemoveAt(steps.SelectedIndex); RefreshSteps(); } };
        Button up = Button("Move up", 100);
        up.Click += (_, _) => { int i = steps.SelectedIndex; if (i > 0) { (macro.Steps[i - 1], macro.Steps[i]) = (macro.Steps[i], macro.Steps[i - 1]); RefreshSteps(); steps.SelectedIndex = i - 1; } };
        Button delete = Button("Delete macro", 130);
        delete.Click += (_, _) =>
        {
            foreach (MouseProfile profile in settings.Profiles)
            {
                foreach (int bit in profile.Assignments.Where(x => x.Value == macro.Id).Select(x => x.Key).ToArray()) profile.Assignments.Remove(bit);
                foreach (int bit in profile.ShiftAssignments.Where(x => x.Value == macro.Id).Select(x => x.Key).ToArray()) profile.ShiftAssignments.Remove(bit);
            }
            settings.Macros.Remove(macro); settings.Save(); macroList.Items.Remove(macro); editor.Controls.Clear();
        };
        actions.Controls.Add(remove); actions.Controls.Add(up); actions.Controls.Add(delete); editor.Controls.Add(actions);
    }

    private void RenderDpi()
    {
        Header("Sensitivity & report rate", "Settings are saved per profile and applied to the connected mouse.");
        FlowLayoutPanel column = Column(); pageHost.Controls.Add(column);
        column.Controls.Add(MakeLabel($"ACTIVE PROFILE   {settings.ActiveProfile.Name}", 650, Accent));
        column.Controls.Add(MakeLabel($"Device reports: {(connected ? $"{deviceDpi} DPI · {(deviceInterval > 0 ? 1000 / deviceInterval : 0)} Hz" : "disconnected")}", 650, Muted));
        column.Controls.Add(MakeLabel("DPI SPEEDS", 650, Color.White));
        ListBox stages = List(column, 400, 155);
        void RefreshStages()
        {
            stages.Items.Clear();
            foreach (int speed in settings.ActiveProfile.DpiStages.Distinct().Order()) stages.Items.Add(speed);
            stages.SelectedItem = settings.ActiveProfile.Dpi;
        }
        RefreshStages();
        column.Controls.Add(MakeLabel("CURRENT / NEW SPEED", 650, Muted));
        NumericUpDown dpi = new() { Minimum = 100, Maximum = 25600, Increment = 50, Value = Math.Clamp(settings.ActiveProfile.Dpi, 100, 25600), Width = 180, BackColor = Surface2, ForeColor = Color.White };
        column.Controls.Add(dpi);
        stages.SelectedIndexChanged += (_, _) => { if (stages.SelectedItem is int speed) dpi.Value = speed; };
        FlowLayoutPanel stageButtons = new() { Width = 520, Height = 45 };
        Button addStage = Button("Add speed", 135);
        addStage.Click += (_, _) =>
        {
            int speed = (int)dpi.Value;
            if (!settings.ActiveProfile.DpiStages.Contains(speed)) settings.ActiveProfile.DpiStages.Add(speed);
            settings.ActiveProfile.DpiStages.Sort(); settings.Save(); RefreshStages(); stages.SelectedItem = speed;
        };
        Button removeStage = Button("Remove speed", 135);
        removeStage.Click += (_, _) =>
        {
            if (stages.SelectedItem is not int speed || settings.ActiveProfile.DpiStages.Count <= 1) return;
            settings.ActiveProfile.DpiStages.Remove(speed);
            if (settings.ActiveProfile.Dpi == speed) settings.ActiveProfile.Dpi = settings.ActiveProfile.DpiStages[0];
            settings.Save(); RefreshStages();
        };
        stageButtons.Controls.Add(addStage); stageButtons.Controls.Add(removeStage); column.Controls.Add(stageButtons);
        column.Controls.Add(MakeLabel("DPI SHIFT SPEED (WHILE HELD)", 650, Color.White));
        NumericUpDown shift = new() { Minimum = 100, Maximum = 25600, Increment = 50, Value = Math.Clamp(settings.ActiveProfile.ShiftDpi, 100, 25600), Width = 180, BackColor = Surface2, ForeColor = Color.White };
        column.Controls.Add(shift);
        column.Controls.Add(MakeLabel("REPORT RATE", 650, Color.White));
        ComboBox rate = new() { Width = 180, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Surface2, ForeColor = Color.White };
        foreach (int hz in new[] { 125, 250, 500, 1000 }) rate.Items.Add(hz);
        rate.SelectedItem = settings.ActiveProfile.ReportRate;
        column.Controls.Add(rate);
        Button apply = Button("Save and apply", 180);
        apply.Click += (_, _) =>
        {
            settings.ActiveProfile.Dpi = (int)dpi.Value;
            if (!settings.ActiveProfile.DpiStages.Contains(settings.ActiveProfile.Dpi)) settings.ActiveProfile.DpiStages.Add(settings.ActiveProfile.Dpi);
            settings.ActiveProfile.DpiStages.Sort();
            settings.ActiveProfile.ShiftDpi = (int)shift.Value;
            settings.ActiveProfile.ReportRate = (int)(rate.SelectedItem ?? 1000);
            settings.Save(); ApplyDeviceSettings(settings.ActiveProfile);
            RefreshStages();
            Status(connected ? "DPI and report rate sent to mouse" : "Settings saved; connect to apply");
        };
        column.Controls.Add(apply);
        column.Controls.Add(MakeLabel("DPI and report rate use HID++ host commands. The app does not write onboard profile memory.", 650, Muted));
    }

    private void RenderProfiles()
    {
        Header("Games & applications", "Profiles switch when their application has focus. Desktop is the fallback.");
        FlowLayoutPanel column = Column(); pageHost.Controls.Add(column);
        ListBox profiles = List(column, 640, 350);
        foreach (MouseProfile item in settings.Profiles) profiles.Items.Add(item);
        profiles.SelectedItem = settings.ActiveProfile;
        FlowLayoutPanel actions = new() { Width = 680, Height = 50 };
        Button add = Button("Add application", 150);
        add.Click += (_, _) =>
        {
            using OpenFileDialog dialog = new() { Filter = "Applications (*.exe)|*.exe", Title = "Choose a game or application" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            MouseProfile item = new() { Name = Path.GetFileNameWithoutExtension(dialog.FileName), ApplicationPath = dialog.FileName, Dpi = settings.ActiveProfile.Dpi, ReportRate = settings.ActiveProfile.ReportRate };
            settings.Profiles.Add(item); settings.Save(); RefreshProfiles(); profiles.Items.Add(item); profiles.SelectedItem = item;
        };
        Button activate = Button("Use profile", 135);
        activate.Click += (_, _) => { if (profiles.SelectedItem is MouseProfile item) profilePicker.SelectedItem = item; };
        Button remove = Button("Remove", 100);
        remove.Click += (_, _) =>
        {
            if (profiles.SelectedItem is not MouseProfile item || item == settings.Profiles[0]) return;
            settings.Profiles.Remove(item); settings.ActiveProfileId = settings.Profiles[0].Id; settings.Save(); RefreshProfiles(); Render();
        };
        actions.Controls.Add(add); actions.Controls.Add(activate); actions.Controls.Add(remove); column.Controls.Add(actions);
        column.Controls.Add(MakeLabel("Assignments, DPI and report rate are stored separately for each profile. Macros can be reused across profiles.", 650, Muted));
    }

    private void RenderDevice()
    {
        Header("Device & diagnostics", "Live input from the G502 X LIGHTSPEED receiver.");
        FlowLayoutPanel column = Column(); pageHost.Controls.Add(column);
        column.Controls.Add(MakeLabel(connected ? $"CONNECTED · {deviceDpi} DPI · {(deviceInterval > 0 ? 1000 / deviceInterval : 0)} Hz" : "DISCONNECTED", 650, connected ? Accent : Muted));
        column.Controls.Add(MakeLabel("Receiver VID:PID 046D:C547 · HID++ 0x8100 host mode · 0x8110 button spy", 750, Muted));
        column.Controls.Add(MakeLabel("Event log", 700, Color.White));
        eventLog = new ListBox();
        eventLog.Width = 740; eventLog.Height = 470; eventLog.BackColor = Surface; eventLog.ForeColor = Color.White;
        eventLog.Font = new Font("Consolas", 9); eventLog.BorderStyle = BorderStyle.None;
        foreach (string line in logLines) eventLog.Items.Add(line);
        column.Controls.Add(eventLog);
    }

    private void Header(string title, string subtitle)
    {
        Label big = MakeLabel(title, 900, Color.White); big.Font = new Font("Segoe UI", 24, FontStyle.Bold); big.Location = new Point(2, 0); big.Height = 50;
        Label small = MakeLabel(subtitle, 930, Muted); small.Location = new Point(4, 54); small.Height = 34;
        pageHeader.Controls.Add(big); pageHeader.Controls.Add(small);
    }
    private static FlowLayoutPanel Column() => new() { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Background, Padding = new Padding(8) };
    private static Label MakeLabel(string text, int width, Color color) => new() { Text = text, ForeColor = color, Width = width, Height = 38, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false };
    private static TextBox Input(string value, int width) => new() { Text = value, Width = width, Height = 34, BackColor = Surface2, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
    private static Button Button(string caption, int width)
    {
        Button button = new() { Text = caption, Width = width, Height = 34, BackColor = Surface2, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand };
        button.FlatAppearance.BorderColor = Color.FromArgb(61, 72, 86);
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(30, 86, 107);
        return button;
    }
    private static ListBox List(Control parent, int width, int height)
    {
        ListBox list = new() { Width = width, Height = height, BackColor = Surface, ForeColor = Color.White, BorderStyle = BorderStyle.None, ItemHeight = 27, Font = new Font("Segoe UI", 10) };
        parent.Controls.Add(list);
        return list;
    }
}
