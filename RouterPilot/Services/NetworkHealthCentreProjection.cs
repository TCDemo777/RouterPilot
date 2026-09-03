using System.Globalization;
using System.Text;
using RouterPilot.Models;
using RouterPilot.ViewModels;

namespace RouterPilot.Services;

public static class NetworkHealthCentreProjection
{
    public static IReadOnlyList<NetworkHealthObservation> Create(DashboardViewModel dashboard)
    {
        List<NetworkHealthObservation> items = [];
        string internetState = dashboard.RouterConnected ? (dashboard.InternetConnected ? "Healthy" : "Attention") : "Unavailable";
        items.Add(new("Internet", "Internet", internetState,
            dashboard.RouterConnected ? (dashboard.InternetConnected ? "The current Internet path is online." : "The current Internet path is unavailable.") : "Router connectivity is unavailable.",
            dashboard.InternetStatusText, "health"));

        string temperatureState = TemperatureState(dashboard.Temperature);
        items.Add(new("Router", "Router", temperatureState,
            temperatureState switch { "Healthy" => "Router resources are within RouterPilot guidance.", "Attention" => "Router temperature is elevated.", "Unavailable" => "Router resource telemetry is unavailable.", _ => "Router resource state is unknown." },
            $"Temperature: {dashboard.Temperature}; memory: {dashboard.MemoryUsage}", "router"));

        string wifiState = dashboard.RouterConnected ? (dashboard.WifiNetworks.Count == 0 ? "Unknown" : "Healthy") : "Unavailable";
        items.Add(new("Wi-Fi", "Wi-Fi", wifiState,
            wifiState == "Healthy" ? $"{dashboard.WifiNetworks.Count} wireless network(s) are loaded." : "Wireless telemetry is not currently available.",
            $"Wireless networks: {dashboard.WifiNetworks.Count}", "wifi"));

        string lanState = dashboard.RouterConnected ? "Healthy" : "Unavailable";
        items.Add(new("LAN", "LAN", lanState, lanState == "Healthy" ? "LAN and DHCP telemetry is available." : "LAN telemetry is unavailable.",
            $"DHCP leases: {(dashboard.DhcpLoaded ? dashboard.DhcpLeases.Count.ToString(CultureInfo.InvariantCulture) : "Unknown")}", "health"));

        string dnsState = dashboard.RouterConnected ? (dashboard.IsAdGuardAvailable ? "Healthy" : "Unavailable") : "Unknown";
        items.Add(new("DNS", "DNS", dnsState, dashboard.IsAdGuardAvailable ? "AdGuard Home telemetry is available." : "DNS activity visibility is currently unavailable.", dashboard.AdGuardStatusText, "protection"));

        string vpnState = dashboard.RouterConnected ? (dashboard.IsVpnConnected ? "Healthy" : "Unknown") : "Unavailable";
        items.Add(new("VPN", "VPN", vpnState, dashboard.IsVpnConnected ? "A VPN connection is active." : "No active VPN connection is currently observed.", dashboard.VpnNetworkSummary, "vpn"));

        string storageState = dashboard.RouterConnected ? "Healthy" : "Unavailable";
        items.Add(new("Storage", "Storage", storageState, "Storage telemetry is available through the RouterPilot router snapshot.", dashboard.StorageUsage, "router"));
        return items;
    }

    public static string TemperatureState(string? value)
    {
        if (!double.TryParse(value?.Replace("°", string.Empty).Replace("C", string.Empty, StringComparison.OrdinalIgnoreCase).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double celsius)) return "Unavailable";
        return celsius >= 80 ? "Attention" : celsius >= 65 ? "Attention" : "Healthy";
    }

    public static string BuildHomeNetworkReport(DashboardViewModel dashboard)
    {
        IReadOnlyList<NetworkHealthObservation> observations = Create(dashboard);
        StringBuilder report = new("RouterPilot Home Network Report\n\n");
        report.AppendLine($"Generated: {DateTime.Now:g}");
        report.AppendLine("This report summarizes currently loaded RouterPilot observations. It is not a complete router configuration backup.\n");
        report.AppendLine("ROUTER");
        report.AppendLine($"Model: {Safe(dashboard.RouterModel)}");
        report.AppendLine($"Firmware: {Safe(dashboard.FirmwareVersion)}");
        report.AppendLine($"Temperature: {Safe(dashboard.Temperature)} ({TemperatureState(dashboard.Temperature)})");
        report.AppendLine("\nDOMAINS");
        foreach (NetworkHealthObservation item in observations)
            report.AppendLine($"{item.Domain}: {item.State} — {item.Summary}");
        report.AppendLine("\nNETWORK");
        report.AppendLine($"Current clients: {dashboard.LanClients.Count(client => client.IsOnline)}");
        report.AppendLine($"Wi-Fi networks loaded: {dashboard.WifiNetworks.Count}");
        report.AppendLine($"DHCP reservations: {(dashboard.DhcpLoaded ? dashboard.DhcpReservations.Count.ToString(CultureInfo.InvariantCulture) : "Unavailable")}");
        report.AppendLine("\nSERVICES");
        RouterAdvancedSnapshot advanced = dashboard.AdvancedRouterSnapshot;
        report.AppendLine($"IoT network: {Bool(advanced.IoTEnabled)}");
        report.AppendLine($"SQM: {Bool(advanced.SqmEnabled)} ({Safe(advanced.SqmQueueDiscipline)})");
        report.AppendLine($"WebDAV: {Bool(advanced.WebDavEnabled)}");
        report.AppendLine($"DLNA runtime: {Bool(advanced.DlnaRunning)}");
        report.AppendLine($"ZeroTier: {(advanced.ZeroTierInstalled == true ? Bool(advanced.ZeroTierEnabled) : "Not observed")}");
        report.AppendLine("\nPrivacy: addresses, client identities, SSIDs, domains, endpoints, paths, credentials and raw router output are omitted.");
        return report.ToString();
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) || value == "-" ? "Unavailable" : value;
    private static string Bool(bool? value) => value switch { true => "Enabled", false => "Disabled", _ => "Unknown" };
}
