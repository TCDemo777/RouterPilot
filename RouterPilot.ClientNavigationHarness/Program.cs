using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;
using RouterPilot.Services;
using RouterPilot.ViewModels;

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

Require(!AdGuardCredentialTransportPolicy.RequiresHttpAcknowledgement(useRouterCredentials: true, useHttps: false),
    "router credential HTTP compatibility does not require the dedicated-credential acknowledgement");
Require(!AdGuardCredentialTransportPolicy.RequiresHttpAcknowledgement(useRouterCredentials: false, useHttps: true),
    "dedicated HTTPS credentials do not require the HTTP acknowledgement");
Require(AdGuardCredentialTransportPolicy.RequiresHttpAcknowledgement(useRouterCredentials: false, useHttps: false),
    "dedicated HTTP credentials require an explicit acknowledgement");
Require(!AdGuardCredentialTransportPolicy.IsAcknowledgementValid(false, useRouterCredentials: false, useHttps: false),
    "unacknowledged dedicated HTTP credentials remain blocked");
Require(AdGuardCredentialTransportPolicy.IsAcknowledgementValid(true, useRouterCredentials: false, useHttps: false),
    "acknowledged dedicated HTTP credentials may proceed");
Require(!AdGuardHttpClientSecurityPolicy.AllowAutoRedirect,
    "authenticated AdGuard clients do not automatically follow redirects");

string routerLogFixture = string.Join('\n', Enumerable.Range(0, 130)
    .Select(index => $"<6>Sat router log entry {index}"));
IReadOnlyList<RouterLogEntry> boundedRouterLogs = RouterLogParser.Parse(
    routerLogFixture,
    RouterLogsViewModel.RecentLogCapacity);
Require(boundedRouterLogs.Count == RouterLogsViewModel.RecentLogCapacity,
    "router logs retain the configured bounded capacity");
Require(boundedRouterLogs.First().Message == "router log entry 30" &&
        boundedRouterLogs.Last().Message == "router log entry 129",
    "router logs retain the newest entries while preserving source order before newest-first projection");
Require(boundedRouterLogs.Select(entry => entry.Message).Distinct().Count() == RouterLogsViewModel.RecentLogCapacity,
    "router logs do not duplicate entries while applying the bound");

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
    parsedDhcpLeases[0].Hostname == "—" && parsedDhcpLeases[0].IpAddress == "192.168.1.42",
    "DHCP lease parser preserves static and unavailable-host semantics");

MethodInfo? parseDhcpConfiguration = typeof(RouterManager).GetMethod("ParseDhcpConfiguration", BindingFlags.Static | BindingFlags.NonPublic);
Require(parseDhcpConfiguration is not null, "DHCP UCI configuration parser is available");
var parsedConfiguration = ((ValueTuple<List<DhcpConfigurationInfo>, List<DhcpReservationInfo>>)parseDhcpConfiguration!.Invoke(null,
    ["dhcp.@host[0]=host\ndhcp.@host[0].mac='AA-BB-CC-DD-EE-01'\ndhcp.@host[0].ip='192.168.1.20'\ndhcp.@host[0].tag='Office Printer'\ndhcp.@host[0].name='unrelated.example'\n"])!);
Require(parsedConfiguration.Item2.Count == 1 && parsedConfiguration.Item2[0].ConfiguredName == "Office Printer" && parsedConfiguration.Item2[0].Hostname == "Office Printer",
    "GL.iNet DHCP host tag is the configured reservation display name, not name");

