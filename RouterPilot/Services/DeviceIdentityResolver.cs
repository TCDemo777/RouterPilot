using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>Shared device identity and MAC vendor resolution with resilient online enrichment.</summary>
public sealed class DeviceIdentityResolver : IDeviceIdentityResolver
{
    private static readonly IReadOnlyDictionary<string, string> Vendors =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["001A11"] = "Google", ["3C5A37"] = "Google", ["F4F5D8"] = "Google",
            ["001B63"] = "Apple", ["3C0754"] = "Apple", ["F0D1A9"] = "Apple",
            ["B827EB"] = "Raspberry Pi", ["DCA632"] = "Raspberry Pi", ["E45F01"] = "Raspberry Pi",
            ["001E10"] = "Shenzhen GL.iNet", ["94D9B3"] = "Shenzhen GL.iNet", ["9424E1"] = "Shenzhen GL.iNet",
            ["001A2B"] = "Cisco", ["001B44"] = "SanDisk", ["001C42"] = "Parallels",
            ["001D7E"] = "Cisco-Linksys", ["001E8C"] = "ASUSTek", ["001F3B"] = "Intel",
            ["0024E8"] = "Dell", ["0026B9"] = "Dell", ["001422"] = "Dell",
            ["00155D"] = "Microsoft", ["7C1E52"] = "Microsoft", ["0050F2"] = "Microsoft",
            ["001A79"] = "Samsung", ["0024E9"] = "Samsung", ["3C5AB4"] = "Google/Nest",
            ["AC84C6"] = "TP-Link", ["50C7BF"] = "TP-Link", ["00195B"] = "D-Link",
            ["001F33"] = "Netgear", ["000C29"] = "VMware", ["001C14"] = "VMware",
            ["080027"] = "Oracle VirtualBox"
        };

    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _onlineCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _negativeOnlineCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _inflight = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _httpClient;
    private DateTime _onlineBackoffUntilUtc;

    public DeviceIdentityResolver(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(3);
    }

    public bool TryParseMac(string? value, out ParsedMacAddress? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        string candidate = value.Trim();
        string hex = candidate.Contains('.') && candidate.Count(c => c == '.') == 2
            ? string.Concat(candidate.Split('.'))
            : new string(candidate.Where(IsAsciiHex).ToArray());
        bool shaped = (candidate.Length == 17 && (candidate.Count(c => c == ':') == 5 || candidate.Count(c => c == '-') == 5)) ||
                      candidate.Length == 14 && candidate.Count(c => c == '.') == 2 ||
                      candidate.Length == 12 && candidate.All(IsAsciiHex);
        if (!shaped || hex.Length != 12 || !hex.All(IsAsciiHex)) return false;
        hex = hex.ToUpperInvariant();
        int first = int.Parse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        MacAddressKind kind = (first & 1) != 0 ? MacAddressKind.Multicast :
            (first & 2) != 0 ? MacAddressKind.Local : MacAddressKind.Universal;
        parsed = new ParsedMacAddress(hex, kind);
        return true;
    }

    public string ResolveManufacturer(string? macAddress, string? friendlyName = null, string? authoritativeManufacturer = null)
    {
        if (IsUsefulManufacturer(authoritativeManufacturer)) return authoritativeManufacturer!;
        if (!TryParseMac(macAddress, out ParsedMacAddress? parsed) || parsed is null)
            return ResolveFromName(friendlyName);
        if (parsed.Kind is MacAddressKind.Local) return "Private/local MAC";
        if (parsed.Kind is MacAddressKind.Multicast) return "Unknown manufacturer";
        string key = parsed.Canonical[..6];
        if (_onlineCache.TryGetValue(key, out string? onlineManufacturer)) return onlineManufacturer;
        return _cache.GetOrAdd(key, _ => Vendors.TryGetValue(key, out string? vendor)
            ? vendor
            : ResolveFromName(friendlyName));
    }

    public async Task<string> ResolveManufacturerAsync(string? macAddress, string? friendlyName = null, string? authoritativeManufacturer = null, CancellationToken cancellationToken = default)
    {
        string local = ResolveManufacturer(macAddress, friendlyName, authoritativeManufacturer);
        if (IsUsefulManufacturer(authoritativeManufacturer) ||
            !TryParseMac(macAddress, out ParsedMacAddress? parsed) || parsed is null ||
            parsed.Kind is MacAddressKind.Local or MacAddressKind.Multicast)
            return local;

        string key = parsed.Canonical[..6];
        if (DateTime.UtcNow < _onlineBackoffUntilUtc) return local;
        if (_onlineCache.TryGetValue(key, out string? cached)) return cached;
        if (_negativeOnlineCache.TryGetValue(key, out DateTime negativeUntil) && negativeUntil > DateTime.UtcNow)
            return local;

        Lazy<Task<string>> lazy = _inflight.GetOrAdd(key,
            _ => new Lazy<Task<string>>(() => LookupOnlineThenFallbackAsync(parsed.Canonical, key, friendlyName, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try { return await lazy.Value.ConfigureAwait(false); }
        finally { _inflight.TryRemove(key, out _); }
    }

    private async Task<string> LookupOnlineThenFallbackAsync(string canonicalMac, string prefix, string? friendlyName, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await _httpClient.GetAsync(
                $"https://api.maclookup.app/v2/macs/{prefix}", cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode == 429)
            {
                int seconds = 60;
                if (response.Headers.RetryAfter?.Delta is TimeSpan delta)
                    seconds = Math.Clamp((int)delta.TotalSeconds, 1, 3600);
                _onlineBackoffUntilUtc = DateTime.UtcNow.AddSeconds(seconds);
                return ResolveManufacturer(canonicalMac, friendlyName);
            }

            if (!response.IsSuccessStatusCode) return ResolveManufacturer(canonicalMac, friendlyName);
            using JsonDocument document = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new JsonException("empty MAC lookup response");
            JsonElement root = document.RootElement;
            bool success = !root.TryGetProperty("success", out JsonElement successValue) || successValue.ValueKind != JsonValueKind.False;
            bool found = !root.TryGetProperty("found", out JsonElement foundValue) || foundValue.ValueKind == JsonValueKind.True;
            string? company = root.TryGetProperty("company", out JsonElement companyValue) && companyValue.ValueKind == JsonValueKind.String
                ? companyValue.GetString() : null;
            if (success && found && !string.IsNullOrWhiteSpace(company))
            {
                string manufacturer = company.Trim();
                _onlineCache[prefix] = manufacturer;
                _negativeOnlineCache.TryRemove(prefix, out _);
                return manufacturer;
            }
            _negativeOnlineCache[prefix] = DateTime.UtcNow.AddMinutes(10);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }
        catch (JsonException) { }
        return ResolveManufacturer(canonicalMac, friendlyName);
    }

    public string ResolveFriendlyName(string? personalisedName, string? authoritativeName, string? persistedName, string? associatedIp = null)
    {
        foreach (string? candidate in new[] { personalisedName, authoritativeName, persistedName })
        {
            if (IsUsefulName(candidate) && !IsGeneratedIpLabel(candidate, associatedIp))
                return candidate!.Trim();
        }
        return "Unknown device";
    }

    public string ResolveFriendlyName(DeviceIdentitySignals signals)
    {
        // Router/DHCP names are preferred over opportunistic discovery names;
        // all candidates are still validated against generated IP labels.
        return ResolveFriendlyName(
            signals.PersonalisedName,
            signals.RouterName,
            signals.DhcpHostname,
            signals.MdnsName,
            signals.AdGuardName,
            signals.PersistedName,
            signals.AssociatedIp);
    }

    private string ResolveFriendlyName(
        string? personalisedName, string? routerName, string? dhcpHostname,
        string? mdnsName, string? adGuardName, string? persistedName, string? associatedIp)
    {
        string?[] candidates = { personalisedName, routerName, dhcpHostname, mdnsName, adGuardName, persistedName };
        for (int index = 0; index < candidates.Length; index++)
        {
            string? clean = CleanDiscoveredName(candidates[index]);
            // An explicit user nickname is authoritative even when it happens
            // to be a platform word (for example, a nickname named "Windows").
            if (index == 0 && IsUsefulName(clean) && !IsGeneratedIpLabel(clean, associatedIp))
                return clean!;
            if (index > 0 && ClassifyDeviceNameCandidate(clean) == DeviceNameCandidateKind.SpecificDeviceName &&
                !IsGeneratedIpLabel(clean, associatedIp)) return clean!;
        }
        return "Unknown device";
    }

    public DeviceNameCandidateKind ClassifyDeviceNameCandidate(string? candidate)
    {
        string? value = CleanDiscoveredName(candidate);
        if (value is null || !IsUsefulName(value)) return DeviceNameCandidateKind.Unavailable;
        if (TryClassifyOperatingSystem(value, out _)) return DeviceNameCandidateKind.OperatingSystem;
        if (value.Equals("Phone", StringComparison.OrdinalIgnoreCase) || value.Equals("Tablet", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Computer", StringComparison.OrdinalIgnoreCase) || value.Equals("Laptop", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Desktop", StringComparison.OrdinalIgnoreCase) || value.Equals("Printer", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Smart Device", StringComparison.OrdinalIgnoreCase) || value.Equals("Device", StringComparison.OrdinalIgnoreCase))
            return DeviceNameCandidateKind.GenericDeviceType;
        if (value.Equals("Apple", StringComparison.OrdinalIgnoreCase) || value.Equals("Samsung", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Espressif", StringComparison.OrdinalIgnoreCase) || value.Equals("Espressif Systems", StringComparison.OrdinalIgnoreCase))
            return DeviceNameCandidateKind.Manufacturer;
        if (value.Equals("AirPlay", StringComparison.OrdinalIgnoreCase) || value.Equals("Google Cast", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Workstation", StringComparison.OrdinalIgnoreCase) || value.Equals("HTTP", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Printer", StringComparison.OrdinalIgnoreCase) || value.Equals("Device Info", StringComparison.OrdinalIgnoreCase))
            return DeviceNameCandidateKind.ServiceType;
        if (IsInternalIdentifier(value)) return DeviceNameCandidateKind.InternalIdentifier;
        return DeviceNameCandidateKind.SpecificDeviceName;
    }

    public string? ResolveOperatingSystem(params string?[] candidates)
    {
        foreach (string? candidate in candidates)
        {
            if (TryClassifyOperatingSystem(candidate, out string? operatingSystem)) return operatingSystem;
        }
        return null;
    }

    private static bool TryClassifyOperatingSystem(string? candidate, out string? operatingSystem)
    {
        operatingSystem = null;
        string value = candidate?.Trim() ?? string.Empty;
        string[] platforms = { "Windows", "Windows 10", "Windows 11", "Android", "Android Phone", "iOS", "iPhone OS", "macOS", "Mac OS", "Linux", "OpenWrt", "Unix", "ChromeOS", "Chrome OS", "Tizen", "webOS" };
        string? match = platforms.FirstOrDefault(platform => value.Equals(platform, StringComparison.OrdinalIgnoreCase));
        if (match is null) return false;
        operatingSystem = match;
        return true;
    }

    private static bool IsInternalIdentifier(string value) =>
        value.All(char.IsDigit) && value.Length >= 8 ||
        value.StartsWith("mac:", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("ip:", StringComparison.OrdinalIgnoreCase);

    private static string? CleanDiscoveredName(string? value)
    {
        string? clean = value?.Trim().TrimEnd('.');
        if (clean is not null && clean.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            clean = clean[..^6];
        return clean;
    }

    private static bool IsUsefulManufacturer(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.Equals("Unknown manufacturer", StringComparison.OrdinalIgnoreCase) &&
        !value.Equals("-", StringComparison.Ordinal);

    private static bool IsUsefulName(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value != "-" &&
        !value.Equals("Unknown", StringComparison.OrdinalIgnoreCase) &&
        !value.Equals("Unknown device", StringComparison.OrdinalIgnoreCase);

    private static bool IsGeneratedIpLabel(string? name, string? ip)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (!IPAddress.TryParse(ClientIdentity.NormalizeEndpoint(ip), out IPAddress? address) ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;
        string digits = string.Concat(address.GetAddressBytes().Select(b => b.ToString(CultureInfo.InvariantCulture)));
        return string.Equals(name.Trim(), digits, StringComparison.Ordinal);
    }

    private static bool IsAsciiHex(char c) =>
        c is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f';

    private static string ResolveFromName(string? name)
    {
        string host = (name ?? string.Empty).ToLowerInvariant();
        if (host.Contains("iphone") || host.Contains("ipad") || host.Contains("macbook") || host.Contains("imac")) return "Apple";
        if (host.Contains("galaxy") || host.Contains("samsung")) return "Samsung";
        if (host.Contains("pixel") || host.Contains("chromecast") || host.Contains("google")) return "Google";
        if (host.Contains("raspberry")) return "Raspberry Pi";
        if (host.Contains("xbox")) return "Microsoft";
        if (host.Contains("playstation") || host.Contains("ps5") || host.Contains("ps4")) return "Sony";
        return "Unknown manufacturer";
    }
}
