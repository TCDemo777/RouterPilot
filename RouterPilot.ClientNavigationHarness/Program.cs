using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;
using RouterPilot.Services;

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

MethodInfo? hasUsableIp = typeof(RouterPilot.ViewModels.ClientsViewModel).GetMethod(
    "HasUsableClientIp", BindingFlags.Static | BindingFlags.NonPublic);
Require(hasUsableIp is not null, "Clients IP filter helper is available");
bool UsableIp(string? value) => (bool)hasUsableIp!.Invoke(null, new object?[] { value })!;
Require(UsableIp("192.168.1.103") && UsableIp("2001:db8::103"), "IP filter accepts IPv4 and IPv6");
Require(!UsableIp(null) && !UsableIp(string.Empty) && !UsableIp(" ") && !UsableIp("-") && !UsableIp("—") && !UsableIp("N/A"), "IP filter rejects unavailable values");
Require(!UsableIp("1921681103"), "IP filter rejects internal stripped-IP identity keys");
Require(UsableIp("[2001:db8::103]:53"), "IP filter accepts bracketed IPv6 endpoints");

var parsedDhcpLeases = DhcpLeaseParser.Parse("0 aa:bb:cc:dd:ee:ff 192.168.1.42 *\n");
Require(parsedDhcpLeases.Count == 1 && parsedDhcpLeases[0].IsStatic &&
    parsedDhcpLeases[0].Hostname == "Unknown device" && parsedDhcpLeases[0].IpAddress == "192.168.1.42",
    "DHCP lease parser preserves static and unknown-host semantics");

var parsedWifi = WifiDiscoveryParser.ParseConfiguredNetworks(
    "N|radio0|dev0|phy0|Home WiFi|5g|36|psk2|Online|lan|HE80\n" +
    "N|radio1|dev1|phy1||6g|auto|open|Configured|guest|\n");
Require(parsedWifi.Count == 2 && parsedWifi[0].Band == "5 GHz" &&
    parsedWifi[0].Security == "WPA2" && parsedWifi[0].ChannelWidth == "80 MHz" &&
    parsedWifi[1].Ssid == "Hidden network" && parsedWifi[1].Band == "6 GHz" &&
    parsedWifi[1].GuestClassification == WifiGuestClassification.VerifiedGuest,
    "Wi-Fi parser preserves configured radio normalization");
Require(WifiDiscoveryParser.ParseHostapdNetworks("L|phy0|wlan0|Home WiFi|2g|6|Online\n").Single().Band == "2.4 GHz" &&
    WifiDiscoveryParser.FormatSignal("-55") == "-55 dBm" &&
    WifiDiscoveryParser.InferBandFromChannel("11") == "2.4 GHz",
    "Wi-Fi parser preserves hostapd and signal/band transformations");

Require(AdGuardRecoveryPolicy.ShouldRetryTransport(new HttpRequestException(), false, false),
    "AdGuard transport recovery retries once");
Require(!AdGuardRecoveryPolicy.ShouldRetryTransport(new HttpRequestException(), false, true) &&
    !AdGuardRecoveryPolicy.ShouldRetryTransport(new HttpRequestException(), true, false),
    "AdGuard transport recovery does not retry repeatedly or after cancellation");
Require(AdGuardRuntimeStatusParser.IsRunning("service status unavailable", "1234 /usr/bin/AdGuardHome --no-check-update"),
    "AdGuard process probe establishes running state when init status is unavailable");
Require(!AdGuardRuntimeStatusParser.IsRunning("not running", ""),
    "AdGuard stopped state is not mistaken for running");
Require(AdGuardRuntimeStatusParser.IsRunning("running", ""),
    "AdGuard init status running state is preserved");
Require(ResumeRecoveryPolicy.Delays.Length == 3 &&
    ResumeRecoveryPolicy.Delays[0] < ResumeRecoveryPolicy.Delays[1] &&
    ResumeRecoveryPolicy.Delays[1] < ResumeRecoveryPolicy.Delays[2],
    "Resume recovery uses a bounded increasing retry sequence");
