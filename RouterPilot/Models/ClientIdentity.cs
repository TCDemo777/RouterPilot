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
