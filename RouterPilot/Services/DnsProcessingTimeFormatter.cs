using System;
using System.Globalization;

namespace RouterPilot.Services;

/// <summary>Formats the authoritative AdGuard Home processing-time statistic.</summary>
public static class DnsProcessingTimeFormatter
{
    public static string Format(double? seconds)
    {
        if (seconds is not double value || !double.IsFinite(value) || value < 0)
        {
            return "—";
        }

        double milliseconds = value * 1_000d;
        if (milliseconds < 1d && value > 0d)
        {
            return Math.Round(value * 1_000_000d).ToString("0", CultureInfo.InvariantCulture) + " µs";
        }

        if (milliseconds < 10d)
        {
            return milliseconds.ToString("0.0", CultureInfo.InvariantCulture) + " ms";
        }

        if (milliseconds < 1_000d)
        {
            return Math.Round(milliseconds).ToString("0", CultureInfo.InvariantCulture) + " ms";
        }

        return value.ToString("0.0", CultureInfo.InvariantCulture) + " s";
    }
}