Require(ResumeRecoveryPolicy.IsRecovered(true, true) &&
    !ResumeRecoveryPolicy.IsRecovered(true, false) &&
    !ResumeRecoveryPolicy.IsRecovered(false, true),
    "Resume recovery requires both router and AdGuard availability");

RouterCapabilitySnapshot unknownCapabilities = RouterCapabilitySnapshot.Unknown;
Require(unknownCapabilities.Temperature == RouterCapabilityState.Unknown &&
    RouterCapabilitySnapshot.FromEvidence(true) == RouterCapabilityState.Supported &&
    RouterCapabilitySnapshot.FromEvidence(false) == RouterCapabilityState.Unknown,
    "Router capability model distinguishes supported evidence from unknown");

DateTime trafficTimestamp = new(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc);
NetworkTrafficSnapshot traffic = NetworkTrafficSnapshotParser.Parse(" eth1 | 123 | 456 ", trafficTimestamp);
Require(traffic.InterfaceName == "eth1" && traffic.ReceivedBytes == 123 &&
    traffic.TransmittedBytes == 456 && traffic.CapturedAtUtc == trafficTimestamp,
    "Network traffic parser preserves counter transformation");
NetworkTrafficSnapshot malformedTraffic = NetworkTrafficSnapshotParser.Parse("||not-a-number", trafficTimestamp);
Require(malformedTraffic.InterfaceName == "-" && malformedTraffic.ReceivedBytes == 0 &&
    malformedTraffic.TransmittedBytes == 0,
    "Network traffic parser preserves malformed fallback behavior");

IReadOnlyList<RouterPortSnapshot> ports = RouterPortTelemetryParser.Parse(
    "P|eth-wan|physical||1|2500|full|aa:bb:cc:dd:ee:ff|100|200|0|0|0|0|br-lan|||\n" +
    "P|br-lan|bridge||0|||aa:bb:cc:dd:ee:00||||||br-lan|||\n" +
    "P|lo|loopback||||||||||||||||\n");
Require(ports.Count == 3 && ports[0].InterfaceName == "eth-wan" &&
    ports[0].LinkState == "Connected" && ports[0].SpeedDisplay == "2.5 Gbps" &&
    ports[0].Duplex == "Full" && ports[0].ErrorsDisplay == "0 / 0" &&
    ports[1].LinkState == "Disconnected" && ports[1].SpeedDisplay == "—" &&
    ports[2].InterfaceType == RouterInterfaceType.Loopback,
    "Router port parser preserves link, speed, counters and interface classification");
Require(RouterPortTelemetryParser.Parse(
    "P|eth0|physical||1|bad|half|mac|bad|2|0|0|0|0||||\n" +
    "P|eth0|physical||1|1000|full|mac|1|2|0|0|0|0||||\n").Count == 1,
    "Router port parser tolerates malformed and duplicate interface records");

RouterMultiWanSnapshot multiWan = RouterMultiWanParser.Parse(
    "S|supported|1|failover|backup|primary\n" +
    "W|backup|Backup|repeater|wwan|wwan0|1|1|online|1|0|1|192.0.2.1|||2|2|1\n" +
    "W|primary|Primary|ethernet|wan|eth0|1|1|offline|0|1|0|198.51.100.1|198.51.100.2|||1|1|3\n" +
    "W|backup|Duplicate|ethernet|wan2|eth2|1|1|online|1|0|0||||||",
    DateTimeOffset.UtcNow);
Require(multiWan.Mode == RouterMultiWanMode.Failover && multiWan.WanPaths.Count == 2 &&
    multiWan.WanPaths[0].Id == "backup" && multiWan.WanPaths[0].RuntimeState == RouterWanRuntimeState.Online &&
    multiWan.WanPaths[1].ConnectionType == RouterWanConnectionType.Ethernet,
    "Multi-WAN parser preserves mode, status, types and deterministic ordering");
