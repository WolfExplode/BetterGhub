using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace BetterGhub.Probe;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        int seconds = 90;
        if (args.Length == 2 && args[0] == "--seconds" && int.TryParse(args[1], out int parsed) && parsed > 0)
            seconds = parsed;
        else if (args.Length != 0)
        {
            Console.Error.WriteLine("Usage: BetterGhub.Probe [--seconds N]");
            Environment.ExitCode = 2;
            return;
        }

        Console.WriteLine("BetterGhub button probe: G502 X LIGHTSPEED receiver 046d:c547");
        Console.WriteLine("Listening for physical receiver mouse/keyboard events and G HUB virtual keyboard events.");
        Console.WriteLine("Movement and unrelated keyboards are ignored. This program does not change device settings.");
        Console.WriteLine($"Capture duration: {seconds} seconds. Press each control once, then hold the macro buttons briefly.");
        Application.Run(new ProbeWindow(seconds));
    }
}

internal sealed class ProbeWindow : Form
{
    private const int WmInput = 0x00FF;
    private const uint RidInput = 0x10000003;
    private const uint RidiDeviceName = 0x20000007;
    private const uint RidevInputSink = 0x00000100;
    private const uint RimMouse = 0;
    private const uint RimKeyboard = 1;
    private const uint RimHid = 2;
    private readonly Dictionary<nint, string> devices = new();
    private readonly System.Windows.Forms.Timer timeout = new();
    private ushort lastButtonMask;

