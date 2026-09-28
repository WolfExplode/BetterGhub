using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace BetterGhub.Device;

/// <summary>
/// Native HID++ 2.0 bridge for the G502 X LIGHTSPEED on a 046d:c547 receiver (device slot 1).
/// Enables volatile host mode (0x8100) and button spying (0x8110), streams button bits, and applies
/// DPI (0x2201) and report rate (0x8060), and reads the battery (0x1004 or 0x1000). Never writes flash. Mirrors bridge/hidpp_bridge.py.
/// </summary>
internal sealed class HidppBridge : IDisposable
{
    private const ushort VendorId = 0x046D, ProductId = 0xC547;
    private const byte Slot = 1, ReceiverIndex = 0xFF, SoftwareId = 0x0D;

    private readonly ConcurrentQueue<(string Command, int Value)> commands = new();
    private Thread? worker;
    private CancellationTokenSource? stop;
    private volatile Session? current;

    public event Action<DeviceEvent>? Event;
    public event Action? Exited;
    public bool Running => worker is { IsAlive: true };

    public void Start()
    {
        if (Running) return;
        stop = new CancellationTokenSource();
        CancellationToken token = stop.Token;
        worker = new Thread(() => Run(token)) { IsBackground = true, Name = "HID++ bridge" };
        worker.Start();
    }

    public void Stop()
    {
        if (stop is null) return;
        stop.Cancel();
        current?.Dispose(); // Unblocks pending reads.
        worker?.Join(2500);
        stop.Dispose();
        stop = null;
        worker = null;
        commands.Clear();
    }

    public void SetDpi(int dpi) => Send("dpi", dpi);
    public void SetReportInterval(int milliseconds) => Send("rate", milliseconds);

    private void Send(string command, int value)
    {
        commands.Enqueue((command, value));
        current?.Wake(); // Don't wait out the report poll; DPI Shift should apply immediately.
    }
    public void Dispose() => Stop();

    private void Emit(DeviceEvent item) => Event?.Invoke(item);

