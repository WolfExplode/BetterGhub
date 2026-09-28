using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace BetterGhub.Views;

/// <summary>Notification-area icon with a native context menu (Shell_NotifyIcon, no WinForms).</summary>
internal sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8001; // WM_APP + 1
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
    private const uint NifMessage = 1, NifIcon = 2, NifTip = 4, NifInfo = 0x10;
    private readonly HwndSource window;
    private readonly uint taskbarCreated;
    private readonly nint icon;
    private string tooltip = "BetterGhub";
    private bool added;

    /// <summary>Left click or double click on the icon.</summary>
    public event Action? Activated;
    /// <summary>Asked for menu items just before the menu opens. Return (text, enabled, checked, action) or null for a separator.</summary>
    public Func<IEnumerable<MenuItem?>>? MenuItems { get; set; }

    public sealed record MenuItem(string Text, Action? Action, bool Enabled = true, bool Checked = false, IReadOnlyList<MenuItem?>? Children = null);

    public TrayIcon()
    {
        // A hidden popup window: it can become foreground, which TrackPopupMenu needs to close correctly.
        window = new HwndSource(new HwndSourceParameters("BetterGhubTray") { Width = 0, Height = 0, WindowStyle = unchecked((int)0x80000000) });
        window.AddHook(WndProc);
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        int size = GetSystemMetrics(49); // SM_CXSMICON
        icon = LoadImage(GetModuleHandle(null), 32512, 1, size, size, 0); // The exe's application icon.
        if (icon == nint.Zero) icon = LoadIcon(nint.Zero, 32512);
        Add();
    }

    private NotifyIconData Data(uint flags)
    {
        NotifyIconData data = new()
        {
            Size = Marshal.SizeOf<NotifyIconData>(),
            Window = window.Handle,
            Id = 1,
            Flags = flags,
            CallbackMessage = CallbackMessage,
            Icon = icon,
            Tip = tooltip,
            Info = "",
            InfoTitle = ""
        };
        return data;
    }

    private void Add()
    {
        NotifyIconData data = Data(NifMessage | NifIcon | NifTip);
        added = Shell_NotifyIcon(NimAdd, ref data);
        data.VersionOrTimeout = 4; // NOTIFYICON_VERSION_4
        Shell_NotifyIcon(NimSetVersion, ref data);
    }

    public void SetTooltip(string text)
    {
        tooltip = text.Length > 120 ? text[..120] : text;
        NotifyIconData data = Data(NifTip);
        if (added) Shell_NotifyIcon(NimModify, ref data);
    }

    public void ShowBalloon(string title, string text)
    {
        NotifyIconData data = Data(NifInfo);
        data.InfoTitle = title;
        data.Info = text;
        data.InfoFlags = 4; // NIIF_USER: use our icon
        if (added) Shell_NotifyIcon(NimModify, ref data);
    }

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == CallbackMessage)
        {
            int eventCode = (int)(lParam.ToInt64() & 0xFFFF);
            switch (eventCode)
            {
                case 0x0400 or 0x0203: // NIN_SELECT (left click with version 4), WM_LBUTTONDBLCLK
                    Activated?.Invoke();
                    break;
                case 0x007B: // WM_CONTEXTMENU (version 4 sends this for right click and keyboard)
                    ShowMenu();
                    break;
            }
            handled = true;
        }
        else if (message == (int)taskbarCreated)
        {
            Add(); // Explorer restarted.
        }
        return nint.Zero;
    }

    private void ShowMenu()
    {
        if (MenuItems is null) return;
        List<Action?> actions = [];
        nint menu = Build(MenuItems(), actions);
        try
        {
            GetCursorPos(out Point point);
            SetForegroundWindow(window.Handle);
            int chosen = TrackPopupMenuEx(menu, 0x0100 | 0x0080 | 0x0008, point.X, point.Y, window.Handle, nint.Zero); // RETURNCMD | NONOTIFY | BOTTOMALIGN
            PostMessage(window.Handle, 0, nint.Zero, nint.Zero);
            if (chosen > 0 && chosen <= actions.Count) actions[chosen - 1]?.Invoke();
        }
        finally { DestroyMenu(menu); }
    }

    private static nint Build(IEnumerable<MenuItem?> items, List<Action?> actions)
    {
        nint menu = CreatePopupMenu();
        foreach (MenuItem? item in items)
        {
            if (item is null) { AppendMenu(menu, 0x0800, 0, null); continue; } // MF_SEPARATOR
            uint flags = (item.Enabled ? 0u : 0x0003u) | (item.Checked ? 0x0008u : 0u); // MF_GRAYED, MF_CHECKED
            if (item.Children is { Count: > 0 })
            {
                nint child = Build(item.Children, actions);
                AppendMenu(menu, flags | 0x0010, (nuint)child, item.Text); // MF_POPUP
                continue;
            }
            actions.Add(item.Action);
            AppendMenu(menu, flags, (nuint)actions.Count, item.Text);
        }
        return menu;
    }

    public void Dispose()
    {
        if (added)
        {
            NotifyIconData data = Data(0);
            Shell_NotifyIcon(NimDelete, ref data);
            added = false;
        }
        window.Dispose();
        if (icon != nint.Zero) DestroyIcon(icon);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size;
        public nint Window;
        public uint Id, Flags, CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint VersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Item;
        public nint BalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint LoadImage(nint instance, nint name, uint type, int cx, int cy, uint load);
    [DllImport("user32.dll")] private static extern nint LoadIcon(nint instance, nint name);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint window, nint parameters);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
