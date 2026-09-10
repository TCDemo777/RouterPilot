using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>Maps existing health observations to established Dashboard destinations without performing navigation or I/O.</summary>
public static class NetworkHealthNavigationTarget
{
    public const string Overview = "overview";
    public const string Router = "router";
    public const string Clients = "clients";
    public const string Protection = "protection";
    public const string Analytics = "analytics";
    public const string Network = "network";
    public const string NetworkHealth = "network-health";
    public const string Health = "health";
    public const string Wifi = "wifi";
    public const string Dhcp = "dhcp";
    public const string Vpn = "vpn";
    public const string MaintenanceFirmware = "maintenance-firmware";

    public static bool IsSupported(string? target) => target?.Trim().ToLowerInvariant() switch
    {
        Overview or Router or Clients or Protection or Analytics or Network or
        NetworkHealth or Health or Wifi or Dhcp or Vpn or MaintenanceFirmware => true,
        _ => false
    };

    /// <summary>
    /// A single stale source can open its owning surface. An aggregate or an
    /// unknown source opens the existing Network Health details surface rather
    /// than presenting a View action that only recreates Overview.
    /// </summary>
    public static string ForDataFreshness(IReadOnlyList<DataFreshnessInfo> staleSources)
    {
        string[] destinations = staleSources
            .Select(source => ForDataFreshnessSource(source.Source))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return destinations.Length == 1 ? destinations[0] : NetworkHealth;
    }

    public static string ForDataFreshnessSource(string? source) => source?.Trim() switch
    {
        "Clients" => Clients,
        "Internet / WAN" => Network,
        "Wi-Fi" => Wifi,
        "DHCP" => Dhcp,
        "AdGuard" => Protection,
        "VPN" => Vpn,
        "Network traffic" => Analytics,
        _ => NetworkHealth
    };
}
