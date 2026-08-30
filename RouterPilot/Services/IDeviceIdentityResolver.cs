using RouterPilot.Models;

namespace RouterPilot.Services;

public enum MacAddressKind
{
    Invalid,
    Universal,
    Local,
    Multicast
}

public sealed record ParsedMacAddress(string Canonical, MacAddressKind Kind);
public sealed record DeviceIdentitySignals(
    string? PersonalisedName,
    string? RouterName,
    string? DhcpHostname,
    string? MdnsName,
    string? AdGuardName,
    string? PersistedName,
    string? AssociatedIp);

public enum DeviceNameCandidateKind
{
    SpecificDeviceName,
    OperatingSystem,
    GenericDeviceType,
    Manufacturer,
    ServiceType,
    ServiceName,
    IpAddress,
    MacAddress,
    InternalIdentifier,
    Unavailable
}

public interface IDeviceIdentityResolver
{
    bool TryParseMac(string? value, out ParsedMacAddress? parsed);
    string ResolveManufacturer(string? macAddress, string? friendlyName = null, string? authoritativeManufacturer = null);
    Task<string> ResolveManufacturerAsync(string? macAddress, string? friendlyName = null, string? authoritativeManufacturer = null, CancellationToken cancellationToken = default);
    string ResolveFriendlyName(string? personalisedName, string? authoritativeName, string? persistedName, string? associatedIp = null);
    string ResolveFriendlyName(DeviceIdentitySignals signals);
    DeviceNameCandidateKind ClassifyDeviceNameCandidate(string? candidate);
    string? ResolveOperatingSystem(params string?[] candidates);
}
