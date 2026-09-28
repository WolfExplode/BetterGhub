using System.Windows.Input;
using BetterGhub.Input;

namespace BetterGhub.Views;

/// <summary>Converts WPF key events into the macro key names used by <see cref="VirtualKeys"/>.</summary>
internal static class KeyCapture
{
    public static Key RealKey(KeyEventArgs e) => e.Key switch
    {
        Key.System => e.SystemKey,
        Key.ImeProcessed => e.ImeProcessedKey,
        Key.DeadCharProcessed => e.DeadCharProcessedKey,
        _ => e.Key
    };

    public static ushort VirtualKey(KeyEventArgs e) => (ushort)KeyInterop.VirtualKeyFromKey(RealKey(e));

    public static bool IsModifier(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    /// <summary>"Ctrl+Shift+T" style combo from a key-down event, or null while only modifiers are held.</summary>
    public static string? Combo(KeyEventArgs e)
    {
        Key key = RealKey(e);
        if (IsModifier(key) || key == Key.None) return null;
        List<string> parts = [];
        ModifierKeys modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(VirtualKeys.NameOf((ushort)KeyInterop.VirtualKeyFromKey(key)));
        return string.Join("+", parts);
    }
}
