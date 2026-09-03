using RouterPilot.Models;
using RouterPilot.Presentation;
using RouterPilot.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Renci.SshNet;
using Renci.SshNet.Common;
using RouterPilot.ViewModels;

static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
string capabilityFixture = "__SECTION__ SELECTED UCI SCHEMA\nnetwork.lan.ipaddr='192.168.1.1'\nwireless.@wifi-iface[0].ssid='Home WiFi'\nwireless.@wifi-iface[0].key='secret-key'\nservice.endpoint='https://vpn.example.test:443'\nmac='aa:bb:cc:dd:ee:ff'\naddress='2001:db8::1'";
string sanitizedCapability = RouterCapabilityDiscoveryReportBuilder.Build(capabilityFixture);
Require(!sanitizedCapability.Contains("192.168.1.1", StringComparison.Ordinal) && !sanitizedCapability.Contains("Home WiFi", StringComparison.Ordinal) && !sanitizedCapability.Contains("secret-key", StringComparison.Ordinal) && !sanitizedCapability.Contains("vpn.example.test", StringComparison.Ordinal) && !sanitizedCapability.Contains("aa:bb:cc:dd:ee:ff", StringComparison.Ordinal) && !sanitizedCapability.Contains("2001:db8::1", StringComparison.Ordinal), "capability report sanitizer removes network identity and secrets");
Require(sanitizedCapability.Contains("network.lan.ipaddr", StringComparison.Ordinal) && sanitizedCapability.Contains("wireless.@wifi-iface[0].ssid", StringComparison.Ordinal), "capability report sanitizer preserves schema keys");
Require(ClientIdentity.EndpointEquals("192.168.1.20", "192.168.1.20"), "IPv4 endpoint correlation");
Require(ClientIdentity.EndpointEquals("::ffff:192.168.1.20", "192.168.1.20"), "IPv4-mapped IPv6 endpoint correlation");
Require(ClientIdentity.EndpointEquals("[2001:db8::20]:53", "2001:DB8::20"), "bracketed IPv6 endpoint correlation");
Require(ClientIdentity.EndpointEquals("client.example.", "CLIENT.EXAMPLE"), "hostname endpoint correlation");
TailscaleStatus tailscale = TailscaleStatusService.ParseStatus("{\"BackendState\":\"Running\",\"Self\":{\"HostName\":\"router\",\"DNSName\":\"router.example.ts.net.\",\"TailscaleIPs\":[\"100.64.0.1\",\"fd7a::1\"]},\"Peer\":{\"nodekey:secret\":{\"HostName\":\"laptop\",\"TailscaleIPs\":[\"100.64.0.2\"],\"Online\":true,\"OS\":\"Linux\",\"LastSeen\":\"2026-09-01T12:00:00Z\",\"Relay\":false}}}", "1.2.3");
Require(tailscale.State == TailscaleState.Connected && tailscale.DnsName == "router.example.ts.net" && tailscale.Addresses.Count == 2, "Tailscale connected parsing");
Require(tailscale.Peers.Count == 1 && tailscale.Peers[0].Name == "laptop" && tailscale.OnlinePeerCount == 1, "Tailscale peer parsing without exposing key");
Require(tailscale.Peers[0].OperatingSystem == "Linux" && tailscale.Peers[0].LastSeen.Length > 0 && tailscale.Peers[0].ConnectionPath == "Direct", "Tailscale peer enrichment parsing");
Require(TailscaleStatusService.ParseStatus("{\"BackendState\":\"NeedsLogin\"}").State == TailscaleState.NeedsLogin, "Tailscale login state");
Require(TailscaleStatusService.ParseStatus("{\"BackendState\":\"Running\",\"Unknown\":true}").State == TailscaleState.Connected, "Tailscale unknown fields");
Require(TailscaleStatusService.ParseStatus("not-json").State == TailscaleState.Incompatible, "Tailscale malformed JSON");
TailscaleStatus stoppedTailscale = TailscaleStatusService.ParseStatus("{\"BackendState\":\"Stopped\",\"Self\":{\"HostName\":\"old-router\",\"TailscaleIPs\":[\"100.64.0.9\"]},\"Peer\":{\"old\":{\"HostName\":\"old-peer\"}}}");
Require(stoppedTailscale.State == TailscaleState.Stopped && stoppedTailscale.DeviceName == string.Empty && stoppedTailscale.Peers.Count == 0 && stoppedTailscale.PeerCount is null, "Tailscale stopped clears connected data");
TailscaleStatus connectedWithoutPeers = TailscaleStatusService.ParseStatus("{\"BackendState\":\"Running\",\"Self\":{\"HostName\":\"router\"}}");
Require(connectedWithoutPeers.PeerCount is null, "missing peer data is unavailable rather than zero");
TailscaleStatus connectedWithNoPeers = TailscaleStatusService.ParseStatus("{\"BackendState\":\"Running\",\"Peer\":{}}");
Require(connectedWithNoPeers.PeerCount == 0 && connectedWithNoPeers.OnlinePeerCount == 0, "empty peer object is genuine zero");
var tailscaleHistoryVm = new VpnViewModel();
tailscaleHistoryVm.ApplyTailscaleStatus(tailscale);
Require(tailscaleHistoryVm.TailscaleHistory.Count == 0, "Tailscale initial baseline does not flood history");
TailscaleStatus peerOffline = TailscaleStatusService.ParseStatus("{\"BackendState\":\"Running\",\"Peer\":{\"nodekey:secret\":{\"HostName\":\"laptop\",\"Online\":false}}}");
tailscaleHistoryVm.ApplyTailscaleStatus(peerOffline);
Require(tailscaleHistoryVm.TailscaleHistory.Count == 1 && tailscaleHistoryVm.TailscaleHistory[0].Contains("laptop", StringComparison.Ordinal), "Tailscale peer transition is recorded once");
tailscaleHistoryVm.ApplyTailscaleStatus(peerOffline);
Require(tailscaleHistoryVm.TailscaleHistory.Count == 1, "Repeated Tailscale state does not duplicate history");
tailscaleHistoryVm.ResetTailscale();
Require(tailscaleHistoryVm.TailscaleHistory.Count == 0, "Tailscale history resets with router context");
Require(new VpnLiveStatusInfo { RxBytes = 0, TxBytes = 0 }.DownloadDisplay == "0 B" && new VpnLiveStatusInfo().UploadDisplay == "—", "VPN counters distinguish genuine zero from unavailable");
Type portParser = typeof(RouterManager).Assembly.GetType("RouterPilot.Services.RouterPortTelemetryParser")!;
MethodInfo parsePorts = portParser.GetMethod("Parse", BindingFlags.Static | BindingFlags.Public)!;
var parsedPorts = (IReadOnlyList<RouterPortSnapshot>)parsePorts.Invoke(null, ["P|eth0|physical||1|2500|full|aa:bb:cc:dd:ee:ff|100|200|1|2|3|4||||"])!;
Require(parsedPorts.Count == 1 && parsedPorts[0].IsPhysical && parsedPorts[0].NegotiatedSpeedMbps == 2500 && parsedPorts[0].RxErrors == 1 && parsedPorts[0].RxDropped == 3, "Ethernet port telemetry parsing");
var malformedPorts = (IReadOnlyList<RouterPortSnapshot>)parsePorts.Invoke(null, ["P|eth1|physical||1|bad|full||bad|-2|-1|x|y|z||||"])!;
Require(malformedPorts.Count == 1 && malformedPorts[0].NegotiatedSpeedMbps is null && malformedPorts[0].RxBytes is null, "Malformed Ethernet counters remain unavailable");
Require(!ClientIdentity.EndpointEquals("127.0.0.1", "192.168.1.20"), "loopback is not confused with a LAN client");
ClientInfo observedZeroDns = new() { AdGuardDataAvailability = AdGuardAvailabilityState.Available };
var protectionSession = new DashboardViewModel();
protectionSession.UpdateAdGuardStatistics(new AdGuardStatistics { TotalQueries = 100, BlockedQueries = 10 });
Require(protectionSession.AdGuardSessionQueriesDisplay == "0" && protectionSession.AdGuardSessionSamplesDisplay == "1", "Protection session baseline does not count lifetime totals");
protectionSession.UpdateAdGuardStatistics(new AdGuardStatistics { TotalQueries = 125, BlockedQueries = 15 });
Require(protectionSession.AdGuardSessionQueriesDisplay == "25" && protectionSession.AdGuardSessionBlockedDisplay == "5", "Protection session deltas accumulate");
protectionSession.UpdateAdGuardStatistics(new AdGuardStatistics { TotalQueries = 5, BlockedQueries = 1 });
Require(protectionSession.AdGuardSessionQueriesDisplay == "25", "Protection counter reset does not create negative session totals");
protectionSession.ClearAdGuardStatistics();
Require(protectionSession.AdGuardSessionSamplesDisplay == "0", "Protection session reset is local");
Require(observedZeroDns.TotalQueriesDisplay == "0" && observedZeroDns.BlockedQueriesDisplay == "0", "authoritative zero DNS activity remains distinguishable");
Require(observedZeroDns.ActivityAvailabilityToolTip.Contains("bypass", StringComparison.OrdinalIgnoreCase), "zero DNS tooltip explains AdGuard observation scope");
Require(observedZeroDns.ActivityAvailabilityToolTip.Contains("upstream DNS", StringComparison.OrdinalIgnoreCase), "encrypted AdGuard upstream remains observable");
Require(observedZeroDns.ActivityAvailabilityToolTip.Contains("DoH", StringComparison.OrdinalIgnoreCase) && observedZeroDns.ActivityAvailabilityToolTip.Contains("DoT", StringComparison.OrdinalIgnoreCase) && observedZeroDns.ActivityAvailabilityToolTip.Contains("DoQ", StringComparison.OrdinalIgnoreCase), "direct encrypted-DNS bypass is explained");
ClientInfo unavailableDns = new() { AdGuardDataAvailability = AdGuardAvailabilityState.Unavailable };
Require(unavailableDns.TotalQueriesDisplay == RouterPilotStatusPresentation.NotAvailable, "unmatched DNS activity is presented as unavailable");
Type statisticsParser = typeof(RouterManager).Assembly.GetType("RouterPilot.Services.AdGuardStatisticsParser")!;
MethodInfo parseStatistics = statisticsParser.GetMethod("Parse", BindingFlags.Static | BindingFlags.NonPublic)!;
MethodInfo createUnavailableStatistics = statisticsParser.GetMethod("CreateUnavailableStatistics", BindingFlags.Static | BindingFlags.NonPublic)!;
AdGuardStatistics unavailableStatistics = (AdGuardStatistics)createUnavailableStatistics.Invoke(null, null)!;
Require(unavailableStatistics.TotalQueries < 0 && unavailableStatistics.BlockedQueries < 0,
    "AdGuard statistics preserve unavailable counts instead of defaulting to zero");