RouterMultiWanSnapshot ordinaryWan = RouterMultiWanParser.Parse(
    "S|unknown|unknown|unknown||\n" +
    "W|wan|Ethernet WAN|ethernet|wan|eth0|1|1|online|1|0|0|192.0.2.1|198.51.100.2||||",
    DateTimeOffset.UtcNow);
Require(ordinaryWan.WanPaths.Count == 1 &&
    ordinaryWan.CapabilityState == RouterCapabilityState.Unknown &&
    ordinaryWan.Mode == RouterMultiWanMode.Unknown,
    "Ordinary WAN telemetry does not imply Multi-WAN support or mode");
RouterMultiWanSnapshot supportedSingleWan = RouterMultiWanParser.Parse(
    "S|supported|0|unknown||\n" +
    "W|wan|Ethernet WAN|ethernet|wan|eth0|1|1|online|1|0|0||||||||",
    DateTimeOffset.UtcNow);
Require(supportedSingleWan.WanPaths.Count == 1 &&
    supportedSingleWan.CapabilityState == RouterCapabilityState.Supported,
    "Authoritatively capable platform may have one configured WAN");
RouterMultiWanSnapshot unsupportedMultiWan = RouterMultiWanParser.Parse(
    "S|unsupported|0|unknown||\n" +
    "W|wan|Ethernet WAN|ethernet|wan|eth0|1|1|online|1|0|0||||||||",
    DateTimeOffset.UtcNow);
Require(unsupportedMultiWan.CapabilityState == RouterCapabilityState.Unsupported,
    "Authoritative unsupported evidence remains Unsupported");
RouterMultiWanSnapshot twoWanWithoutEvidence = RouterMultiWanParser.Parse(
    "S|unknown|unknown|unknown||\n" +
    "W|wan-a|WAN A|ethernet|wan-a|eth0|1|1|online|1|1|0||||||||\n" +
    "W|wan-b|WAN B|ethernet|wan-b|eth1|1|1|online|1|0|0||||||||",
    DateTimeOffset.UtcNow);
Require(twoWanWithoutEvidence.WanPaths.Count == 2 &&
    twoWanWithoutEvidence.CapabilityState == RouterCapabilityState.Unknown &&
    twoWanWithoutEvidence.Mode == RouterMultiWanMode.Unknown,
    "Multiple WAN paths do not imply Multi-WAN capability or mode");
Require(RouterMultiWanParser.Parse(string.Empty, DateTimeOffset.UtcNow).CapabilityState == RouterCapabilityState.Unknown,
    "Multi-WAN empty probe remains unknown");

RouterDnsSnapshot dns = RouterDnsParser.Parse(
    "S|supported|dnsmasq|automatic|running|doh|unknown|unknown\n" +
    "U| 1.1.1.1\nU|https://user:secret@dns.example.test/path?token=redacted\nU|1.1.1.1\n",
    DateTimeOffset.UtcNow);
Require(dns.CapabilityState == RouterCapabilityState.Supported &&
    dns.ServiceName == "dnsmasq" &&
    dns.Mode == RouterDnsMode.Automatic && dns.RuntimeState == RouterDnsRuntimeState.Running &&
    dns.EncryptionMode == RouterDnsEncryptionMode.DoH && dns.UpstreamResolvers.Count == 2 &&
    dns.UpstreamResolvers.All(value => !value.Contains("secret", StringComparison.OrdinalIgnoreCase) &&
        !value.Contains("token", StringComparison.OrdinalIgnoreCase)),
    "Router DNS parser preserves safe configuration and redacts resolver credentials");
RouterDnsSnapshot unknownDns = RouterDnsParser.Parse(string.Empty, DateTimeOffset.UtcNow);
Require(unknownDns.CapabilityState == RouterCapabilityState.Unknown &&
    unknownDns.Mode == RouterDnsMode.Unknown && unknownDns.UpstreamResolvers.Count == 0,
    "Router DNS probe failure remains unknown");