var configuredLease = new DhcpLeaseInfo { MacAddress = "aa:bb:cc:dd:ee:01", IpAddress = "192.168.1.20", Hostname = "HP1234", ClientName = "HP1234", IsStatic = true };
DhcpConfiguredNameResolver.Apply([configuredLease], [new DhcpReservationInfo { MacAddress = "AA-BB-CC-DD-EE-01", IpAddress = "192.168.1.20", ConfiguredName = "Office Printer", Hostname = "Office Printer" }]);
Require(configuredLease.ConfiguredName == "Office Printer" && configuredLease.ClientName == "Office Printer", "configured UCI name wins over observed DHCP hostname with normalized MAC");
var observedOnlyLease = new DhcpLeaseInfo { MacAddress = "AA:BB:CC:DD:EE:02", IpAddress = "192.168.1.21", Hostname = "NAS", ClientName = "NAS", IsStatic = true };
DhcpConfiguredNameResolver.Apply([observedOnlyLease], [new DhcpReservationInfo { MacAddress = "AA:BB:CC:DD:EE:02", IpAddress = "192.168.1.21", Hostname = "—" }]);
Require(observedOnlyLease.ConfiguredName is null && observedOnlyLease.ClientName == "NAS", "observed hostname remains the fallback without a configured name");
var unavailableNameLease = new DhcpLeaseInfo { MacAddress = "AA:BB:CC:DD:EE:03", IpAddress = "192.168.1.22", Hostname = "—", ClientName = "—", IsStatic = true };
DhcpConfiguredNameResolver.Apply([unavailableNameLease], Array.Empty<DhcpReservationInfo>());
Require(unavailableNameLease.ClientName == "—", "missing configured and observed names display an em dash");
var duplicateLease = new DhcpLeaseInfo { MacAddress = "AA:BB:CC:DD:EE:04", IpAddress = "192.168.1.23", Hostname = "Observed", ClientName = "Observed", IsStatic = true };
DhcpConfiguredNameResolver.Apply([duplicateLease], [new DhcpReservationInfo { MacAddress = "AA:BB:CC:DD:EE:04", IpAddress = "192.168.1.23", ConfiguredName = "First" }, new DhcpReservationInfo { MacAddress = "aa-bb-cc-dd-ee-04", IpAddress = "192.168.1.23", ConfiguredName = "Second" }]);
Require(duplicateLease.ConfiguredName is null && duplicateLease.ClientName == "Observed", "duplicate reservation identities are not guessed");

var routerNames = ClientConfiguredNameResolver.RouterByMac([
    new DhcpReservationInfo { MacAddress = "aa:bb:cc:dd:ee:ff", IpAddress = "192.168.1.20", ConfiguredName = "Office Printer" }]);
Require(routerNames.TryGetValue("AABBCCDDEEFF", out string? routerConfigured) && routerConfigured == "Office Printer", "router configured names normalize MAC identities");
var adGuardNames = ClientConfiguredNameResolver.AdGuardByIdentity([
    new ClientInfo { Name = "Lounge Television", MacAddress = "AA:BB:CC:DD:EE:FF", IsConfiguredAdGuardClient = true },
    new ClientInfo { Name = "IP-only client", IpAddress = "192.168.1.25", IsConfiguredAdGuardClient = true },
    new ClientInfo { Name = "CIDR must not match", IpAddress = "192.168.1.0/24", IsConfiguredAdGuardClient = true }]);
Require(adGuardNames.ByMac.TryGetValue("AABBCCDDEEFF", out string? adGuardConfigured) && adGuardConfigured == "Lounge Television" &&
    adGuardNames.ByIp.TryGetValue("192.168.1.25", out string? ipConfigured) && ipConfigured == "IP-only client" && !adGuardNames.ByIp.ContainsKey("192.168.1.0/24"),
    "configured AdGuard names use only exact MAC or IP identities, never CIDR");
Require(ClientNamePresentation.Resolve(ClientNameSource.Automatic, "LGwebOSTV", "Living Room TV", "Lounge Television") == "LGwebOSTV" &&
    ClientNamePresentation.Resolve(ClientNameSource.Router, "LGwebOSTV", "Living Room TV", "Lounge Television") == "Living Room TV" &&
    ClientNamePresentation.Resolve(ClientNameSource.AdGuard, "LGwebOSTV", "Living Room TV", "Lounge Television") == "Lounge Television" &&
    ClientNamePresentation.Resolve(ClientNameSource.AdGuard, "LGwebOSTV", "Living Room TV", null) == "LGwebOSTV" &&
    ClientNamePresentation.Resolve(ClientNameSource.ConfiguredNames, "LGwebOSTV", "Living Room TV", "Lounge Television") == "Living Room TV" &&
    ClientNamePresentation.Resolve(ClientNameSource.ConfiguredNames, "LGwebOSTV", null, "Lounge Television") == "Lounge Television" &&
    ClientNamePresentation.Resolve(ClientNameSource.ConfiguredNames, "LGwebOSTV", " ", " ") == "LGwebOSTV",
    "naming preference selects configured source with per-client fallback");
Require(ClientNamePresentation.ResolveSource(ClientNameSource.Router, "Living Room TV", "Lounge Television") == "Router" &&
    ClientNamePresentation.ResolveSource(ClientNameSource.ConfiguredNames, "", "Lounge Television") == "AdGuard" &&
    ClientNamePresentation.ResolveSource(ClientNameSource.AdGuard, "Living Room TV", "") == "RouterPilot" &&
    ClientNamePresentation.ResolveSource(ClientNameSource.Automatic, "Living Room TV", "Lounge Television") == "RouterPilot",
    "name-source presentation follows the same configured-name precedence");
