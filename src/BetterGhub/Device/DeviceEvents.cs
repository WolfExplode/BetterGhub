namespace BetterGhub.Device;

internal abstract record DeviceEvent;
internal sealed record ConnectedEvent(int Dpi, int ReportIntervalMs) : DeviceEvent;
internal sealed record DisconnectedEvent(string Reason) : DeviceEvent;
internal sealed record ButtonEvent(int Bit, bool Down) : DeviceEvent;
internal sealed record DpiEvent(int Dpi) : DeviceEvent;
internal sealed record ReportIntervalEvent(int ReportIntervalMs) : DeviceEvent;
internal sealed record DeviceErrorEvent(string Message) : DeviceEvent;