// Shared identity resolver: strict EUI-48 parsing, address classification,
// consistent vendor precedence, and safe handling of non-MAC identifiers.
IDeviceIdentityResolver identityResolver = new DeviceIdentityResolver();
foreach (string macForm in new[] { "00:BB:CC:DD:EE:FF", "00-BB-CC-DD-EE-FF", "00bb.ccdd.eeff", "00BBCCDDEEFF", "00bbccddeeff" })
{
    Require(identityResolver.TryParseMac(macForm, out ParsedMacAddress? parsed) &&
        parsed is not null && parsed.Canonical == "00BBCCDDEEFF" && parsed.Kind == MacAddressKind.Universal,
        $"strict MAC parser accepts {macForm}");
}
foreach (string invalidMac in new[] { "192.168.1.1", "2001:db8::1", "1921681103", "living-room", "AA:BB:CC:DD:EE" })
    Require(!identityResolver.TryParseMac(invalidMac, out _), $"strict MAC parser rejects {invalidMac}");
Require(identityResolver.ResolveManufacturer("00:1B:63:DD:EE:FF") == "Apple", "existing vendor mapping preserved");
Require(identityResolver.ResolveManufacturer("02:1B:63:DD:EE:FF") == "Private/local MAC", "locally administered MAC is classified factually");
Require(identityResolver.ResolveManufacturer("01:1B:63:DD:EE:FF") == "Unknown manufacturer", "multicast MAC has no IEEE attribution");
Require(identityResolver.ResolveManufacturer("00:BB:CC:DD:EE:FF", "Living Room TV") == "Unknown manufacturer", "unknown vendor does not fabricate from friendly name");
Require(identityResolver.ResolveManufacturer("00:BB:CC:DD:EE:FF", authoritativeManufacturer: "Trusted Vendor") == "Trusted Vendor", "trusted manufacturer takes precedence");
Require(identityResolver.ResolveManufacturer("00:1B:63:DD:EE:FF") == "Apple", "duplicate vendor lookup reuses consistent result");
Require(identityResolver.ResolveFriendlyName(new DeviceIdentitySignals("My iPhone", "router-name", "dhcp-name", "mdns-name", "adguard-name", "saved-name", "192.168.1.20")) == "My iPhone", "personalised name wins");
Require(identityResolver.ResolveFriendlyName(new DeviceIdentitySignals(null, "router-name", "dhcp-name", "mdns-name", "adguard-name", "saved-name", "192.168.1.20")) == "router-name", "router name wins over lower-priority sources");
Require(identityResolver.ResolveFriendlyName(new DeviceIdentitySignals(null, "-", "dhcp-name", "mdns-name", "adguard-name", "saved-name", "192.168.1.20")) == "dhcp-name", "DHCP name fills missing router name");
Require(identityResolver.ResolveFriendlyName(new DeviceIdentitySignals(null, "-", null, "Living-Room-TV.local", "adguard-name", "saved-name", "192.168.1.20")) == "Living-Room-TV", "mDNS name is normalized");
Require(identityResolver.ResolveFriendlyName(new DeviceIdentitySignals(null, "-", null, null, "adguard-name", "saved-name", "192.168.1.20")) == "adguard-name", "AdGuard name is used only as a correlated fallback");
Require(identityResolver.ResolveFriendlyName(new DeviceIdentitySignals(null, "-", null, null, null, "saved-name", "192.168.1.20")) == "saved-name", "persisted name is retained");
Require(identityResolver.ResolveFriendlyName(new DeviceIdentitySignals(null, "1921681103", null, null, null, null, "192.168.1.103")) == "Unknown device", "generated IP identity does not become a friendly name");
Require(identityResolver.ResolveFriendlyName(new DeviceIdentitySignals(null, "Windows", null, null, null, null, "192.168.1.20")) == "Unknown device", "operating system is not promoted to device name");
Require(identityResolver.ResolveFriendlyName(null, "Windows", null, "192.168.1.20") == "Unknown device", "legacy resolver overload also rejects operating-system names");
Require(identityResolver.ClassifyDeviceNameCandidate("192.168.1.20") == DeviceNameCandidateKind.IpAddress, "raw IP is not a device name");
Require(identityResolver.ClassifyDeviceNameCandidate("AA:BB:CC:DD:EE:FF") == DeviceNameCandidateKind.MacAddress, "raw MAC is not a device name");
Require(identityResolver.ResolveFriendlyName(new DeviceIdentitySignals(null, "DESKTOP-A1B2C3", null, null, null, null, "192.168.1.20")) == "DESKTOP-A1B2C3", "specific machine hostname is retained");
Require(identityResolver.ResolveFriendlyName(new DeviceIdentitySignals(null, "-", null, "AirPlay", null, null, "192.168.1.20")) == "Unknown device", "service type is not promoted to device name");
Require(identityResolver.ClassifyDeviceNameCandidate("Android") == DeviceNameCandidateKind.OperatingSystem, "Android is classified as operating system");
Require(identityResolver.ResolveOperatingSystem("Windows") == "Windows", "operating system remains available separately");
Require(identityResolver.ResolveFriendlyName(new DeviceIdentitySignals("Windows", "Android", null, null, null, null, "192.168.1.20")) == "Windows", "explicit user nickname is preserved");
ClientInfo unavailableDnsClient = new() { AdGuardDataAvailability = AdGuardAvailabilityState.Unavailable, TotalQueries = 17, BlockedQueries = 4 };
Require(unavailableDnsClient.TotalQueriesDisplay == RouterPilotStatusPresentation.NotAvailable &&
    unavailableDnsClient.BlockedQueriesDisplay == RouterPilotStatusPresentation.NotAvailable &&
    unavailableDnsClient.BlockRateDisplay == RouterPilotStatusPresentation.NotAvailable,
    "unavailable DNS metrics never become numeric zeros");