string clientsViewSource = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "RouterPilot", "Views", "ClientsView.xaml"));
int nameSourceStart = clientsViewSource.IndexOf("Text=\"{Binding NameSource, StringFormat=Name from: {0}}\"", StringComparison.Ordinal);
int nameSourceEnd = clientsViewSource.IndexOf("</TextBlock>", nameSourceStart, StringComparison.Ordinal);
string nameSourcePresentation = nameSourceStart >= 0 && nameSourceEnd > nameSourceStart
    ? clientsViewSource[nameSourceStart..nameSourceEnd]
    : string.Empty;
Require(nameSourcePresentation.Contains("DataTrigger Binding=\"{Binding NameSource}\" Value=\"Unknown\"", StringComparison.Ordinal) &&
        nameSourcePresentation.Contains("DataTrigger Binding=\"{Binding NameSource}\" Value=\"\"", StringComparison.Ordinal) &&
        !nameSourcePresentation.Contains("<Button", StringComparison.Ordinal) &&
        !nameSourcePresentation.Contains("Click=", StringComparison.Ordinal),
    "Clients cards present the accepted name source textually, omit no-useful-provenance fallback, and add no interaction");
Require(new AppSettings().ClientNameSource == ClientNameSource.Automatic, "missing persisted name-source setting defaults to Automatic");
MethodInfo? flightDeckClick = typeof(RouterPilot.Views.AboutView).GetMethod(
    "IsFlightDeckActivationClick",
    BindingFlags.Static | BindingFlags.NonPublic);
Require(flightDeckClick is not null, "About Flight Deck activation contract is available");
int flightDeckClicks = 0;
DateTime flightDeckWindow = default;
DateTime flightDeckNow = new(2026, 9, 9, 20, 0, 0, DateTimeKind.Utc);
for (int click = 1; click <= 6; click++)
{
    object?[] arguments = [flightDeckClicks, flightDeckWindow, flightDeckNow];
    Require(!(bool)flightDeckClick!.Invoke(null, arguments)!, $"Flight Deck remains inactive after click {click}");
    flightDeckClicks = (int)arguments[0]!;
    flightDeckWindow = (DateTime)arguments[1]!;
}
object?[] seventhClick = [flightDeckClicks, flightDeckWindow, flightDeckNow];
Require((bool)flightDeckClick!.Invoke(null, seventhClick)!, "Flight Deck activates on the seventh click");
object?[] expiredWindowClick = [(int)seventhClick[0]!, (DateTime)seventhClick[1]!, flightDeckNow.AddSeconds(4)];
Require(!(bool)flightDeckClick!.Invoke(null, expiredWindowClick)!, "Flight Deck click window resets after three seconds");
var sharedHttpHandler = new StubMacLookupHandler();
using var sharedHttpClient = new HttpClient(sharedHttpHandler);
await sharedHttpClient.GetAsync("https://example.invalid/first-request");
var resolverAfterSharedRequest = new DeviceIdentityResolver(sharedHttpClient);
Require(await resolverAfterSharedRequest.ResolveManufacturerAsync("00:11:22:33:44:55") == "Example Vendor" && sharedHttpHandler.RequestCount == 2,
    "client identity resolver does not mutate an injected HttpClient after its first request");
MethodInfo? parseAdGuardClients = typeof(RouterManager).GetMethod("ParseAdGuardClients", BindingFlags.Static | BindingFlags.NonPublic);
Require(parseAdGuardClients is not null, "AdGuard clients parser is available");
var parsedAdGuard = (List<ClientInfo>)parseAdGuardClients!.Invoke(null,
    ["{\"clients\":[{\"name\":\"Lounge Television\",\"ids\":[\"192.168.1.25\",\"192.168.1.0/24\"]}],\"auto_clients\":[{\"name\":\"Runtime name\",\"ip\":\"192.168.1.26\"}]}"])!;
Require(parsedAdGuard.Any(item => item.Name == "Lounge Television" && item.IsConfiguredAdGuardClient) &&
    parsedAdGuard.Any(item => item.Name == "Runtime name" && !item.IsConfiguredAdGuardClient),
    "AdGuard configured clients are distinguished from automatic runtime clients");

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
Require(ResumeRecoveryPolicy.Delays.Length == 6 &&
    ResumeRecoveryPolicy.Delays[0] < ResumeRecoveryPolicy.Delays[1] &&
    ResumeRecoveryPolicy.Delays[1] < ResumeRecoveryPolicy.Delays[2] &&
    ResumeRecoveryPolicy.Delays.Aggregate(TimeSpan.Zero, (total, delay) => total + delay) >= TimeSpan.FromSeconds(110) &&
    ResumeRecoveryPolicy.Delays.Aggregate(TimeSpan.Zero, (total, delay) => total + delay) < ResumeRecoveryPolicy.MaximumRecoveryWindow,
    "Resume recovery uses bounded attempts throughout the existing re-establishment window");
