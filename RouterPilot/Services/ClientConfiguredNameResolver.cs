using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>Builds safe, bulk-read name maps for client-card presentation.</summary>
public static class ClientConfiguredNameResolver
{
    public static IReadOnlyDictionary<string, string> RouterByMac(IEnumerable<DhcpReservationInfo> reservations) =>
        Unique(reservations.Where(item => HasName(item.ConfiguredName))
            .Select(item => (ClientIdentity.NormalizeHexMac(item.MacAddress), item.ConfiguredName!))
            .Where(item => ClientIdentity.IsMacKey(item.Item1)));

    public static (IReadOnlyDictionary<string, string> ByMac, IReadOnlyDictionary<string, string> ByIp) AdGuardByIdentity(IEnumerable<ClientInfo> clients) =>
        (Unique(clients.Where(item => item.IsConfiguredAdGuardClient && HasName(Name(item)))
            .Select(item => (ClientIdentity.NormalizeHexMac(item.MacAddress), Name(item)))
            .Where(item => ClientIdentity.IsMacKey(item.Item1))),
         Unique(clients.Where(item => item.IsConfiguredAdGuardClient && HasName(Name(item)) && System.Net.IPAddress.TryParse(item.IpAddress, out _))
            .Select(item => (ClientIdentity.NormalizeEndpoint(item.IpAddress), Name(item)))
            .Where(item => item.Item1.Length > 0)));

    private static IReadOnlyDictionary<string, string> Unique(IEnumerable<(string Key, string Name)> entries) =>
        entries.GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Name, StringComparer.OrdinalIgnoreCase);

    private static bool HasName(string? value) => !string.IsNullOrWhiteSpace(value) && value != "-" && value != "—";
    private static string Name(ClientInfo item) => HasName(item.AdGuardConfiguredName) ? item.AdGuardConfiguredName : item.Name;
}

public static class ClientNamePresentation
{
    public static string Resolve(ClientNameSource source, string automaticName, string? routerName, string? adGuardName) => source switch
    {
        ClientNameSource.Router when HasName(routerName) => routerName!,
        ClientNameSource.AdGuard when HasName(adGuardName) => adGuardName!,
        ClientNameSource.ConfiguredNames when HasName(routerName) => routerName!,
        ClientNameSource.ConfiguredNames when HasName(adGuardName) => adGuardName!,
        _ => automaticName
    };

    private static bool HasName(string? value) => !string.IsNullOrWhiteSpace(value) && value != "-" && value != "—";
}
