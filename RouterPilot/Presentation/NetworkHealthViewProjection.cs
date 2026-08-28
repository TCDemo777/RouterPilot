using RouterPilot.Models;

namespace RouterPilot.Presentation;

/// <summary>Pure presentation projection. It deliberately owns no I/O, timers or scheduling.</summary>
public static class NetworkHealthViewProjection
{
    public static NetworkHealthViewSnapshot Create(NetworkHealthViewInput input)
    {
        if (input.RouterFreshness == DataFreshnessState.Loading)
            return new("Initializing", RouterPilotStatus.Pending, "Waiting for the existing router refresh.", []);

        var checks = new List<NetworkHealthViewCheck>
        {
            Router(input), Wan(input), AdGuard(input), Vpn(input), Wifi(input), Dhcp(input), Resources(input), Firmware(input), DataStatistics(input)
        };

        bool routerUnavailable = checks[0].Status is "Unavailable" or "Stale";
        bool attention = checks.Any(check => check.AffectsOverall && check.Severity is RouterPilotStatus.Error or RouterPilotStatus.Pending or RouterPilotStatus.Disabled or RouterPilotStatus.NotAvailable);
        return routerUnavailable
            ? new("Unavailable", RouterPilotStatus.Error, "Router status is unavailable or stale.", checks)
            : attention
                ? new("Attention needed", RouterPilotStatus.Pending, "One or more reported checks need attention.", checks)
                : new("Healthy", RouterPilotStatus.Active, "All currently reported required checks are healthy.", checks);
    }

    private static NetworkHealthViewCheck Router(NetworkHealthViewInput x) => x.RouterFreshness switch
    {
        DataFreshnessState.Stale => Check("Router", "Stale", "Last successful refresh: " + x.RouterLastSuccess, RouterPilotStatus.Pending, "overview"),
        DataFreshnessState.Unavailable => Check("Router", "Unavailable", "Router session is unavailable.", RouterPilotStatus.Error, "overview"),
        _ when x.RouterConnected => Check("Router", "Connected", "Last successful refresh: " + x.RouterLastSuccess, RouterPilotStatus.Connected, "overview"),
        _ => Check("Router", "Unavailable", "Router session is unavailable.", RouterPilotStatus.Error, "overview")
    };

    private static NetworkHealthViewCheck Wan(NetworkHealthViewInput x) => x.InternetFreshness switch
    {
        DataFreshnessState.Loading => Check("Internet / WAN", "Loading", "Waiting for WAN data.", RouterPilotStatus.Pending, "network"),
        DataFreshnessState.Stale => Check("Internet / WAN", "Stale", Detail(x.WanIp, x.Gateway, x.ExternalDns), RouterPilotStatus.Pending, "network"),
        DataFreshnessState.Unavailable => Check("Internet / WAN", "Unavailable", "WAN data is unavailable.", RouterPilotStatus.Error, "network"),
        _ when x.InternetConnected => Check("Internet / WAN", "Connected", Detail(x.WanIp, x.Gateway, x.ExternalDns), RouterPilotStatus.Connected, "network"),
        _ => Check("Internet / WAN", "Disconnected", Detail(x.WanIp, x.Gateway, x.ExternalDns), RouterPilotStatus.Error, "network")
    };

    private static NetworkHealthViewCheck AdGuard(NetworkHealthViewInput x)
    {
        if (x.AdGuardFreshness == DataFreshnessState.Loading) return Check("DNS / AdGuard", "Loading", "Waiting for AdGuard status.", RouterPilotStatus.Pending, "protection");
        if (x.AdGuardFreshness == DataFreshnessState.Stale) return Check("DNS / AdGuard", "Stale", "AdGuard status has not refreshed.", RouterPilotStatus.Pending, "protection");
        if (x.AdGuardAvailability != AdGuardAvailabilityState.Available) return Check("DNS / AdGuard", "Unavailable", "AdGuard Home is unavailable.", RouterPilotStatus.Error, "protection");
        if (!x.AdGuardProtectionKnown) return Check("DNS / AdGuard", "Protection state unavailable", "AdGuard Home is running; protection state is not yet available.", RouterPilotStatus.Pending, "protection");
        string state = x.AdGuardPaused ? "Paused" : x.AdGuardProtected ? "Protected" : "Disabled";
        RouterPilotStatus severity = x.AdGuardPaused ? RouterPilotStatus.Pending : x.AdGuardProtected ? RouterPilotStatus.Active : RouterPilotStatus.Disabled;
        return Check("DNS / AdGuard", state, "AdGuard: Running · Filtering: " + state, severity, "protection");
    }