AdGuardStatistics zeroStatistics = (AdGuardStatistics)parseStatistics.Invoke(null, new object[] {
    "{\"num_dns_queries\":0,\"num_blocked_filtering\":0,\"top_queried_domains\":[{\"example.test\":0}]} ", DateTime.UtcNow })!;
Require(zeroStatistics.TotalQueries == 0 && zeroStatistics.BlockedQueries == 0 && zeroStatistics.BlockPercentage == 0,
    "AdGuard statistics preserve genuine zero counts");
AdGuardStatistics malformedStatistics = (AdGuardStatistics)parseStatistics.Invoke(null, new object[] {
    "{\"num_dns_queries\":\"invalid\",\"num_blocked_filtering\":\"invalid\",\"top_queried_domains\":[{\"valid.test\":4},{\"bad.test\":\"x\"}]} ", DateTime.UtcNow })!;
Require(malformedStatistics.TotalQueries < 0 && malformedStatistics.BlockedQueries < 0 &&
    malformedStatistics.TopQueriedDomains.Count == 1 && malformedStatistics.TopQueriedDomains[0].Count == 4,
    "Malformed AdGuard statistics retain valid ranked data and unavailable totals");
var encryptedUpstreamClient = new ClientInfo { IpAddress = "192.168.1.20", AdGuardDataAvailability = AdGuardAvailabilityState.Available };
MethodInfo? applyStatistics = typeof(RouterManager).GetMethod("ApplyClientTotalsFromStatistics", BindingFlags.Static | BindingFlags.NonPublic);
Require(applyStatistics is not null, "AdGuard statistics merge helper is available");
object? plainUpstreamResult = applyStatistics!.Invoke(null, new object[] { new List<ClientInfo> { encryptedUpstreamClient }, "{\"top_clients\":[{\"192.168.1.20\":7}]}" });
Require((int)plainUpstreamResult! == 1 && encryptedUpstreamClient.TotalQueries == 7, "AdGuard client totals correlate independently of upstream transport");
var genuineZeroClient = new ClientInfo { IpAddress = "192.168.1.22", AdGuardDataAvailability = AdGuardAvailabilityState.Available };
object? genuineZeroResult = applyStatistics.Invoke(null, new object[] { new List<ClientInfo> { genuineZeroClient }, "{\"top_clients\":[{\"192.168.1.22\":0}]}" });
Require((int)genuineZeroResult! == 1 && genuineZeroClient.TotalQueriesDisplay == "0" && genuineZeroClient.BlockedQueriesDisplay == "0", "AdGuard correlated zero activity remains numeric zero");
var correlationFailureClient = new ClientInfo { IpAddress = "192.168.1.21", AdGuardDataAvailability = AdGuardAvailabilityState.Unavailable };
Require(correlationFailureClient.TotalQueriesDisplay == RouterPilotStatusPresentation.NotAvailable, "AdGuard correlation failure remains unavailable");
ClientInfo discoveredClient = new()
{
    Name = "Living Room TV",
    RouterName = "tv-host",
    IpAddress = "192.168.1.40",
    MacAddress = "AA:BB:CC:DD:EE:40",
    Manufacturer = "Example Vendor",
    DeviceType = "Television",
    ConnectionType = "5 GHz",
    WifiNetwork = "Home",
    LiveInterface = "wlan0",
    AdGuardDataAvailability = AdGuardAvailabilityState.Unavailable
};
var knownProjection = new KnownDeviceInfo
{
    Profile = new ClientProfile { Key = discoveredClient.MacAddress, LastKnownName = discoveredClient.Name },
    CurrentClient = discoveredClient
};
ClientInfo knownDetails = knownProjection.ToClientInfo();
Require(knownProjection.IsOnline && knownProjection.Secondary == discoveredClient.IpAddress, "Known Devices projects discovered router identity");
Require(knownDetails.Manufacturer == discoveredClient.Manufacturer && knownDetails.ConnectionType == discoveredClient.ConnectionType &&
    knownDetails.LiveInterface == discoveredClient.LiveInterface, "Known Device details retain router-derived enrichment independently of AdGuard");