Require(unavailableDnsClient.ActivityAvailabilityToolTip.Contains("policy-based VPN", StringComparison.OrdinalIgnoreCase) &&
    unavailableDnsClient.ActivityAvailabilityToolTip.Contains("control DNS", StringComparison.OrdinalIgnoreCase), "DNS unavailable tooltip explains policy-based VPN configuration");
// DNS observability regression: global AdGuard availability does not imply
// per-client attribution. The same live client must transition from
// unavailable -> correlated activity/zero without being replaced or duplicated.
ClientInfo bypassedClient = new()
{
    Name = "Office laptop",
    Manufacturer = "Dell",
    MacAddress = "00:24:E8:AA:BB:CC",
    IpAddress = "192.168.1.42",
    AdGuardDataAvailability = AdGuardAvailabilityState.Unavailable
};
List<ClientInfo> visibleClients = [bypassedClient];
Require(visibleClients.Count == 1 &&
    bypassedClient.TotalQueriesDisplay == RouterPilotStatusPresentation.NotAvailable &&
    bypassedClient.BlockedQueriesDisplay == RouterPilotStatusPresentation.NotAvailable &&
    bypassedClient.BlockRateDisplay == RouterPilotStatusPresentation.NotAvailable,
    "AdGuard-available-but-unmatched client remains visible with unavailable DNS metrics");
bypassedClient.AdGuardDataAvailability = AdGuardAvailabilityState.Available;
bypassedClient.TotalQueries = 12;
bypassedClient.BlockedQueries = 3;
Require(visibleClients.Count == 1 && bypassedClient.TotalQueriesDisplay == "12" &&
    bypassedClient.BlockedQueriesDisplay == "3" && bypassedClient.BlockRateDisplay == "25.0%" &&
    bypassedClient.Name == "Office laptop" && bypassedClient.Manufacturer == "Dell",
    "later AdGuard correlation updates the existing client without duplication or identity loss");
