using BetterGhub.Input;

namespace BetterGhub.Core;

public enum MouseView { Top, Side }
public enum CalloutSide { Left, Right }

/// <summary>A physical control on the mouse and where its callout sits on the artwork.</summary>
/// <param name="ImageX">Hotspot in source-image pixels.</param>
/// <param name="LabelY">Callout row in canvas units (0–1, top to bottom).</param>
public sealed record MouseControl(
    string Id, string Label, string DefaultAction, int? DefaultBit, bool Calibratable,
    MouseView View, double ImageX, double ImageY, CalloutSide Side, double LabelY)
{
    public bool IsWheel => DefaultBit is RawMouseWheel.Up or RawMouseWheel.Down;
    /// <summary>Default action BetterGhub runs itself, since host mode turns off the mouse's onboard handling of these buttons.</summary>
    public string? SoftwareDefault => Id switch
    {
        "G6" => MouseControls.G304 ? BuiltinActions.DpiCycle : BuiltinActions.DpiShift,
        "G7" => BuiltinActions.DpiDown,
        "G8" => BuiltinActions.DpiUp,
        "G9" => BuiltinActions.DpiCycle,
        _ => null
    };
}

public static class MouseControls
{
    /// <summary>The G304 (or G305) is connected instead of the G502 X; set once at startup from the receiver found.</summary>
    public static bool G304 { get; set; }

    public static IReadOnlyList<MouseControl> All => G304 ? G304Controls : G502Controls;

    /// <summary>Whether the artwork has a side view (the G304's side buttons show in the top view).</summary>
    public static bool HasSideView => All.Any(c => c.View == MouseView.Side);

    // Hotspots measured on images/g502-x-lightspeed-mouse-top-angle-transparent.png (1020×1620)
    // and images/g502-x-lightspeed-mouse-profile-angle.png (1382×551).
    private static readonly IReadOnlyList<MouseControl> G502Controls =
    [
        new("G1", "Primary click", "Left click", 0x0001, false, MouseView.Top, 380, 255, CalloutSide.Left, 0.13),
        new("G8", "G8", "DPI up", null, true, MouseView.Top, 262, 385, CalloutSide.Left, 0.27),
        new("TiltLeft", "Wheel tilt left", "Scroll left", RawMouseWheel.Left, true, MouseView.Top, 468, 440, CalloutSide.Left, 0.41),
        new("G7", "G7", "DPI down", null, true, MouseView.Top, 250, 485, CalloutSide.Left, 0.55),
        new("WheelDown", "Scroll down", "Scroll down", RawMouseWheel.Down, false, MouseView.Top, 573, 540, CalloutSide.Left, 0.69),
        new("WheelUp", "Scroll up", "Scroll up", RawMouseWheel.Up, false, MouseView.Top, 573, 335, CalloutSide.Right, 0.10),
        new("G2", "Secondary click", "Right click", 0x0002, false, MouseView.Top, 760, 300, CalloutSide.Right, 0.24),
        new("G3", "Middle click", "Middle click", 0x0004, true, MouseView.Top, 573, 440, CalloutSide.Right, 0.38),
        new("TiltRight", "Wheel tilt right", "Scroll right", RawMouseWheel.Right, true, MouseView.Top, 680, 440, CalloutSide.Right, 0.52),
        new("G9", "G9", "Profile / DPI cycle", null, true, MouseView.Top, 575, 765, CalloutSide.Right, 0.69),
        new("G5", "G5 · Forward", "Forward", null, true, MouseView.Side, 675, 262, CalloutSide.Left, 0.30),
        new("G6", "G6 · DPI Shift", "DPI Shift", null, true, MouseView.Side, 545, 400, CalloutSide.Left, 0.66),
        new("G4", "G4 · Back", "Back", null, true, MouseView.Side, 895, 232, CalloutSide.Right, 0.30),
    ];

    // Hotspots measured on assets/artwork/g304-lightspeed-mouse-top.png (977×1775), from the quick start guide.
    private static readonly IReadOnlyList<MouseControl> G304Controls =
    [
        new("G1", "Primary click", "Left click", 0x0001, false, MouseView.Top, 250, 420, CalloutSide.Left, 0.13),
        new("G5", "G5 · Forward", "Forward", null, true, MouseView.Top, 22, 700, CalloutSide.Left, 0.40),
        new("G4", "G4 · Back", "Back", null, true, MouseView.Top, 16, 1010, CalloutSide.Left, 0.62),
        new("WheelUp", "Scroll up", "Scroll up", RawMouseWheel.Up, false, MouseView.Top, 500, 230, CalloutSide.Right, 0.06),
        new("G2", "Secondary click", "Right click", 0x0002, false, MouseView.Top, 750, 420, CalloutSide.Right, 0.22),
        new("G3", "Middle click", "Middle click", 0x0004, true, MouseView.Top, 500, 340, CalloutSide.Right, 0.38),
        new("WheelDown", "Scroll down", "Scroll down", RawMouseWheel.Down, false, MouseView.Top, 500, 450, CalloutSide.Right, 0.54),
        new("G6", "G6 · DPI", "DPI cycle", null, true, MouseView.Top, 500, 628, CalloutSide.Right, 0.70),
    ];

    public static MouseControl? ById(string? id) => All.FirstOrDefault(c => c.Id == id);

    /// <summary>Bits that belong to controls with a fixed bit (left and right click are always bits 0 and 1).</summary>
    public static bool IsFixedBit(int bit) => All.Any(c => !c.Calibratable && c.DefaultBit == bit);
}