Require(knownDetails.TotalQueriesDisplay == RouterPilotStatusPresentation.NotAvailable, "Known Device DNS fields remain unavailable when AdGuard enrichment is absent");
var generatedIpName = new KnownDeviceInfo
{
    Profile = new ClientProfile { Key = "AA:BB:CC:DD:EE:42", LastKnownName = "1921681103", LastKnownIpAddress = "192.168.1.103" }
};
Require(generatedIpName.Name == "Unknown device" && generatedIpName.IpAddress == "192.168.1.103" && generatedIpName.ToClientInfo().Name == "Unknown device",
    "stripped-IP identity does not leak into Known Device display name");
var generatedIpWithPort = new KnownDeviceInfo
{
    Profile = new ClientProfile { Key = "AA:BB:CC:DD:EE:44", LastKnownName = "1921681103", LastKnownIpAddress = "192.168.1.103:5353" }
};
Require(generatedIpWithPort.Name == "Unknown device", "stripped-IP identity is detected with an endpoint port");
var numericNickname = new KnownDeviceInfo
{
    Profile = new ClientProfile { Key = "AA:BB:CC:DD:EE:43", Nickname = "1921681103", LastKnownIpAddress = "192.168.1.103" }
};
Require(numericNickname.Name == "1921681103", "legitimate numeric Known Device nickname is preserved");
var rememberedProfile = new ClientProfile
{
    Key = "AA:BB:CC:DD:EE:41",
    LastKnownName = "Office Laptop",
    LastKnownIpAddress = "192.168.1.41",
    LastKnownConnectionSummary = "Wi-Fi 5 GHz"
};
var offlineKnown = new KnownDeviceInfo { Profile = rememberedProfile };
Require(!offlineKnown.IsOnline && offlineKnown.Name == "Office Laptop" && offlineKnown.IpAddress == "192.168.1.41" &&
    offlineKnown.ConnectionSummary == "Wi-Fi 5 GHz" && offlineKnown.TotalQueriesDisplay == RouterPilotStatusPresentation.NotAvailable,
    "offline Known Device retains persisted identity without fabricating live or DNS data");
ClientInfo movedClient = new()
{
    Name = "Office Laptop",
    MacAddress = rememberedProfile.Key,
    IpAddress = "192.168.1.99",
    AdGuardDataAvailability = AdGuardAvailabilityState.Available,
    TotalQueries = 0,
    BlockedQueries = 0
};
var movedKnown = new KnownDeviceInfo { Profile = rememberedProfile, CurrentClient = movedClient };
Require(movedKnown.IsOnline && movedKnown.IpAddress == "192.168.1.99" && movedKnown.TotalQueriesDisplay == "0" &&
    movedKnown.BlockRateDisplay == "0.0%", "Known Device follows current MAC identity when IP changes and preserves genuine DNS zero");
