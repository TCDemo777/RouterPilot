using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>Parses the fixed, read-only interface probe without router state.</summary>
internal static class RouterPortTelemetryParser
{
    public static IReadOnlyList<RouterPortSnapshot> Parse(string? output)
    {
        var results = new List<RouterPortSnapshot>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string rawLine in (output ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = rawLine.Trim().Split('|');
            if (fields.Length < 2 || !string.Equals(fields[0], "P", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(fields[1]))
                continue;

            string name = fields[1].Trim();
            if (!seen.Add(name)) continue;

            RouterInterfaceType type = ParseType(Field(fields, 2));
            bool? carrier = Field(fields, 4).Trim() switch { "1" or "up" => true, "0" or "down" => false, _ => null };
            string link = carrier switch { true => "Connected", false => "Disconnected", _ => "Unknown" };
            int? speed = ParseSpeed(Field(fields, 5));
            string duplex = Field(fields, 6).Trim().ToLowerInvariant() switch { "full" => "Full", "half" => "Half", _ => "Unknown" };
            bool physical = type == RouterInterfaceType.PhysicalEthernet;
            results.Add(new RouterPortSnapshot(
                name, name, name, fields[3].Trim(), type, carrier, link, speed, duplex,
                Field(fields, 7).Trim(), SplitAddresses(Field(fields, 16)), SplitAddresses(Field(fields, 17)),
                ParseCounter(Field(fields, 8)), ParseCounter(Field(fields, 9)), ParseCounter(Field(fields, 10)),
                ParseCounter(Field(fields, 11)), ParseCounter(Field(fields, 12)), ParseCounter(Field(fields, 13)),
                NullIfUnavailable(Field(fields, 14)), physical, !physical && type is not RouterInterfaceType.Unknown));
        }

        return results.OrderBy(item => item.InterfaceType == RouterInterfaceType.PhysicalEthernet ? 0 : 1)
            .ThenBy(item => item.InterfaceName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static RouterInterfaceType ParseType(string value) => value.Trim().ToLowerInvariant() switch
    {
        "physical" => RouterInterfaceType.PhysicalEthernet,
        "bridge" => RouterInterfaceType.Bridge,
        "vlan" => RouterInterfaceType.Vlan,
        "wireless" => RouterInterfaceType.Wireless,
        "vpn" => RouterInterfaceType.Vpn,
        "loopback" => RouterInterfaceType.Loopback,
        "logical" => RouterInterfaceType.Logical,
        "virtual" => RouterInterfaceType.Virtual,
        _ => RouterInterfaceType.Unknown
    };

    private static int? ParseSpeed(string value)
    {
        if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int speed) || speed <= 0 || speed > 100000)
            return null;
        return speed;
    }

    private static long? ParseCounter(string value) => long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long result) && result >= 0 ? result : null;
    private static string Field(string[] fields, int index) => index < fields.Length ? fields[index] : string.Empty;
    private static string? NullIfUnavailable(string value) => string.IsNullOrWhiteSpace(value) || value.Trim() == "-" ? null : value.Trim();
    private static IReadOnlyList<string> SplitAddresses(string value) => string.IsNullOrWhiteSpace(value) ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
