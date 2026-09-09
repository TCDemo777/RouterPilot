using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>One read-only, application-wide policy over the normal bulk name snapshots.</summary>
public interface IClientDisplayNameService
{
    event EventHandler? Changed;
    ClientNameSource Source { get; }
    bool HasRouterConfiguredNames { get; }
    bool HasAdGuardConfiguredNames { get; }
    void SetSource(ClientNameSource source);
    void UpdateRouterReservations(IEnumerable<DhcpReservationInfo> reservations);
    void UpdateAdGuardClients(IEnumerable<ClientInfo> clients);
    string Resolve(ClientInfo client);
    string ResolveNameSource(ClientInfo client);
    string Resolve(string? macAddress, string? ipAddress, string? automaticName);
}

public sealed class ClientDisplayNameService : IClientDisplayNameService
{
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private IReadOnlyDictionary<string, string> _routerByMac = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, string> _adGuardByMac = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, string> _adGuardByIp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public ClientDisplayNameService(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _settings = settingsService.Load();
    }

    public event EventHandler? Changed;
    public ClientNameSource Source => _settings.ClientNameSource;
    public bool HasRouterConfiguredNames => _routerByMac.Count > 0;
    public bool HasAdGuardConfiguredNames => _adGuardByMac.Count > 0 || _adGuardByIp.Count > 0;

    public void SetSource(ClientNameSource source)
    {
        if (_settings.ClientNameSource == source) return;
        _settings.ClientNameSource = source;
        _settingsService.Save(_settings);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateRouterReservations(IEnumerable<DhcpReservationInfo> reservations)
    {
        _routerByMac = ClientConfiguredNameResolver.RouterByMac(reservations);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateAdGuardClients(IEnumerable<ClientInfo> clients)
    {
        (_adGuardByMac, _adGuardByIp) = ClientConfiguredNameResolver.AdGuardByIdentity(clients);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public string Resolve(ClientInfo client)
    {
        string automatic = Useful(client.AutomaticName) ? client.AutomaticName : client.Name;
        client.RouterConfiguredName = Router(client.MacAddress);
        client.AdGuardConfiguredName = AdGuard(client.MacAddress, client.IpAddress);
        return ClientNamePresentation.Resolve(Source, automatic, client.RouterConfiguredName, client.AdGuardConfiguredName);
    }

    public string ResolveNameSource(ClientInfo client)
    {
        string router = Router(client.MacAddress);
        string adGuard = AdGuard(client.MacAddress, client.IpAddress);
        return ClientNamePresentation.ResolveSource(Source, router, adGuard);
    }

    public string Resolve(string? macAddress, string? ipAddress, string? automaticName) =>
        ClientNamePresentation.Resolve(Source, automaticName ?? "-", Router(macAddress), AdGuard(macAddress, ipAddress));

    private string Router(string? mac) => _routerByMac.TryGetValue(ClientIdentity.NormalizeHexMac(mac), out string? name) ? name : string.Empty;
    private string AdGuard(string? mac, string? ip)
    {
        string macKey = ClientIdentity.NormalizeHexMac(mac);
        if (ClientIdentity.IsMacKey(macKey) && _adGuardByMac.TryGetValue(macKey, out string? macName)) return macName;
        return _adGuardByIp.TryGetValue(ClientIdentity.NormalizeEndpoint(ip), out string? ipName) ? ipName : string.Empty;
    }

    private static bool Useful(string? value) => !string.IsNullOrWhiteSpace(value) && value != "-" && value != "—";
}

/// <summary>Used only by isolated presentation harnesses that do not build the app container.</summary>
public sealed class PassthroughClientDisplayNameService : IClientDisplayNameService
{
    public event EventHandler? Changed { add { } remove { } }
    public ClientNameSource Source => ClientNameSource.Automatic;
    public bool HasRouterConfiguredNames => false;
    public bool HasAdGuardConfiguredNames => false;
    public void SetSource(ClientNameSource source) { }
    public void UpdateRouterReservations(IEnumerable<DhcpReservationInfo> reservations) { }
    public void UpdateAdGuardClients(IEnumerable<ClientInfo> clients) { }
    public string Resolve(ClientInfo client) => client.Name;
    public string ResolveNameSource(ClientInfo client) => "RouterPilot";
    public string Resolve(string? macAddress, string? ipAddress, string? automaticName) => automaticName ?? "-";
}
