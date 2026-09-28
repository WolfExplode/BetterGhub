using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BetterGhub.Device;

/// <summary>Minimal Win32 HID access: enumeration by VID/PID/usage and overlapped report I/O with timeouts.</summary>
internal sealed class HidDevice : IDisposable
{
    public string Path { get; }
    public ushort UsagePage { get; }
    public ushort Usage { get; }
    public int InputLength { get; }
    public int OutputLength { get; }
    private readonly SafeFileHandle handle;
    private readonly nint readBuffer, readOverlapped, readEvent;
    private readonly object writeGate = new();
    private bool disposed;

    private HidDevice(string path, SafeFileHandle handle, HidCaps caps)
    {
        Path = path;
        this.handle = handle;
        UsagePage = caps.UsagePage;
        Usage = caps.Usage;
        InputLength = caps.InputReportByteLength;
        OutputLength = caps.OutputReportByteLength;
        readBuffer = Marshal.AllocHGlobal(Math.Max(InputLength, 1));
        readOverlapped = Marshal.AllocHGlobal(Marshal.SizeOf<Overlapped>());
        readEvent = CreateEvent(nint.Zero, true, false, null);
    }

    public sealed record Info(string Path, ushort VendorId, ushort ProductId, ushort UsagePage, ushort Usage);

    /// <summary>Lists present HID interfaces for a vendor/product without opening them for I/O.</summary>
    public static List<Info> Enumerate(ushort vendorId, ushort productId)
    {
        List<Info> found = [];
        HidD_GetHidGuid(out Guid guid);
        nint set = SetupDiGetClassDevs(ref guid, null, nint.Zero, 0x12); // DIGCF_PRESENT | DIGCF_DEVICEINTERFACE
        if (set == new nint(-1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not list HID devices");
        try
        {
            DeviceInterfaceData data = new() { Size = Marshal.SizeOf<DeviceInterfaceData>() };
            for (uint index = 0; SetupDiEnumDeviceInterfaces(set, nint.Zero, ref guid, index, ref data); index++)
            {
                string? path = InterfacePath(set, ref data);
                if (path is null || !path.Contains($"vid_{vendorId:x4}&pid_{productId:x4}", StringComparison.OrdinalIgnoreCase)) continue;
                // Zero access is enough to read attributes and capabilities, even for devices opened exclusively.
                using SafeFileHandle probe = CreateFile(path, 0, 3, nint.Zero, 3, 0, nint.Zero);
                if (probe.IsInvalid) continue;
                if (!TryCaps(probe, out HidCaps caps)) continue;
                HidAttributes attributes = new() { Size = Marshal.SizeOf<HidAttributes>() };
                if (!HidD_GetAttributes(probe, ref attributes)) continue;
                found.Add(new Info(path, attributes.VendorId, attributes.ProductId, caps.UsagePage, caps.Usage));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return found;
    }

    public static HidDevice Open(string path)
    {
        // GENERIC_READ | GENERIC_WRITE, shared read/write, FILE_FLAG_OVERLAPPED — the same as hidapi.
        SafeFileHandle handle = CreateFile(path, 0xC0000000, 3, nint.Zero, 3, 0x40000000, nint.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the receiver");
        if (!TryCaps(handle, out HidCaps caps))
        {
            handle.Dispose();
            throw new IOException("Could not read the receiver's HID capabilities");
        }
        return new HidDevice(path, handle, caps);
    }

    private static bool TryCaps(SafeFileHandle handle, out HidCaps caps)
    {
        caps = default;
        if (!HidD_GetPreparsedData(handle, out nint preparsed)) return false;
        try { return HidP_GetCaps(preparsed, out caps) == 0x00110000; } // HIDP_STATUS_SUCCESS
        finally { HidD_FreePreparsedData(preparsed); }
    }

    private static string? InterfacePath(nint set, ref DeviceInterfaceData data)
    {
        SetupDiGetDeviceInterfaceDetail(set, ref data, nint.Zero, 0, out int required, nint.Zero);
        if (required <= 0) return null;
        nint detail = Marshal.AllocHGlobal(required);
        try
        {
            Marshal.WriteInt32(detail, nint.Size == 8 ? 8 : 6); // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
            if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, required, out _, nint.Zero)) return null;
            return Marshal.PtrToStringUni(detail + 4);
        }
        finally { Marshal.FreeHGlobal(detail); }
    }

    /// <summary>Writes one output report, padded to the report length. The first byte is the report id.</summary>
    public void Write(ReadOnlySpan<byte> report, int timeoutMs = 1000)
    {
        lock (writeGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            byte[] padded = new byte[Math.Max(OutputLength, report.Length)];
            report.CopyTo(padded);
            nint buffer = Marshal.AllocHGlobal(padded.Length);
            nint overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<Overlapped>());
            nint done = CreateEvent(nint.Zero, true, false, null);
            try
            {
                Marshal.Copy(padded, 0, buffer, padded.Length);
                Marshal.StructureToPtr(new Overlapped { Event = done }, overlapped, false);
                if (!WriteFile(handle, buffer, padded.Length, out _, overlapped))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != 997) throw new Win32Exception(error, "Receiver write failed"); // ERROR_IO_PENDING
                    if (WaitForSingleObject(done, (uint)timeoutMs) != 0)
                    {
                        CancelIoEx(handle, overlapped);
                        GetOverlappedResult(handle, overlapped, out _, true);
                        throw new TimeoutException("Receiver write timed out");
                    }
                }
                if (!GetOverlappedResult(handle, overlapped, out int written, false))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Receiver write failed");
                if (written != padded.Length) throw new IOException($"Wrote {written} of {padded.Length} bytes");
            }
            finally
            {
                CloseHandle(done);
                Marshal.FreeHGlobal(overlapped);
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>Blocks for one input report. Returns null on timeout. Use from one reader thread only.</summary>
    public byte[]? Read(int timeoutMs)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ResetEvent(readEvent);
        Marshal.StructureToPtr(new Overlapped { Event = readEvent }, readOverlapped, false);
        if (!ReadFile(handle, readBuffer, InputLength, out _, readOverlapped))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != 997) throw new Win32Exception(error, "The receiver stopped responding");
            if (WaitForSingleObject(readEvent, (uint)timeoutMs) != 0)
            {
                CancelIoEx(handle, readOverlapped);
                GetOverlappedResult(handle, readOverlapped, out _, true);
                return null;
            }
        }
        if (!GetOverlappedResult(handle, readOverlapped, out int read, false))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The receiver stopped responding");
        byte[] report = new byte[read];
        Marshal.Copy(readBuffer, report, 0, read);
        return report;
    }

