namespace BetterGhub.Core;

/// <summary>
/// G HUB's battery-life estimate for the G502 X LIGHTSPEED. The mouse only reports a charge percentage;
/// G HUB divides the battery's energy by a per-part power draw. The draws come from G HUB's power model
/// for the G502 wireless family (depots\g502_wireless\battery.xml), and they match what G HUB shows for the
/// G502 X (system 6 mW, report rate 8 mW at 1 ms). The G502 X's own model ships encrypted, so its battery
/// energy is derived from G HUB's "max charge approx 149 hours" at 1000 Hz.
/// </summary>
internal static class PowerModel
{
    private const double SensorMilliwatts = 4.28, McuMilliwatts = 1.640666667;
    private const double BatteryMilliwattHours = 2098;

    /// <summary>HERO sensor plus microcontroller.</summary>
    public static double SystemMilliwatts => SensorMilliwatts + McuMilliwatts;

    public static double ReportRateMilliwatts(int intervalMs) => intervalMs switch
    {
        1 => 8.162673333,
        2 => 4.770892222,
        4 => 3.075001667,
        _ => 2.227056389
    };

    /// <summary>Hours a full charge lasts at the given report interval.</summary>
    public static double MaxHours(int intervalMs) => BatteryMilliwattHours / (SystemMilliwatts + ReportRateMilliwatts(intervalMs));

    public static double HoursLeft(int percent, int intervalMs) => MaxHours(intervalMs) * percent / 100;
}