bypassedClient.TotalQueries = 0;
bypassedClient.BlockedQueries = 0;
Require(bypassedClient.TotalQueriesDisplay == "0" && bypassedClient.BlockedQueriesDisplay == "0" &&
    bypassedClient.BlockRateDisplay == "0.0%",
    "correlated genuine zero activity remains distinct from unavailable DNS");
MethodInfo? cleanMdns = typeof(MdnsIdentityService).GetMethod("CleanHostnameForDisplay", BindingFlags.Static | BindingFlags.NonPublic);
Require(cleanMdns is not null, "mDNS hostname cleanup helper is available");
string? CleanMdns(string value) => (string?)cleanMdns!.Invoke(null, new object?[] { value });
Require(CleanMdns("Aaron-iPhone.local.") == "Aaron-iPhone", "mDNS local suffix and trailing dot are removed");
Require(CleanMdns("localhost") is null && CleanMdns("192.168.1.10") is null, "unusable mDNS names are rejected");
var onlineStub = new StubMacLookupHandler();
var onlineResolver = new DeviceIdentityResolver(new HttpClient(onlineStub));
Require(await onlineResolver.ResolveManufacturerAsync("00:BB:CC:DD:EE:FF") == "Example Vendor", "online MACLookup result is used");
Require(await onlineResolver.ResolveManufacturerAsync("00:BB:CC:11:22:33") == "Example Vendor" && onlineStub.RequestCount == 1, "same prefix uses the online cache and request de-duplication");
var fallbackStub = new StubMacLookupHandler { StatusCode = HttpStatusCode.InternalServerError };
var fallbackResolver = new DeviceIdentityResolver(new HttpClient(fallbackStub));
Require(await fallbackResolver.ResolveManufacturerAsync("00:1B:63:DD:EE:FF") == "Apple", "HTTP failure falls back to local vendor data");
var privateStub = new StubMacLookupHandler();
var privateResolver = new DeviceIdentityResolver(new HttpClient(privateStub));
Require(await privateResolver.ResolveManufacturerAsync("02:1B:63:DD:EE:FF") == "Private/local MAC" && privateStub.RequestCount == 0, "private MAC skips online lookup");
var offlineKnown = new KnownDeviceInfo
{
    Profile = new ClientProfile { Key = "001B63DDEEFF", LastKnownName = "Offline laptop" },
    IdentityResolver = identityResolver
};
Require(offlineKnown.Manufacturer == "Apple" && offlineKnown.ToClientInfo().Manufacturer == "Apple", "offline known client resolves persisted MAC manufacturer");
MethodInfo? isUnknownName = typeof(RouterPilot.ViewModels.ClientsViewModel).GetMethod(
    "IsUnknownDeviceName", BindingFlags.Static | BindingFlags.NonPublic);
Require(isUnknownName is not null, "Known-device name filter helper is available");
bool UnknownName(string? value) => (bool)isUnknownName!.Invoke(null, new object?[] { value })!;
Require(UnknownName("Unknown device") && UnknownName("Unknown") && UnknownName("—"), "unknown device display states are filterable");
Require(!UnknownName("Living Room TV") && !UnknownName("Unknown manufacturer"), "friendly names remain visible despite unknown metadata");
MethodInfo? isOnline = typeof(RouterPilot.ViewModels.ClientsViewModel).GetMethod(
    "IsOnlineStatus", BindingFlags.Static | BindingFlags.NonPublic);
Require(isOnline is not null, "online status presentation helper is available");
bool Online(string value) => (bool)isOnline!.Invoke(null, new object?[] { value })!;
Require(Online("Online") && Online("Active") && Online("Recently active"), "live status values are online");
Require(!Online("Offline") && !Online("Unknown"), "offline and unknown status values are not online");
Require(Online("Online"), "online classification is independent of manufacturer lookup");