Require(ResumeRecoveryPolicy.IsRecovered(true, true) &&
    !ResumeRecoveryPolicy.IsRecovered(true, false) &&
    !ResumeRecoveryPolicy.IsRecovered(false, true),
    "Resume recovery requires both router and AdGuard availability");
Require(ResumeRecoveryPolicy.ShouldContinue(AdGuardAvailabilityState.Available) &&
    ResumeRecoveryPolicy.ShouldContinue(AdGuardAvailabilityState.Unavailable) &&
    !ResumeRecoveryPolicy.ShouldContinue(AdGuardAvailabilityState.NotConfigured),
    "Resume recovery retries transient unavailability but not an unconfigured AdGuard Home");
Require(ResumeRecoveryPolicy.MaximumRecoveryWindow == TimeSpan.FromMinutes(2),
    "resume recovery remains bounded by the existing freshness re-establishment window");

await using (var coordinator = new RefreshCoordinator())
{
    const string recoveryTask = "resume-test";
    var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    int calls = 0;
    int active = 0;
    int maximumActive = 0;
    coordinator.Register(recoveryTask, TimeSpan.FromMinutes(1), async cancellationToken =>
    {
        int current = Interlocked.Increment(ref active);
        maximumActive = Math.Max(maximumActive, current);
        try
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstEntered.SetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
            }
        }
        finally
        {
            Interlocked.Decrement(ref active);
        }
    }, enabled: false);

    Task<bool> firstRefresh = coordinator.RunNowAsync(recoveryTask);
    await firstEntered.Task;
    Require(!await coordinator.RunNowAsync(recoveryTask),
        "ordinary non-blocking refresh reports a busy scheduler slot");
    Task<bool> recoveryRefresh = coordinator.RunWhenAvailableAsync(recoveryTask);
    Require(!recoveryRefresh.IsCompleted,
        "resume recovery waits for a cancelled pre-suspend refresh instead of spending an attempt");
    releaseFirst.SetResult();
    Require(await firstRefresh && await recoveryRefresh && calls == 2 && maximumActive == 1,
        "resume recovery executes the same callback once the scheduler slot is available without overlap");
}

await using (var coordinator = new RefreshCoordinator())
{
    const string cancellationTask = "resume-cancellation-test";
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    int calls = 0;
    coordinator.Register(cancellationTask, TimeSpan.FromMinutes(1), async cancellationToken =>
    {
        if (Interlocked.Increment(ref calls) == 1)
        {
            entered.SetResult();
            await release.Task.WaitAsync(cancellationToken);
        }
    }, enabled: false);
    Task<bool> activeRefresh = coordinator.RunNowAsync(cancellationTask);
    await entered.Task;
    using var cancellation = new CancellationTokenSource();
    Task<bool> cancelledRecovery = coordinator.RunWhenAvailableAsync(cancellationTask, cancellation.Token);
    cancellation.Cancel();
    try
    {
        await cancelledRecovery;
        throw new InvalidOperationException("cancelled recovery unexpectedly ran");
    }
    catch (OperationCanceledException)
    {
    }
    release.SetResult();
    await activeRefresh;
    Require(calls == 1, "cancelled resume recovery does not run after the active refresh completes");
}

var adGuardRefreshEpoch = new AdGuardRefreshEpoch();
long preSuspendEpoch = adGuardRefreshEpoch.Capture();
long resumedEpoch = adGuardRefreshEpoch.Advance();
Require(!adGuardRefreshEpoch.IsCurrent(preSuspendEpoch) &&
    adGuardRefreshEpoch.IsCurrent(resumedEpoch),
    "pre-suspend AdGuard refresh results cannot overwrite a newer recovery state");

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

MethodInfo? detectHealth = typeof(RouterPilot.ViewModels.ClientsViewModel).GetMethod(
    "DetectHealth", BindingFlags.Static | BindingFlags.NonPublic);
Require(detectHealth is not null, "client presence status helper is available");
var currentRouterClientWithOldDnsActivity = new ClientInfo
{
    MacAddress = "AA:BB:CC:DD:EE:90",
    AdGuardDataAvailability = AdGuardAvailabilityState.Available,
    LastSeen = DateTime.Now.AddHours(-2).ToString("O"),
    TotalQueries = 42
};
var currentRouterClientStatus = ((ValueTuple<string, string>)detectHealth!.Invoke(
    null, new object?[] { currentRouterClientWithOldDnsActivity })!);
