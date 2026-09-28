using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace BetterGhub.App;

internal sealed class RawMouseWheel
{
    public const int Up = 0x10000;
    public const int Down = 0x20000;
    public const int Left = 0x40000;
    public const int Right = 0x80000;
    private readonly Dictionary<nint, string> devices = [];

    public void Register(nint window)
    {
        RawInputDevice[] registrations = [new(1, 2, 0x100, window)];
        if (!RegisterRawInputDevices(registrations, 1, (uint)Marshal.SizeOf<RawInputDevice>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Mouse Raw Input registration failed");
    }

    public int Read(nint input)
    {
        uint headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        uint size = 0;
        if (GetRawInputData(input, 0x10000003, nint.Zero, ref size, headerSize) == uint.MaxValue || size < headerSize + 8) return 0;
        nint buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            if (GetRawInputData(input, 0x10000003, buffer, ref size, headerSize) == uint.MaxValue) return 0;
            RawInputHeader header = Marshal.PtrToStructure<RawInputHeader>(buffer);
            if (header.Type != 0 || !DeviceName(header.Device).Contains("VID_046D&PID_C547", StringComparison.OrdinalIgnoreCase)) return 0;
            nint data = buffer + (int)headerSize;
            ushort flags = unchecked((ushort)Marshal.ReadInt16(data, 4));
            short amount = Marshal.ReadInt16(data, 6);
            if ((flags & 0x0400) != 0) return amount > 0 ? Up : Down;
            if ((flags & 0x0800) != 0) return amount > 0 ? Right : Left;
            return 0;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private string DeviceName(nint handle)
    {
        if (devices.TryGetValue(handle, out string? found)) return found;
        uint chars = 0;
        _ = GetRawInputDeviceInfo(handle, 0x20000007, null, ref chars);
        if (chars == 0) return devices[handle] = "unknown";
        StringBuilder name = new(checked((int)chars));
        if (GetRawInputDeviceInfo(handle, 0x20000007, name, ref chars) == uint.MaxValue) return devices[handle] = "unknown";
        return devices[handle] = name.ToString();
    }

    [StructLayout(LayoutKind.Sequential)] private readonly struct RawInputDevice(ushort page, ushort usage, uint flags, nint target)
    {
        public readonly ushort Page = page, Usage = usage;
        public readonly uint Flags = flags;
        public readonly nint Target = target;
    }
    [StructLayout(LayoutKind.Sequential)] private readonly struct RawInputHeader
    {
        public readonly uint Type, Size;
        public readonly nint Device, WParam;
    }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputData(nint input, uint command, nint data, ref uint size, uint headerSize);
    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(nint device, uint command, StringBuilder? name, ref uint size);
}
