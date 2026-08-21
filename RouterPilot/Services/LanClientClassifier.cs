using System;
using System.Collections.Generic;
using System.Linq;
using RouterPilot.Models;

namespace RouterPilot.Services;

public enum LanConnectionType { WiFi, Wired, Unknown }

public static class LanClientClassifier
{
    public static LanConnectionType Classify(string macAddress, ISet<string> currentWifiMacs, string structuredInterface)
    {
        if (currentWifiMacs.Contains(NormalizeMac(macAddress))) return LanConnectionType.WiFi;
        return (structuredInterface ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "2.4g" or "2.4g_iot" or "5g" => LanConnectionType.WiFi,
            "cable" => LanConnectionType.Wired,
            _ => LanConnectionType.Unknown
        };
    }

    public static string NormalizeMac(string value) => ClientIdentity.NormalizeHexMac(value);
}
