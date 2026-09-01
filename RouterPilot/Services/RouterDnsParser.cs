using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using RouterPilot.Models;

namespace RouterPilot.Services;

internal static class RouterDnsParser
{
    public static RouterDnsSnapshot Parse(string? output, DateTimeOffset capturedAt)
    {
        RouterDnsCapability capability = RouterDnsCapability.Unknown;
        string? service = null;
        RouterDnsMode mode = RouterDnsMode.Unknown;
        RouterDnsRuntimeState runtime = RouterDnsRuntimeState.Unknown;
        RouterDnsEncryptionMode encryption = RouterDnsEncryptionMode.Unknown;
        bool? handles = null;
        string? vpn = null;
        var upstreams = new List<string>();

        foreach (string raw in (output ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = raw.Trim().Split('|');
            if (fields.Length == 0) continue;
            switch (fields[0].Trim())
            {
                case "S":
                    capability = Field(fields, 1).ToLowerInvariant() switch
                    {
                        "supported" => RouterDnsCapability.Supported,
                        "unsupported" => RouterDnsCapability.Unsupported,
                        _ => RouterDnsCapability.Unknown
                    };
                    service = NullIf(Field(fields, 2));
                    mode = ParseMode(Field(fields, 3));
                    runtime = ParseRuntime(Field(fields, 4));
                    encryption = ParseEncryption(Field(fields, 5));
                    handles = ParseBool(Field(fields, 6));
                    vpn = NullIf(Field(fields, 7));
                    break;
                case "U":
                    string resolver = NormalizeResolver(Field(fields, 1));
                    if (!string.IsNullOrEmpty(resolver) && !upstreams.Contains(resolver, StringComparer.OrdinalIgnoreCase))
                        upstreams.Add(resolver);
                    break;
            }
        }

        return new RouterDnsSnapshot(
            capability == RouterDnsCapability.Supported ? RouterCapabilityState.Supported :
            capability == RouterDnsCapability.Unsupported ? RouterCapabilityState.Unsupported : RouterCapabilityState.Unknown,
            service, mode, runtime, encryption, upstreams.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(), handles, vpn, capturedAt);
    }

    private enum RouterDnsCapability { Supported, Unsupported, Unknown }
    private static RouterDnsMode ParseMode(string value) => value.Trim().ToLowerInvariant() switch
    {
        "automatic" or "auto" => RouterDnsMode.Automatic,
        "manual" => RouterDnsMode.Manual,
        "adguard" => RouterDnsMode.AdGuard,
        "doh" => RouterDnsMode.DoH,
        "dot" => RouterDnsMode.DoT,
        "encrypted" => RouterDnsMode.Encrypted,
        "vpn" => RouterDnsMode.Vpn,
        "mixed" => RouterDnsMode.Mixed,
        _ => RouterDnsMode.Unknown
    };
    private static RouterDnsRuntimeState ParseRuntime(string value) => value.Trim().ToLowerInvariant() switch
    {
        "running" or "up" => RouterDnsRuntimeState.Running,
        "stopped" or "down" => RouterDnsRuntimeState.Stopped,
        _ => RouterDnsRuntimeState.Unknown
    };
    private static RouterDnsEncryptionMode ParseEncryption(string value) => value.Trim().ToLowerInvariant() switch
    {
        "plain" or "none" => RouterDnsEncryptionMode.Plain,
        "doh" => RouterDnsEncryptionMode.DoH,
        "dot" => RouterDnsEncryptionMode.DoT,
        "encrypted" => RouterDnsEncryptionMode.Encrypted,
        _ => RouterDnsEncryptionMode.Unknown
    };
    private static bool? ParseBool(string value) => value.Trim().ToLowerInvariant() switch { "1" or "true" or "yes" => true, "0" or "false" or "no" => false, _ => null };
    private static string Field(string[] fields, int index) => index < fields.Length ? fields[index].Trim() : string.Empty;
    private static string? NullIf(string value) => string.IsNullOrWhiteSpace(value) || value == "-" ? null : value.Trim();
    private static string NormalizeResolver(string value)
    {
        string candidate = value.Trim();
        if (candidate.Length == 0 || candidate.Contains(' ') || candidate.Contains('\t')) return string.Empty;
        if (IPAddress.TryParse(candidate, out _)) return candidate;
        if (Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) && (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) || uri.Scheme.Equals("tls", StringComparison.OrdinalIgnoreCase)))
            return new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty, Query = string.Empty, Fragment = string.Empty }.Uri.ToString().TrimEnd('/');
        if (candidate.Contains('@')) return string.Empty;
        return candidate;
    }
}