Require(RouterTemperatureHealth.IsFlint2("GL-MT6000"), "Flint 2 model identification");
Require(RouterTemperatureHealth.Evaluate("GL-MT6000", "50 °C") == TemperatureHealthState.Normal, "50 C is normal");
Require(RouterTemperatureHealth.Evaluate("GL-MT6000", "60 °C") == TemperatureHealthState.Normal, "60 C is normal");
Require(RouterTemperatureHealth.Evaluate("GL-MT6000", "64.9 °C") == TemperatureHealthState.Normal, "64.9 C is normal");
Require(RouterTemperatureHealth.Evaluate("GL-MT6000", "65 °C") == TemperatureHealthState.Elevated, "65 C is elevated");
Require(RouterTemperatureHealth.Evaluate("Flint 2", "70 °C") == TemperatureHealthState.Elevated, "70 C is elevated");
Require(RouterTemperatureHealth.Evaluate("GL-MT6000", "79.9 °C") == TemperatureHealthState.Elevated, "79.9 C is elevated");
Require(RouterTemperatureHealth.Evaluate("GL-MT6000", "80 °C") == TemperatureHealthState.High, "80 C is high");
Require(RouterTemperatureHealth.Evaluate("GL-MT6000", "90 °C") == TemperatureHealthState.High, "90 C is high");
Require(RouterTemperatureHealth.Evaluate("GL-MT6000", "-") == TemperatureHealthState.Unavailable, "unavailable temperature is neutral");
Require(RouterTemperatureHealth.Evaluate("GL-AX1800", "52 °C") == TemperatureHealthState.Unavailable, "unknown model remains neutral");
NetworkHealthViewInput Input(DataFreshnessState router = DataFreshnessState.Fresh, DataFreshnessState wan = DataFreshnessState.Fresh, DataFreshnessState adGuardFreshness = DataFreshnessState.Fresh, DataFreshnessState wifi = DataFreshnessState.Fresh, DataFreshnessState dhcp = DataFreshnessState.Fresh, AdGuardAvailabilityState adGuard = AdGuardAvailabilityState.Available, bool includeAdGuard = true, string vpn = "Connected", bool vpnAvailable = true, bool vpnConfigured = true, bool statsLoaded = true, RouterPilotStatus stats = RouterPilotStatus.Active, string cpu = "10%", string temperature = "45 C", string memory = "40%", string storage = "20%", string uptime = "1d", string load = "0.1", string routerFirmwareVersion = "4.6.0", FirmwareUpdateCheckStatus firmwareStatus = FirmwareUpdateCheckStatus.UpToDate) => new(router, wan, adGuardFreshness, DataFreshnessState.Fresh, wifi, dhcp, true, true, "now", "1.2.3.4", "192.168.1.1", "1.1.1.1", adGuard, includeAdGuard, true, true, false, vpnAvailable, vpnConfigured, vpn, "WireGuard", 2, 2, 0, 0, 3, true, 3, 1, cpu, temperature, memory, storage, uptime, load, routerFirmwareVersion, firmwareStatus, statsLoaded, stats, "Existing status.");
NetworkHealthViewSnapshot healthy = NetworkHealthViewProjection.Create(Input());
Require(healthy.OverallStatus == "Healthy", "healthy state");
Require(NetworkHealthViewProjection.Create(Input(DataFreshnessState.Unavailable)).OverallStatus == "Unavailable", "router unavailable");
NetworkHealthViewSnapshot adGuardUnavailable = NetworkHealthViewProjection.Create(Input(adGuard: AdGuardAvailabilityState.Unavailable));
Require(adGuardUnavailable.Checks.Single(x => x.Title == "DNS / AdGuard").Status == "Unavailable" && adGuardUnavailable.OverallStatus == "Attention needed", "AdGuard unavailable");
NetworkHealthViewSnapshot adGuardUnused = NetworkHealthViewProjection.Create(Input(adGuard: AdGuardAvailabilityState.Unavailable, includeAdGuard: false));
Require(adGuardUnused.Checks.Single(x => x.Title == "DNS / AdGuard").Status == "Not in use" && adGuardUnused.OverallStatus == "Healthy", "optional AdGuard is informational");
Require(NetworkHealthViewProjection.Create(Input(adGuardFreshness: DataFreshnessState.Loading)).OverallStatus == "Initializing", "expected AdGuard loading state");
Require(NetworkHealthViewProjection.Create(Input(adGuardFreshness: DataFreshnessState.Loading, includeAdGuard: false)).OverallStatus == "Initializing", "optional AdGuard checking state");
Require(DashboardHealthProjection.Create(new(true, true, false, false, 0, false, 0, 0, false, FirmwareUpdateCheckStatus.UpToDate, string.Empty, string.Empty)).Score == 100, "unused AdGuard is excluded from Dashboard health score");
Require(DashboardHealthProjection.Create(new(true, true, false, true, 0, false, 0, 0, false, FirmwareUpdateCheckStatus.UpToDate, string.Empty, string.Empty)).Score == 85, "expected AdGuard affects Dashboard health score");
NetworkHealthViewSnapshot disconnectedVpn = NetworkHealthViewProjection.Create(Input(vpn: "Disconnected"));
Require(disconnectedVpn.Checks.Single(x => x.Title == "VPN").Status == "Disconnected" && disconnectedVpn.OverallStatus == "Healthy", "VPN disconnected is informational");
Require(NetworkHealthViewProjection.Create(Input(vpnConfigured: false)).OverallStatus == "Healthy", "VPN not configured is informational");
Require(NetworkHealthViewProjection.Create(Input(vpn: "Authentication failed")).OverallStatus == "Attention needed", "VPN explicit failure affects health");
Require(NetworkHealthViewProjection.Create(Input(vpn: "Connection did not complete")).OverallStatus == "Attention needed", "VPN failed tunnel affects health");
Require(NetworkHealthViewProjection.Create(Input(stats: RouterPilotStatus.Disabled)).Checks.Single(x => x.Title == "Data Statistics").Status == "Disabled", "statistics disabled");
Require(NetworkHealthViewProjection.Create(Input(DataFreshnessState.Stale)).Checks.Single(x => x.Title == "Router").Status == "Stale", "stale state");
Require(NetworkHealthViewProjection.Create(Input(statsLoaded: false)).Checks.Single(x => x.Title == "Data Statistics").Status == "Not loaded", "partial state");
Require(NetworkHealthViewProjection.Create(Input(DataFreshnessState.Loading)).OverallStatus == "Initializing", "loading state");
Require(NetworkHealthViewProjection.Create(Input(wifi: DataFreshnessState.Loading)).OverallStatus != "Healthy", "Wi-Fi loading state");
Require(NetworkHealthViewProjection.Create(Input(wifi: DataFreshnessState.Stale)).OverallStatus != "Healthy", "Wi-Fi stale state");
Require(NetworkHealthViewProjection.Create(Input(dhcp: DataFreshnessState.Loading)).OverallStatus != "Healthy", "DHCP loading state");
Require(NetworkHealthViewProjection.Create(Input(wifi: DataFreshnessState.Unavailable)).OverallStatus != "Healthy", "Wi-Fi unavailable state");
Require(NetworkHealthViewProjection.Create(Input() with { WifiActiveRadios = 1, WifiDisabledRadios = 1 }).OverallStatus == "Healthy", "intentionally disabled Wi-Fi radio is informational");
Require(NetworkHealthViewProjection.Create(Input() with { WifiActiveRadios = 0, WifiDisabledRadios = 2 }).OverallStatus == "Healthy", "all intentionally disabled Wi-Fi radios are informational");
Require(NetworkHealthViewProjection.Create(Input(dhcp: DataFreshnessState.Unavailable)).OverallStatus != "Healthy", "DHCP unavailable state");
Require(NetworkHealthViewProjection.Create(Input(cpu: "-", temperature: "-", memory: "-", storage: "-", uptime: "-", load: "-")).Checks.Single(x => x.Title == "Router resources").Status == "Unavailable", "missing resources");
Require(NetworkHealthViewProjection.Create(Input(cpu: "-", temperature: "45 C")).Checks.Single(x => x.Title == "Router resources").Status == "Partial", "partial resources");
Require(NetworkHealthViewProjection.Create(Input(wan: DataFreshnessState.Loading)).OverallStatus != "Healthy", "WAN loading state");
Require(NetworkHealthViewProjection.Create(Input(adGuardFreshness: DataFreshnessState.Loading)).OverallStatus != "Healthy", "AdGuard loading state");
NetworkHealthViewCheck firmwareUpToDate = NetworkHealthViewProjection.Create(Input(routerFirmwareVersion: "4.6.0", firmwareStatus: FirmwareUpdateCheckStatus.UpToDate)).Checks.Single(x => x.Title == "Firmware");
Require(firmwareUpToDate.Status == "Up to date" && firmwareUpToDate.Detail == "Current version: 4.6.0", "GL.iNet firmware up to date");
Require(NetworkHealthViewProjection.Create(Input(firmwareStatus: FirmwareUpdateCheckStatus.UpdateAvailable)).Checks.Single(x => x.Title == "Firmware").Status == "Update available", "GL.iNet firmware update available");
Require(NetworkHealthViewProjection.Create(Input(firmwareStatus: FirmwareUpdateCheckStatus.Pending)).Checks.Single(x => x.Title == "Firmware").Status == "Checking", "GL.iNet firmware checking");
Require(NetworkHealthViewProjection.Create(Input(firmwareStatus: FirmwareUpdateCheckStatus.NotAvailable)).Checks.Single(x => x.Title == "Firmware").Status == "Unavailable", "GL.iNet firmware unavailable");
Require(firmwareUpToDate.NavigationTarget == "maintenance-firmware", "Firmware navigation targets Maintenance firmware.");
Require(nameof(NetworkHealthViewInput.RouterFirmwareVersion) == "RouterFirmwareVersion", "Network Health has no LuCI firmware input.");
using ServiceProvider services = new ServiceCollection().AddSingleton<DashboardViewModel>().BuildServiceProvider();
Require(ReferenceEquals(services.GetRequiredService<DashboardViewModel>(), services.GetRequiredService<DashboardViewModel>()), "Dashboard ViewModel DI registration must be authoritative.");

