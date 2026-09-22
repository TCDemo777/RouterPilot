namespace RouterPilot.Models;

/// <summary>Strict MAC-backed device identity for the shared inventory boundary.</summary>
public readonly struct DeviceIdentity : IEquatable<DeviceIdentity>
{
    private DeviceIdentity(string canonicalMac) => CanonicalMac = canonicalMac;

    /// <summary>Existing strict shared-inventory key format: uppercase, separator-free hexadecimal MAC.</summary>
    public string CanonicalMac { get; }

    public static bool TryCreate(string? value, out DeviceIdentity identity)
    {
        string canonical = ClientIdentity.NormalizeHexMac(value);
        if (canonical.Length != 12)
        {
            identity = default;
            return false;
        }

        identity = new DeviceIdentity(canonical);
        return true;
    }

    public bool Equals(DeviceIdentity other) =>
        string.Equals(CanonicalMac, other.CanonicalMac, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is DeviceIdentity other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(CanonicalMac ?? string.Empty);

    public override string ToString() => CanonicalMac ?? string.Empty;

    public static bool operator ==(DeviceIdentity left, DeviceIdentity right) => left.Equals(right);
    public static bool operator !=(DeviceIdentity left, DeviceIdentity right) => !left.Equals(right);
}