    private void Run(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using Session session = Session.Open();
                current = session;
                if (token.IsCancellationRequested) break;
                Serve(session, token);
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                Emit(new DisconnectedEvent(Friendly(error)));
                token.WaitHandle.WaitOne(2000);
            }
            catch (Exception) { /* Stopping. */ }
            finally { current = null; }
        }
        Exited?.Invoke();
    }

    private static string Friendly(Exception error) => error switch
    {
        ReceiverMissingException => "Receiver not found. Is it plugged in?",
        TimeoutException => "Mouse is asleep or switched off",
        Win32Exception { NativeErrorCode: 5 } => "The receiver is in use by another program (is G HUB running?)",
        _ => error.Message
    };

    private void Serve(Session session, CancellationToken token)
    {
        byte modeIndex = session.Call(0, 0, 0x81, 0x00, 0)[0];
        byte spyIndex = session.Call(0, 0, 0x81, 0x10, 0)[0];
        byte dpiIndex = session.Call(0, 0, 0x22, 0x01, 0)[0];
        byte rateIndex = session.Call(0, 0, 0x80, 0x60, 0)[0];
        if (modeIndex == 0 || spyIndex == 0) throw new IOException("This mouse does not support host mode or button reporting");

        EnsureHostMode(session, modeIndex);
        session.Call(spyIndex, 1, 0, 0, 0); // startSpy
        int dpi = 0, interval = 0;
        if (dpiIndex != 0)
        {
            byte[] payload = session.Call(dpiIndex, 2, 0, 0, 0);
            dpi = (payload[1] << 8) | payload[2];
        }
        if (rateIndex != 0) interval = session.Call(rateIndex, 1, 0, 0, 0)[0];
        Emit(new ConnectedEvent(dpi, interval));
        Emit(new FirmwareEvent(MouseFirmware(session), ReceiverFirmware(session)));
        Battery battery = Battery.Find(session);
        if (battery.Read(session) is { } charge) Emit(charge);
        Stopwatch sinceBattery = Stopwatch.StartNew();

        int previous = 0;
        Stopwatch sinceReport = Stopwatch.StartNew();
        while (!token.IsCancellationRequested)
        {
            while (commands.TryDequeue(out (string Command, int Value) command))
            {
                // Only the latest DPI matters when several are queued (e.g. a quick shift press and release).
                if (command.Command == "dpi" && commands.Any(x => x.Command == "dpi")) continue;
                try
                {
                    if (command.Command == "dpi")
                    {
                        if (dpiIndex == 0 || command.Value is < 100 or > 25600) throw new ArgumentException("DPI unavailable or outside 100–25600");
                        byte[] payload = session.Call(dpiIndex, 3, 0, (byte)(command.Value >> 8), (byte)command.Value); // Echoes the DPI set.
                        int applied = (payload[1] << 8) | payload[2];
                        Emit(new DpiEvent(applied > 0 ? applied : command.Value));
                    }
                    else
                    {
                        if (rateIndex == 0 || command.Value is not (1 or 2 or 4 or 8)) throw new ArgumentException("Report rate unavailable or unsupported");
                        session.Call(rateIndex, 2, (byte)command.Value, 0, 0);
                        Emit(new ReportIntervalEvent(session.Call(rateIndex, 1, 0, 0, 0)[0]));
                    }
                }
                catch (TimeoutException) { throw; }
                catch (Exception error) when (error is not IOException and not Win32Exception) { Emit(new DeviceErrorEvent(error.Message)); }
            }

            byte[]? report = session.Next(100);
            if (report is { Length: > 0 }) sinceReport.Restart();
            if (report is { Length: >= 6 } && report[0] == 0x11 && report[1] == Slot && report[2] == spyIndex && report[3] == 0)
            {
                int mask = (report[4] << 8) | report[5];
                int changed = mask ^ previous;
                for (int bit = 0; bit < 16; bit++)
                {
                    int flag = 1 << bit;
                    if ((changed & flag) != 0) Emit(new ButtonEvent(flag, (mask & flag) != 0));
                }
                previous = mask;
            }
            else if (battery.Parse(report) is { } changed) Emit(changed);
            if (sinceBattery.Elapsed > TimeSpan.FromSeconds(60))
            {
                if (battery.Read(session) is { } polled) Emit(polled);
                sinceBattery.Restart();
            }
            if (sinceReport.Elapsed > TimeSpan.FromSeconds(5))
            {
                // The receiver stays open while the mouse sleeps; re-arm so wake-ups and power cycles recover.
                EnsureHostMode(session, modeIndex);
                session.Call(spyIndex, 1, 0, 0, 0);
                sinceReport.Restart();
            }
        }
    }

    /// <summary>Unified Battery (0x1004) where the mouse has it, else Battery Status (0x1000).</summary>
    private sealed record Battery(byte Index, bool Unified)
    {
        public static Battery Find(Session session)
        {
            byte unified = session.Call(0, 0, 0x10, 0x04, 0)[0];
            return unified != 0 ? new Battery(unified, true) : new Battery(session.Call(0, 0, 0x10, 0x00, 0)[0], false);
        }

        /// <summary>Asks for the charge; null when the mouse has no battery feature or refuses the request.</summary>
        public BatteryEvent? Read(Session session)
        {
            if (Index == 0) return null;
            try { return Decode(session.Call(Index, Unified ? (byte)1 : (byte)0, 0, 0, 0)); }
            catch (HidppErrorException) { return null; }
        }

        /// <summary>The mouse broadcasts a battery event (function 0, software id 0) when the charge or power source changes.</summary>
        public BatteryEvent? Parse(byte[]? report) =>
            Index != 0 && report is { Length: >= 7 } && report[0] is 0x10 or 0x11 && report[1] == Slot && report[2] == Index && report[3] == 0
                ? Decode(report[4..]) : null;

        private BatteryEvent Decode(byte[] payload)
        {
            if (!Unified) return new BatteryEvent(payload[0], payload[2] is >= 1 and <= 4);
            // 0x1004: state of charge, level flags (1 critical, 2 low, 4 good, 8 full), charging status, external power.
            int percent = payload[0] != 0 ? payload[0] : payload[1] switch { >= 8 => 100, >= 4 => 50, >= 2 => 20, >= 1 => 5, _ => 0 };
            return new BatteryEvent(percent, payload[2] is 1 or 2 or 3);
        }
    }

    /// <summary>Main application firmware from DeviceInformation (0x0003): number, revision and build, shown as hex like G HUB.</summary>
    private static string? MouseFirmware(Session session)
    {
        try
        {
            byte index = session.Call(0, 0, 0x00, 0x03, 0)[0];
            if (index == 0) return null;
            int entities = session.Call(index, 0, 0, 0, 0)[0];
            for (int entity = 0; entity < entities; entity++)
            {
                byte[] info = session.Call(index, 1, (byte)entity, 0, 0); // type, 3-letter prefix, number, revision, build (2 bytes)
                if (info.Length >= 8 && info[0] == 0) return $"{info[4]:X}.{info[5]:X}.{(info[6] << 8) | info[7]:X}";
            }
        }
        catch (HidppErrorException) { }
        return null;
    }

    /// <summary>Receiver firmware from HID++ 1.0 register 0xF1: major and minor, then build.</summary>
    private static string? ReceiverFirmware(Session session)
    {
        try
        {
            byte[] version = session.ReadReceiverRegister(0xF1, 1); // Echoes the parameter first.
            byte[] build = session.ReadReceiverRegister(0xF1, 2);
            return $"{version[1]:X}.{version[2]:X}.{(build[1] << 8) | build[2]:X}";
        }
        catch (HidppErrorException) { return null; }
    }

    private static void EnsureHostMode(Session session, byte modeIndex)
    {
        if (session.Call(modeIndex, 2, 0, 0, 0)[0] == 2) return;
        session.Call(modeIndex, 1, 2, 0, 0);
        if (session.Call(modeIndex, 2, 0, 0, 0)[0] != 2) throw new IOException("Host mode did not stick in device memory");
    }

    /// <summary>Read-only diagnostic for --probe: lists interfaces and reads mode, DPI and rate without changing anything.</summary>
    public static string Probe()
    {
        StringBuilder text = new();
        text.AppendLine($"BetterGhub probe {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        List<HidDevice.Info> interfaces = HidDevice.Enumerate(VendorId, ProductId);
        text.AppendLine($"{interfaces.Count} HID interface(s) for {VendorId:x4}:{ProductId:x4}");
        foreach (HidDevice.Info item in interfaces)
            text.AppendLine($"  usage page 0x{item.UsagePage:x4} usage 0x{item.Usage:x2}  {item.Path}");
        try
        {
            using Session session = Session.Open();
            text.AppendLine($"Opened short ({session.ShortLengths}) and long ({session.LongLengths}) HID++ collections");
            foreach ((string name, byte high, byte low) in new[] { ("OnboardProfiles 0x8100", (byte)0x81, (byte)0x00), ("MouseButtonSpy 0x8110", (byte)0x81, (byte)0x10), ("AdjustableDpi 0x2201", (byte)0x22, (byte)0x01), ("ReportRate 0x8060", (byte)0x80, (byte)0x60) })
                text.AppendLine($"  {name}: feature index {session.Call(0, 0, high, low, 0)[0]}");
            byte mode = session.Call(0, 0, 0x81, 0x00, 0)[0];
            if (mode != 0) text.AppendLine($"  Current mode: {session.Call(mode, 2, 0, 0, 0)[0]} (1 onboard, 2 host)");
            byte dpi = session.Call(0, 0, 0x22, 0x01, 0)[0];
            if (dpi != 0) { byte[] p = session.Call(dpi, 2, 0, 0, 0); text.AppendLine($"  DPI: {(p[1] << 8) | p[2]}"); }
            byte rate = session.Call(0, 0, 0x80, 0x60, 0)[0];
            if (rate != 0) text.AppendLine($"  Report interval: {session.Call(rate, 1, 0, 0, 0)[0]} ms");
            text.AppendLine($"  Mouse firmware: {MouseFirmware(session) ?? "unknown"}");
            text.AppendLine($"  Receiver firmware: {ReceiverFirmware(session) ?? "unknown"}");
            Battery battery = Battery.Find(session);
            text.AppendLine($"  Battery ({(battery.Unified ? "UnifiedBattery 0x1004" : "BatteryStatus 0x1000")}): feature index {battery.Index}");
            if (battery.Read(session) is { } charge) text.AppendLine($"  Battery: {charge.Percent}%{(charge.Charging ? " charging" : "")}");
        }
        catch (Exception error) { text.AppendLine($"Probe stopped: {Friendly(error)} ({error.GetType().Name}: {error.Message})"); }
        return text.ToString();
    }

    private sealed class ReceiverMissingException(string message) : IOException(message);
    /// <summary>The mouse answered with a HID++ error: the request was refused, the link is fine.</summary>
    private sealed class HidppErrorException(string message) : IOException(message);

    /// <summary>The two vendor collections: usage 1 (short 0x10 reports) and usage 2 (long 0x11 reports).</summary>
    private sealed class Session : IDisposable
    {
        private readonly HidDevice shortDevice, longDevice;
        private readonly BlockingCollection<byte[]> reports = new(256);
        private readonly Queue<byte[]> pending = new();
        private readonly Thread[] readers;
        private volatile Exception? failure;
        private volatile bool disposed;
        private int disposing;

        public string ShortLengths => $"in {shortDevice.InputLength} / out {shortDevice.OutputLength}";
        public string LongLengths => $"in {longDevice.InputLength} / out {longDevice.OutputLength}";

        private Session(HidDevice shortDevice, HidDevice longDevice)
        {
            this.shortDevice = shortDevice;
            this.longDevice = longDevice;
            readers = [Reader(shortDevice), Reader(longDevice)];
        }

        public static Session Open()
        {
            List<HidDevice.Info> vendor = HidDevice.Enumerate(VendorId, ProductId).Where(x => x.UsagePage == 0xFF00).ToList();
            HidDevice.Info[] shortMatches = vendor.Where(x => x.Usage == 1).ToArray();
            HidDevice.Info[] longMatches = vendor.Where(x => x.Usage == 2).ToArray();
            if (shortMatches.Length != 1 || longMatches.Length != 1)
                throw new ReceiverMissingException($"Expected one of each HID++ collection; found {shortMatches.Length} short and {longMatches.Length} long");
            HidDevice shortDevice = HidDevice.Open(shortMatches[0].Path);
            try { return new Session(shortDevice, HidDevice.Open(longMatches[0].Path)); }
            catch { shortDevice.Dispose(); throw; }
        }

        private Thread Reader(HidDevice device)
        {
            Thread thread = new(() =>
            {
                try
                {
                    while (!disposed)
                        if (device.Read(250) is { Length: > 0 } report) reports.TryAdd(report);
                }
                catch (Exception error) when (!disposed) { failure = error; }
                catch (Exception) { /* Closing. */ }
            }) { IsBackground = true, Name = $"HID++ reader {device.Usage}" };
            thread.Start();
            return thread;
        }

        private byte[]? Take(int timeoutMs)
        {
            if (failure is { } error) throw new IOException("The receiver stopped responding", error);
            return reports.TryTake(out byte[]? report, timeoutMs) ? report : null;
        }

        /// <summary>Wakes a pending <see cref="Next"/> so queued commands run now.</summary>
        public void Wake() => reports.TryAdd([]);

        /// <summary>Next unsolicited report (e.g. button spy), or null after the timeout.</summary>
        public byte[]? Next(int timeoutMs) => pending.Count > 0 ? pending.Dequeue() : Take(timeoutMs);

        /// <summary>Short HID++ request; returns the reply parameters (bytes after the 4-byte header).</summary>
        public byte[] Call(byte feature, byte function, byte a, byte b, byte c) =>
            Request(Slot, feature, (byte)((function << 4) | SoftwareId), a, b, c);

        /// <summary>Reads a HID++ 1.0 register (sub-id 0x81) of the receiver itself (device index 0xFF).</summary>
        public byte[] ReadReceiverRegister(byte address, byte a) => Request(ReceiverIndex, 0x81, address, a, 0, 0);

        private byte[] Request(byte device, byte feature, byte functionSoftware, byte a, byte b, byte c)
        {
            byte[] request = [0x10, device, feature, functionSoftware, a, b, c];
            shortDevice.Write(request);
            Stopwatch clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 2000)
            {
                byte[]? reply = Take((int)Math.Max(1, 2000 - clock.ElapsedMilliseconds));
                if (reply is null) break;
                if (reply.Length == 0) continue; // Wake signal.
                if (reply.Length >= 4 && reply[1] == device)
                {
                    if (reply[0] == 0x8F || (reply[0] == 0x10 && reply[2] == 0x8F))
                        throw new HidppErrorException($"HID++ error response: {Convert.ToHexString(reply)}");
                    if (reply[0] == 0x11 && reply.Length >= 6 && reply[2] == 0xFF && reply[3] == feature && reply[4] == functionSoftware)
                        throw new HidppErrorException($"HID++ error {reply[5]} for feature {feature} function {functionSoftware >> 4}");
                    if (reply[0] is 0x10 or 0x11 && reply[2] == feature && reply[3] == functionSoftware)
                        return reply[4..];
                }
                if (pending.Count < 64) pending.Enqueue(reply); // Keep button reports that arrive mid-call.
            }
            throw new TimeoutException($"No reply to HID++ feature {feature} function {functionSoftware >> 4}");
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposing, 1) == 1) return;
            disposed = true;
            // Cancel pending reads and let the readers finish before their buffers are freed.
            shortDevice.Cancel();
            longDevice.Cancel();
            foreach (Thread reader in readers) reader.Join(1500);
            shortDevice.Dispose();
            longDevice.Dispose();
        }
    }
}