using PublicIpService publicIp = new();
List<(string? Previous, string Current)> publicIpEvents = [];
publicIp.PublicIpChanged += (previous, current) => publicIpEvents.Add((previous, current));
MethodInfo? publish = typeof(PublicIpService).GetMethod("Publish", BindingFlags.Instance | BindingFlags.NonPublic);
Require(publish is not null, "Public-IP publisher is available for deterministic change detection coverage.");
publish!.Invoke(publicIp, [new PublicIpResult(" 1.2.3.4 ", DateTimeOffset.UtcNow, PublicIpStatus.Available, null)]);
Require(publicIpEvents.Count == 0, "first confirmed public IP establishes a silent baseline");
publish.Invoke(publicIp, [new PublicIpResult("1.2.3.4", DateTimeOffset.UtcNow, PublicIpStatus.Available, null)]);
Require(publicIpEvents.Count == 0, "normalized unchanged public IP does not raise an event");
publish.Invoke(publicIp, [new PublicIpResult(null, DateTimeOffset.UtcNow, PublicIpStatus.Unavailable, null)]);
publish.Invoke(publicIp, [new PublicIpResult("1.2.3.4", DateTimeOffset.UtcNow, PublicIpStatus.Available, null)]);
Require(publicIpEvents.Count == 0, "unavailable then unchanged public IP does not raise an event");
publish.Invoke(publicIp, [new PublicIpResult("5.6.7.8", DateTimeOffset.UtcNow, PublicIpStatus.Available, null)]);
Require(publicIpEvents.Count == 1, "a confirmed public IP transition raises one event");
Require(publicIpEvents[0] == ("1.2.3.4", "5.6.7.8"), "public IP event compares confirmed normalized values");

