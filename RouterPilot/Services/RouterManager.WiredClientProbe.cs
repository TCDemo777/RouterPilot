#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

public partial class RouterManager
{
    // Fixed Debug-only, read-only probe. No command text is supplied by the UI.
    public async Task<string> GetWiredClientDiscoveryProbeReportAsync()
    {
        List<WifiRadioInfo> radios = await GetWifiRadiosAsync();
        var wifiMacs = radios.SelectMany(radio => radio.Clients)
            .Select(client => NormalizeProbeMac(client.MacAddress))
            .Where(mac => mac.Length == 12)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string clientJson = await _ssh.RunCommandAsync("ubus call gl-clients list 2>/dev/null || true");
        List<ProbeClient> clients = ParseProbeClients(clientJson);
        string fdbOutput = await _ssh.RunCommandAsync("if command -v bridge >/dev/null 2>&1; then printf '__RP_BRIDGE_FDB:yes\\n'; bridge fdb show 2>/dev/null; else printf '__RP_BRIDGE_FDB:no\\n'; fi");
        string membersOutput = await _ssh.RunCommandAsync("if [ -d /sys/class/net/br-lan/brif ]; then printf '__RP_BRIDGE_MEMBERS:yes\\n'; for entry in /sys/class/net/br-lan/brif/*; do [ -e \"$entry\" ] || continue; name=${entry##*/}; type=$(cat /sys/class/net/$name/type 2>/dev/null); wireless=no; [ -d /sys/class/net/$name/wireless ] && wireless=yes; port=$(cat /sys/class/net/$name/phys_port_name 2>/dev/null); subsystem=$(readlink /sys/class/net/$name/device/subsystem 2>/dev/null); printf 'M|%s|%s|%s|%s|%s\\n' \"$name\" \"$type\" \"$wireless\" \"$port\" \"$subsystem\"; done; else printf '__RP_BRIDGE_MEMBERS:no\\n'; fi");
        string neighbourOutput = await _ssh.RunCommandAsync("ip neigh 2>/dev/null || true");
        string linkOutput = await _ssh.RunCommandAsync("ip -o link show 2>/dev/null || true");

        var members = ParseBridgeMembers(membersOutput);
        var physicalMembers = members.Where(member => member.IsVerifiedPhysical).Select(member => member.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fdbByMac = ParseFdb(fdbOutput);
        var connectionValues = clients.SelectMany(client => client.ConnectionValues)
            .GroupBy(value => value.Field, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{group.Key}: {string.Join(", ", group.Select(value => value.Value).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))}")
            .ToList();

        var wired = new List<ProbeClient>();
        int wifi = 0, unknown = 0, fdbPhysicalMatches = 0;
        foreach (ProbeClient client in clients.Where(client => client.Online))
        {
            if (wifiMacs.Contains(client.NormalizedMac))
            {
                wifi++;
                continue;
            }

            bool explicitWired = client.ConnectionValues.Any(value => IsExplicitWired(value.Value));
            bool fdbWired = fdbByMac.TryGetValue(client.NormalizedMac, out ProbeFdbEntry? fdb) &&
                             fdb.IsCurrentDynamic && physicalMembers.Contains(fdb.Device);
            if (explicitWired || fdbWired)
            {
                wired.Add(client with { Evidence = explicitWired ? "gl-clients explicit wired value" : $"FDB dev {fdb!.Device}" });
                if (fdbWired) fdbPhysicalMatches++;
            }
            else unknown++;
        }

        var report = new StringBuilder();
        report.AppendLine("RouterPilot Wired vs Wi-Fi Client Discovery Probe (Debug only)");
        report.AppendLine($"Current Wi-Fi client count: {wifiMacs.Count}");
        report.AppendLine($"gl-clients records: {clients.Count}; online: {clients.Count(client => client.Online)}");
        report.AppendLine($"gl-clients connection/type values: {(connectionValues.Count == 0 ? "none observed" : string.Join(" | ", connectionValues))}");
        report.AppendLine($"BRIDGE FDB AVAILABLE: {(fdbOutput.Contains("__RP_BRIDGE_FDB:yes", StringComparison.Ordinal) ? "YES" : "NO")}");
        report.AppendLine($"Bridge members: {(members.Count == 0 ? "none/unavailable" : string.Join(", ", members.Select(member => $"{member.Name} ({member.Category})")))}");
        report.AppendLine($"Physical Ethernet/DSA interfaces: {(physicalMembers.Count == 0 ? "none positively identified" : string.Join(", ", physicalMembers.OrderBy(value => value)))}");
        report.AppendLine($"FDB physical-port mappings: {fdbPhysicalMatches}");
        report.AppendLine($"Neighbour table: {(string.IsNullOrWhiteSpace(neighbourOutput) ? "unavailable" : "available; dev br-lan is treated as correlation only, not wired proof")}");
        report.AppendLine($"Counts — Wi-Fi: {wifi}; Wired: {wired.Count}; Unknown: {unknown}");
        report.AppendLine($"Wired candidates: {(wired.Count == 0 ? "none" : string.Join(" | ", wired.Select(client => $"{client.SafeName} {client.SafeIp} ({client.Evidence})")))}");
        report.AppendLine($"WIRED CLIENT SOURCE IDENTIFIED: {(wired.Count > 0 ? "YES" : "NO")}");
        report.AppendLine("Preferred classifier: hostapd MAC => Wi-Fi; otherwise explicit gl-clients cable/wired/ethernet; otherwise current FDB dev in a verified physical DSA/physical bridge member; otherwise Unknown.");
        report.AppendLine($"ip link observed: {(string.IsNullOrWhiteSpace(linkOutput) ? "no" : "yes")}");
        return report.ToString();
    }

    private static List<ProbeClient> ParseProbeClients(string json)
    {
        var clients = new List<ProbeClient>();
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            foreach (JsonElement record in EnumerateClientObjects(document.RootElement))
            {
                string mac = GetFlexibleString(record, "mac", "macaddr", "mac_address");
                string normalized = NormalizeProbeMac(mac);
                if (normalized.Length != 12 || clients.Any(client => client.NormalizedMac == normalized)) continue;
                var values = new List<ProbeValue>();
                foreach (string field in new[] { "iface", "interface", "connection", "type" })
                {
                    if (record.TryGetProperty(field, out JsonElement value) && !string.IsNullOrWhiteSpace(value.ToString())) values.Add(new ProbeValue(field, value.ToString().Trim()));
                }
                clients.Add(new ProbeClient(normalized, GetFlexibleBoolean(record, "online", true), GetFlexibleString(record, "name", "hostname", "host_name"), GetFlexibleString(record, "ip", "ipaddr", "ip_address"), values));
            }
        }
        catch (JsonException) { }
        return clients;
    }

