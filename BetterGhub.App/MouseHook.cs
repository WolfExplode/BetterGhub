using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BetterGhub.App;

/// <summary>Optional OS default-action suppression for ordinary mouse inputs.</summary>
internal sealed class MouseHook : IDisposable
{
    private readonly Func<int, bool> shouldSuppress;
    private readonly HookProcedure callback;
    private nint hook;

    public MouseHook(Func<int, bool> shouldSuppress)
    {
        this.shouldSuppress = shouldSuppress;
        callback = OnMouse;
        hook = SetWindowsHookEx(14, callback, GetModuleHandle(null), 0);
        if (hook == nint.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Mouse hook installation failed");
    }

    private nint OnMouse(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            MouseHookData data = Marshal.PtrToStructure<MouseHookData>(lParam);
            if ((data.Flags & 1) == 0) // Keep BetterGhub's own injected macro actions.
            {
                int message = unchecked((int)wParam);
                int bit = message switch
                {
                    0x0204 or 0x0205 => 0x0002, // right
                    0x0207 or 0x0208 => 0x0004, // middle
                    0x020B or 0x020C => (data.MouseData >> 16) == 1 ? 0x0400 : 0x0200,
                    0x020A => unchecked((short)(data.MouseData >> 16)) > 0 ? RawMouseWheel.Up : RawMouseWheel.Down,
                    0x020E => unchecked((short)(data.MouseData >> 16)) > 0 ? RawMouseWheel.Right : RawMouseWheel.Left,
                    _ => 0
                };
                if (bit != 0 && shouldSuppress(bit)) return 1;
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

    [StructLayout(LayoutKind.Sequential)] private readonly struct Point { public readonly int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private readonly struct MouseHookData
    {
        public readonly Point Location;
        public readonly uint MouseData, Flags, Time;
        public readonly nint ExtraInfo;
    }
    private delegate nint HookProcedure(int code, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int idHook, HookProcedure procedure, nint module, uint threadId);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
