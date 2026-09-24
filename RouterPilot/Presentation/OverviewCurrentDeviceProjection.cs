using RouterPilot.Models;

namespace RouterPilot.Presentation;

/// <summary>Pure, bounded Overview presentation of the accepted current device inventory.</summary>
public static class OverviewCurrentDeviceProjection
{
    public const int PreviewLimit = 4;

    public static OverviewCurrentDeviceSnapshot Create(
        DeviceInventorySnapshot? inventory,
        string? expectedRouterProfileId,
        long expectedContextVersion)
    {
        if (!IsAcceptedForCurrentContext(inventory, expectedRouterProfileId, expectedContextVersion))
            return OverviewCurrentDeviceSnapshot.Waiting;

        List<(DeviceObservation Observation, string Name)> qualifying = inventory!.Observations
            .Where(pair => IsValidObservation(pair.Key, pair.Value) && pair.Value.IsOnline != false)
            .Select(pair => (Observation: pair.Value, Name: DisplayName(pair.Value.Client)))
            .OrderByDescending(item => item.Observation.IsOnline == true)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Observation.Identity.CanonicalMac, StringComparer.Ordinal)
            .ToList();

        if (qualifying.Count == 0)
            return OverviewCurrentDeviceSnapshot.Empty;

        IReadOnlyList<OverviewCurrentDeviceItem> preview = qualifying
            .Take(PreviewLimit)
            .Select(item => new OverviewCurrentDeviceItem(
                item.Name,
                ConnectionDescription(item.Observation.Client),
                item.Observation.IsOnline == true))
            .ToList();

        return new OverviewCurrentDeviceSnapshot(
            OverviewCurrentDeviceSnapshotState.Populated,
            $"{qualifying.Count} {(qualifying.Count == 1 ? "device" : "devices")} currently observed",
            qualifying.Count,
            preview);
    }

    private static bool IsAcceptedForCurrentContext(
        DeviceInventorySnapshot? inventory,
        string? expectedRouterProfileId,
        long expectedContextVersion) =>
        inventory is not null &&
        !string.IsNullOrWhiteSpace(inventory.RouterProfileId) &&
        inventory.ContextVersion > 0 &&
        !string.IsNullOrWhiteSpace(expectedRouterProfileId) &&
        expectedContextVersion > 0 &&
        inventory.ContextVersion == expectedContextVersion &&
        string.Equals(inventory.RouterProfileId, expectedRouterProfileId, StringComparison.Ordinal);

    private static bool IsValidObservation(DeviceIdentity identity, DeviceObservation observation) =>
        DeviceIdentity.TryCreate(identity.CanonicalMac, out DeviceIdentity keyIdentity) &&
        keyIdentity == observation.Identity &&
        observation.Client is not null;

    private static string DisplayName(ClientInfo client) =>
        IsUseful(client.Name) ? client.Name : "Unknown device";

    private static string? ConnectionDescription(ClientInfo client) =>
        client.HasConnectionSummary ? client.ConnectionSummary : null;

    private static bool IsUseful(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value is not "-" and not "—";
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
    IReadOnlyList<OverviewCurrentDeviceItem> Preview)
{
    public static OverviewCurrentDeviceSnapshot Waiting { get; } = new(
        OverviewCurrentDeviceSnapshotState.Waiting,
        "Waiting for current device information…",
        0,
        []);

    public static OverviewCurrentDeviceSnapshot Empty { get; } = new(
        OverviewCurrentDeviceSnapshotState.Empty,
        "No devices currently observed",
        0,
        []);
}

public sealed record OverviewCurrentDeviceItem(
    string Name,
    string? Connection,
    bool IsExplicitlyOnline);
