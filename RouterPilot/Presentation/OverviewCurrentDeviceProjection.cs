using RouterPilot.Models;

namespace RouterPilot.Presentation;

/// <summary>Pure, bounded Overview presentation of the accepted current device inventory.</summary>
public static class OverviewCurrentDeviceProjection
{
    public static OverviewCurrentDeviceSnapshot Create(
        DeviceInventorySnapshot? inventory,
        string? expectedRouterProfileId,
        long expectedContextVersion)
    {
        if (!IsAcceptedForCurrentContext(inventory, expectedRouterProfileId, expectedContextVersion))
            return OverviewCurrentDeviceSnapshot.Waiting;

        List<DeviceObservation> qualifying = inventory!.Observations
            .Where(pair => IsValidObservation(pair.Key, pair.Value) && pair.Value.IsOnline != false)
            .Select(pair => pair.Value)
            .ToList();

        if (qualifying.Count == 0)
            return OverviewCurrentDeviceSnapshot.Empty;

        return new OverviewCurrentDeviceSnapshot(
            OverviewCurrentDeviceSnapshotState.Populated,
            $"{qualifying.Count} {(qualifying.Count == 1 ? "device" : "devices")} currently observed",
            qualifying.Count,
            qualifying.Count(item => item.Client.IsWifiConnection),
            qualifying.Count(item => item.Client.IsEthernetConnection));
    }

    private static bool IsAcceptedForCurrentContext(
        DeviceInventorySnapshot? inventory,
        string? expectedRouterProfileId,
        long expectedContextVersion)
    {
        if (inventory is null || string.IsNullOrWhiteSpace(expectedRouterProfileId))
            return false;

        if (!string.IsNullOrWhiteSpace(inventory.RouterProfileId))
        {
            return inventory.ContextVersion == expectedContextVersion &&
                string.Equals(inventory.RouterProfileId, expectedRouterProfileId, StringComparison.Ordinal);
        }

        // The live Devices page owns the initial process-local reconciliation.
        // Before a router switch, the active context version is zero and that
        // reconciliation has no persisted context stamp. Its observed records
        // are therefore safe to project for the initial active router only.
        // Router switching clears the inventory and advances the version, so an
        // un-stamped completion can never be accepted for a later router.
        return expectedContextVersion == 0 && inventory.Observations.Count > 0;
    }

    private static bool IsValidObservation(DeviceIdentity identity, DeviceObservation observation) =>
        DeviceIdentity.TryCreate(identity.CanonicalMac, out DeviceIdentity keyIdentity) &&
        keyIdentity == observation.Identity &&
        observation.Client is not null;

}

public enum OverviewCurrentDeviceSnapshotState
{
    Waiting,
    Empty,
    Populated
}

public sealed record OverviewCurrentDeviceSnapshot(
    OverviewCurrentDeviceSnapshotState State,
    string Summary,
    int ObservedCount,
    int WifiCount,
    int EthernetCount)
{
    public string ObservedCountDisplay => State == OverviewCurrentDeviceSnapshotState.Waiting
        ? "—"
        : ObservedCount.ToString("N0");
    public string ObservedDetail => State == OverviewCurrentDeviceSnapshotState.Populated
        ? "devices currently observed"
        : Summary;
    public bool HasWifiBreakdown => State == OverviewCurrentDeviceSnapshotState.Populated && WifiCount > 0;
    public bool HasEthernetBreakdown => State == OverviewCurrentDeviceSnapshotState.Populated && EthernetCount > 0;

    public static OverviewCurrentDeviceSnapshot Waiting { get; } = new(
        OverviewCurrentDeviceSnapshotState.Waiting,
        "Waiting for current device information…",
        0,
        0,
        0);

    public static OverviewCurrentDeviceSnapshot Empty { get; } = new(
        OverviewCurrentDeviceSnapshotState.Empty,
        "No devices currently observed",
        0,
        0,
        0);
}
