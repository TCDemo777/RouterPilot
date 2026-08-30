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

public interface IDeviceIdentityResolver
{
    bool TryParseMac(string? value, out ParsedMacAddress? parsed);
    string ResolveManufacturer(string? macAddress, string? friendlyName = null, string? authoritativeManufacturer = null);
    Task<string> ResolveManufacturerAsync(string? macAddress, string? friendlyName = null, string? authoritativeManufacturer = null, CancellationToken cancellationToken = default);
    string ResolveFriendlyName(string? personalisedName, string? authoritativeName, string? persistedName, string? associatedIp = null);
}