    private static NetworkHealthViewCheck Vpn(NetworkHealthViewInput x)
    {
        if (x.VpnFreshness == DataFreshnessState.Stale) return Check("VPN", "Stale", "VPN status has not refreshed.", RouterPilotStatus.Pending, "vpn");
        if (!x.VpnAvailable) return Check("VPN", "Unavailable", "VPN client backend is unavailable.", RouterPilotStatus.NotAvailable, "vpn", false);
        if (!x.VpnConfigured) return Check("VPN", "Not configured", "No VPN tunnel is configured.", RouterPilotStatus.NotAvailable, "vpn", false);
        if (IsExplicitVpnFailure(x.VpnState))
            return Check("VPN", "Error", string.IsNullOrWhiteSpace(x.VpnDetail) ? x.VpnState : x.VpnDetail, RouterPilotStatus.Error, "vpn");
        bool disconnected = x.VpnState == "Disconnected";
        return Check("VPN", x.VpnState, string.IsNullOrWhiteSpace(x.VpnDetail) ? "VPN tunnel status." : x.VpnDetail,
            x.VpnState == "Connected" ? RouterPilotStatus.Connected : x.VpnState == "Connecting" ? RouterPilotStatus.Pending : disconnected ? RouterPilotStatus.Disabled : RouterPilotStatus.NotAvailable, "vpn", false);
    }

    private static bool IsExplicitVpnFailure(string state) =>
        state.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
        state.Contains("error", StringComparison.OrdinalIgnoreCase) ||
        state.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
        state.Contains("needs attention", StringComparison.OrdinalIgnoreCase) ||
        state.Contains("connection did not complete", StringComparison.OrdinalIgnoreCase);

    private static NetworkHealthViewCheck Wifi(NetworkHealthViewInput x) => x.WifiFreshness switch
    {
        DataFreshnessState.Loading => Check("Wi-Fi", "Loading", "Waiting for Wi-Fi radio data.", RouterPilotStatus.Pending, "wifi"),
        DataFreshnessState.Stale => Check("Wi-Fi", "Stale", "Wi-Fi status has not refreshed.", RouterPilotStatus.Pending, "wifi"),
        DataFreshnessState.Unavailable => Check("Wi-Fi", "Unavailable", "Wi-Fi data is unavailable.", RouterPilotStatus.NotAvailable, "wifi"),
        _ when x.WifiRadios == 0 => Check("Wi-Fi", "Unavailable", "No Wi-Fi radios were reported.", RouterPilotStatus.NotAvailable, "wifi", false),
        _ when x.WifiDisabledRadios == x.WifiRadios => Check("Wi-Fi", "Disabled", $"Radios: {x.WifiRadios} disabled; connected clients: {x.WifiClients}", RouterPilotStatus.Disabled, "wifi"),
        _ when x.WifiDisabledRadios > 0 || x.WifiUnknownRadios > 0 => Check("Wi-Fi", "Partial", $"Radios: {x.WifiActiveRadios} active, {x.WifiDisabledRadios} disabled, {x.WifiUnknownRadios} unavailable; connected clients: {x.WifiClients}", RouterPilotStatus.Pending, "wifi"),
        _ => Check("Wi-Fi", "Available", $"Radios: {x.WifiRadios} · Connected clients: {x.WifiClients}", RouterPilotStatus.Active, "wifi")
    };

    private static NetworkHealthViewCheck Dhcp(NetworkHealthViewInput x) => x.DhcpFreshness switch
    {
        DataFreshnessState.Loading => Check("DHCP", "Loading", "Waiting for DHCP lease data.", RouterPilotStatus.Pending, "dhcp"),
        DataFreshnessState.Stale => Check("DHCP", "Stale", "DHCP lease data has not refreshed.", RouterPilotStatus.Pending, "dhcp"),
        DataFreshnessState.Unavailable => Check("DHCP", "Unavailable", "DHCP data is unavailable.", RouterPilotStatus.NotAvailable, "dhcp"),
        _ when !x.DhcpLoaded => Check("DHCP", "Loading", "Waiting for DHCP lease data.", RouterPilotStatus.Pending, "dhcp"),
        _ => Check("DHCP", "Available", $"Leases: {x.DhcpLeases} · Reservations: {x.DhcpReservations}", RouterPilotStatus.Active, "dhcp")
    };

