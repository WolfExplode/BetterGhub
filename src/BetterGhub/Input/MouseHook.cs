using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BetterGhub.Input;

/// <summary>
/// Optional low-level hook that blocks the normal Windows action of a standard mouse input.
/// It sees every mouse on the system, so the callback decides per physical control id
/// (see <see cref="Core.MouseControls"/>): G2 right, G3 middle, G4 back (X1), G5 forward (X2), wheel and tilt.
/// </summary>
internal sealed class MouseHook : IDisposable
{
    private readonly Func<string, bool> shouldSuppress;
    private readonly HookProcedure callback;
    private nint hook;

    public MouseHook(Func<string, bool> shouldSuppress)
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
            if ((data.Flags & 1) == 0) // Never block BetterGhub's own injected macro input.
            {
                string? control = unchecked((int)wParam) switch
                {
                    0x0204 or 0x0205 => "G2",
                    0x0207 or 0x0208 => "G3",
                    0x020B or 0x020C => (data.MouseData >> 16) == 1 ? "G4" : "G5",
                    0x020A => unchecked((short)(data.MouseData >> 16)) > 0 ? "WheelUp" : "WheelDown",
                    0x020E => unchecked((short)(data.MouseData >> 16)) > 0 ? "TiltRight" : "TiltLeft",
                    _ => null
                };
                if (control is not null && shouldSuppress(control)) return 1;
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
