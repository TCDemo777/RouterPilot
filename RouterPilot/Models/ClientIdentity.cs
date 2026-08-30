using System.Linq;
using System.Net;

namespace RouterPilot.Models;

/// <summary>Pure normalization and comparison rules for RouterPilot client identities.</summary>
public static class ClientIdentity
{
    /// <summary>Preserves the existing profile/presence identity format: uppercase, separator-free alphanumeric text.</summary>
    public static string NormalizeMac(string? value) => Normalize(value, hexadecimalOnly: false);

    /// <summary>Preserves the stricter hardware-MAC format used by LAN/DHCP reconciliation: uppercase hexadecimal text.</summary>
    public static string NormalizeHexMac(string? value) => Normalize(value, hexadecimalOnly: true);

    public static bool MacEquals(string? left, string? right) =>
        string.Equals(NormalizeMac(left), NormalizeMac(right), StringComparison.Ordinal);

    public static bool IsMacKey(string? value) => NormalizeMac(value).Length == 12;

    /// <summary>Canonicalizes an AdGuard/router client endpoint for correlation.</summary>
    public static string NormalizeEndpoint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        string candidate = value.Trim();
        if (candidate.StartsWith("[", StringComparison.Ordinal) &&
            candidate.IndexOf(']') is int closingBracket && closingBracket > 0)
        {
            candidate = candidate[1..closingBracket];
        }
        else if (candidate.Count(character => character == ':') == 1 &&
                 candidate.LastIndexOf(':') is int separator &&
                 int.TryParse(candidate[(separator + 1)..], out _))
        {
            candidate = candidate[..separator];
        }

        if (IPAddress.TryParse(candidate, out IPAddress? address))
        {
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            return address.ToString().ToLowerInvariant();
        }

        return candidate.TrimEnd('.').ToLowerInvariant();
    }

    public static bool EndpointEquals(string? left, string? right) =>
        string.Equals(NormalizeEndpoint(left), NormalizeEndpoint(right), StringComparison.OrdinalIgnoreCase) &&
        NormalizeEndpoint(left).Length > 0;

    private static string Normalize(string? value, bool hexadecimalOnly)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        char[] characters = new char[value.Length];
        int length = 0;
        foreach (char character in value)
        {
            if (!hexadecimalOnly ? char.IsLetterOrDigit(character) : Uri.IsHexDigit(character))
                characters[length++] = char.ToUpperInvariant(character);
        }

        return length == 0 ? string.Empty : new string(characters, 0, length);
    }
}