    private static NetworkHealthViewCheck Resources(NetworkHealthViewInput x)
    {
        string[] values = [x.Cpu, x.Temperature, x.Memory, x.Storage, x.Uptime, x.Load];
        int available = values.Count(IsKnown);
        if (x.RouterFreshness == DataFreshnessState.Loading)
            return Check("Router resources", "Loading", "Waiting for router resource information.", RouterPilotStatus.Pending, "analytics");
        if (x.RouterFreshness == DataFreshnessState.Stale)
            return Check("Router resources", "Stale", "Router resource information has not refreshed.", RouterPilotStatus.Pending, "analytics");
        if (available == 0)
            return Check("Router resources", "Unavailable", "Router resource information is unavailable.", RouterPilotStatus.NotAvailable, "analytics");
        return available == values.Length
            ? Check("Router resources", "Available", Detail(values), RouterPilotStatus.Active, "analytics", false)
            : Check("Router resources", "Partial", Detail(values) + $"; {values.Length - available} value(s) unavailable", RouterPilotStatus.Pending, "analytics");
    }
    private static NetworkHealthViewCheck Firmware(NetworkHealthViewInput x) => x.FirmwareStatus switch
    {
        FirmwareUpdateCheckStatus.UpdateAvailable => Check("Firmware", "Update available", Known(x.FirmwareVersion), RouterPilotStatus.Pending, "overview"),
        FirmwareUpdateCheckStatus.UpToDate => Check("Firmware", "Up to date", Known(x.FirmwareVersion), RouterPilotStatus.Active, "overview"),
        FirmwareUpdateCheckStatus.Pending => Check("Firmware", "Checking", Known(x.FirmwareVersion), RouterPilotStatus.Pending, "overview", false),
        _ => Check("Firmware", "Unavailable", Known(x.FirmwareVersion), RouterPilotStatus.NotAvailable, "overview", false)
    };
    private static NetworkHealthViewCheck DataStatistics(NetworkHealthViewInput x) => !x.DataStatisticsLoaded
        ? Check("Data Statistics", "Not loaded", "Open Analytics to load its existing Data Statistics state.", RouterPilotStatus.NotAvailable, "analytics", false)
        : x.DataStatisticsStatus switch
        {
            RouterPilotStatus.Active => Check("Data Statistics", "Available", x.DataStatisticsDetail, RouterPilotStatus.Active, "analytics", false),
            RouterPilotStatus.Disabled => Check("Data Statistics", "Disabled", x.DataStatisticsDetail, RouterPilotStatus.Disabled, "analytics", false),
            RouterPilotStatus.Pending => Check("Data Statistics", "Unavailable", x.DataStatisticsDetail, RouterPilotStatus.NotAvailable, "analytics", false),
            _ => Check("Data Statistics", "Unavailable", x.DataStatisticsDetail, RouterPilotStatus.NotAvailable, "analytics", false)
        };

    private static NetworkHealthViewCheck Check(string title, string status, string detail, RouterPilotStatus severity, string target, bool affectsOverall = true) => new(title, status, detail, severity, target, affectsOverall);
    private static string Known(string value) => string.IsNullOrWhiteSpace(value) || value == "-" ? "Current firmware version is unavailable." : "Current version: " + value;
    private static bool IsKnown(string value) => !string.IsNullOrWhiteSpace(value) && value != "-" && value != RouterPilotStatusPresentation.NotAvailable;
    private static string Detail(params string[] parts) => string.Join(" · ", parts.Where(value => !string.IsNullOrWhiteSpace(value) && value != "-").ToArray());
}

public sealed record NetworkHealthViewCheck(string Title, string Status, string Detail, RouterPilotStatus Severity, string NavigationTarget, bool AffectsOverall);
public sealed record NetworkHealthViewSnapshot(string OverallStatus, RouterPilotStatus OverallSeverity, string OverallDetail, IReadOnlyList<NetworkHealthViewCheck> Checks);
public sealed record NetworkHealthViewInput(DataFreshnessState RouterFreshness, DataFreshnessState InternetFreshness, DataFreshnessState AdGuardFreshness, DataFreshnessState VpnFreshness, DataFreshnessState WifiFreshness, DataFreshnessState DhcpFreshness, bool RouterConnected, bool InternetConnected, string RouterLastSuccess, string WanIp, string Gateway, string ExternalDns, AdGuardAvailabilityState AdGuardAvailability, bool AdGuardProtectionKnown, bool AdGuardProtected, bool AdGuardPaused, bool VpnAvailable, bool VpnConfigured, string VpnState, string VpnDetail, int WifiRadios, int WifiActiveRadios, int WifiDisabledRadios, int WifiUnknownRadios, int WifiClients, bool DhcpLoaded, int DhcpLeases, int DhcpReservations, string Cpu, string Temperature, string Memory, string Storage, string Uptime, string Load, string FirmwareVersion, FirmwareUpdateCheckStatus FirmwareStatus, bool DataStatisticsLoaded, RouterPilotStatus DataStatisticsStatus, string DataStatisticsDetail);
