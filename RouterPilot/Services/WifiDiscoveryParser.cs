using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>
/// Stateless conversion and normalization for the delimited records emitted
/// by RouterManager's existing read-only Wi-Fi probes.
/// </summary>
internal static class WifiDiscoveryParser
{
    public static List<WifiRadioInfo> ParseConfiguredNetworks(string output)
    {
        var networks = new List<WifiRadioInfo>();
        foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split('|');
            if (parts.Length < 11 || parts[0] != "N") continue;

            string rawBand = parts[5].Trim().ToLowerInvariant();
            string band = NormalizeBand(rawBand, parts[6]);
            networks.Add(new WifiRadioInfo
            {
                Radio = string.IsNullOrWhiteSpace(parts[2]) ? "-" : parts[2].Trim(),
                Interface = string.IsNullOrWhiteSpace(parts[3]) ? "-" : parts[3].Trim(),
                Ssid = string.IsNullOrWhiteSpace(parts[4]) ? "Hidden network" : parts[4].Trim(),
                Band = band,
                Channel = string.IsNullOrWhiteSpace(parts[6]) ? "auto" : parts[6].Trim(),
                Security = FormatSecurity(parts[7]),
                Status = string.IsNullOrWhiteSpace(parts[8]) ? "Configured" : parts[8].Trim(),
                NetworkAssociation = string.IsNullOrWhiteSpace(parts[9]) ? "N/A" : parts[9].Trim(),
                ChannelWidth = FormatChannelWidth(parts[10]),
                HardwareMode = string.IsNullOrWhiteSpace(parts[5]) ? "N/A" : parts[5].Trim(),
                // Preserve RouterManager's established association/interface
                // inputs exactly; this parser is a move, not a reinterpretation.
                GuestClassification = ClassifyGuestNetwork(parts[9], parts[3], parts[2])
            });
        }
        return networks;
    }

    public static List<WifiRadioInfo> ParseHostapdNetworks(string output)
    {
        var networks = new List<WifiRadioInfo>();
        foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split('|');
            if (parts.Length < 7 || parts[0] != "L") continue;

            networks.Add(new WifiRadioInfo
            {
                Radio = string.IsNullOrWhiteSpace(parts[1]) ? "-" : parts[1].Trim(),
                Interface = string.IsNullOrWhiteSpace(parts[2]) ? "-" : parts[2].Trim(),
                Ssid = string.IsNullOrWhiteSpace(parts[3]) ? "Hidden network" : parts[3].Trim(),
                Band = NormalizeBand(parts[4].Trim().ToLowerInvariant(), parts[5]),
                Channel = string.IsNullOrWhiteSpace(parts[5]) ? "auto" : parts[5].Trim(),
                Status = string.IsNullOrWhiteSpace(parts[6]) ? "Configured" : parts[6].Trim()
            });
        }
        return networks;
    }

    public static int ReadDiscoveryCount(string output, string name)
    {
        foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split('|');
            for (int index = 1; index + 1 < parts.Length; index += 2)
            {
                if (parts[0] == "D" && parts[index].Equals(name, StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(parts[index + 1], out int count)) return count;
            }
        }
        return 0;
    }

    public static string FormatSignal(string signal)
    {
        if (string.IsNullOrWhiteSpace(signal)) return "-";
        string value = signal.Trim();
        return value.Contains("dbm", StringComparison.OrdinalIgnoreCase) ? value : $"{value} dBm";
    }

    public static string FormatSecurity(string encryption)
    {
        string value = encryption?.Trim().ToLowerInvariant() ?? string.Empty;
        if (value == "none" || value == "open") return "Open";
        if (value.Contains("sae") && value.Contains("psk")) return "WPA2 / WPA3";
        if (value.Contains("sae")) return "WPA3";
        if (value.Contains("psk2")) return "WPA2";
        if (value.Contains("psk")) return "WPA";
        return string.IsNullOrWhiteSpace(encryption) ? "Unknown" : encryption.Trim();
    }

    public static string FormatChannelWidth(string hardwareMode)
    {
        if (string.IsNullOrWhiteSpace(hardwareMode)) return "N/A";
        Match match = Regex.Match(hardwareMode, @"(?:HT|VHT|HE|EHT)(20|40|80|160|320)", RegexOptions.IgnoreCase);
        return match.Success ? $"{match.Groups[1].Value} MHz" : "N/A";
    }

    public static WifiGuestClassification ClassifyGuestNetwork(string networkAssociation, string ssid, string interfaceName)
    {
        if (!string.IsNullOrWhiteSpace(networkAssociation) && networkAssociation.Contains("guest", StringComparison.OrdinalIgnoreCase))
            return WifiGuestClassification.VerifiedGuest;
        return ContainsGuestMarker(ssid) || ContainsGuestMarker(interfaceName)
            ? WifiGuestClassification.LikelyGuest
            : WifiGuestClassification.Unknown;
    }

    public static string InferBandFromChannel(string channelValue)
    {
        if (int.TryParse(channelValue?.Trim(), out int channel)) return channel <= 14 ? "2.4 GHz" : "5 GHz";
        return "Unknown";
    }

    private static string NormalizeBand(string rawBand, string channel) =>
        rawBand.Contains("2g") || rawBand.Contains("11g") || rawBand.Contains("11b") ? "2.4 GHz" :
        rawBand.Contains("5g") || rawBand.Contains("11a") || rawBand.Contains("11ac") || rawBand.Contains("11ax") ? "5 GHz" :
        rawBand.Contains("6g") ? "6 GHz" : InferBandFromChannel(channel);

    private static bool ContainsGuestMarker(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains("guest", StringComparison.OrdinalIgnoreCase);
}
