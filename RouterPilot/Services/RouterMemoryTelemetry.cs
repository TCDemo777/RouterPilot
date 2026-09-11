using System.Globalization;

namespace RouterPilot.Services;

/// <summary>
/// Parses the memory fields obtained from the existing /proc/meminfo telemetry
/// command.  Its Used value follows LuCI's Status Overview definition:
/// MemTotal minus MemFree.  Cached and buffered pages remain separate values.
/// </summary>
internal sealed record RouterMemoryTelemetry(
    long? TotalKilobytes,
    long? FreeKilobytes,
    long? AvailableKilobytes,
    long? BufferedKilobytes,
    long? CachedKilobytes)
{
    public bool IsAvailable => TotalKilobytes is > 0
                               && FreeKilobytes is >= 0
                               && FreeKilobytes <= TotalKilobytes;

    public long? UsedKilobytes => IsAvailable
        ? Math.Clamp(TotalKilobytes!.Value - FreeKilobytes!.Value, 0, TotalKilobytes.Value)
        : null;

    public double? UsagePercentage => UsedKilobytes is long used && TotalKilobytes is long total && total > 0
        ? Math.Clamp(Math.Round(used / (double)total * 100, 1), 0, 100)
        : null;
}

/// <summary>
/// The sole display projection for router memory telemetry.  Every router
/// surface receives values already normalised from the same snapshot, rather
/// than calculating or formatting memory values independently.
/// </summary>
internal sealed record RouterMemoryPresentation(
    string Usage,
    string Total,
    string Used,
    string Available,
    string Buffered,
    string Cached)
{
    public static RouterMemoryPresentation From(RouterMemoryTelemetry snapshot)
    {
        if (!snapshot.IsAvailable || snapshot.UsagePercentage is not double percentage || snapshot.UsedKilobytes is not long used)
        {
            return new RouterMemoryPresentation("-", "-", "-", "-", "-", "-");
        }

        return new RouterMemoryPresentation(
            percentage.ToString("0.#", CultureInfo.InvariantCulture) + "%",
            FormatKilobytes(snapshot.TotalKilobytes!.Value),
            FormatKilobytes(used),
            snapshot.AvailableKilobytes is long available ? FormatKilobytes(available) : "-",
            snapshot.BufferedKilobytes is long buffered ? FormatKilobytes(buffered) : "-",
            snapshot.CachedKilobytes is long cached ? FormatKilobytes(cached) : "-");
    }

    private static string FormatKilobytes(long kilobytes)
    {
        double megabytes = kilobytes / 1024d;
        return megabytes >= 1024d
            ? $"{megabytes / 1024d:0.##} GiB"
            : $"{megabytes:0.##} MiB";
    }
}

internal static class RouterMemoryTelemetryParser
{
    public static RouterMemoryTelemetry Parse(string? telemetry)
    {
        long? total = null;
        long? free = null;
        long? available = null;
        long? buffered = null;
        long? cached = null;

        foreach (string line in (telemetry ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = line.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || !long.TryParse(pair[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) || value < 0)
                continue;

            switch (pair[0])
            {
                case "MemTotal": total = value; break;
                case "MemFree": free = value; break;
                case "MemAvailable": available = value; break;
                case "Buffers": buffered = value; break;
                case "Cached": cached = value; break;
            }
        }

        if (total is > 0)
        {
            if (available > total)
            {
                available = null;
            }

            if (buffered > total)
            {
                buffered = null;
            }

            if (cached > total)
            {
                cached = null;
            }
        }

        return new RouterMemoryTelemetry(total, free, available, buffered, cached);
    }
}