Require(currentRouterClientStatus.Item1 == "Online",
    "a current router snapshot remains online when its DNS activity is old");

static ClientInfo Client(string mac, string name, string ip) => new()
{
    MacAddress = mac,
    Name = name,
    RouterName = name,
    IpAddress = ip
};

// Slice 01 / Phase 1 characterization: the historic alphanumeric profile
// normalizer and the strict hardware-MAC inventory normalizer are deliberately
// distinct. A future typed MAC identity must adopt only the strict path.
Require(ClientIdentity.NormalizeMac("aa:bb:cc:dd:ee:ff") == "AABBCCDDEEFF" &&
    ClientIdentity.NormalizeHexMac("aa:bb:cc:dd:ee:ff") == "AABBCCDDEEFF",
    "case and colon separators canonicalize consistently for valid MACs");
Require(ClientIdentity.NormalizeHexMac("aa-bb-cc-dd-ee-ff") == "AABBCCDDEEFF" &&
    ClientIdentity.NormalizeHexMac("aa.bb.cc.dd.ee.ff") == "AABBCCDDEEFF" &&
    ClientIdentity.NormalizeHexMac("aabbccddeeff") == "AABBCCDDEEFF",
    "hyphen, dot, and separator-free valid MAC forms use the shared strict key");
Require(ClientIdentity.NormalizeMac("gg:hh:ii:jj:kk:ll") == "GGHHIIJJKKLL" &&
    ClientIdentity.NormalizeHexMac("gg:hh:ii:jj:kk:ll") == string.Empty,
    "historic alphanumeric normalization remains distinct from strict hexadecimal eligibility");
Require(ClientIdentity.NormalizeHexMac("AA:BB:CC:DD:EE") == "AABBCCDDEE" &&
    ClientIdentity.NormalizeHexMac(null) == string.Empty &&
    ClientIdentity.NormalizeHexMac("not-a-mac") != "AABBCCDDEEFF",
    "wrong length, null, and invalid values do not produce a strict shared MAC key");

var typedIdentities = new HashSet<DeviceIdentity>();
foreach (string form in new[] { "aa:bb:cc:dd:ee:ff", "AA-BB-CC-DD-EE-FF", "aa.bb.cc.dd.ee.ff", "AABBCCDDEEFF" })
{
    Require(DeviceIdentity.TryCreate(form, out DeviceIdentity identity) && identity.CanonicalMac == "AABBCCDDEEFF",
        $"typed identity accepts the existing strict form {form}");
    typedIdentities.Add(identity);
}
Require(DeviceIdentity.TryCreate("AABBCCDDEEFF", out DeviceIdentity canonicalIdentity),
    "canonical strict MAC creates a typed identity");
Require(typedIdentities.Count == 1 && typedIdentities.Single().GetHashCode() == canonicalIdentity.GetHashCode(),
    "typed identity equality and hashing use the canonical strict MAC key");
var typedIdentityMap = new Dictionary<DeviceIdentity, string>
{
    [canonicalIdentity] = "canonical"
};
Require(typedIdentityMap.TryGetValue(typedIdentities.Single(), out string? typedIdentityValue) &&
    typedIdentityMap.Count == 1 && typedIdentityValue == "canonical",
    "typed identity supports deterministic dictionary and set lookup");
foreach (string? invalid in new[] { null, "", "AA:BB:CC:DD:EE", "GG:HH:II:JJ:KK:LL", "not-a-mac" })
    Require(!DeviceIdentity.TryCreate(invalid, out _), "invalid input cannot create a typed MAC identity");

var inventoryCharacterization = new ClientInventoryState();
ClientInfo duplicateFirst = Client("aa:bb:cc:dd:ee:10", "First observed", "192.168.8.10");
ClientInfo duplicateLast = Client("AA-BB-CC-DD-EE-10", "Last observed", "192.168.8.11");
inventoryCharacterization.Update([duplicateFirst, duplicateLast, Client("GG:HH:II:JJ:KK:LL", "Invalid", "192.168.8.12")]);
Require(inventoryCharacterization.Snapshot.Count == 1 &&
    inventoryCharacterization.Snapshot.TryGetValue("AABBCCDDEE10", out ClientInfo? publishedDuplicate) &&
    ReferenceEquals(publishedDuplicate, duplicateLast),
    "legacy ClientInventoryState normalizes equivalent observations and retains its current last-record winner");