static ClientInfo Client(string mac, string name, string ip) => new()
{
    MacAddress = mac,
    Name = name,
    RouterName = name,
    IpAddress = ip
};

static ClientProfile Profile(string mac, string name) => new()
{
    Key = ClientIdentity.NormalizeMac(mac),
    Nickname = name
};

const string targetMac = "AA:BB:CC:DD:EE:01";
ClientInfo target = Client(targetMac, "Office laptop", "192.168.8.31");

async Task<ClientDetailsNavigationTarget?> ResolveColdAsync(
    ClientInventoryState inventory,
    ClientInventoryCoordinator coordinator,
    IReadOnlyDictionary<string, ClientProfile>? profiles = null,
    string identity = targetMac) =>
    await ClientDetailsNavigationPreparation.ResolveAsync(
        identity,
        inventory,
        coordinator,
        profiles ?? new Dictionary<string, ClientProfile>(StringComparer.OrdinalIgnoreCase));

foreach (string source in new[] { "ColdAnalyticsDeepLink", "ColdNetworkDeepLink" })
{
    var inventory = new ClientInventoryState();
    int reconciliationCount = 0;
    var coordinator = new ClientInventoryCoordinator(inventory, async _ =>
    {
        reconciliationCount++;
        await Task.Yield();
        return new[] { target };
    });

    ClientDetailsNavigationTarget? result = await ResolveColdAsync(inventory, coordinator);
    Require(reconciliationCount == 1, $"{source} did not perform exactly one shared reconciliation.");
    Require(ReferenceEquals(result?.LiveClient, target), $"{source} did not return the authoritative client object.");
}

var wiredInventory = new ClientInventoryState();
ClientInfo wired = Client("AA:BB:CC:DD:EE:02", "Desk switch", "192.168.8.42");
var wiredCoordinator = new ClientInventoryCoordinator(wiredInventory, _ =>
    Task.FromResult<IReadOnlyList<ClientInfo>>(new[] { wired }));
ClientDetailsNavigationTarget? wiredResult = await ResolveColdAsync(
    wiredInventory, wiredCoordinator, identity: wired.MacAddress);
Require(ReferenceEquals(wiredResult?.LiveClient, wired), "Wired inventory-only client was not resolved.");

const string profileMac = "AA:BB:CC:DD:EE:03";
var profileInventory = new ClientInventoryState();
int profileLoadCount = 0;
var profileCoordinator = new ClientInventoryCoordinator(profileInventory, _ =>
{
    profileLoadCount++;
    return Task.FromResult<IReadOnlyList<ClientInfo>>(Array.Empty<ClientInfo>());
});
var profiles = new Dictionary<string, ClientProfile>(StringComparer.OrdinalIgnoreCase)
{
    [ClientIdentity.NormalizeMac(profileMac)] = Profile(profileMac, "Offline camera")
};
ClientDetailsNavigationTarget? profileResult = await ResolveColdAsync(
    profileInventory, profileCoordinator, profiles, profileMac);
Require(profileResult?.Profile is not null && profileResult.LiveClient is null, "Profile-only client did not use the offline target.");
Require(profileLoadCount == 1, "Cold profile navigation did not perform the shared reconciliation before using the offline target.");

var profiledLiveInventory = new ClientInventoryState();
var profiledLiveCoordinator = new ClientInventoryCoordinator(profiledLiveInventory, _ =>
    Task.FromResult<IReadOnlyList<ClientInfo>>(new[] { target }));
var savedTargetProfile = new Dictionary<string, ClientProfile>(StringComparer.OrdinalIgnoreCase)
{
    [ClientIdentity.NormalizeMac(targetMac)] = Profile(targetMac, "Saved office laptop")
};
ClientDetailsNavigationTarget? profiledLiveResult = await ResolveColdAsync(
    profiledLiveInventory, profiledLiveCoordinator, savedTargetProfile);
