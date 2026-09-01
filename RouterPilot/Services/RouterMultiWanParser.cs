using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RouterPilot.Models;

namespace RouterPilot.Services;

internal static class RouterMultiWanParser
{
    public static RouterMultiWanSnapshot Parse(string? output, DateTimeOffset capturedAt)
    {
        bool? enabled = null;
        RouterCapabilityState capability = RouterCapabilityState.Unknown;
        RouterMultiWanMode mode = RouterMultiWanMode.Unknown;
        string? active = null, @default = null;
        var paths = new List<RouterWanPathSnapshot>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in (output ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string[] f = line.Trim().Split('|');
            if (f.Length == 0) continue;
            if (f[0] == "S")
            {
                capability = Field(f, 1).ToLowerInvariant() switch { "supported" => RouterCapabilityState.Supported, "unsupported" => RouterCapabilityState.Unsupported, _ => RouterCapabilityState.Unknown };
                enabled = ParseBool(Field(f, 2));
                mode = Field(f, 3).ToLowerInvariant() switch { "single" => RouterMultiWanMode.SingleWan, "failover" => RouterMultiWanMode.Failover, "load-balance" or "loadbalancing" => RouterMultiWanMode.LoadBalancing, _ => RouterMultiWanMode.Unknown };
                active = NullIf(Field(f, 4)); @default = NullIf(Field(f, 5));
                continue;
            }
            if (f[0] != "W" || string.IsNullOrWhiteSpace(Field(f, 1)) || !seen.Add(Field(f, 1))) continue;
            RouterWanRuntimeState state = Field(f, 8).ToLowerInvariant() switch { "online" or "up" => RouterWanRuntimeState.Online, "offline" or "down" => RouterWanRuntimeState.Offline, _ => RouterWanRuntimeState.Unknown };
            paths.Add(new RouterWanPathSnapshot(Field(f, 1), Field(f, 2), ParseType(Field(f, 3)), Field(f, 4), Field(f, 5), ParseBool(Field(f, 6)), ParseBool(Field(f, 7)), state, ParseBool(Field(f, 9)), ParseBool(Field(f, 10)) == true, ParseBool(Field(f, 11)) == true, NullIf(Field(f, 12)), NullIf(Field(f, 13)), NullIf(Field(f, 14)), ParseInt(Field(f, 15)), ParseInt(Field(f, 16)), ParseInt(Field(f, 17))));
        }
        paths = paths.OrderByDescending(p => p.IsActive).ThenByDescending(p => p.IsDefault).ThenBy(p => p.Priority ?? int.MaxValue).ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList();
        return new RouterMultiWanSnapshot(capability, enabled, mode, active, @default, paths, capturedAt);
    }

    private static RouterWanConnectionType ParseType(string value) => value.Trim().ToLowerInvariant() switch { "ethernet" => RouterWanConnectionType.Ethernet, "repeater" or "wifi" => RouterWanConnectionType.Repeater, "tethering" or "usb" => RouterWanConnectionType.Tethering, "cellular" or "modem" => RouterWanConnectionType.Cellular, _ => RouterWanConnectionType.Unknown };
    private static bool? ParseBool(string value) => value.Trim().ToLowerInvariant() switch { "1" or "true" or "yes" or "up" => true, "0" or "false" or "no" or "down" => false, _ => null };
    private static int? ParseInt(string value) => int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) && result >= 0 ? result : null;
    private static string Field(string[] fields, int index) => index < fields.Length ? fields[index].Trim() : string.Empty;
    private static string? NullIf(string value) => string.IsNullOrWhiteSpace(value) || value == "-" ? null : value;
}