Require(DeviceIdentity.TryCreate("AA:BB:CC:DD:EE:10", out DeviceIdentity duplicateIdentity),
    "accepted shared inventory key creates a typed identity");
Require(inventoryCharacterization.DeviceSnapshot.Observations.Keys.Select(identity => identity.CanonicalMac)
    .OrderBy(key => key).SequenceEqual(inventoryCharacterization.Snapshot.Keys.OrderBy(key => key)) &&
    inventoryCharacterization.DeviceSnapshot.Observations.TryGetValue(duplicateIdentity, out DeviceObservation? typedDuplicate) &&
    ReferenceEquals(typedDuplicate.Client, duplicateLast) && typedDuplicate.IsOnline is null,
    "typed and legacy inventory views derive from the same accepted duplicate winner with unknown presence");
inventoryCharacterization.UpdateAuthoritativePresence(new Dictionary<string, bool>
{
    ["aa-bb-cc-dd-ee-10"] = false,
    ["GG:HH:II:JJ:KK:LL"] = true
});
Require(inventoryCharacterization.PresenceSnapshot.Count == 1 &&
    inventoryCharacterization.PresenceSnapshot.TryGetValue("AABBCCDDEE10", out bool explicitOffline) && !explicitOffline &&
    !inventoryCharacterization.PresenceSnapshot.ContainsKey("AABBCCDDEE11"),
    "presence retains only strict identities; an absent identity remains Unknown rather than Offline");
Require(inventoryCharacterization.DeviceSnapshot.Observations[duplicateIdentity].IsOnline == false,
    "typed observation preserves explicit offline presence without treating absence as offline");
inventoryCharacterization.UpdateAuthoritativePresence(new Dictionary<string, bool>
{
    ["AA:BB:CC:DD:EE:10"] = true
});
Require(inventoryCharacterization.DeviceSnapshot.Observations[duplicateIdentity].IsOnline == true,
    "typed observation preserves explicit online presence");
inventoryCharacterization.Clear();
Require(inventoryCharacterization.Snapshot.Count == 0 && inventoryCharacterization.PresenceSnapshot.Count == 0 &&
    inventoryCharacterization.DeviceSnapshot.IsEmpty,
    "clearing shared inventory leaves typed and legacy views coherently empty");

// Current profile lookup remains a normalized-map contract at its consumers.
// ClientProfileService owns raw persisted keys and must not be changed by this phase.
var normalizedProfileLookup = new Dictionary<string, ClientProfile>(StringComparer.OrdinalIgnoreCase)
{
    [ClientIdentity.NormalizeHexMac("aa-bb-cc-dd-ee-20")] = new ClientProfile
    {
        Key = "aa-bb-cc-dd-ee-20",
        Nickname = "Profile-owned name"
    }
};
Require(normalizedProfileLookup.TryGetValue(ClientIdentity.NormalizeHexMac("AA:BB:CC:DD:EE:20"), out ClientProfile? matchedProfile) &&
    matchedProfile.Nickname == "Profile-owned name" && matchedProfile.Key == "aa-bb-cc-dd-ee-20",
    "normalized consumer lookup preserves the existing profile key and user-owned nickname");
ClientDetailsNavigationTarget? separatorVariantProfile = ClientDetailsNavigationPreparation.Resolve(
    "AA:BB:CC:DD:EE:20", new Dictionary<string, ClientInfo>(), normalizedProfileLookup);
Require(ReferenceEquals(separatorVariantProfile?.Profile, matchedProfile!) && separatorVariantProfile.LiveClient is null,
    "existing deep-link profile lookup accepts equivalent MAC separator forms through its normalized consumer map");

// The context seam deliberately exposes the present defect: reset/serialization
// alone does not stop a prior router refresh from publishing after a switch.
var staleInventory = new ClientInventoryState();
var staleContext = new HarnessActiveRouterContext("router-a", version: 1);
var staleStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var releaseStale = new TaskCompletionSource<IReadOnlyList<ClientInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
var staleCoordinator = new ClientInventoryCoordinator(staleInventory, staleContext, async _ =>
{
    staleStarted.SetResult();
    return await releaseStale.Task;
});
Task<bool> staleRefresh = staleCoordinator.RefreshAuthoritativeInventoryAsync();
await staleStarted.Task;
staleContext.SwitchTo("router-b", version: 2);
staleInventory.Clear();
staleCoordinator.ResetForRouterSession();
releaseStale.SetResult([Client("AA:BB:CC:DD:EE:30", "Router A client", "192.168.8.30")]);
bool stalePublished = await staleRefresh;
Require(!stalePublished && staleInventory.Snapshot.Count == 0 && staleInventory.DeviceSnapshot.IsEmpty,
    "prior-router inventory refresh cannot publish typed or legacy state after the active router context changes");

