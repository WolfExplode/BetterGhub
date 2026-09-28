namespace BetterGhub.Device;

internal abstract record DeviceEvent;
internal sealed record ConnectedEvent(int Dpi, int ReportIntervalMs) : DeviceEvent;
internal sealed record DisconnectedEvent(string Reason) : DeviceEvent;
internal sealed record ButtonEvent(int Bit, bool Down) : DeviceEvent;
internal sealed record DpiEvent(int Dpi) : DeviceEvent;
internal sealed record ReportIntervalEvent(int ReportIntervalMs) : DeviceEvent;
/// <summary>Charge in percent and whether the mouse is on its cable or charging dock.</summary>
internal sealed record BatteryEvent(int Percent, bool Charging) : DeviceEvent;
/// <summary>Firmware versions as G HUB shows them (e.g. 30.0.14); null when the device won't say.</summary>
internal sealed record FirmwareEvent(string? Mouse, string? Receiver) : DeviceEvent;
internal sealed record DeviceErrorEvent(string Message) : DeviceEvent;