    /// <summary>Aborts in-flight I/O so a blocked reader returns. Call before joining the reader, then Dispose.</summary>
    public void Cancel()
    {
        if (!handle.IsClosed) CancelIoEx(handle, nint.Zero);
    }

    /// <summary>Frees the handle and buffers. The reader thread must have exited first.</summary>
    public void Dispose()
    {
        lock (writeGate)
        {
            if (disposed) return;
            disposed = true;
        }
        Cancel();
        handle.Dispose();
        CloseHandle(readEvent);
        Marshal.FreeHGlobal(readOverlapped);
        Marshal.FreeHGlobal(readBuffer);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Overlapped { public nint Internal, InternalHigh; public uint Offset, OffsetHigh; public nint Event; }
    [StructLayout(LayoutKind.Sequential)] private struct DeviceInterfaceData { public int Size; public Guid ClassGuid; public int Flags; public nint Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct HidAttributes { public int Size; public ushort VendorId, ProductId, Version; }
    [StructLayout(LayoutKind.Sequential)] private struct HidCaps
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices, NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] private static extern bool HidD_GetAttributes(SafeFileHandle device, ref HidAttributes attributes);
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle device, out nint data);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(nint data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(nint data, out HidCaps caps);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint SetupDiGetClassDevs(ref Guid guid, string? enumerator, nint parent, int flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(nint set, nint info, ref Guid guid, uint index, ref DeviceInterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInterfaceDetail(nint set, ref DeviceInterfaceData data, nint detail, int size, out int required, nint info);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(nint set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadFile(SafeFileHandle file, nint buffer, int count, out int read, nint overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteFile(SafeFileHandle file, nint buffer, int count, out int written, nint overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetOverlappedResult(SafeFileHandle file, nint overlapped, out int transferred, bool wait);
    [DllImport("kernel32.dll")] private static extern bool CancelIoEx(SafeFileHandle file, nint overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateEvent(nint security, bool manualReset, bool initialState, string? name);
    [DllImport("kernel32.dll")] private static extern bool ResetEvent(nint handle);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
