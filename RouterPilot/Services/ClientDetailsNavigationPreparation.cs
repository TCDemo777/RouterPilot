using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>
/// Prepares the existing Client Details navigation target from a durable MAC identity.
/// It returns only a current inventory record or saved profile, never a partial client
/// constructed from a caller's presentation data.
/// </summary>
public static class ClientDetailsNavigationPreparation
{
    public static ClientDetailsNavigationTarget? Resolve(
        string? deviceIdentity,
        IReadOnlyDictionary<string, ClientInfo> inventory,
        IReadOnlyDictionary<string, ClientProfile> profiles)
    {
        string macKey = ClientIdentity.NormalizeMac(deviceIdentity);
        if (!ClientIdentity.IsMacKey(macKey)) return null;

        if (inventory.TryGetValue(macKey, out ClientInfo? liveClient))
            return new ClientDetailsNavigationTarget(macKey, liveClient, null);

        return profiles.TryGetValue(macKey, out ClientProfile? profile)
            ? new ClientDetailsNavigationTarget(macKey, null, profile)
            : null;
    }

    /// <summary>
    /// Uses the existing session-level reconciliation only when a current record is
    /// needed. A saved profile can open the established offline details path directly.
    /// </summary>
    public static async Task<ClientDetailsNavigationTarget?> ResolveAsync(
        string? deviceIdentity,
        ClientInventoryState inventory,
        ClientInventoryCoordinator coordinator,
        IReadOnlyDictionary<string, ClientProfile> profiles,
        CancellationToken cancellationToken = default)
    {
        string macKey = ClientIdentity.NormalizeMac(deviceIdentity);
        if (!ClientIdentity.IsMacKey(macKey)) return null;

        ClientDetailsNavigationTarget? initial = Resolve(macKey, inventory.Snapshot, profiles);
        if (initial?.Profile is not null && initial.LiveClient is null)
            return initial;

        if (!coordinator.IsAuthoritativelyLoaded)
            await coordinator.EnsureAuthoritativeInventoryAsync(cancellationToken);

        return Resolve(macKey, inventory.Snapshot, profiles);
    }
}

public sealed record ClientDetailsNavigationTarget(
    string MacKey,
    ClientInfo? LiveClient,
    ClientProfile? Profile);
