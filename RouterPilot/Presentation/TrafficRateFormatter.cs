using System;
using System.Globalization;

namespace RouterPilot.Presentation;

/// <summary>
/// Shared presentation for RouterPilot traffic rates. Rates use binary units
/// because the underlying counters are bytes and the traffic session already
/// derives bytes per second.
/// </summary>
public static class TrafficRateFormatter
{
    public static string Format(long bytesPerSecond)
    {
        double value = Math.Max(0, bytesPerSecond);
        string[] units = ["B/s", "KiB/s", "MiB/s", "GiB/s", "TiB/s"];
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? value.ToString("0", CultureInfo.InvariantCulture) + " " + units[unit]
            : value.ToString("0.##", CultureInfo.InvariantCulture) + " " + units[unit];
    }
}
