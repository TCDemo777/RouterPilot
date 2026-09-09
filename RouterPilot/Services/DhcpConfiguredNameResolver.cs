using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>Maps explicit UCI DHCP-host labels onto exact active lease identities.</summary>
public static class DhcpConfiguredNameResolver
{
    public static void Apply(IEnumerable<DhcpLeaseInfo> leases, IEnumerable<DhcpReservationInfo> reservations)
    {
        ArgumentNullException.ThrowIfNull(leases);
        ArgumentNullException.ThrowIfNull(reservations);

        var namesByIdentity = reservations
            .Where(reservation => HasName(reservation.ConfiguredName))
            .Select(reservation => new
            {
                Mac = ClientIdentity.NormalizeHexMac(reservation.MacAddress),
                Ip = reservation.IpAddress?.Trim(),
                Name = reservation.ConfiguredName!
            })
            .Where(item => ClientIdentity.IsMacKey(item.Mac) && HasIp(item.Ip))
            .GroupBy(item => $"{item.Mac}|{item.Ip}", StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Name, StringComparer.OrdinalIgnoreCase);

        foreach (DhcpLeaseInfo lease in leases)
        {
            string mac = ClientIdentity.NormalizeHexMac(lease.MacAddress);
            string? ip = lease.IpAddress?.Trim();
            if (!ClientIdentity.IsMacKey(mac) || !HasIp(ip) ||
                !namesByIdentity.TryGetValue($"{mac}|{ip}", out string? configuredName)) continue;

            lease.ConfiguredName = configuredName;
            lease.ClientName = configuredName;
        }
    }

    private static bool HasName(string? value) => !string.IsNullOrWhiteSpace(value) && value != "—";
    private static bool HasIp(string? value) => !string.IsNullOrWhiteSpace(value) && value != "-" && value != "—" && !string.Equals(value, "N/A", StringComparison.OrdinalIgnoreCase);
}
