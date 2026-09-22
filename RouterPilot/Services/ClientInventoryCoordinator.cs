using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>Owns the session-level, authoritative client reconciliation independently of any page.</summary>
public sealed class ClientInventoryCoordinator
{
    private readonly IRouterManagerProvider _provider;
    private readonly ClientInventoryState _inventory;
    private readonly ClientProfileService _profiles;
    private readonly IClientPresenceHistoryService _presence;
    private readonly IActiveRouterContext? _activeRouter;
    private readonly Func<CancellationToken, Task<IReadOnlyList<ClientInfo>>>? _testReconciliation;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _loaded;

    public ClientInventoryCoordinator(IRouterManagerProvider provider, ClientInventoryState inventory,
        ClientProfileService profiles, IClientPresenceHistoryService presence, IActiveRouterContext activeRouter)
    {
        _provider = provider;
        _inventory = inventory;
        _profiles = profiles;
        _presence = presence;
        _activeRouter = activeRouter;
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

    /// <summary>
    /// Deterministic-harness seam for exercising router-context publication
    /// without changing the production reconciliation or transport path.
    /// </summary>
    internal ClientInventoryCoordinator(ClientInventoryState inventory, IActiveRouterContext activeRouter,
        Func<CancellationToken, Task<IReadOnlyList<ClientInfo>>> testReconciliation)
        : this(inventory, testReconciliation)
    {
        _activeRouter = activeRouter;
    }

    public bool IsAuthoritativelyLoaded => _loaded;

    public async Task<bool> EnsureAuthoritativeInventoryAsync(CancellationToken token = default)
    {
        return await LoadAuthoritativeInventoryAsync(forceRefresh: false, token).ConfigureAwait(false);
    }

    /// <summary>Refreshes the existing shared LAN client inventory for a read-only consumer.</summary>
    public async Task<bool> RefreshAuthoritativeInventoryAsync(CancellationToken token = default)
    {
        return await LoadAuthoritativeInventoryAsync(forceRefresh: true, token).ConfigureAwait(false);
    }

    private async Task<bool> LoadAuthoritativeInventoryAsync(bool forceRefresh, CancellationToken token)
    {
        if (!forceRefresh && _loaded) return true;
        await _gate.WaitAsync(token);
        try
        {
            if (!forceRefresh && _loaded) return true;

            string? capturedProfileId = _activeRouter?.CurrentProfileId;
            long capturedContextVersion = _activeRouter?.Version ?? 0;

            if (_testReconciliation is not null)
            {
                IReadOnlyList<ClientInfo> reconciled = await _testReconciliation(token);
                if (!CanPublish(capturedProfileId, capturedContextVersion)) return false;
                _inventory.Update(reconciled);
                _loaded = true;
                return true;
            }

            RouterManager router = await _provider.GetRouterManagerAsync(token);
            // AdGuard is enrichment only.  A failure here must not prevent the
            // router's authoritative LAN inventory from reaching read-only
            // client surfaces such as Known Devices.
            Task<List<ClientInfo>> adGuardTask = CaptureAdGuardAsync(router, token);
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
            if (!CanPublish(capturedProfileId, capturedContextVersion)) return false;
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
    public void ResetForRouterSession() => _loaded = false;

    private bool CanPublish(string? capturedProfileId, long capturedContextVersion) =>
        _activeRouter is null ||
        (_activeRouter.Version == capturedContextVersion &&
         string.Equals(_activeRouter.CurrentProfileId, capturedProfileId, StringComparison.Ordinal));

    private static async Task<List<ClientInfo>> CaptureAdGuardAsync(RouterManager router, CancellationToken token)
    {
        try
        {
            return await router.GetAdGuardClientsAsync();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    private static ClientInfo ToClient(WifiClientInfo source, IReadOnlyList<ClientInfo> adGuard,
        IReadOnlyDictionary<string, ClientProfile> profiles)
    {
        string key = ClientIdentity.NormalizeMac(source.MacAddress);
        ClientInfo? dns = adGuard.FirstOrDefault(client => ClientIdentity.NormalizeMac(client.MacAddress) == key) ??
            adGuard.FirstOrDefault(client => WifiClientInfo.Useful(source.IpAddress) &&
                ClientIdentity.EndpointEquals(source.IpAddress, client.IpAddress));
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
            IsConfiguredAdGuardClient = dns?.IsConfiguredAdGuardClient == true,
            AdGuardConfiguredName = dns?.IsConfiguredAdGuardClient == true ? dns.Name : string.Empty,
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
