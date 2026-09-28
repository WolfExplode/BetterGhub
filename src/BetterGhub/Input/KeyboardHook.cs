using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BetterGhub.Input;

/// <summary>
/// Low-level keyboard hook that can swallow physical key presses before Windows acts on them,
/// including system shortcuts such as Alt+Tab and the Win key that normal window key events never block.
/// The callback receives the virtual key and whether it went down, and returns true to swallow it.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private readonly Func<ushort, bool, bool> onKey;
    private readonly HookProcedure callback;
    private nint hook;

    public KeyboardHook(Func<ushort, bool, bool> onKey)
    {
        this.onKey = onKey;
        callback = OnKey;
        hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
        if (hook == nint.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Keyboard hook installation failed");
    }

    private nint OnKey(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            KeyboardHookData data = Marshal.PtrToStructure<KeyboardHookData>(lParam);
            if ((data.Flags & 0x10) == 0) // Never swallow injected input, including BetterGhub's own macros.
            {
                bool down = (data.Flags & 0x80) == 0;
                if (onKey((ushort)data.VirtualKey, down)) return 1;
            }
        }
        return CallNextHookEx(hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (hook == nint.Zero) return;
        _ = UnhookWindowsHookEx(hook);
        hook = nint.Zero;
        GC.KeepAlive(callback);
    }

    [StructLayout(LayoutKind.Sequential)] private readonly struct KeyboardHookData
    {
        public readonly uint VirtualKey, ScanCode, Flags, Time;
        public readonly nint ExtraInfo;
    }
    private delegate nint HookProcedure(int code, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int idHook, HookProcedure procedure, nint module, uint threadId);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
