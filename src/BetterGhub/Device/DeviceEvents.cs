using BetterGhub.Core;

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
/// <summary>On-board profile slots read from the mouse's memory.</summary>
internal sealed record OnboardMemoryEvent(OnboardMemory Memory) : DeviceEvent;
/// <summary>Result of writing on-board memory; a fresh <see cref="OnboardMemoryEvent"/> follows either way.</summary>
internal sealed record OnboardWriteEvent(bool Success, string Message) : DeviceEvent;
/// <summary>One sector to write, and what the app last read there.</summary>
internal sealed record SectorWrite(int Sector, byte[] Expected, byte[] Data);
/// <summary>Which mode the mouse is in: the on-board slot's sector, or 0 for host mode (BetterGhub handles the buttons).</summary>
internal sealed record ModeEvent(int OnboardSector) : DeviceEvent;
internal sealed record DeviceErrorEvent(string Message) : DeviceEvent;
