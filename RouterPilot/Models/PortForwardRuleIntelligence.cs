using System;
using System.Collections.Generic;
using System.Linq;

namespace RouterPilot.Models;

/// <summary>Local, read-only rule health derived from existing DHCP and Wi-Fi snapshots.</summary>
public static class PortForwardRuleIntelligence
{
    public static void Evaluate(
        IEnumerable<PortForwardRuleInfo> rules,
        IEnumerable<DhcpLeaseInfo> leases,
        IEnumerable<DhcpReservationInfo> reservations,
        IEnumerable<WifiRadioInfo> wifiNetworks,
        bool dhcpLoaded)
    {
        List<PortForwardRuleInfo> ruleList = rules.ToList();
        if (!dhcpLoaded)
        {
            foreach (PortForwardRuleInfo rule in ruleList)
                rule.SetTargetIntelligence(string.Empty, "Checking target…", string.Empty, "Checking");
            return;
        }

        List<DhcpLeaseInfo> leaseList = leases.ToList();
        List<DhcpReservationInfo> reservationList = reservations.Where(item => item.Enabled).ToList();
        HashSet<string> onlineMacs = wifiNetworks.SelectMany(network => network.Clients)
            .Select(client => NormalizeMac(client.MacAddress)).Where(mac => mac.Length == 12).ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> onlineIps = wifiNetworks.SelectMany(network => network.Clients)
            .Select(client => client.IpAddress?.Trim() ?? string.Empty).Where(IsUsableIp).ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> conflicts = FindConflictingRuleIds(ruleList);

        foreach (PortForwardRuleInfo rule in ruleList)
        {
            DhcpLeaseInfo? lease = leaseList.FirstOrDefault(item => SameIp(item.IpAddress, rule.DestinationIp));
            DhcpReservationInfo? reservation = reservationList.FirstOrDefault(item => SameIp(item.IpAddress, rule.DestinationIp));
            string mac = NormalizeMac(lease?.MacAddress ?? reservation?.MacAddress);
            if (mac.Length == 12)
                reservation ??= reservationList.FirstOrDefault(item => NormalizeMac(item.MacAddress) == mac);

            string clientName = DisplayName(lease?.ClientName, lease?.Hostname, reservation?.Hostname);
            bool currentWifi = (mac.Length == 12 && onlineMacs.Contains(mac)) || onlineIps.Contains(rule.DestinationIp.Trim());
            DhcpLeaseInfo? movedLease = mac.Length == 12
                ? leaseList.FirstOrDefault(item => NormalizeMac(item.MacAddress) == mac && !SameIp(item.IpAddress, rule.DestinationIp))
                : null;

            if (conflicts.Contains(rule.Id))
                rule.SetTargetIntelligence(clientName, "External port conflict", "Another enabled rule overlaps this protocol and external port.", "Critical");
            else if (movedLease is not null)
                rule.SetTargetIntelligence(clientName, "Target IP changed", $"Rule: {rule.DestinationIp} • Current device IP: {movedLease.IpAddress}", "Warning");
            else if (lease is null && reservation is null)
                rule.SetTargetIntelligence(string.Empty, "Device not found", "No current RouterPilot DHCP client matches this internal IP.", "Warning");
            else if (reservation is null)
                rule.SetTargetIntelligence(clientName, "Dynamic IP", "No DHCP reservation. This rule may stop working if the device IP changes.", "Warning");
            else if (lease is null && !currentWifi)
                rule.SetTargetIntelligence(clientName, "Device offline", "The reserved target is not currently active in RouterPilot's client snapshots.", "Offline");
            else
                rule.SetTargetIntelligence(clientName, "Reserved IP", string.Empty, "Success");
        }
    }

    private static HashSet<string> FindConflictingRuleIds(IReadOnlyList<PortForwardRuleInfo> rules)
    {
        HashSet<string> conflicts = new(StringComparer.OrdinalIgnoreCase);
        for (int left = 0; left < rules.Count; left++)
        for (int right = left + 1; right < rules.Count; right++)
        {
            PortForwardRuleInfo a = rules[left], b = rules[right];
            if (!a.Enabled || !b.Enabled || !ProtocolsOverlap(a.Protocol, b.Protocol) || !PortsOverlap(a.ExternalPort, b.ExternalPort)) continue;
            conflicts.Add(a.Id); conflicts.Add(b.Id);
        }
        return conflicts;
    }

    private static bool PortsOverlap(string? left, string? right) => TryParsePortRange(left, out int aStart, out int aEnd) && TryParsePortRange(right, out int bStart, out int bEnd) && aStart <= bEnd && bStart <= aEnd;
    private static bool TryParsePortRange(string? value, out int start, out int end)
    {
        start = end = 0;
        string[] parts = (value ?? string.Empty).Trim().Split(['-', ':'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2 || !int.TryParse(parts[0], out start) || start is < 1 or > 65535) return false;
        end = start;
        return parts.Length == 1 || (int.TryParse(parts[1], out end) && end >= start && end <= 65535);
    }
    private static bool ProtocolsOverlap(string? left, string? right) => Protocols(left).Intersect(Protocols(right), StringComparer.OrdinalIgnoreCase).Any();
    private static IEnumerable<string> Protocols(string? value) => (value ?? string.Empty).Split([' ', '+', ',', '/'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(item => item.ToLowerInvariant()).Where(item => item is "tcp" or "udp");
    private static string NormalizeMac(string? value) => new string((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static bool SameIp(string? left, string? right) => string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
    private static bool IsUsableIp(string value) => !string.IsNullOrWhiteSpace(value) && value != "-" && value != "—";
    private static string DisplayName(params string?[] names) => names.FirstOrDefault(name => !string.IsNullOrWhiteSpace(name) && name != "-" && !string.Equals(name, "Unknown device", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
}