Require(ReferenceEquals(profiledLiveResult?.LiveClient, target), "Cold navigation did not replace a saved profile projection with the current live client.");

var unknownInventory = new ClientInventoryState();
var unknownCoordinator = new ClientInventoryCoordinator(unknownInventory, _ =>
    Task.FromResult<IReadOnlyList<ClientInfo>>(Array.Empty<ClientInfo>()));
Require(await ResolveColdAsync(unknownInventory, unknownCoordinator, identity: "AA:BB:CC:DD:EE:99") is null,
    "Unknown MAC produced a navigation target.");

var identityInventory = new ClientInventoryState();
ClientInfo sameNameA = Client("AA:BB:CC:DD:EE:04", "Shared name", "192.168.8.50");
ClientInfo sameNameB = Client("AA:BB:CC:DD:EE:05", "Shared name", "192.168.8.51");
identityInventory.Update(new[] { sameNameA, sameNameB });
var identityCoordinator = new ClientInventoryCoordinator(identityInventory, _ =>
    Task.FromResult<IReadOnlyList<ClientInfo>>(new[] { sameNameA, sameNameB }));
ClientDetailsNavigationTarget? normalized = await ResolveColdAsync(
    identityInventory, identityCoordinator, identity: "aa:bb:cc:dd:ee:04");
Require(ReferenceEquals(normalized?.LiveClient, sameNameA), "MAC normalization or duplicate-name resolution selected the wrong client.");
Require(normalized?.LiveClient?.IpAddress == "192.168.8.50", "A stale or unrelated IP changed MAC-backed resolution.");
Require(normalized?.LiveClient is ClientInfo normalizedClient && normalizedClient.Name == sameNameA.Name && normalizedClient.RouterName == sameNameA.RouterName,
    "Known Device navigation did not preserve the authoritative current-client record.");

var warmInventory = new ClientInventoryState();
int warmLoadCount = 0;
var warmCoordinator = new ClientInventoryCoordinator(warmInventory, _ =>
{
    warmLoadCount++;
    return Task.FromResult<IReadOnlyList<ClientInfo>>(new[] { target });
});
ClientDetailsNavigationTarget? cold = await ResolveColdAsync(warmInventory, warmCoordinator);
ClientDetailsNavigationTarget? warm = await ResolveColdAsync(warmInventory, warmCoordinator);
Require(ReferenceEquals(cold?.LiveClient, warm?.LiveClient) && warmLoadCount == 1,
    "Cold and warm deep links did not reuse the same authoritative client state.");

var concurrentInventory = new ClientInventoryState();
int concurrentLoadCount = 0;
var concurrentCoordinator = new ClientInventoryCoordinator(concurrentInventory, async _ =>
{
    Interlocked.Increment(ref concurrentLoadCount);
    await Task.Delay(25);
    return new[] { target };
});
ClientDetailsNavigationTarget?[] concurrent = await Task.WhenAll(
    ResolveColdAsync(concurrentInventory, concurrentCoordinator),
    ResolveColdAsync(concurrentInventory, concurrentCoordinator));
Require(concurrentLoadCount == 1 && concurrent.All(result => ReferenceEquals(result?.LiveClient, target)),
    "Concurrent deep links did not coalesce authoritative reconciliation.");

Console.WriteLine("Client Details deep-link regression fixtures passed: 8/8.");

sealed class StubMacLookupHandler : HttpMessageHandler
{
    public int RequestCount { get; private set; }
    public HttpStatusCode StatusCode { get; init; } = HttpStatusCode.OK;
    public string Body { get; init; } = "{\"success\":true,\"found\":true,\"company\":\"Example Vendor\"}";
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        return Task.FromResult(new HttpResponseMessage(StatusCode)
        {
            Content = new StringContent(Body, System.Text.Encoding.UTF8, "application/json")
        });
    }
}