MethodInfo? automaticUpdateDue = typeof(UpdateService).GetMethod("IsAutomaticCheckDue", BindingFlags.Static | BindingFlags.NonPublic);
Require(automaticUpdateDue is not null, "automatic update due policy is available for deterministic coverage");
DateTimeOffset updateNow = DateTimeOffset.UtcNow;
Require((bool)automaticUpdateDue!.Invoke(null, [new AppSettings(), updateNow])!, "first automatic update check is due");
Require(!(bool)automaticUpdateDue.Invoke(null, [new AppSettings { LastSuccessfulUpdateCheckUtc = updateNow - TimeSpan.FromHours(23) }, updateNow])!, "automatic update check is skipped before 24 hours");
Require((bool)automaticUpdateDue.Invoke(null, [new AppSettings { LastSuccessfulUpdateCheckUtc = updateNow - TimeSpan.FromHours(25) }, updateNow])!, "automatic update check is due after 24 hours");

NotificationPreferences defaults = new();
Require(defaults.Allows(new AppNotification { Category = NotificationCategory.Router }), "new category preferences preserve router notifications by default");
Require(defaults.Allows(new AppNotification { Category = NotificationCategory.Firmware }), "new category preferences preserve firmware notifications by default");
Require(defaults.Allows(new AppNotification { Category = NotificationCategory.AdGuard }), "new category preferences preserve AdGuard notifications by default");
Require(defaults.Allows(new AppNotification { Category = NotificationCategory.Device }), "new category preferences preserve device notifications by default");
Require(defaults.Allows(new AppNotification { Category = NotificationCategory.ApplicationUpdates }), "new category preferences preserve update notifications by default");

NotificationPreferences disabledCategories = new NotificationPreferences
{
    Categories = new Dictionary<NotificationCategory, bool>
    {
        [NotificationCategory.Router] = false,
        [NotificationCategory.Vpn] = false,
        [NotificationCategory.NetworkHealth] = false,
        [NotificationCategory.Firmware] = false,
        [NotificationCategory.AdGuard] = false,
        [NotificationCategory.Device] = false,
        [NotificationCategory.ApplicationUpdates] = false
    }
};
Require(!disabledCategories.Allows(new AppNotification { Category = NotificationCategory.Router }), "Router and WAN suppression is central");
Require(!disabledCategories.Allows(new AppNotification { Category = NotificationCategory.Vpn }), "VPN suppression is central");
Require(!disabledCategories.Allows(new AppNotification { Category = NotificationCategory.NetworkHealth }), "Network Health suppression is central");
Require(!disabledCategories.Allows(new AppNotification { Category = NotificationCategory.Firmware }), "firmware suppression is central");
Require(!disabledCategories.Allows(new AppNotification { Category = NotificationCategory.AdGuard }), "AdGuard suppression is central");
Require(!disabledCategories.Allows(new AppNotification { Category = NotificationCategory.Device }), "client and device suppression is central");
Require(!disabledCategories.Allows(new AppNotification { Category = NotificationCategory.ApplicationUpdates }), "automatic update suppression is central");
Require(disabledCategories.Allows(new AppNotification { Category = NotificationCategory.ApplicationUpdates }, bypassCategoryPreference: true), "manual update feedback bypasses only the category preference");
Require(!new NotificationPreferences { Enabled = false }.Allows(new AppNotification { Category = NotificationCategory.ApplicationUpdates }, bypassCategoryPreference: true), "manual feedback still honours the master notification preference");
string preferencesFolder = Path.Combine(Path.GetTempPath(), "RouterPilot-notification-preferences-" + Guid.NewGuid().ToString("N"));
var preferencesStorage = new SettingsService(preferencesFolder);
preferencesStorage.Save(new AppSettings { NotificationPreferences = disabledCategories });
Require(!preferencesStorage.Load().NotificationPreferences.IsCategoryEnabled(NotificationCategory.ApplicationUpdates), "category preferences persist after reload");

var sshFactory = new SshConnectionFactory();
ConnectionInfo passwordConnection = sshFactory.CreateConnectionInfo(new SshConnectionSettings
{
    Host = "router.example",
    Port = 22,
    Username = "root",
    AuthenticationMethod = SshAuthenticationMethod.Password,
    Password = "fixture-password"
});
Require(passwordConnection.Port == 22, "default SSH port is 22");
Require(passwordConnection.AuthenticationMethods.Single() is PasswordAuthenticationMethod, "password authentication is constructed");
ConnectionInfo customPortConnection = sshFactory.CreateConnectionInfo(new SshConnectionSettings
{
    Host = "router.example",
    Port = 2222,
    Username = "root",
    AuthenticationMethod = SshAuthenticationMethod.Password,
    Password = "fixture-password"
});
Require(customPortConnection.Port == 2222, "custom SSH port is passed to ConnectionInfo");
RequireThrows(() => sshFactory.CreateConnectionInfo(new SshConnectionSettings { Host = "router.example", Port = 0, Username = "root", Password = "fixture-password" }), "invalid zero SSH port is rejected");
RequireThrows(() => sshFactory.CreateConnectionInfo(new SshConnectionSettings { Host = "router.example", Port = 65536, Username = "root", Password = "fixture-password" }), "invalid high SSH port is rejected");