    public ProbeWindow(int seconds)
    {
        Text = "BetterGhub Button Probe";
        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        Load += (_, _) => Hide();
        timeout.Interval = checked(seconds * 1000);
        timeout.Tick += (_, _) => Close();
        Shown += (_, _) => timeout.Start();

        RawInputDevice[] registrations =
        [
            new(1, 2, RidevInputSink, Handle), // Generic desktop mouse
            new(1, 6, RidevInputSink, Handle), // Generic desktop keyboard
            new(0xff00, 1, RidevInputSink, Handle), // Logitech HID++ short reports
            new(0xff00, 2, RidevInputSink, Handle), // Logitech HID++ long reports
        ];
        if (!RegisterRawInputDevices(registrations, (uint)registrations.Length, (uint)Marshal.SizeOf<RawInputDevice>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterRawInputDevices failed");
        Console.WriteLine("READY — press mouse controls now.");
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmInput)
            ReadInput(message.LParam);
        base.WndProc(ref message);
    }

    private void ReadInput(nint rawHandle)
    {
        uint headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        uint size = 0;
        if (GetRawInputData(rawHandle, RidInput, nint.Zero, ref size, headerSize) == uint.MaxValue || size < headerSize)
            return;

        nint buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            if (GetRawInputData(rawHandle, RidInput, buffer, ref size, headerSize) == uint.MaxValue)
                return;
            RawInputHeader header = Marshal.PtrToStructure<RawInputHeader>(buffer);
            string device = DeviceName(header.Device);
            bool physical = device.Contains("VID_046D&PID_C547", StringComparison.OrdinalIgnoreCase);
            bool virtualKeyboard = device.Contains("VID_046D&PID_C232", StringComparison.OrdinalIgnoreCase);
            if (!physical && !virtualKeyboard)
                return;

            string source = physical ? "PHYSICAL 046d:c547" : "G HUB VIRTUAL c232";
            nint data = buffer + checked((int)headerSize);
            if (header.Type == RimMouse)
            {
                // RAWMOUSE: flags (2), padding (2), button flags/data (4), raw buttons (4), X/Y (8).
                ushort flags = unchecked((ushort)Marshal.ReadInt16(data, 4));
                ushort wheelData = unchecked((ushort)Marshal.ReadInt16(data, 6));
                uint rawButtons = unchecked((uint)Marshal.ReadInt32(data, 8));
                if (flags == 0 && rawButtons == 0)
                    return; // Movement only.
                Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {source} MOUSE {MouseFlags(flags, wheelData)} rawButtons=0x{rawButtons:x8}");
            }
            else if (header.Type == RimKeyboard)
            {
                ushort scan = unchecked((ushort)Marshal.ReadInt16(data, 0));
                ushort flags = unchecked((ushort)Marshal.ReadInt16(data, 2));
                ushort key = unchecked((ushort)Marshal.ReadInt16(data, 6));
                string edge = (flags & 1) == 0 ? "DOWN" : "UP";
                Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {source} KEY {edge} vk=0x{key:x2} scan=0x{scan:x2} flags=0x{flags:x2}");
            }
            else if (header.Type == RimHid && physical)
            {
                uint reportSize = unchecked((uint)Marshal.ReadInt32(data, 0));
                uint reportCount = unchecked((uint)Marshal.ReadInt32(data, 4));
                if (reportSize == 0 || reportSize > 256 || reportCount == 0 || reportCount > 32)
                    return;
                int bytes = checked((int)(reportSize * reportCount));
                if ((ulong)headerSize + 8UL + (uint)bytes > size)
                    return;
                byte[] reports = new byte[bytes];
                Marshal.Copy(data + 8, reports, 0, bytes);
                string endpoint = device.Contains("COL02", StringComparison.OrdinalIgnoreCase) ? "COL02" : "COL01";
                if (endpoint == "COL02" && reports.Length >= 6 && reports[0] == 0x11 && reports[1] == 0x01 && reports[2] == 0x0a)
                {
                    ushort mask = (ushort)((reports[4] << 8) | reports[5]);
                    ushort changed = (ushort)(mask ^ lastButtonMask);
                    for (int bit = 0; bit < 16; bit++)
                    {
                        ushort flag = (ushort)(1 << bit);
                        if ((changed & flag) != 0)
                            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {source} BUTTON bit=0x{flag:x4} {((mask & flag) != 0 ? "DOWN" : "UP")}");
                    }
                    lastButtonMask = mask;
                }
                else
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {source} HID {endpoint} size={reportSize} count={reportCount} {Convert.ToHexString(reports)}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private string DeviceName(nint handle)
    {
        if (devices.TryGetValue(handle, out string? cached))
            return cached;
        uint chars = 0;
        _ = GetRawInputDeviceInfo(handle, RidiDeviceName, null, ref chars);
        if (chars == 0)
            return devices[handle] = "unknown";
        StringBuilder name = new(checked((int)chars));
        if (GetRawInputDeviceInfo(handle, RidiDeviceName, name, ref chars) == uint.MaxValue)
            return devices[handle] = "unknown";
        return devices[handle] = name.ToString();
    }

    private static string MouseFlags(ushort flags, ushort data)
    {
        List<string> labels = [];
        string[] names = ["LEFT", "RIGHT", "MIDDLE", "X1", "X2"];
        for (int i = 0; i < names.Length; i++)
        {
            if ((flags & (1 << (i * 2))) != 0) labels.Add(names[i] + " DOWN");
            if ((flags & (2 << (i * 2))) != 0) labels.Add(names[i] + " UP");
        }
        if ((flags & 0x0400) != 0) labels.Add($"WHEEL {(short)data}");
        if ((flags & 0x0800) != 0) labels.Add($"H-WHEEL {(short)data}");
        if (labels.Count == 0) labels.Add($"flags=0x{flags:x4}");
        return string.Join(", ", labels);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct RawInputDevice(ushort usagePage, ushort usage, uint flags, nint target)
    {
        public readonly ushort UsagePage = usagePage;
        public readonly ushort Usage = usage;
        public readonly uint Flags = flags;
        public readonly nint Target = target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct RawInputHeader
    {
        public readonly uint Type;
        public readonly uint Size;
        public readonly nint Device;
        public readonly nint WParam;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(nint input, uint command, nint data, ref uint size, uint headerSize);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(nint device, uint command, StringBuilder? name, ref uint size);
}
