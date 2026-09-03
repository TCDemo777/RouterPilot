using System.Text.RegularExpressions;
using RouterPilot.Models;

namespace RouterPilot.Services;

public static class RouterLogParser
{
    private static readonly Regex Priority = new(@"^<(?<n>[0-7])>\s*", RegexOptions.Compiled);
    public static IReadOnlyList<RouterLogEntry> Parse(string? output, int maximum = 250)
    {
        List<RouterLogEntry> entries = new();
        foreach (string raw in (output ?? string.Empty).Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            Match priority = Priority.Match(line);
            string severity = priority.Success ? SeverityFor(int.Parse(priority.Groups["n"].Value)) : "Unknown";
            if (priority.Success) line = line[priority.Length..];
            string timestamp = string.Empty;
            string message = line.Trim();
            int firstSpace = message.IndexOf(' ');
            if (firstSpace > 0 && firstSpace < 32) { timestamp = message[..firstSpace]; message = message[(firstSpace + 1)..].Trim(); }
            string source = SourceFor(message);
            entries.Add(new RouterLogEntry(timestamp, severity, CategoryFor(message), source, message));
            if (entries.Count >= maximum) break;
        }
        return entries;
    }

    private static string SeverityFor(int n) => n switch { 0 => "Emergency", 1 => "Alert", 2 => "Critical", 3 => "Error", 4 => "Warning", 5 => "Notice", 6 => "Info", 7 => "Debug", _ => "Unknown" };
    private static string SourceFor(string message)
    {
        string[] sources = ["dnsmasq", "netifd", "hostapd", "wpa_supplicant", "odhcpd", "fw4", "nftables", "tailscaled", "openvpn", "AdGuardHome", "samba", "mountd", "kernel"];
        return sources.FirstOrDefault(source => message.Contains(source, StringComparison.OrdinalIgnoreCase)) ?? "router";
    }
    private static string CategoryFor(string message)
    {
        if (message.Contains("dnsmasq", StringComparison.OrdinalIgnoreCase)) return "DHCP / DNS";
        if (message.Contains("odhcpd", StringComparison.OrdinalIgnoreCase) || message.Contains("netifd", StringComparison.OrdinalIgnoreCase)) return "Network / WAN";
        if (message.Contains("hostapd", StringComparison.OrdinalIgnoreCase) || message.Contains("wpa_", StringComparison.OrdinalIgnoreCase)) return "Wi-Fi";
        if (message.Contains("fw4", StringComparison.OrdinalIgnoreCase) || message.Contains("nft", StringComparison.OrdinalIgnoreCase) || message.Contains("firewall", StringComparison.OrdinalIgnoreCase)) return "Firewall";
        if (message.Contains("tailscale", StringComparison.OrdinalIgnoreCase) || message.Contains("openvpn", StringComparison.OrdinalIgnoreCase) || message.Contains("wireguard", StringComparison.OrdinalIgnoreCase)) return "VPN";
        if (message.Contains("adguard", StringComparison.OrdinalIgnoreCase)) return "AdGuard";
        if (message.Contains("samba", StringComparison.OrdinalIgnoreCase) || message.Contains("mountd", StringComparison.OrdinalIgnoreCase) || message.Contains("usb", StringComparison.OrdinalIgnoreCase)) return "Storage / File Sharing";
        if (message.Contains("kernel", StringComparison.OrdinalIgnoreCase)) return "Kernel";
        return "System";
    }
}