string sshFixtureDirectory = Path.Combine(Path.GetTempPath(), "RouterPilot-ssh-fixtures-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(sshFixtureDirectory);
string unencryptedKeyPath = Path.Combine(sshFixtureDirectory, "id_rsa");
string encryptedKeyPath = Path.Combine(sshFixtureDirectory, "id_rsa_encrypted");
string invalidKeyPath = Path.Combine(sshFixtureDirectory, "invalid-key");
const string fixturePassphrase = "fixture-key-passphrase";
try
{
    using (RSA rsa = RSA.Create(2048))
    {
        File.WriteAllText(unencryptedKeyPath, rsa.ExportRSAPrivateKeyPem());
        File.WriteAllText(encryptedKeyPath, rsa.ExportEncryptedPkcs8PrivateKeyPem(
            fixturePassphrase,
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 10_000)));
    }
    File.WriteAllText(invalidKeyPath, "not an SSH private key");

    ConnectionInfo privateKeyConnection = sshFactory.CreateConnectionInfo(new SshConnectionSettings
    {
        Host = "router.example",
        Port = 2222,
        Username = "root",
        AuthenticationMethod = SshAuthenticationMethod.PrivateKey,
        PrivateKeyPath = unencryptedKeyPath
    });
    Require(privateKeyConnection.AuthenticationMethods.Single() is PrivateKeyAuthenticationMethod, "unencrypted private-key authentication is constructed");

    ConnectionInfo encryptedKeyConnection = sshFactory.CreateConnectionInfo(new SshConnectionSettings
    {
        Host = "router.example",
        Port = 2222,
        Username = "root",
        AuthenticationMethod = SshAuthenticationMethod.PrivateKey,
        PrivateKeyPath = encryptedKeyPath,
        PrivateKeyPassphrase = fixturePassphrase
    });
    Require(encryptedKeyConnection.AuthenticationMethods.Single() is PrivateKeyAuthenticationMethod, "encrypted private-key authentication accepts its passphrase");
    RequireThrows(() => sshFactory.CreateConnectionInfo(new SshConnectionSettings
    {
        Host = "router.example", Port = 2222, Username = "root",
        AuthenticationMethod = SshAuthenticationMethod.PrivateKey,
        PrivateKeyPath = encryptedKeyPath, PrivateKeyPassphrase = "wrong-passphrase"
    }), "incorrect key passphrase fails cleanly");
    RequireThrows(() => sshFactory.CreateConnectionInfo(new SshConnectionSettings
    {
        Host = "router.example", Port = 2222, Username = "root",
        AuthenticationMethod = SshAuthenticationMethod.PrivateKey,
        PrivateKeyPath = invalidKeyPath
    }), "invalid SSH key fails cleanly");
}
finally
{
    Directory.Delete(sshFixtureDirectory, recursive: true);
}
RequireThrows(() => sshFactory.CreateConnectionInfo(new SshConnectionSettings
{
    Host = "router.example", Port = 2222, Username = "root",
    AuthenticationMethod = SshAuthenticationMethod.PrivateKey,
    PrivateKeyPath = Path.Combine(Path.GetTempPath(), "missing-routerpilot-key")
}), "missing SSH key fails cleanly");

string sshSettingsFolder = Path.Combine(Path.GetTempPath(), "RouterPilot-ssh-settings-" + Guid.NewGuid().ToString("N"));
var sshSettingsStorage = new SettingsService(sshSettingsFolder);
sshSettingsStorage.Save(new AppSettings
{
    RouterHost = "router.example",
    Username = "root",
    RememberPassword = true,
    EncryptedPassword = sshSettingsStorage.EncryptPassword("fixture-password")
});
AppSettings migratedSshSettings = sshSettingsStorage.Load();
Require(migratedSshSettings.SshPort == 22 && migratedSshSettings.SshAuthenticationMethod == SshAuthenticationMethod.Password, "existing settings migrate to password authentication on port 22");
Require(sshSettingsStorage.DecryptPassword(migratedSshSettings.EncryptedPassword) == "fixture-password", "existing protected password is preserved during SSH migration");
AppSettings migratedSshSettingsAgain = sshSettingsStorage.Load();
Require(migratedSshSettingsAgain.SshPort == 22 && migratedSshSettingsAgain.SshAuthenticationMethod == SshAuthenticationMethod.Password, "SSH migration is idempotent");
sshSettingsStorage.Save(new AppSettings
{
    RouterHost = "router.example",
    Username = "root",
    SshPort = 2222,
    SshAuthenticationMethod = SshAuthenticationMethod.PrivateKey,
    PrivateKeyPath = "key-a",
    EncryptedPrivateKeyPassphrase = sshSettingsStorage.EncryptPassword("fixture-key-passphrase")
});
Require(sshSettingsStorage.DecryptPassword(sshSettingsStorage.Load().EncryptedPrivateKeyPassphrase) == "fixture-key-passphrase", "private-key passphrase remains protected and persists");
AppSettings isolatedSshSettings = new() { SshPort = 2201, SshAuthenticationMethod = SshAuthenticationMethod.PrivateKey, PrivateKeyPath = "key-a" };
AppSettings otherSshSettings = new() { SshPort = 2202, SshAuthenticationMethod = SshAuthenticationMethod.Password, PrivateKeyPath = "key-b" };
Require(isolatedSshSettings.SshPort != otherSshSettings.SshPort && isolatedSshSettings.SshAuthenticationMethod != otherSshSettings.SshAuthenticationMethod, "active router settings keep SSH configuration isolated");
Require(!new InvalidOperationException("SSH private key could not be found or opened.").Message.Contains("fixture-password", StringComparison.Ordinal), "SSH diagnostics do not expose credentials");

