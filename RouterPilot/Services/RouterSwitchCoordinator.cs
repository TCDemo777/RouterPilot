using RouterPilot.Models;
using RouterPilot.ViewModels;

namespace RouterPilot.Services;

public interface IRouterSwitchCoordinator
{
    bool IsSwitching { get; }
    event EventHandler<RouterProfile>? Switched;
    Task<bool> SwitchAsync(string profileId, CancellationToken cancellationToken = default);
    Task ReconnectActiveAsync(CancellationToken cancellationToken = default);
}

/// <summary>Serializes the single active-router lifecycle; inactive profiles never connect.</summary>
public sealed class RouterSwitchCoordinator : IRouterSwitchCoordinator
{
    private readonly IRouterProfileService _profiles;
    private readonly IRouterManagerProvider _router;
    private readonly ClientInventoryState _inventory;
    private readonly ClientInventoryCoordinator _inventoryCoordinator;
    private readonly INetworkHealthService _health;
    private readonly IVpnLiveStatusService _vpn;
    private readonly AdGuardAvailabilityService _adGuard;
    private readonly DataStatisticsViewModel _statistics;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public bool IsSwitching { get; private set; }
    public event EventHandler<RouterProfile>? Switched;

    public RouterSwitchCoordinator(IRouterProfileService profiles, IRouterManagerProvider router,
        ClientInventoryState inventory, ClientInventoryCoordinator inventoryCoordinator,
        INetworkHealthService health, IVpnLiveStatusService vpn, AdGuardAvailabilityService adGuard,
        DataStatisticsViewModel statistics)
    { _profiles = profiles; _router = router; _inventory = inventory; _inventoryCoordinator = inventoryCoordinator; _health = health; _vpn = vpn; _adGuard = adGuard; _statistics = statistics; }

    public Task ReconnectActiveAsync(CancellationToken cancellationToken = default) => SwitchCoreAsync(null, cancellationToken);
    public Task<bool> SwitchAsync(string profileId, CancellationToken cancellationToken = default) => SwitchCoreAsync(profileId, cancellationToken);

    private async Task<bool> SwitchCoreAsync(string? profileId, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (profileId is not null && !_profiles.GetProfiles().Any(p => p.Id == profileId)) return false;
            IsSwitching = true;
            _inventory.Clear(); _inventoryCoordinator.ResetForRouterSession();
            _statistics.ResetForRouterSession(); _vpn.Clear(); _adGuard.SetState(AdGuardAvailabilityState.Unavailable);
            if (_health is NetworkHealthService concreteHealth) concreteHealth.ResetForRouterSession();
            await _router.ResetAsync(token).ConfigureAwait(false);
            if (profileId is not null && !_profiles.SetActiveProfile(profileId)) return false;
            RouterProfile active = _profiles.GetActiveProfile()!;
            Switched?.Invoke(this, active);
            return true;
        }
        finally { IsSwitching = false; _gate.Release(); }
    }
}
