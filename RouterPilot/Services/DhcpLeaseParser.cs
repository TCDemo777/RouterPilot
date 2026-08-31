using System;
using System.Collections.Generic;
using System.Linq;
using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>
/// Pure conversion of dnsmasq's lease-file rows into RouterPilot models.
/// Transport, caching, and refresh lifecycle remain owned by RouterManager.
/// </summary>
internal static class DhcpLeaseParser
{
    public static List<DhcpLeaseInfo> Parse(string output)
    {
        var leases = new List<DhcpLeaseInfo>();
        foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 4 || !long.TryParse(fields[0], out long expirySeconds)) continue;

            bool isStatic = expirySeconds == 0;
            DateTimeOffset? expiry = isStatic ? null : DateTimeOffset.FromUnixTimeSeconds(expirySeconds);
            string hostname = fields[3] == "*" ? "Unknown device" : fields[3];
            leases.Add(new DhcpLeaseInfo
            {
                Hostname = hostname,
                ClientName = hostname,
                MacAddress = fields[1],
                IpAddress = fields[2],
                IsStatic = isStatic,
                Expiry = expiry,
                RemainingLease = FormatRemainingLease(expiry, isStatic)
            });
        }

        return leases.OrderBy(lease => lease.Hostname, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string FormatRemainingLease(DateTimeOffset? expiry, bool isStatic)
    {
        if (isStatic) return "Static";
        if (expiry is null) return "N/A";
        TimeSpan remaining = expiry.Value - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) return "Expired";
        if (remaining.TotalMinutes < 60) return $"{Math.Ceiling(remaining.TotalMinutes):0} min";
        if (remaining.TotalHours < 24) return $"{Math.Ceiling(remaining.TotalHours):0} hr";
        return $"{Math.Ceiling(remaining.TotalDays):0} days";
    }
}
