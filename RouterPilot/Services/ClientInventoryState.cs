using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>Current client snapshot produced by the existing Clients refresh path.</summary>
public sealed class ClientInventoryState
{
    private readonly Dictionary<string, ClientInfo> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _presence = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? Changed;

    public IReadOnlyDictionary<string, ClientInfo> Snapshot => _clients;
    /// <summary>Typed view derived from the same accepted clients as <see cref="Snapshot"/>.</summary>
    public DeviceInventorySnapshot DeviceSnapshot { get; private set; } = DeviceInventorySnapshot.Empty;
    /// <summary>
    /// Explicit current presence from a successfully-read aggregate client inventory.
    /// Absence from this map is deliberately Unknown rather than Offline.
    /// </summary>
    public IReadOnlyDictionary<string, bool> PresenceSnapshot => _presence;

    /// <summary>
    /// Replaces accepted client records without discarding an already accepted
    /// router-context stamp. An un-stamped first publication remains un-stamped.
    /// </summary>
    public void Update(IEnumerable<ClientInfo> clients) =>
        Publish(clients, DeviceSnapshot.RouterProfileId, DeviceSnapshot.ContextVersion);

    /// <summary>Publishes one accepted client reconciliation for a verified router context.</summary>
    public void Update(IEnumerable<ClientInfo> clients, string? routerProfileId, long contextVersion) =>
        Publish(clients, routerProfileId, contextVersion);

    private void Publish(IEnumerable<ClientInfo> clients, string? routerProfileId, long contextVersion)
    {
        _clients.Clear();
        foreach (ClientInfo client in clients)
        {
            if (DeviceIdentity.TryCreate(client.MacAddress, out DeviceIdentity identity))
                _clients[identity.CanonicalMac] = client;
        }
        RebuildDeviceSnapshot(routerProfileId, contextVersion);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        if (_clients.Count == 0 && _presence.Count == 0 && DeviceSnapshot.IsEmpty) return;
        _clients.Clear();
        _presence.Clear();
        DeviceSnapshot = DeviceInventorySnapshot.Empty;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Replaces only presence values supplied by an authoritative aggregate router response.</summary>
    public void UpdateAuthoritativePresence(IReadOnlyDictionary<string, bool> presence)
    {
        _presence.Clear();
        foreach ((string identity, bool online) in presence)
        {
            string mac = ClientIdentity.NormalizeHexMac(identity);
            if (mac.Length == 12) _presence[mac] = online;
        }
        RebuildDeviceSnapshot(DeviceSnapshot.RouterProfileId, DeviceSnapshot.ContextVersion);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Adds clients observed by an existing application-level router snapshot
    /// without replacing the richer Clients-page reconciliation when it exists.
    /// </summary>
    public void AddMissing(IEnumerable<ClientInfo> clients) =>
        AddMissing(clients, DeviceSnapshot.RouterProfileId, DeviceSnapshot.ContextVersion);

    /// <summary>
    /// Adds observations from an already context-validated router snapshot.
    /// </summary>
    public void AddMissing(IEnumerable<ClientInfo> clients, string? routerProfileId, long contextVersion)
    {
        bool changed = false;
        foreach (ClientInfo client in clients)
        {
            if (!DeviceIdentity.TryCreate(client.MacAddress, out DeviceIdentity identity) || _clients.ContainsKey(identity.CanonicalMac)) continue;
            _clients[identity.CanonicalMac] = client;
            changed = true;
        }

        bool contextChanged = !string.Equals(DeviceSnapshot.RouterProfileId, routerProfileId, StringComparison.Ordinal) ||
            DeviceSnapshot.ContextVersion != contextVersion;
        if (!changed && !contextChanged) return;
        RebuildDeviceSnapshot(routerProfileId, contextVersion);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void RebuildDeviceSnapshot(string? routerProfileId, long contextVersion)
    {
        DeviceSnapshot = DeviceInventorySnapshot.Create(
            _clients.Select(pair => new DeviceObservation(
                DeviceIdentity.TryCreate(pair.Key, out DeviceIdentity identity) ? identity : default,
                pair.Value,
                _presence.TryGetValue(pair.Key, out bool online) ? online : null)),
            routerProfileId,
            contextVersion);
    }
}
