using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace BetterGhub.Input;

/// <summary>A pickable window: its executable and its visible bounds in physical screen pixels.</summary>
internal sealed record PickTarget(string Path, int X, int Y, int Width, int Height);

/// <summary>
/// Eyedropper for applications: while active, the next left click anywhere on screen is swallowed and
/// resolved to the executable that owns the clicked window. Right-click or Esc cancels.
/// The arrow cursor becomes a crosshair until the pick ends.
/// </summary>
internal sealed class WindowPicker : IDisposable
{
    private readonly Action<string?> done;
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly HookProcedure callback;
    private readonly KeyboardHook keys;
    private nint hook, hovered;
    private bool pressed, finished;
    private string? picked;

    /// <summary>Raised on the UI thread when the pointer moves onto another window; null when there is nothing pickable under it.</summary>
    public event Action<PickTarget?>? Hover;

    /// <summary>Starts picking; <paramref name="done"/> receives the executable path, or null when cancelled or unreadable.</summary>
    public WindowPicker(Action<string?> done)
    {
        this.done = done;
        callback = OnMouse;
        hook = SetWindowsHookEx(14, callback, GetModuleHandle(null), 0);
        if (hook == nint.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Mouse hook installation failed");
        keys = new KeyboardHook((key, down) =>
        {
            if (key != 0x1B) return false; // Esc
            if (down && !pressed) Finish(null);
            return true;
        });
        nint cross = CopyIcon(LoadCursor(nint.Zero, 32515)); // IDC_CROSS; SetSystemCursor takes ownership of the copy.
        if (cross != nint.Zero) _ = SetSystemCursor(cross, 32512); // OCR_NORMAL
    }

    private nint OnMouse(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && !finished)
        {
            MouseHookData data = Marshal.PtrToStructure<MouseHookData>(lParam);
            if ((data.Flags & 1) == 0)
            {
                switch (unchecked((int)wParam))
                {
                    case 0x0200 when !pressed: // Move: only resolve the window here, describe it off the hook callback.
                        nint window = WindowAt(data.Location);
                        if (window != hovered)
                        {
                            hovered = window;
                            dispatcher.BeginInvoke(() => { if (!finished && hovered == window) Hover?.Invoke(Describe(window)); });
                        }
                        break;
                    case 0x0201 when !pressed: // Left down picks the window under the pointer...
                        pressed = true;
                        picked = PathOf(WindowAt(data.Location));
                        return 1;
                    case 0x0204 when !pressed: // ...right down cancels...
                        pressed = true;
                        return 1;
                    case 0x0202 or 0x0205 when pressed: // ...and the matching release ends the pick, so no half-click leaks through.
                        Finish(picked);
                        return 1;
                }
            }
        }
        return CallNextHookEx(hook, code, wParam, lParam);
    }

    private void Finish(string? path)
    {
        if (finished) return;
        finished = true;
        RestoreCursor();
        // Unhook outside the hook callback.
        dispatcher.BeginInvoke(() => { Dispose(); done(path); });
    }

    private static nint WindowAt(Point point) => GetAncestor(WindowFromPoint(point), 2); // GA_ROOT

    private static PickTarget? Describe(nint window)
    {
        if (PathOf(window) is not string path) return null;
        // The DWM frame excludes the invisible resize borders that GetWindowRect includes.
        if (DwmGetWindowAttribute(window, 9, out Rect frame, Marshal.SizeOf<Rect>()) != 0) return null; // DWMWA_EXTENDED_FRAME_BOUNDS
        return new PickTarget(path, frame.Left, frame.Top, frame.Right - frame.Left, frame.Bottom - frame.Top);
    }

    private static string? PathOf(nint window)
    {
        if (window == nint.Zero) return null;
        _ = GetWindowThreadProcessId(window, out uint pid);
        if (pid == 0 || pid == Environment.ProcessId) return null;
        nint process = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION also works on most elevated processes.
        if (process == nint.Zero) return null;
        try
        {
            StringBuilder buffer = new(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally { _ = CloseHandle(process); }
    }

    private static void RestoreCursor() => _ = SystemParametersInfo(0x0057, 0, nint.Zero, 0); // SPI_SETCURSORS reloads the user's cursors.

    public void Dispose()
    {
        if (hook == nint.Zero) return;
        _ = UnhookWindowsHookEx(hook);
        hook = nint.Zero;
        keys.Dispose();
        if (!finished) RestoreCursor();
        finished = true;
        GC.KeepAlive(callback);
    }

    [StructLayout(LayoutKind.Sequential)] private readonly struct Point { public readonly int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private readonly struct Rect { public readonly int Left, Top, Right, Bottom; }
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
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out Rect value, int size);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
    [DllImport("user32.dll")] private static extern nint LoadCursor(nint instance, int cursor);
    [DllImport("user32.dll")] private static extern nint CopyIcon(nint icon);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetSystemCursor(nint cursor, uint id);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SystemParametersInfo(uint action, uint param, nint value, uint winIni);
}
