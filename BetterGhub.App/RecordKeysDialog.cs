using System.Diagnostics;

namespace BetterGhub.App;

internal sealed class RecordKeysDialog : Form
{
    private readonly Stopwatch stopwatch = new();
    private readonly HashSet<Keys> pressed = [];
    private readonly ListBox events = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(24, 29, 37), ForeColor = Color.White, Font = new Font("Consolas", 10) };
    private long previous;
    public List<MacroStep> Recorded { get; } = [];

    public RecordKeysDialog()
    {
        Text = "Record keystrokes";
        ClientSize = new Size(540, 430);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(11, 14, 19);
        ForeColor = Color.White;
        KeyPreview = true;
        Label instructions = new() { Dock = DockStyle.Top, Height = 70, Padding = new Padding(12), Text = "Type the keys for this macro. Press Stop when done. Timing and key down/up edges are recorded. This window captures keys only while it has focus." };
        Button stop = new() { Text = "Stop and add", DialogResult = DialogResult.OK, Dock = DockStyle.Bottom, Height = 42, BackColor = Color.FromArgb(36, 181, 216) };
        Button cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Dock = DockStyle.Bottom, Height = 36 };
        Controls.Add(events); Controls.Add(cancel); Controls.Add(stop); Controls.Add(instructions);
        Shown += (_, _) => { stopwatch.Start(); ActiveControl = events; };
        KeyDown += CaptureDown;
        KeyUp += CaptureUp;
    }

    private void CaptureDown(object? sender, KeyEventArgs e)
    {
        if (pressed.Add(e.KeyCode)) Add(ActionKind.KeyDown, e.KeyCode);
        e.SuppressKeyPress = true;
    }
    private void CaptureUp(object? sender, KeyEventArgs e)
    {
        if (pressed.Remove(e.KeyCode)) Add(ActionKind.KeyUp, e.KeyCode);
        e.SuppressKeyPress = true;
    }
    private void Add(ActionKind kind, Keys key)
    {
        long now = stopwatch.ElapsedMilliseconds;
        if (Recorded.Count > 0 && now - previous > 0)
            Recorded.Add(new MacroStep { Kind = ActionKind.Delay, DelayMs = (int)Math.Min(now - previous, 60000) });
        MacroStep step = new() { Kind = kind, Value = key.ToString() };
        Recorded.Add(step);
        events.Items.Add($"{now,6} ms   {step}");
        events.TopIndex = events.Items.Count - 1;
        previous = now;
    }
}