var contextBInventory = new ClientInventoryState();
var contextB = new HarnessActiveRouterContext("router-b", version: 2);
var contextBCoordinator = new ClientInventoryCoordinator(contextBInventory, contextB, _ =>
    Task.FromResult<IReadOnlyList<ClientInfo>>([Client("AA:BB:CC:DD:EE:31", "Router B client", "192.168.9.31")]));
Require(await contextBCoordinator.RefreshAuthoritativeInventoryAsync() &&
    contextBInventory.DeviceSnapshot.RouterProfileId == "router-b" &&
    contextBInventory.DeviceSnapshot.ContextVersion == 2 &&
    contextBInventory.DeviceSnapshot.Observations.Keys.Select(identity => identity.CanonicalMac).OrderBy(key => key)
        .SequenceEqual(contextBInventory.Snapshot.Keys.OrderBy(key => key)),
    "current-context refresh publishes one context-stamped typed snapshot equivalent to the legacy map");

// Slice 01 / Phase 5A: exercise the migrated Known Devices ViewModel itself,
// with an in-memory profile map so the fixture never writes user profiles.
var knownInventory = new ClientInventoryState();
ClientInfo knownCurrent = Client("AA:BB:CC:DD:EE:50", "Router tablet", "192.168.10.50");
knownInventory.Update([knownCurrent], "router-a", 1);
using (KnownDevicesViewModel knownDevices = CreateKnownDevicesViewModel(
    knownInventory,
    new Dictionary<string, ClientProfile>(StringComparer.OrdinalIgnoreCase)
    {
        ["aa-bb-cc-dd-ee-50"] = new ClientProfile
        {
            Key = "aa-bb-cc-dd-ee-50", Nickname = "Kitchen tablet", IsFavorite = true,
            LastKnownName = "Saved tablet", LastKnownIpAddress = "192.168.10.50"
        }
    },
    out ClientsViewModel knownClients))
{
    Require(knownDevices.Devices.Count == 1 && knownDevices.Devices[0].IsOnline &&
        knownDevices.Devices[0].Profile.IsFavorite && knownDevices.Devices[0].Name == "Router tablet",
        "Known Devices ViewModel joins one typed current observation with one matching profile without duplication");

    knownDevices.SelectedDevice = knownDevices.Devices[0];
    Require(knownClients.SelectedClient is not null &&
        ClientIdentity.NormalizeHexMac(knownClients.SelectedClient.MacAddress) == "AABBCCDDEE50" &&
        knownDevices.SelectedDevice.MacKey == "AABBCCDDEE50",
        "Known Devices ViewModel selection retains the existing canonical string-MAC Client Details compatibility path");
}

var filterSortInventory = new ClientInventoryState();
filterSortInventory.Update(
[
    Client("AA:BB:CC:DD:EE:51", "Bravo", "192.168.10.51"),
    Client("AA:BB:CC:DD:EE:52", "Alpha", "192.168.10.52")
], "router-a", 1);
using (KnownDevicesViewModel filterSortDevices = CreateKnownDevicesViewModel(
    filterSortInventory,
    new Dictionary<string, ClientProfile>(StringComparer.OrdinalIgnoreCase)
    {
        ["AA:BB:CC:DD:EE:51"] = new ClientProfile { Key = "AA:BB:CC:DD:EE:51", LastKnownName = "Saved Bravo" },
        ["aa-bb-cc-dd-ee-52"] = new ClientProfile { Key = "aa-bb-cc-dd-ee-52", LastKnownName = "Saved Alpha" },
        ["AA:BB:CC:DD:EE:53"] = new ClientProfile { Key = "AA:BB:CC:DD:EE:53", LastKnownName = "Charlie" }
    },
    out _))
{
    Require(filterSortDevices.Devices.Count == 3 && filterSortDevices.OnlineCount == 2,
        "Known Devices ViewModel includes typed current and profile-only entries exactly once");
    filterSortDevices.SelectedFilter = "Online";
    Require(filterSortDevices.Devices.Count == 2 && filterSortDevices.Devices.All(device => device.IsOnline),
        "Known Devices ViewModel Online filter retains only current typed observations");
    filterSortDevices.SelectedFilter = "Offline";
    Require(filterSortDevices.Devices.Count == 1 && !filterSortDevices.Devices[0].IsOnline &&
        filterSortDevices.Devices[0].Name == "Charlie",
        "Known Devices ViewModel Offline filter retains the profile-only projection");
    filterSortDevices.SelectedFilter = "All";
    filterSortDevices.SearchText = "Bravo";
    Require(filterSortDevices.Devices.Count == 1 && filterSortDevices.Devices[0].Name == "Bravo",
        "Known Devices ViewModel search filtering remains based on the existing projection");
    filterSortDevices.SearchText = string.Empty;
    filterSortDevices.SelectedSort = "Name";
    Require(filterSortDevices.Devices.Select(device => device.Name).SequenceEqual(["Alpha", "Bravo", "Charlie"]),
        "Known Devices ViewModel Name sorting remains the established ordinal-ignore-case projection order");
}