    private static List<ProbeBridgeMember> ParseBridgeMembers(string output) => output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Where(line => line.StartsWith("M|", StringComparison.Ordinal)).Select(line => line.Split('|')).Where(parts => parts.Length >= 6)
        .Select(parts => new ProbeBridgeMember(parts[1], parts[2], parts[3], parts[4], parts[5])).ToList();

    private static Dictionary<string, ProbeFdbEntry> ParseFdb(string output) => output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(line => new { Line = line, Match = Regex.Match(line, "^(?<mac>[0-9a-fA-F:]{17}).*?\\bdev\\s+(?<dev>[^\\s]+)") })
        .Where(item => item.Match.Success)
        .Select(item => new ProbeFdbEntry(NormalizeProbeMac(item.Match.Groups["mac"].Value), item.Match.Groups["dev"].Value, item.Line))
        .GroupBy(entry => entry.Mac, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

    private static bool IsExplicitWired(string value) => value.Contains("cable", StringComparison.OrdinalIgnoreCase) || value.Contains("wired", StringComparison.OrdinalIgnoreCase) || value.Contains("ethernet", StringComparison.OrdinalIgnoreCase);
    private static string NormalizeProbeMac(string value) => new string((value ?? string.Empty).Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();

    private sealed record ProbeValue(string Field, string Value);
    private sealed record ProbeClient(string NormalizedMac, bool Online, string Name, string Ip, IReadOnlyList<ProbeValue> ConnectionValues, string Evidence = "")
    { public string SafeName => string.IsNullOrWhiteSpace(Name) ? "Unknown device" : Name; public string SafeIp => string.IsNullOrWhiteSpace(Ip) ? "IP unavailable" : Ip; }
    private sealed record ProbeFdbEntry(string Mac, string Device, string Raw)
    { public bool IsCurrentDynamic => !Raw.Contains(" self", StringComparison.OrdinalIgnoreCase) && !Raw.Contains(" permanent", StringComparison.OrdinalIgnoreCase); }
    private sealed record ProbeBridgeMember(string Name, string LinkType, string Wireless, string PhysicalPort, string Subsystem)
    { public bool IsVerifiedPhysical => !string.IsNullOrWhiteSpace(PhysicalPort) || Subsystem.Contains("/dsa", StringComparison.OrdinalIgnoreCase); public string Category => Wireless == "yes" ? "wireless" : IsVerifiedPhysical ? "physical/DSA" : "unknown/virtual"; }
}
#endif
