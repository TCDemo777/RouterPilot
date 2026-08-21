using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>Current client snapshot produced by the existing Clients refresh path.</summary>
public sealed class ClientInventoryState
{
    private readonly Dictionary<string, ClientInfo> _clients = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? Changed;

    public IReadOnlyDictionary<string, ClientInfo> Snapshot => _clients;

    public void Update(IEnumerable<ClientInfo> clients)
    {
        _clients.Clear();
        foreach (ClientInfo client in clients)
        {
            string mac = ClientIdentity.NormalizeHexMac(client.MacAddress);
            if (mac.Length == 12) _clients[mac] = client;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
