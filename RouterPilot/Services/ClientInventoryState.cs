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

    public void Clear()
    {
        if (_clients.Count == 0) return;
        _clients.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Adds clients observed by an existing application-level router snapshot
    /// without replacing the richer Clients-page reconciliation when it exists.
    /// </summary>
    public void AddMissing(IEnumerable<ClientInfo> clients)
    {
        bool changed = false;
        foreach (ClientInfo client in clients)
        {
            string mac = ClientIdentity.NormalizeHexMac(client.MacAddress);
            if (mac.Length != 12 || _clients.ContainsKey(mac)) continue;
            _clients[mac] = client;
            changed = true;
        }

        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }
}
