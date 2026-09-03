using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace RouterPilot.Services;

/// <summary>Builds a paste-safe, read-only capability inventory from fixed probe output.</summary>
public static partial class RouterCapabilityDiscoveryReportBuilder
{
    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    { "password", "passwd", "key", "secret", "token", "credential", "auth", "cookie", "session", "psk", "private", "username" };

    public static string Build(string raw)
    {
        int ipv4 = 0, ipv6 = 0, mac = 0, secret = 0, identity = 0;
        var report = new StringBuilder("ROUTERPILOT FLINT 2 CAPABILITY DISCOVERY\n\n");
        string section = "";
        foreach (string original in (raw ?? string.Empty).Replace("\r", string.Empty).Split('\n'))
        {
            string line = original.TrimEnd();
            if (line.StartsWith("__SECTION__", StringComparison.Ordinal))
            {
                section = line[11..];
                report.AppendLine($"=== {section} ===");
                continue;
            }
            if (string.IsNullOrWhiteSpace(line)) continue;
            string sanitized = SanitizeLine(line, ref ipv4, ref ipv6, ref mac, ref secret, ref identity);
            report.AppendLine(sanitized);
        }
        report.AppendLine();
        report.AppendLine("=== SANITIZATION SUMMARY ===");
        report.AppendLine($"IPv4 redactions: {ipv4}");
        report.AppendLine($"IPv6 redactions: {ipv6}");
        report.AppendLine($"MAC redactions: {mac}");
        report.AppendLine($"Secret-field redactions: {secret}");
        report.AppendLine($"Identity-field redactions: {identity}");
        report.AppendLine();
        report.AppendLine("=== OPERATION SUMMARY ===");
        report.AppendLine("Read-only commands: fixed aggregate capability probe");
        report.AppendLine("Mutation commands: 0");
        report.AppendLine("Configuration writes: 0");
        report.AppendLine("Service restarts: 0");
        report.AppendLine("Package changes: 0");
        return report.ToString();
    }

    private static string SanitizeLine(string line, ref int ipv4, ref int ipv6, ref int mac, ref int secret, ref int identity)
    {
        int equals = line.IndexOf('=');
        if (equals > 0)
        {
            string key = line[..equals];
            string value = line[(equals + 1)..];
            string keyName = key[(key.LastIndexOf('.') + 1)..].Trim(' ', '\'', '"');
            if (SecretKeys.Any(token => keyName.Contains(token, StringComparison.OrdinalIgnoreCase)))
            {
                secret++;
                return $"{key}=<redacted>";
            }
            if (keyName.Contains("ssid", StringComparison.OrdinalIgnoreCase) || keyName.Contains("hostname", StringComparison.OrdinalIgnoreCase) || keyName.Contains("domain", StringComparison.OrdinalIgnoreCase) || keyName.Contains("path", StringComparison.OrdinalIgnoreCase) || keyName.Contains("share", StringComparison.OrdinalIgnoreCase))
            {
                identity++;
                value = "<redacted>";
            }
            line = key + "=" + value;
        }
        MatchCollection urlMatches = UrlRegex().Matches(line); identity += urlMatches.Count; line = UrlRegex().Replace(line, "<endpoint-redacted>");
        MatchCollection ipv4Matches = IpV4Regex().Matches(line); ipv4 += ipv4Matches.Count; line = IpV4Regex().Replace(line, "<ipv4-redacted>");
        MatchCollection macMatches = MacRegex().Matches(line); mac += macMatches.Count; line = MacRegex().Replace(line, "<mac-redacted>");
        MatchCollection ipv6Matches = IpV6Regex().Matches(line); ipv6 += ipv6Matches.Count; line = IpV6Regex().Replace(line, "<ipv6-redacted>");
        return line;
    }

    [GeneratedRegex(@"(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?![\d.])")]
    private static partial Regex IpV4Regex();
    [GeneratedRegex(@"(?i)(?<![0-9a-f])(?:[0-9a-f]{2}:){5}[0-9a-f]{2}(?![0-9a-f])")]
    private static partial Regex MacRegex();
    [GeneratedRegex(@"(?i)(?<![0-9a-f:])(?:[0-9a-f]{0,4}:){2,7}[0-9a-f]{0,4}(?![0-9a-f:])")]
    private static partial Regex IpV6Regex();
    [GeneratedRegex("(?i)https?://[^\\s'\\\"]+")]
    private static partial Regex UrlRegex();
}
