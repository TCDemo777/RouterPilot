using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>Owns the session-level, authoritative client reconciliation independently of any page.</summary>
public sealed class ClientInventoryCoordinator
{
    private readonly IRouterManagerProvider _provider;
    private readonly ClientInventoryState _inventory;
    private readonly ClientProfileService _profiles;
    private readonly IClientPresenceHistoryService _presence;
    private readonly Func<CancellationToken, Task<IReadOnlyList<ClientInfo>>>? _testReconciliation;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _loaded;

    public ClientInventoryCoordinator(IRouterManagerProvider provider, ClientInventoryState inventory,
        ClientProfileService profiles, IClientPresenceHistoryService presence)
    {
        _provider = provider;
        _inventory = inventory;
        _profiles = profiles;
        _presence = presence;
    }

    internal ClientInventoryCoordinator(ClientInventoryState inventory,
        Func<CancellationToken, Task<IReadOnlyList<ClientInfo>>> testReconciliation)
    {
        _provider = null!;
        _inventory = inventory;
        _profiles = null!;
        _presence = null!;
        _testReconciliation = testReconciliation;
    }

    public bool IsAuthoritativelyLoaded => _loaded;

    public async Task<bool> EnsureAuthoritativeInventoryAsync(CancellationToken token = default)
    {
        if (_loaded) return true;
        await _gate.WaitAsync(token);
        try
        {
            if (_loaded) return true;

            if (_testReconciliation is not null)
            {
                _inventory.Update(await _testReconciliation(token));
                _loaded = true;
                return true;
            }

            RouterManager router = await _provider.GetRouterManagerAsync(token);
            Task<List<ClientInfo>> adGuardTask = router.GetAdGuardClientsAsync();
            Task<List<WifiRadioInfo>> radiosTask = router.GetWifiRadiosAsync();
            Task<List<WifiClientInfo>> inventoryTask = router.GetGlClientInventoryAsync();
            await Task.WhenAll(adGuardTask, radiosTask, inventoryTask);

            List<WifiClientInfo> live = radiosTask.Result.SelectMany(radio => radio.Clients.Select(client =>
            {
                client.Ssid = WifiClientInfo.Useful(client.Ssid) ? client.Ssid : radio.Ssid;
                client.Band = WifiClientInfo.Useful(client.Band) ? client.Band : radio.Band;
                client.Interface = WifiClientInfo.Useful(client.Interface) ? client.Interface : radio.Interface;
                return client;
            })).ToList();
            foreach (WifiClientInfo client in inventoryTask.Result)
            {
                string key = ClientIdentity.NormalizeMac(client.MacAddress);
                if (!live.Any(existing => ClientIdentity.NormalizeMac(existing.MacAddress) == key)) live.Add(client);
            }

            Dictionary<string, ClientProfile> profiles = _profiles.Load();
            List<ClientInfo> clients = live.Where(client => ClientIdentity.IsMacKey(client.MacAddress))
                .GroupBy(client => ClientIdentity.NormalizeMac(client.MacAddress), StringComparer.OrdinalIgnoreCase)
                .Select(group => ToClient(group.First(), adGuardTask.Result, profiles))
                .ToList();
            _inventory.Update(clients);
            _presence.Observe(clients);
            _loaded = true;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void MarkAuthoritativelyLoaded() => _loaded = true;

    private static ClientInfo ToClient(WifiClientInfo source, IReadOnlyList<ClientInfo> adGuard,
        IReadOnlyDictionary<string, ClientProfile> profiles)
    {
        string key = ClientIdentity.NormalizeMac(source.MacAddress);
        ClientInfo? dns = adGuard.FirstOrDefault(client => ClientIdentity.NormalizeMac(client.MacAddress) == key) ??
            adGuard.FirstOrDefault(client => WifiClientInfo.Useful(source.IpAddress) && source.IpAddress == client.IpAddress);
        profiles.TryGetValue(key, out ClientProfile? profile);
        return new ClientInfo
        {
            Name = !string.IsNullOrWhiteSpace(profile?.Nickname) ? profile.Nickname : source.Name,
            RouterName = source.Name,
            MacAddress = source.MacAddress,
            IpAddress = source.IpAddress,
            WifiNetwork = source.Ssid,
            ConnectionType = source.Band,
            SignalStrength = source.Signal,
            LiveInterface = source.Interface,
            TotalQueries = dns?.TotalQueries ?? 0,
            BlockedQueries = dns?.BlockedQueries ?? 0,
            LastSeen = dns?.LastSeen ?? "-",
            QueryLogAvailable = dns?.QueryLogAvailable ?? false,
            AdGuardDataAvailability = dns is null ? AdGuardAvailabilityState.Unavailable : AdGuardAvailabilityState.Available,
            Notes = profile?.Notes ?? string.Empty,
            CustomCategory = profile?.Category ?? string.Empty,
            IsFavorite = profile?.IsFavorite ?? false,
            MonitorAvailability = profile?.MonitorAvailability ?? false,
            NeedsReview = profile?.NeedsReview ?? false,
            FirstSeenUtc = profile?.FirstSeenUtc ?? default,
            LastObservedUtc = profile?.LastSeenUtc ?? default
        };
    }
}