var staleKnownInventory = new ClientInventoryState();
var staleKnownContext = new HarnessActiveRouterContext("router-a", version: 1);
var staleKnownStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var releaseStaleKnown = new TaskCompletionSource<IReadOnlyList<ClientInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
var staleKnownCoordinator = new ClientInventoryCoordinator(staleKnownInventory, staleKnownContext, async _ =>
{
    staleKnownStarted.SetResult();
    return await releaseStaleKnown.Task;
});
using (KnownDevicesViewModel staleKnownDevices = CreateKnownDevicesViewModel(
    staleKnownInventory,
    new Dictionary<string, ClientProfile>(StringComparer.OrdinalIgnoreCase)
    {
        ["AA:BB:CC:DD:EE:54"] = new ClientProfile { Key = "AA:BB:CC:DD:EE:54", LastKnownName = "Router A remembered" },
        ["AA:BB:CC:DD:EE:55"] = new ClientProfile { Key = "AA:BB:CC:DD:EE:55", LastKnownName = "Router B remembered" }
    },
    out _))
{
    Task<bool> staleKnownRefresh = staleKnownCoordinator.RefreshAuthoritativeInventoryAsync();
    await staleKnownStarted.Task;
    staleKnownContext.SwitchTo("router-b", version: 2);
    staleKnownInventory.Clear();
    staleKnownCoordinator.ResetForRouterSession();
    releaseStaleKnown.SetResult([Client("AA:BB:CC:DD:EE:54", "Router A current", "192.168.10.54")]);
    Require(!await staleKnownRefresh && staleKnownDevices.Devices.Count == 2 &&
        staleKnownDevices.Devices.All(device => !device.IsOnline) &&
        staleKnownDevices.Devices.All(device => device.Name != "Router A current"),
        "Known Devices ViewModel never displays a delayed router-A observation as current after switching to router B");
}

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

static KnownDevicesViewModel CreateKnownDevicesViewModel(
    ClientInventoryState inventory,
    IReadOnlyDictionary<string, ClientProfile> profiles,
    out ClientsViewModel clients)
{
    var settings = new SettingsService(System.IO.Path.GetTempPath());
    var displayNames = new ClientDisplayNameService(settings);
    clients = new ClientsViewModel(
        null!, new AdGuardAvailabilityService(), settings, displayNames, null!, null!, null!,
        new DataFreshnessService(), inventory, null!, new DeviceIdentityResolver(), new HarnessMdnsIdentityService());
    var viewModel = new KnownDevicesViewModel(inventory, clients, new DeviceIdentityResolver(), displayNames);
    FieldInfo profilesField = typeof(KnownDevicesViewModel).GetField("_profileMap", BindingFlags.Instance | BindingFlags.NonPublic)!;
    MethodInfo rebuild = typeof(KnownDevicesViewModel).GetMethod("Rebuild", BindingFlags.Instance | BindingFlags.NonPublic)!;
    profilesField.SetValue(viewModel, new Dictionary<string, ClientProfile>(profiles, StringComparer.OrdinalIgnoreCase));
    rebuild.Invoke(viewModel, null);
    return viewModel;
}

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

Console.WriteLine("Client Details deep-link regression fixtures passed, including DHCP configured-name fixtures.");

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

sealed class HarnessActiveRouterContext : IActiveRouterContext
{
    private RouterProfile _profile;

    public HarnessActiveRouterContext(string profileId, long version)
    {
        _profile = new RouterProfile { Id = profileId };
        Version = version;
    }

    public RouterProfile CurrentProfile => _profile;
    public string CurrentProfileId => _profile.Id;
    public long Version { get; private set; }
    public void InvalidateSession() => Version++;

    public void SwitchTo(string profileId, long version)
    {
        _profile = new RouterProfile { Id = profileId };
        Version = version;
    }
}

sealed class HarnessMdnsIdentityService : IMdnsIdentityService
{
    public Task<string?> ResolveHostnameAsync(string ipAddress, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);
}