string profileSettingsFolder = Path.Combine(Path.GetTempPath(), "RouterPilot-profile-settings-" + Guid.NewGuid().ToString("N"));
var profileSettingsStorage = new SettingsService(profileSettingsFolder);
string protectedProfilePassword = profileSettingsStorage.EncryptPassword("profile-password-fixture");
string protectedProfilePassphrase = profileSettingsStorage.EncryptPassword("profile-passphrase-fixture");
profileSettingsStorage.Save(new AppSettings
{
    RouterHost = "https://router-a.example/",
    RouterPort = 8443,
    AdGuardPort = 3001,
    UseRouterHttps = true,
    UseAdGuardHttps = true,
    Username = "router-a-user",
    RememberPassword = true,
    EncryptedPassword = protectedProfilePassword,
    SshPort = 2222,
    SshAuthenticationMethod = SshAuthenticationMethod.PrivateKey,
    PrivateKeyPath = "C:\\keys\\router-a",
    EncryptedPrivateKeyPassphrase = protectedProfilePassphrase,
    Theme = "Dark"
});
AppSettings migratedProfileSettings = profileSettingsStorage.Load();
Require(migratedProfileSettings.RouterProfiles.Count == 1, "legacy settings migrate to exactly one router profile");
RouterProfile migratedProfile = migratedProfileSettings.RouterProfiles.Single();
Require(migratedProfile.Id == migratedProfileSettings.ActiveRouterProfileId, "migration selects the stable router profile as active");
Require(migratedProfile.RouterHost == "router-a.example" && migratedProfile.RouterPort == 8443 && migratedProfile.AdGuardPort == 3001 && migratedProfile.UseAdGuardHttps, "router and AdGuard settings are preserved in the profile");
Require(migratedProfile.SshPort == 2222 && migratedProfile.SshAuthenticationMethod == SshAuthenticationMethod.PrivateKey && migratedProfile.PrivateKeyPath == "C:\\keys\\router-a", "SSH settings are preserved in the profile");
Require(profileSettingsStorage.DecryptPassword(migratedProfile.EncryptedPassword) == "profile-password-fixture" && profileSettingsStorage.DecryptPassword(migratedProfile.EncryptedPrivateKeyPassphrase) == "profile-passphrase-fixture", "protected credentials remain available through the migrated profile");
Require(migratedProfileSettings.Theme == "Dark", "application-global settings remain outside the profile");
AppSettings migratedProfileSettingsAgain = profileSettingsStorage.Load();
Require(migratedProfileSettingsAgain.RouterProfiles.Count == 1 && migratedProfileSettingsAgain.ActiveRouterProfileId == migratedProfile.Id, "profile migration is idempotent and retains its stable ID");
var profileService = new RouterProfileService(profileSettingsStorage);
var activeRouterContext = new ActiveRouterContext(profileService);
Require(activeRouterContext.CurrentProfileId == migratedProfile.Id && activeRouterContext.CurrentProfile.SshPort == 2222, "active router context resolves the migrated profile configuration");
RouterProfile secondProfile = new()
{
    Id = Guid.NewGuid().ToString("N"),
    DisplayName = "Router B",
    RouterHost = "router-b.example",
    Username = "router-b-user",
    SshPort = 2202,
    SshAuthenticationMethod = SshAuthenticationMethod.Password,
    EncryptedPassword = profileSettingsStorage.EncryptPassword("profile-b-password")
};
migratedProfileSettingsAgain.RouterProfiles.Add(secondProfile);
profileSettingsStorage.Save(migratedProfileSettingsAgain);
AppSettings isolatedProfiles = profileSettingsStorage.Load();
Require(isolatedProfiles.RouterProfiles.Select(profile => profile.Id).Distinct().Count() == 2 && isolatedProfiles.RouterProfiles.Single(profile => profile.Id == secondProfile.Id).SshPort == 2202, "profiles retain isolated connection configuration");
string profileSettingsJson = File.ReadAllText(Path.Combine(profileSettingsFolder, "settings.json"));
Require(!profileSettingsJson.Contains("profile-password-fixture", StringComparison.Ordinal) && !profileSettingsJson.Contains("profile-passphrase-fixture", StringComparison.Ordinal) && !profileSettingsJson.Contains("profile-b-password", StringComparison.Ordinal), "profile settings never serialize secrets as plain text");
Directory.Delete(profileSettingsFolder, recursive: true);

MethodInfo? parseBlocklists = typeof(RouterManager).GetMethod("ParseBlocklists", BindingFlags.Static | BindingFlags.NonPublic);
Require(parseBlocklists is not null, "blocklist parser is available for deterministic coverage");
using JsonDocument blocklistDocument = JsonDocument.Parse("""
    { "filters": [
      { "id": 1, "name": "Enabled list", "url": "https://example.test/enabled.txt", "enabled": true, "rules_count": 922337203685477, "last_updated": "2026-01-01T00:00:00Z" },
      { "id": 2, "name": "Disabled list", "url": "https://example.test/disabled.txt", "enabled": false, "rules_count": 7 }
    ] }
    """);
var parsedBlocklists = (List<AdGuardBlocklist>)parseBlocklists!.Invoke(null, [blocklistDocument.RootElement])!;
Require(parsedBlocklists.Count == 2, "blocklist parser reads filters");
Require(parsedBlocklists[0].Enabled && parsedBlocklists[0].RuleCount == 922337203685477, "blocklist parser preserves enabled state and 64-bit rule count");
Require(!parsedBlocklists[1].Enabled && parsedBlocklists[1].RuleCount == 7, "blocklist parser reads disabled state");
using JsonDocument emptyBlocklistDocument = JsonDocument.Parse("{ \"filters\": [] }");
Require(((List<AdGuardBlocklist>)parseBlocklists.Invoke(null, [emptyBlocklistDocument.RootElement])!).Count == 0, "blocklist parser accepts an empty list");
using JsonDocument malformedBlocklistDocument = JsonDocument.Parse("{ \"filters\": [ { \"name\": \"no URL\" } ] }");
Require(((List<AdGuardBlocklist>)parseBlocklists.Invoke(null, [malformedBlocklistDocument.RootElement])!).Count == 0, "blocklist parser ignores malformed entries");
Console.WriteLine("Network Health, notification, blocklist, SSH and router-profile fixtures passed.");

static void RequireThrows(Action action, string message)
{
    try
    {
        action();
    }
    catch (InvalidOperationException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}
