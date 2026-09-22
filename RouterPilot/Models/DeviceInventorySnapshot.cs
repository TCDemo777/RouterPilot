using System.Collections.ObjectModel;

namespace RouterPilot.Models;

/// <summary>Current accepted inventory observation for one strict device identity.</summary>
public sealed record DeviceObservation(DeviceIdentity Identity, ClientInfo Client, bool? IsOnline);

/// <summary>Immutable typed view of the accepted shared device inventory for one router context.</summary>
public sealed class DeviceInventorySnapshot
{
    private static readonly IReadOnlyDictionary<DeviceIdentity, DeviceObservation> EmptyObservations =
        new ReadOnlyDictionary<DeviceIdentity, DeviceObservation>(new Dictionary<DeviceIdentity, DeviceObservation>());

    public static DeviceInventorySnapshot Empty { get; } = new(EmptyObservations, null, 0);

    internal DeviceInventorySnapshot(
        IReadOnlyDictionary<DeviceIdentity, DeviceObservation> observations,
        string? routerProfileId,
        long contextVersion)
    {
        Observations = observations;
        RouterProfileId = routerProfileId;
        ContextVersion = contextVersion;
    }

    public IReadOnlyDictionary<DeviceIdentity, DeviceObservation> Observations { get; }
    public string? RouterProfileId { get; }
    public long ContextVersion { get; }
    public bool IsEmpty => Observations.Count == 0;

    internal static DeviceInventorySnapshot Create(
        IEnumerable<DeviceObservation> observations,
        string? routerProfileId,
        long contextVersion)
    {
        var map = new Dictionary<DeviceIdentity, DeviceObservation>();
        foreach (DeviceObservation observation in observations)
            map[observation.Identity] = observation;
        return new DeviceInventorySnapshot(
            new ReadOnlyDictionary<DeviceIdentity, DeviceObservation>(map),
            routerProfileId,
            contextVersion);
    }
}
