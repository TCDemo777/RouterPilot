using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;
using RouterPilot.Configuration;

namespace RouterPilot.Services
{
    public partial class RouterManager : IDisposable
    {
        private readonly GLInetSshService _ssh;
        private readonly GLInetSessionService _sessionService;
        private readonly string _routerIp;
        private readonly RouterInfoService _routerInfo;
        private readonly NetworkService _network;
        private readonly CookieContainer _adGuardCookies;
        private readonly HttpClient _adGuardClient;
        private readonly Uri _adGuardBaseUri;
        private readonly AdGuardTransportSecurityService _adGuardTransportSecurity;
        private readonly object _adGuardCookieLock = new();
        private bool _disposed;
        private IReadOnlyList<DhcpConfigurationInfo>? _dhcpConfigurationCache;
        private IReadOnlyList<DhcpReservationInfo>? _dhcpReservationCache;
        private IReadOnlyList<DhcpNetworkScopeInfo>? _dhcpScopeCache;
        private readonly SemaphoreSlim _dhcpScopeGate = new(1, 1);

        private readonly SemaphoreSlim _tokenLock =
            new SemaphoreSlim(1, 1);

        private string? _adminToken;

        public RouterManager(
            string routerIp,
            string username,
            string password,
            ISshHostKeyTrustService hostKeyTrustService,
            IRouterCertificateTrustService certificateTrustService,
            int adGuardPort,
            bool useAdGuardHttps,
            AdGuardTransportSecurityService adGuardTransportSecurity)
        {
            if (string.IsNullOrWhiteSpace(routerIp))
            {
                throw new ArgumentException(
                    "Router IP address cannot be empty.",
                    nameof(routerIp));
            }

            if (string.IsNullOrWhiteSpace(username))
            {
                throw new ArgumentException(
                    "Username cannot be empty.",
                    nameof(username));
            }

            if (string.IsNullOrWhiteSpace(password))
            {
                throw new ArgumentException(
                    "Password cannot be empty.",
                    nameof(password));
            }

            _routerIp =
                NormaliseRouterHost(
                    routerIp);

            if (adGuardPort is < 1 or > 65535)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(adGuardPort),
                    "AdGuard port must be between 1 and 65535.");
            }

            _adGuardTransportSecurity = adGuardTransportSecurity ??
                throw new ArgumentNullException(
                    nameof(adGuardTransportSecurity));

            _ssh =
                new GLInetSshService(
                    _routerIp,
                    username,
                    password,
                    hostKeyTrustService);

            _sessionService =
                new GLInetSessionService(
                    _routerIp,
                    username,
                    password,
                    certificateTrustService);

            _routerInfo =
                new RouterInfoService(
                    _ssh);

            _network =
                new NetworkService(
                    _ssh);

            _adGuardBaseUri = new UriBuilder(
                useAdGuardHttps
                    ? Uri.UriSchemeHttps
                    : Uri.UriSchemeHttp,
                _routerIp,
                adGuardPort,
                "/").Uri;

            _adGuardCookies = new CookieContainer();

            var adGuardHandler = new HttpClientHandler
            {
                CookieContainer = _adGuardCookies,
                UseCookies = true,
                AutomaticDecompression =
                    DecompressionMethods.GZip |
                    DecompressionMethods.Deflate
            };

            _adGuardClient = new HttpClient(adGuardHandler)
            {
                BaseAddress = _adGuardBaseUri,
                Timeout = TimeSpan.FromSeconds(10)
            };

            _adGuardClient.DefaultRequestHeaders
                .Accept
                .ParseAdd("application/json");
        }

        //
        // Router
        //

        public Task<RouterInfo> GetRouterInfoAsync()
        {
            return _routerInfo
                .GetRouterInfoAsync();
        }

        /// <summary>
        /// Performs a read-only inventory of known speed-test executables. The
        /// result is intentionally conservative: RouterPilot does not execute a
        /// detected binary until it has a verified Internet-test protocol and
        /// safe fixed arguments for that backend.
        /// </summary>
        public async Task<RouterSpeedTestCapability> DiscoverSpeedTestCapabilityAsync(
            CancellationToken cancellationToken = default)
        {
            const string discoveryCommand =
                "for tool in speedtest speedtest-cli speedtest-netperf speedtestpp librespeed-cli iperf3 netperf; do " +
                "if command -v \"$tool\" >/dev/null 2>&1; then printf '%s\\n' \"$tool\"; fi; done";

            string output = await _ssh.RunCommandAsync(discoveryCommand, cancellationToken);
            if (output.StartsWith("SSH_", StringComparison.OrdinalIgnoreCase))
                return new RouterSpeedTestCapability { SafeStatus = "ssh-unavailable" };

            string? detected = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim())
                .FirstOrDefault(value => value is "speedtest" or "speedtest-cli" or "speedtest-netperf" or
                    "speedtestpp" or "librespeed-cli" or "iperf3" or "netperf");

            return new RouterSpeedTestCapability
            {
                // iperf3/netperf alone are not Internet tests without a verified
                // remote server, and no installed CLI is assumed safe to invoke
                // with guessed provider-specific arguments.
                IsSupported = false,
                DetectedBinary = detected,
                SafeStatus = detected is null ? "unavailable" : "unverified-backend"
            };
        }

        /// <summary>
        /// Uses the stock GL.iNet SDK4 upgrade service's read-only
        /// <c>check_firmware_online</c> RPC. This method never downloads or installs firmware.
        /// </summary>
        public async Task<FirmwareUpdateCheck> CheckFirmwareUpdateAsync(
            CancellationToken cancellationToken = default)
        {
            string sessionId = await _sessionService.GetAdminTokenAsync(cancellationToken);
            using JsonDocument document = await _sessionService.CallAsync(
                sessionId,
                "upgrade",
                "check_firmware_online",
                cancellationToken);

            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("result", out JsonElement result) ||
                result.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("The router firmware check returned no result.");
            }


            string current = ReadFirmwareValue(result,
                "current_version", "current_firmware_version", "version");
            string latest = ReadFirmwareValue(result,
                "new_firmware_version", "new_version", "version_new", "latest_version", "firmware_version");
            string channel = ReadFirmwareValue(result,
                "new_firmware_type", "current_type", "channel", "firmware_type");

            bool prompted = result.TryGetProperty("prompt", out JsonElement prompt) &&
                prompt.ValueKind is JsonValueKind.True;

            var check = new FirmwareUpdateCheck
            {
                CurrentVersion = current,
                LatestVersion = latest,
                ReleaseChannel = channel,
                ReleaseDate = ReadFirmwareDate(result, "new_firmware_time", "release_date", "date"),
                ReleaseNotesUrl = ValidateFirmwareUrl(ReadFirmwareValue(result,
                    "release_notes_url", "release_note_url", "changelog_url")),
                ReleaseNotes = ReadFirmwareValue(result,
                    "release_note", "release_notes"),
                DownloadUrl = ValidateFirmwareUrl(ReadFirmwareValue(result,
                    "new_firmware_url", "download_url", "url")),
                LastChecked = DateTimeOffset.UtcNow
            };

            if (TryCompareFirmwareVersions(current, latest, out int comparison))
            {
                check.Status = comparison < 0
                    ? FirmwareUpdateCheckStatus.UpdateAvailable
                    : FirmwareUpdateCheckStatus.UpToDate;
            }
            else
            {
                // A router prompt without a comparable concrete version is not enough
                // to claim an update. Keep the result explicit and safe.
                check.Status = FirmwareUpdateCheckStatus.NotAvailable;
                check.ErrorCategory = prompted
                    ? "version-comparison-unavailable"
                    : "version-unavailable";
            }

            return check;
        }

        private static string ReadFirmwareValue(JsonElement element, params string[] names)
        {
            foreach (string name in names)
            {
                if (element.TryGetProperty(name, out JsonElement value) &&
                    value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString()?.Trim() ?? string.Empty;
                }
            }
            return string.Empty;
        }

        private static DateTimeOffset? ReadFirmwareDate(JsonElement element, params string[] names)
        {
            string value = ReadFirmwareValue(element, names);
            return DateTimeOffset.TryParse(value, out DateTimeOffset date) ? date : null;
        }

        private static string? ValidateFirmwareUrl(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
                uri.Scheme != Uri.UriSchemeHttps)
            {
                return null;
            }

            string host = uri.Host;
            return host.Equals("gl-inet.com", StringComparison.OrdinalIgnoreCase) ||
                   host.EndsWith(".gl-inet.com", StringComparison.OrdinalIgnoreCase)
                ? uri.AbsoluteUri
                : null;
        }

        public static bool TryCompareFirmwareVersions(string current, string latest, out int comparison)
        {
            comparison = 0;
            static int[]? Parse(string value)
            {
                System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(
                    value ?? string.Empty, @"\d+(?:\.\d+)+");
                if (!match.Success)
                    return null;

                string[] parts = match.Value.Split('.');
                var result = new int[parts.Length];
                for (int index = 0; index < parts.Length; index++)
                {
                    if (!int.TryParse(parts[index], out result[index]))
                        return null;
                }
                return result;
            }

            int[]? left = Parse(current);
            int[]? right = Parse(latest);
            if (left is null || right is null)
                return false;

            int length = Math.Max(left.Length, right.Length);
            for (int index = 0; index < length; index++)
            {
                int l = index < left.Length ? left[index] : 0;
                int r = index < right.Length ? right[index] : 0;
                if (l == r) continue;
                comparison = l.CompareTo(r);
                return true;
            }
            return true;
        }

        //
        // Network
        //

        public Task<NetworkInfo> GetNetworkInfoAsync()
        {
            return _network
                .GetNetworkInfoAsync();
        }

        public async Task<List<WifiRadioInfo>> GetWifiRadiosAsync()
        {
            // Read configured APs first.  GL.iNet's own client service is then
            // used as the primary station source because MediaTek firmware does
            // not consistently expose associations through iw/iwinfo.
            string networkCommand = """
                configured_count=0
                for s in $(uci show wireless 2>/dev/null | sed -n 's/^wireless\.\([^.=]*\)=wifi-iface$/\1/p'); do
                    configured_count=$((configured_count + 1))
                    mode=$(uci -q get wireless.$s.mode)
                    [ -z "$mode" -o "$mode" = "ap" ] || continue
                    dev=$(uci -q get wireless.$s.device)
                    [ -n "$dev" ] || continue
                    ssid=$(uci -q get wireless.$s.ssid)
                    [ -n "$ssid" ] || ssid='Hidden network'
                    band=$(uci -q get wireless.$dev.band)
                    [ -n "$band" ] || band=$(uci -q get wireless.$dev.hwmode)
                    channel=$(uci -q get wireless.$dev.channel)
                    [ -n "$channel" ] || channel='auto'
                    encryption=$(uci -q get wireless.$s.encryption)
                    [ -n "$encryption" ] || encryption='open'
                    network=$(uci -q get wireless.$s.network)
                    width=$(uci -q get wireless.$dev.htmode)
                    disabled=$(uci -q get wireless.$s.disabled)
                    rdisabled=$(uci -q get wireless.$dev.disabled)
                    iface=''

                    for i in $(iw dev 2>/dev/null | awk '$1 == "Interface" { print $2 }'); do
                        runtime_ssid=$(iw dev "$i" info 2>/dev/null | sed -n 's/^[[:space:]]*ssid //p' | head -n1)
                        if [ "$runtime_ssid" = "$ssid" ]; then
                            iface="$i"
                            runtime_channel=$(iw dev "$i" info 2>/dev/null | awk '$1 == "channel" { print $2; exit }')
                            [ -n "$runtime_channel" ] && channel="$runtime_channel"
                            break
                        fi
                    done

                    state='Online'
                    [ "$disabled" = "1" -o "$rdisabled" = "1" ] && state='Disabled'
                    [ -z "$iface" -a "$state" = 'Online' ] && state='Configured'
                    display_iface="$iface"
                    [ -n "$display_iface" ] || display_iface="$dev"
                    printf 'N|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s\n' "$s" "$dev" "$display_iface" "$ssid" "$band" "$channel" "$encryption" "$state" "$network" "$width"
                done
                runtime_count=$(iw dev 2>/dev/null | awk '$1 == "Interface" { count++ } END { print count + 0 }')
                physical_count=$(uci show wireless 2>/dev/null | sed -n 's/^wireless\.\([^.=]*\)=wifi-device$/\1/p' | wc -l)
                printf 'D|configured|%s|runtime|%s|physical|%s|virtual|%s\n' "$configured_count" "$runtime_count" "$physical_count" "$configured_count"
                """;

            string networkOutput = await _ssh.RunCommandAsync(networkCommand);
            var networks = new List<WifiRadioInfo>();
            LogWifiDiscoveryResult("configured-networks", networkOutput);

            foreach (string line in networkOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = line.Split('|');
                if (parts.Length < 11 || parts[0] != "N")
                {
                    continue;
                }

                string rawBand = parts[5].Trim().ToLowerInvariant();
                string band = rawBand.Contains("2g") || rawBand.Contains("11g") || rawBand.Contains("11b")
                    ? "2.4 GHz"
                    : rawBand.Contains("5g") || rawBand.Contains("11a") || rawBand.Contains("11ac") || rawBand.Contains("11ax")
                        ? "5 GHz"
                        : rawBand.Contains("6g")
                            ? "6 GHz"
                            : InferBandFromChannel(parts[6]);

                networks.Add(new WifiRadioInfo
                {
                    Radio = string.IsNullOrWhiteSpace(parts[2]) ? "-" : parts[2].Trim(),
                    Interface = string.IsNullOrWhiteSpace(parts[3]) ? "-" : parts[3].Trim(),
                    Ssid = string.IsNullOrWhiteSpace(parts[4]) ? "Hidden network" : parts[4].Trim(),
                    Band = band,
                    Channel = string.IsNullOrWhiteSpace(parts[6]) ? "auto" : parts[6].Trim(),
                    Security = FormatWifiSecurity(parts[7]),
                    Status = string.IsNullOrWhiteSpace(parts[8]) ? "Configured" : parts[8].Trim(),
                    NetworkAssociation = string.IsNullOrWhiteSpace(parts[9]) ? "N/A" : parts[9].Trim(),
                    ChannelWidth = FormatWifiChannelWidth(parts[10]),
                    HardwareMode = string.IsNullOrWhiteSpace(parts[5]) ? "N/A" : parts[5].Trim(),
                    GuestClassification = ClassifyGuestNetwork(parts[9], parts[3], parts[2])
                });
            }

            Debug.WriteLine(
                $"[WiFiDiscovery] stage=configured-networks parsed={networks.Count} " +
                $"interfaces={ReadDiscoveryCount(networkOutput, "runtime")} " +
                $"physical={ReadDiscoveryCount(networkOutput, "physical")} " +
                $"virtual={ReadDiscoveryCount(networkOutput, "virtual")} " +
                $"configured={ReadDiscoveryCount(networkOutput, "configured")}");

            if (networks.Count == 0)
            {
                string reason = ReadDiscoveryCount(networkOutput, "configured") == 0
                    ? "no-configured-interfaces"
                    : "parsing-failure";
                Debug.WriteLine(
                    $"[WiFiDiscovery] stage=configured-networks reason={reason}");
            }

            if (networks.Count == 0)
            {
                (List<WifiRadioInfo> fallbackNetworks, string fallbackOutput) =
                    await DiscoverWifiRadiosFromHostapdAsync();
                networks = fallbackNetworks;
                Debug.WriteLine(
                    $"[WiFiDiscovery] stage=hostapd-fallback parsed={networks.Count} " +
                    $"uci={ReadDiscoveryCount(fallbackOutput, "uci")} " +
                    $"hostapd={ReadDiscoveryCount(fallbackOutput, "hostapd")}");
            }

            if (networks.Count == 0)
            {
                Debug.WriteLine(
                    "[WiFiDiscovery] stage=complete reason=no-interfaces-returned");
                return networks;
            }

            // GL.iNet firmware's client service knows the connection type even
            // where the MediaTek driver returns an empty station dump.
            string clientJson = await _ssh.RunCommandAsync(
                "ubus call gl-clients list 2>/dev/null || true");

            if (!string.IsNullOrWhiteSpace(clientJson))
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(clientJson);
                    foreach (JsonElement client in EnumerateClientObjects(document.RootElement))
                    {
                        if (!GetFlexibleBoolean(client, "online", true))
                        {
                            continue;
                        }

                        string iface = GetFlexibleString(client, "iface", "interface", "connection", "type");
                        string band = NormaliseClientBand(iface);
                        if (band.Length == 0)
                        {
                            continue; // Cable/VPN clients do not belong to a Wi-Fi card.
                        }

                        WifiRadioInfo? network = FindClientNetwork(networks, client, band);
                        if (network == null)
                        {
                            continue;
                        }

                        string mac = GetFlexibleString(client, "mac", "macaddr", "mac_address");
                        if (mac.Length == 0 || network.Clients.Any(c =>
                                string.Equals(c.MacAddress, mac, StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        string name = GetFlexibleString(client, "name", "hostname", "host_name");
                        string ip = GetFlexibleString(client, "ip", "ipaddr", "ip_address");
                        string signal = GetFlexibleString(client, "signal", "rssi", "wifi_signal", "signal_strength", "rssi_dbm");

                        network.Clients.Add(new WifiClientInfo
                        {
                            Name = string.IsNullOrWhiteSpace(name) ? "Unknown device" : name,
                            IpAddress = string.IsNullOrWhiteSpace(ip) ? "-" : ip,
                            MacAddress = mac,
                            Signal = FormatSignal(signal),
                            Band = band,
                            Interface = string.IsNullOrWhiteSpace(iface) ? network.Interface : iface,
                            Ssid = network.Ssid
                        });
                    }
                }
                catch (JsonException)
                {
                    // Keep the configured networks visible.  Older firmware may
                    // briefly return an empty or incomplete gl-clients payload.
                }
            }

            // Virtual and guest APs are often visible to hostapd even when the
            // kernel station dump is empty. Query hostapd first, then use iw as
            // a second source for drivers that do expose a station table.
            await EnrichWifiClientsFromHostapdAsync(networks);
            await EnrichWifiClientsFromStationDumpAsync(networks);

            Debug.WriteLine(
                $"[WiFiDiscovery] stage=complete records={networks.Count} " +
                $"clients={networks.Sum(network => network.ClientCount)}");

            return networks;
        }

        /// <summary>
        /// Reads DHCP state using only /tmp/dhcp.leases and UCI's read-only
        /// show command. DHCP configuration is cached for this RouterManager
        /// session; active leases remain part of the normal refresh lifecycle.
        /// </summary>
        public async Task<DhcpSnapshot> GetDhcpSnapshotAsync(bool forceConfigurationRefresh = false)
        {
            // DHCP host configuration may have changed outside RouterPilot. A
            // bounded user-requested refresh can discard only this read cache;
            // it still uses the established UCI and lease read path.
            if (forceConfigurationRefresh)
            {
                _dhcpConfigurationCache = null;
                _dhcpReservationCache = null;
            }
            if (_dhcpConfigurationCache is null || _dhcpReservationCache is null)
            {
                string configurationOutput = await _ssh.RunCommandAsync("uci show dhcp 2>/dev/null || true");
                (List<DhcpConfigurationInfo> configurations, List<DhcpReservationInfo> reservations) =
                    ParseDhcpConfiguration(configurationOutput);
                _dhcpConfigurationCache = configurations;
                _dhcpReservationCache = reservations;
            }

            string leaseOutput = await _ssh.RunCommandAsync("cat /tmp/dhcp.leases 2>/dev/null || true");
            List<DhcpLeaseInfo> leases = ParseDhcpLeaseSnapshot(leaseOutput);
            IReadOnlyList<DhcpNetworkScopeInfo> scopes = await GetDhcpNetworkScopesAsync(CancellationToken.None);
            CorrelateDhcpScopes(leases, _dhcpReservationCache, scopes);
            List<string> warnings = DetectDhcpConflicts(_dhcpReservationCache, leases);

            return new DhcpSnapshot
            {
                Configurations = _dhcpConfigurationCache,
                Reservations = _dhcpReservationCache,
                Leases = leases,
                Warnings = warnings
                ,Scopes = scopes
            };
        }

        public async Task<IReadOnlyList<DhcpNetworkScopeInfo>> GetDhcpNetworkScopesAsync(CancellationToken cancellationToken)
        {
            if (_dhcpScopeCache is not null) return _dhcpScopeCache;
            await _dhcpScopeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_dhcpScopeCache is not null) return _dhcpScopeCache;
                string raw = await _ssh.RunCommandAsync("uci show dhcp 2>/dev/null || true", cancellationToken);
                var sections = ParseDhcpUciSections(raw).Values.Where(s => s.Type.Equals("dhcp", StringComparison.OrdinalIgnoreCase) && !GetDhcpOption(s, "ignore").Equals("1", StringComparison.Ordinal)).ToList();
                var result = new List<DhcpNetworkScopeInfo>();
                foreach (DhcpUciSection section in sections)
                {
                    string iface = GetDhcpOption(section, "interface", string.Empty);
                    if (!Regex.IsMatch(iface, "^[A-Za-z0-9_-]+$")) { result.Add(new DhcpNetworkScopeInfo { ScopeId=section.Id, InterfaceName=iface, DisplayName=FriendlyScopeName(iface), DhcpEnabled=true, Status="Error", FailureCategory="Invalid interface identifier" }); continue; }
                    string output = await _ssh.RunCommandAsync($"ubus call network.interface.{iface} status 2>/dev/null", cancellationToken);
                    result.Add(ParseDhcpScope(section, iface, output));
                }
                _dhcpScopeCache = result;
                return _dhcpScopeCache;
            }
            finally { _dhcpScopeGate.Release(); }
        }

        private static DhcpNetworkScopeInfo ParseDhcpScope(DhcpUciSection section, string iface, string json)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;
                bool up = root.TryGetProperty("up", out JsonElement upValue) && upValue.ValueKind == JsonValueKind.True;
                var ips = root.TryGetProperty("ipv4-address", out JsonElement array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().Where(x => x.TryGetProperty("address", out _) && x.TryGetProperty("mask", out _)).ToList() : [];
                if (ips.Count != 1) return new DhcpNetworkScopeInfo { ScopeId=section.Id, InterfaceName=iface, DisplayName=FriendlyScopeName(iface), DhcpEnabled=true, InterfaceUp=up, LeaseTime=GetDhcpOption(section,"leasetime"), Status=ips.Count > 1 ? "Ambiguous" : "N/A", FailureCategory=ips.Count > 1 ? "Multiple IPv4 addresses" : "No IPv4 address" };
                string address = ips[0].GetProperty("address").GetString() ?? string.Empty;
                int prefix = ips[0].GetProperty("mask").GetInt32();
                if (!IPAddress.TryParse(address, out IPAddress? ip) || prefix is < 0 or > 32) throw new JsonException();
                (IPAddress netmask, IPAddress network, IPAddress broadcast) = GetIpv4Subnet(ip, prefix);
                int? start = int.TryParse(GetDhcpOption(section,"start"), out int s) ? s : null, limit = int.TryParse(GetDhcpOption(section,"limit"), out int l) ? l : null;
                string? rangeStart=null, rangeEnd=null;
                if (start >= 0 && limit > 0) { uint baseValue=ToUInt(network); uint end=baseValue+(uint)start.Value+(uint)limit.Value-1; if (end <= ToUInt(broadcast)) { rangeStart=FromUInt(baseValue+(uint)start.Value).ToString(); rangeEnd=FromUInt(end).ToString(); } }
                return new DhcpNetworkScopeInfo { ScopeId=section.Id, InterfaceName=iface, DisplayName=FriendlyScopeName(iface), DhcpEnabled=true, InterfaceUp=up, IPv4Address=address, PrefixLength=prefix, Netmask=netmask.ToString(), NetworkAddress=network.ToString(), BroadcastAddress=broadcast.ToString(), RouterAddress=address, DhcpStart=start, DhcpLimit=limit, DynamicRangeStart=rangeStart, DynamicRangeEnd=rangeEnd, LeaseTime=GetDhcpOption(section,"leasetime"), Status=up ? "Active" : "N/A" };
            }
            catch { return new DhcpNetworkScopeInfo { ScopeId=section.Id, InterfaceName=iface, DisplayName=FriendlyScopeName(iface), DhcpEnabled=true, LeaseTime=GetDhcpOption(section,"leasetime"), Status="Error", FailureCategory="Interface status unavailable" }; }
        }
        private static string FriendlyScopeName(string value) => value.Equals("lan",StringComparison.OrdinalIgnoreCase)?"LAN":value.Equals("guest",StringComparison.OrdinalIgnoreCase)?"Guest":value.Equals("iot",StringComparison.OrdinalIgnoreCase)?"IoT":value;
        private static (IPAddress,IPAddress,IPAddress) GetIpv4Subnet(IPAddress ip,int prefix) { uint mask=prefix==0?0:uint.MaxValue << (32-prefix), raw=ToUInt(ip), n=raw&mask; return (FromUInt(mask),FromUInt(n),FromUInt(n|~mask)); }
        private static uint ToUInt(IPAddress ip) { byte[] b=ip.GetAddressBytes(); return ((uint)b[0]<<24)|((uint)b[1]<<16)|((uint)b[2]<<8)|b[3]; }
        private static IPAddress FromUInt(uint value) => new(new[]{(byte)(value>>24),(byte)(value>>16),(byte)(value>>8),(byte)value});
        private static void CorrelateDhcpScopes(IEnumerable<DhcpLeaseInfo> leases, IEnumerable<DhcpReservationInfo> reservations, IReadOnlyList<DhcpNetworkScopeInfo> scopes) { foreach (var item in leases.Cast<dynamic>().Concat(reservations.Cast<dynamic>())) { var matches=scopes.Where(scope=>scope.DhcpEnabled && scope.ContainsAddress(IPAddress.TryParse((string)item.IpAddress,out var ip)?ip:IPAddress.None)).ToList(); item.ScopeDisplay=matches.Count==1?matches[0].DisplayName:matches.Count>1?"Ambiguous":"Unknown"; } }

#if DEBUG
        public async Task<string> GetPortForwardContractProbeReportAsync()
        {
            string firewall = await SafeFirewallProbeAsync("uci show firewall 2>/dev/null");
            string fw4 = await SafeFirewallProbeAsync("command -v fw4 2>/dev/null");
            string nft = await SafeFirewallProbeAsync("command -v nft 2>/dev/null");
            string iptables = await SafeFirewallProbeAsync("command -v iptables 2>/dev/null");
            string ubusList = await SafeFirewallProbeAsync("ubus list 2>/dev/null");
            string firewallService = await SafeFirewallProbeAsync("ubus call service list '{\"name\":\"firewall\"}' 2>/dev/null");
            string configPackages = await SafeFirewallProbeAsync("ls -1 /etc/config 2>/dev/null");
            string webProcesses = await SafeFirewallProbeAsync("ps w 2>/dev/null | grep -E '[u]httpd|[n]ginx|[l]ighttpd' | head -n 12");
            string uhttpdConfig = await SafeFirewallProbeAsync("uci show uhttpd 2>/dev/null");
            string nginxRoots = await SafeFirewallProbeAsync("grep -hE '^[[:space:]]*(root|alias)[[:space:]]+' /etc/nginx/nginx.conf /etc/nginx/conf.d/*.conf 2>/dev/null | head -n 24");
            string nginxRouting = await SafeFirewallProbeAsync("grep -nE '^[[:space:]]*(include|location|root|alias|proxy_pass|rewrite)[[:space:]]+' /etc/nginx/nginx.conf /etc/nginx/conf.d/*.conf 2>/dev/null | head -n 120");
            string uiPackagesOutput = await SafeFirewallProbeAsync("opkg list-installed 2>/dev/null | cut -d ' ' -f 1 | grep -Ei '(gl.*(ui|web)|luci|admin|portal|frontend)' | head -n 30");
            var uiPackages = uiPackagesOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim()).Where(IsSafePackageIdentifier).Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToList();
            var uiPackageFiles = new List<string>();
            foreach (string package in uiPackages)
                uiPackageFiles.AddRange((await SafeFirewallProbeAsync($"opkg files {package} 2>/dev/null")).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
            var assetRoots = ExtractProbeAssetRoots(uhttpdConfig, nginxRoots, uiPackageFiles).Take(16).ToList();
            string[] firewallUiPackages = { "gl-sdk4-ui-firewallview", "gl-sdk4-ui-advanced", "gl-sdk4-ui-core" };
            var firewallUiFiles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var firewallUiDependencies = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string package in firewallUiPackages)
            {
                firewallUiFiles[package] = (await SafeFirewallProbeAsync($"opkg files {package} 2>/dev/null"))
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(value => value.Trim()).Where(IsSafeFrontendAssetPath).ToList();
                firewallUiDependencies[package] = ExtractPackageDependencies(await SafeFirewallProbeAsync($"opkg status {package} 2>/dev/null"));
            }
            var firewallUiSearchableFiles = firewallUiFiles.Values.SelectMany(files => files).Where(IsSearchableProbeAssetFile).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string firewallUiMatches = await SearchProbeFilesAsync(firewallUiSearchableFiles, "grep -IlE 'port[_ -]?forward|portforward|redirect|dmz|nat|src_dport|dest_ip|dest_port|firewall|external port|internal port'");
            string firewallUiIdentifiers = await SearchProbeFilesAsync(firewallUiSearchableFiles, "grep -hoE '[A-Za-z][A-Za-z0-9_.-]*(port_forward|portforward|redirect|firewall|dmz|nat)[A-Za-z0-9_.-]*'");
            string firewallUiRpcTokens = await SearchProbeFilesAsync(firewallUiSearchableFiles, "grep -hoE '(module|func)[[:space:]]*:[[:space:]]*[^,}[:space:]]+'");
            var firewallBackendFiles = (await SafeFirewallProbeAsync("opkg files gl-sdk4-firewall 2>/dev/null"))
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(value => value.Trim()).Where(IsSafeProbeBackendPath).ToList();
            var firewallBackendDependencies = ExtractPackageDependencies(await SafeFirewallProbeAsync("opkg status gl-sdk4-firewall 2>/dev/null"));
            var firewallBackendSearchable = firewallBackendFiles.Where(IsSearchableProbeBackendFile).ToList();
            string firewallBackendIdentifiers = await SearchProbeBackendFilesAsync(firewallBackendSearchable, "grep -hoE '[A-Za-z][A-Za-z0-9_.-]*(port_forward|portforward|redirect|forward|dmz|open_port|firewall|src_dport|dest_ip|dest_port)[A-Za-z0-9_.-]*'");
            string firewallBackendFunctions = await SearchProbeBackendFilesAsync(firewallBackendSearchable, "grep -hoE 'function[[:space:]]+[A-Za-z0-9_.:]+'");
            string firewallBackendDispatch = await SearchProbeBackendFilesAsync(firewallBackendSearchable, "grep -hoE '(module|register|gl-session|ubus|rpc|oui)[A-Za-z0-9_.:-]*'");
            string firewallBackendApply = await SearchProbeBackendFilesAsync(firewallBackendSearchable, "grep -hoE '(uci[[:space:]]+commit[[:space:]]+firewall|/etc/init.d/firewall[[:space:]]+(reload|restart)|nft|iptables)'");
            string firewallRpcMetadata = await SafeFirewallProbeAsync("file -L /usr/lib/oui-httpd/rpc/firewall 2>/dev/null; ls -ld /usr/lib/oui-httpd/rpc/firewall 2>/dev/null; readlink -f /usr/lib/oui-httpd/rpc/firewall 2>/dev/null");
            string firewallRpcIdentifiers = await SafeFirewallProbeAsync("strings /usr/lib/oui-httpd/rpc/firewall 2>/dev/null | grep -E 'port_forward|portforward|redirect|dmz|firewall|add_|set_|del_|get_|list_|src_dport|dest_ip|dest_port|proto|module|func' | sort -u | head -n 180");
            string portForwardMutationSignatures = await SafeFirewallProbeAsync("strings -a /usr/lib/oui-httpd/rpc/firewall 2>/dev/null | grep -E -C 10 'add_port_forward|set_port_forward|remove_port_forward' | head -n 240");
            string firewallValidatorContract = await SafeFirewallProbeAsync("grep -n -E -C 8 'add_port_forward|set_port_forward|remove_port_forward|port|proto|dest_ip|dest_port|src_dport|forward|valid|required|range' /usr/share/gl-validator.d/firewall.lua 2>/dev/null | head -n 260");
            string firewallUiSource = await ReadFirewallViewSourceAsync();
            string addPortForwardCallSite = ExtractFrontendCallContexts(firewallUiSource, "add_port_forward");
            string setPortForwardCallSite = ExtractFrontendCallContexts(firewallUiSource, "set_port_forward");
            string removePortForwardCallSite = ExtractFrontendCallContexts(firewallUiSource, "remove_port_forward");
            string portForwardSupport = await SafeFirewallProbeAsync("grep -hE 'port_forward|firewall[.]pf|uci|nft|iptables|reload|restart|start|stop|add_|set_|del_|remove|dmz|src_dport|dest_ip|dest_port' /etc/init.d/port_forward /etc/firewall.pf /etc/uci-defaults/03_port_forward.sh 2>/dev/null | head -n 180");
            PortForwardReadProbeResult portForwardRead = await TryGetPortForwardListProbeAsync();
            string frontendFiles = await SearchProbeAssetsAsync(assetRoots, "grep -RIlE --include='*.js' --include='*.json' --include='*.html' --include='*.map' 'port[_ -]?forward|portforward|redirect|firewall|dmz|nat|forwarding'");
            string frontendIdentifiers = await SearchProbeAssetsAsync(assetRoots, "grep -RhoE --include='*.js' --include='*.json' --include='*.html' --include='*.map' '[A-Za-z][A-Za-z0-9_.-]*(port_forward|portforward|redirect|firewall|dmz|nat)[A-Za-z0-9_.-]*'");
            string frontendRpcTokens = await SearchProbeAssetsAsync(assetRoots, "grep -RhoE --include='*.js' --include='*.json' --include='*.html' --include='*.map' '(module|func)[[:space:]]*:[[:space:]]*[^,}[:space:]]+'");
            string frontendDispatchTokens = await SearchProbeAssetsAsync(assetRoots, "grep -RhoE --include='*.js' --include='*.json' --include='*.html' --include='*.map' '(gl-session[.]call|rpc[.]call|ubus[.]call)'");
            Dictionary<string, FirewallProbeSection> sections = ParseUciProbeSections(firewall, "firewall");
            var counts = sections.Values.GroupBy(s => s.Type).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count());
            var candidates = FindPortForwardCandidates(sections.Values).ToList();
            var ubusObjects = ubusList.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim()).Where(IsRelevantPortForwardObject).Take(24).ToList();
            var frontendFileNames = frontendFiles.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim()).Where(IsSafeFrontendAssetPath).Take(24).ToList();
            var frontendApiIdentifiers = frontendIdentifiers.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim()).Where(value => value.Length <= 160 && IsSafeProbeIdentifier(value)).Take(64).ToList();
            var frontendRpcIdentifiers = frontendRpcTokens.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim()).Where(IsSafeFrontendRpcToken).Take(80).ToList();
            var frontendDispatchIdentifiers = frontendDispatchTokens.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim()).Where(value => value.Length <= 64 && IsSafeProbeIdentifier(value)).Take(24).ToList();
            var packageNames = configPackages.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim()).Where(IsRelevantPortForwardPackage).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
            var packageSections = new List<FirewallProbeSection>();
            foreach (string package in packageNames)
            {
                string output = package.Equals("firewall", StringComparison.OrdinalIgnoreCase)
                    ? firewall : await SafeFirewallProbeAsync($"uci show {package} 2>/dev/null");
                packageSections.AddRange(ParseUciProbeSections(output, package).Values);
            }
            var overlayCandidates = FindPortForwardCandidates(packageSections)
                .Where(section => !section.Package.Equals("firewall", StringComparison.OrdinalIgnoreCase)).ToList();
            var dmzRecords = packageSections.Where(section => section.Type.Contains("dmz", StringComparison.OrdinalIgnoreCase) ||
                section.Id.Contains("dmz", StringComparison.OrdinalIgnoreCase) || section.Options.Keys.Any(key => key.Contains("dmz", StringComparison.OrdinalIgnoreCase))).ToList();
            bool dynamicMappingSource = ubusObjects.Any(name => name.Contains("upnp", StringComparison.OrdinalIgnoreCase) || name.Contains("pcp", StringComparison.OrdinalIgnoreCase) || name.Contains("natpmp", StringComparison.OrdinalIgnoreCase)) ||
                packageNames.Any(name => name.Contains("upnp", StringComparison.OrdinalIgnoreCase) || name.Contains("pcp", StringComparison.OrdinalIgnoreCase) || name.Contains("natpmp", StringComparison.OrdinalIgnoreCase));
            var allForwardCandidates = candidates.Concat(overlayCandidates).ToList();
            IReadOnlyList<DhcpNetworkScopeInfo> scopes = allForwardCandidates.Count == 0 ? Array.Empty<DhcpNetworkScopeInfo>() : await GetDhcpNetworkScopesAsync(CancellationToken.None);
            List<WifiClientInfo> clients = allForwardCandidates.Count == 0 ? new List<WifiClientInfo>() : await GetGlClientInventoryAsync();
            var potentialWriteMethods = new List<string>();
            var report = new StringBuilder();
            report.AppendLine("RouterPilot Port Forwarding Contract Probe");
            report.AppendLine("(Debug only)");
            report.AppendLine("LOCAL NETWORK INFORMATION - DO NOT PUBLISH");
            report.AppendLine();
            report.AppendLine("Firewall section summary");
            foreach (var count in counts) report.AppendLine($"- {count.Key}: {count.Value}");
            report.AppendLine();
            report.AppendLine($"Port-forward candidate count: {candidates.Count}");
            AppendPortForwardCandidates(report, candidates, scopes, clients);
            string identityStyle = candidates.Count == 0 ? "Not applicable" : candidates.Any(s => s.Id.StartsWith("@", StringComparison.Ordinal)) &&
                candidates.Any(s => !s.Id.StartsWith("@", StringComparison.Ordinal)) ? "mixed" :
                candidates.Any(s => s.Id.StartsWith("@", StringComparison.Ordinal)) ? "anonymous" : "named";
            report.AppendLine();
            report.AppendLine($"Named vs anonymous candidates: {identityStyle}");
            report.AppendLine();
            report.AppendLine("GL.iNet Port Forwarding Discovery");
            report.AppendLine("GL.iNet Port Forwarding UI state: 0 configured rules (user-confirmed reference)");
            report.AppendLine("GL.iNet Admin UI Discovery");
            report.AppendLine($"Web server: {(string.IsNullOrWhiteSpace(webProcesses) ? "Not identified" : string.Join(" | ", webProcesses.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(value => value.Trim())))}");
            report.AppendLine($"Document root / asset roots: {(assetRoots.Count == 0 ? "Not identified" : string.Join(", ", assetRoots))}");
            report.AppendLine($"GL.iNet UI packages: {(uiPackages.Count == 0 ? "None found" : string.Join(", ", uiPackages))}");
            report.AppendLine("SDK4 UI Package Inspection");
            foreach (string package in firewallUiPackages)
            {
                report.AppendLine($"{package} dependencies: {(firewallUiDependencies[package].Count == 0 ? "None reported" : string.Join(", ", firewallUiDependencies[package]))}");
                report.AppendLine($"{package} files ({firewallUiFiles[package].Count}):");
                foreach (string file in firewallUiFiles[package]) report.AppendLine($"- {file} [{ClassifyProbeAssetFile(file)}]");
            }
            report.AppendLine($"SDK4 matching files: {(string.IsNullOrWhiteSpace(firewallUiMatches) ? "None found" : string.Join(", ", firewallUiMatches.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(value => value.Trim())))}");
            report.AppendLine($"SDK4 route/API identifiers: {(string.IsNullOrWhiteSpace(firewallUiIdentifiers) ? "None found" : string.Join(", ", firewallUiIdentifiers.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(value => value.Trim()).Where(IsSafeProbeIdentifier).Take(80)))}");
            report.AppendLine($"SDK4 RPC module/function tokens: {(string.IsNullOrWhiteSpace(firewallUiRpcTokens) ? "None found" : string.Join("; ", firewallUiRpcTokens.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(value => value.Trim()).Where(IsSafeFrontendRpcToken).Take(80)))}");
            report.AppendLine($"nginx admin UI routing: {(string.IsNullOrWhiteSpace(nginxRouting) ? "No relevant routes found" : string.Join(" | ", nginxRouting.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(value => value.Trim())))}");
            report.AppendLine("gl-sdk4-firewall Backend Inspection");
            report.AppendLine($"gl-sdk4-firewall dependencies: {(firewallBackendDependencies.Count == 0 ? "None reported" : string.Join(", ", firewallBackendDependencies))}");
            report.AppendLine($"gl-sdk4-firewall files ({firewallBackendFiles.Count}):");
            foreach (string file in firewallBackendFiles) report.AppendLine($"- {file} [{ClassifyProbeBackendFile(file)}]");
            report.AppendLine($"Backend identifiers: {SummariseProbeOutput(firewallBackendIdentifiers)}");
            report.AppendLine($"Backend functions: {SummariseProbeOutput(firewallBackendFunctions)}");
            report.AppendLine($"Backend dispatch/registration identifiers: {SummariseProbeOutput(firewallBackendDispatch)}");
            report.AppendLine($"Backend apply/storage identifiers: {SummariseProbeOutput(firewallBackendApply)}");
            report.AppendLine("GL.iNet Port Forward Backend");
            report.AppendLine($"RPC endpoint: /usr/lib/oui-httpd/rpc/firewall");
            report.AppendLine($"RPC endpoint implementation metadata: {SummariseProbeOutput(firewallRpcMetadata)}");
            report.AppendLine($"RPC endpoint identifiers: {SummariseProbeOutput(firewallRpcIdentifiers)}");
            report.AppendLine($"Mutation signature evidence (static; not called): {SummariseProbeOutput(portForwardMutationSignatures)}");
            report.AppendLine($"Validator evidence (static): {SummariseProbeOutput(firewallValidatorContract)}");
            report.AppendLine("Port Forward Mutation Contract — Static Verification");
            report.AppendLine($"Add call-site context: {SummariseProbeContractOutput(addPortForwardCallSite)}");
            report.AppendLine($"Edit call-site context: {SummariseProbeContractOutput(setPortForwardCallSite)}");
            report.AppendLine($"Delete call-site context: {SummariseProbeContractOutput(removePortForwardCallSite)}");
            report.AppendLine("ADD: add_port_forward — signature pending frontend/validator extraction.");
            report.AppendLine("EDIT: set_port_forward — signature and current-rule identity pending frontend/validator extraction.");
            report.AppendLine("DELETE: remove_port_forward — signature and current-rule identity pending frontend/validator extraction.");
            report.AppendLine($"Port-forward support evidence: {SummariseProbeOutput(portForwardSupport)}");
            report.AppendLine("Verified Port Forward Read Contract");
            report.AppendLine("RPC module: firewall (fixed endpoint mapping; verified only if the call below succeeds)");
            report.AppendLine("Function: get_port_forward_list");
            report.AppendLine("Parameters: {}");
            report.AppendLine("Read-only evidence: distinct get_port_forward_list endpoint; no apply/reload/mutation method is invoked by this probe.");
            report.AppendLine($"Invocation: {(portForwardRead.Success ? "Available" : "Unavailable")}");
            report.AppendLine($"Response structure: {portForwardRead.ResponseShape}");
            report.AppendLine($"Rule list field: {portForwardRead.RuleListField}");
            report.AppendLine($"Configured rule count: {(portForwardRead.RuleCount.HasValue ? portForwardRead.RuleCount.Value.ToString() : "Unavailable")}");
            report.AppendLine("Potential mutation functions: add_port_forward — NOT CALLED; set_port_forward — NOT CALLED; remove_port_forward — NOT CALLED.");
            report.AppendLine("Rule identity evidence: old_proto and old_dest_port are present in the backend; parameter contracts remain unverified.");
            report.AppendLine($"Web UI asset matches: {(frontendFileNames.Count == 0 ? "None found" : string.Join(", ", frontendFileNames))}");
            report.AppendLine($"Web UI API identifiers: {(frontendApiIdentifiers.Count == 0 ? "None found" : string.Join(", ", frontendApiIdentifiers))}");
            report.AppendLine($"Web UI RPC module/function tokens: {(frontendRpcIdentifiers.Count == 0 ? "None found" : string.Join("; ", frontendRpcIdentifiers))}");
            report.AppendLine($"Web UI dispatch identifiers: {(frontendDispatchIdentifiers.Count == 0 ? "None found" : string.Join(", ", frontendDispatchIdentifiers))}");
            report.AppendLine("RouterPilot GL.iNet RPC: authenticated /rpc Ubus dispatcher; no existing port-forward service identifier is hard-coded.");
            report.AppendLine($"Relevant ubus objects: {(ubusObjects.Count == 0 ? "None found" : string.Join(", ", ubusObjects))}");
            foreach (string ubusObject in ubusObjects)
            {
                string signature = await SafeFirewallProbeAsync($"ubus -v list {ubusObject} 2>/dev/null");
                report.AppendLine($"- {ubusObject}: {(string.IsNullOrWhiteSpace(signature) ? "Unavailable" : "Available")}");
                foreach (UbusProbeMethod method in ExtractUbusMethods(signature))
                {
                    string classification = IsClearlyReadOnlyMethod(method.Name) ? "READ" : IsLikelyWriteMethod(method.Name) ? "LIKELY WRITE" : "UNKNOWN";
                    report.AppendLine($"  {method.Name}: {classification}; signature: {method.Signature}");
                    if (classification == "LIKELY WRITE") potentialWriteMethods.Add($"{ubusObject}.{method.Name}");
                    if (classification == "READ" && IsParameterlessReadSignature(method.Signature))
                    {
                        string readResult = await SafeFirewallProbeAsync($"ubus call {ubusObject} {method.Name} '{{}}' 2>/dev/null");
                        report.AppendLine($"    read invocation: {(string.IsNullOrWhiteSpace(readResult) ? "Unavailable" : "Available")}");
                    }
                }
            }
            report.AppendLine($"Relevant UCI/config packages: {(packageNames.Count == 0 ? "None found" : string.Join(", ", packageNames))}");
            report.AppendLine($"Static forwarding records outside firewall UCI: {overlayCandidates.Count}");
            AppendPortForwardCandidates(report, overlayCandidates, scopes, clients);
            report.AppendLine($"Dynamic UPnP/NAT-PMP/PCP mapping source: {(dynamicMappingSource ? "Discovered (not invoked)" : "Not discovered")}");
            report.AppendLine($"DMZ support/include configuration: {dmzRecords.Count}");
            foreach (FirewallProbeSection section in dmzRecords) report.AppendLine($"- {section.Package}.{section.Id}; Type: {section.Type}; Options: {string.Join(", ", section.Options.Keys.OrderBy(key => key))}");
            report.AppendLine("Actual DMZ/exposed-host state: Not discovered.");
            string backend = !string.IsNullOrWhiteSpace(fw4) ? "fw4" : !string.IsNullOrWhiteSpace(nft) ? "nftables binary present" : !string.IsNullOrWhiteSpace(iptables) ? "iptables binary present" : "Not identified";
            string preferredBackend = overlayCandidates.Count > 0 ? "Discovered GL.iNet UCI/config source (not yet verified)" : candidates.Count > 0 ? "Standard firewall UCI" : ubusObjects.Count > 0 ? "Relevant ubus service discovery required" : "Not discovered";
            report.AppendLine($"Firewall backend: {backend}");
            report.AppendLine($"Firewall service state: {(string.IsNullOrWhiteSpace(firewallService) ? "Unavailable" : "Available")}");
            report.AppendLine($"firewall/service ubus objects: {(ubusObjects.Any(name => name.Equals("firewall", StringComparison.OrdinalIgnoreCase) || name.Equals("service", StringComparison.OrdinalIgnoreCase)) ? "Available" : "Unavailable")}");
            report.AppendLine($"Preferred future read backend: {preferredBackend}");
            report.AppendLine(overlayCandidates.Count > 0 ? "GL.iNet overlay: Structured static forwarding candidates were discovered; contract remains unverified." : "GL.iNet overlay: No structured static forwarding records were found in inspected relevant UCI packages.");
            report.AppendLine($"Potential write methods (discovered - not called): {(potentialWriteMethods.Count == 0 ? "None" : string.Join(", ", potentialWriteMethods.Distinct(StringComparer.OrdinalIgnoreCase)))}");
            report.AppendLine();
            report.AppendLine("GL.iNet RPC Port Forwarding Module Discovery");
            report.AppendLine("Port Forward page identifier: Not identified unless shown by the targeted Web UI asset results above.");
            report.AppendLine($"RPC module/read function: {(portForwardRead.Success ? "firewall.get_port_forward_list" : "firewall.get_port_forward_list could not be verified")}");
            report.AppendLine($"Read parameters/response shape/rule count: {{}}; {portForwardRead.ResponseShape}; {(portForwardRead.RuleCount.HasValue ? portForwardRead.RuleCount.Value.ToString() : "unavailable")}.");
            bool readBackendVerified = portForwardRead.Success;
            bool emptyStateVerified = portForwardRead.Success && portForwardRead.RuleCount == 0;
            report.AppendLine($"PORT FORWARD READ BACKEND IDENTIFIED: {(readBackendVerified ? "YES" : "NO")}");
            report.AppendLine($"PORT FORWARD EMPTY-STATE READ VERIFIED: {(emptyStateVerified ? "YES" : "NO")}");
            report.AppendLine("PORT FORWARD WRITE CONTRACT VERIFIED: NO");
            report.AppendLine("Read-only discovery only.");
            return report.ToString();
        }
        private async Task<PortForwardReadProbeResult> TryGetPortForwardListProbeAsync()
        {
            try
            {
                string sessionId = await _sessionService.GetAdminTokenAsync();
                using JsonDocument document = await _sessionService.CallAsync(sessionId, "firewall", "get_port_forward_list");
                JsonElement root = document.RootElement;
                ProbeJsonProperty? ruleList = FindPortForwardRuleList(root);
                return new PortForwardReadProbeResult(
                    true,
                    DescribeProbeJsonShape(root),
                    ruleList is null ? "Not exposed" : ruleList.Name,
                    ruleList is not null && ruleList.Value.ValueKind == JsonValueKind.Array ? ruleList.Value.GetArrayLength() : null);
            }
            catch
            {
                return new PortForwardReadProbeResult(false, "Unavailable", "Unavailable", null);
            }
        }
        private async Task<string> ReadFirewallViewSourceAsync()
        {
            // The asset-retrieval experiment was retired once the firewall RPC
            // contract was verified. Keep the old probe self-contained if a
            // developer builds it, without reintroducing transfer tooling.
            await Task.CompletedTask;
            return string.Empty;
        }
        private static string ExtractFrontendCallContexts(string source, string literal)
        {
            if (string.IsNullOrWhiteSpace(source)) return "Unavailable";
            var contexts = new List<string>();
            int index = 0;
            while ((index = source.IndexOf(literal, index, StringComparison.Ordinal)) >= 0 && contexts.Count < 4)
            {
                int start = Math.Max(0, index - 2600);
                int length = Math.Min(source.Length - start, 5600);
                string context = source.Substring(start, length)
                    .Replace(";", ";\n", StringComparison.Ordinal)
                    .Replace(",", ",\n", StringComparison.Ordinal)
                    .Replace("{", "{\n", StringComparison.Ordinal)
                    .Replace("}", "}\n", StringComparison.Ordinal);
                contexts.Add(context);
                index += literal.Length;
            }
            return contexts.Count == 0 ? "Not found" : string.Join("\n--- occurrence ---\n", contexts);
        }
        private static ProbeJsonProperty? FindPortForwardRuleList(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Array &&
                        (property.Name.Contains("port", StringComparison.OrdinalIgnoreCase) ||
                         property.Name.Contains("forward", StringComparison.OrdinalIgnoreCase) ||
                         property.Name.Contains("rule", StringComparison.OrdinalIgnoreCase) ||
                         property.Name.Equals("list", StringComparison.OrdinalIgnoreCase) ||
                         property.Name.Equals("res", StringComparison.OrdinalIgnoreCase)))
                        return new ProbeJsonProperty(property.Name, property.Value);
                    ProbeJsonProperty? nested = FindPortForwardRuleList(property.Value);
                    if (nested is not null) return nested;
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in value.EnumerateArray())
                {
                    ProbeJsonProperty? nested = FindPortForwardRuleList(item);
                    if (nested is not null) return nested;
                }
            }
            return null;
        }
        private static string DescribeProbeJsonShape(JsonElement value, int depth = 0)
        {
            if (value.ValueKind == JsonValueKind.Array)
                return $"array[{value.GetArrayLength()}]";
            if (value.ValueKind != JsonValueKind.Object)
                return value.ValueKind.ToString().ToLowerInvariant();

            return "object { " + string.Join(", ", value.EnumerateObject().Take(16).Select(property =>
                depth < 2 && (property.Value.ValueKind == JsonValueKind.Object || property.Value.ValueKind == JsonValueKind.Array)
                    ? $"{property.Name}: {DescribeProbeJsonShape(property.Value, depth + 1)}"
                    : property.Value.ValueKind == JsonValueKind.Array
                        ? $"{property.Name}: array[{property.Value.GetArrayLength()}]"
                        : $"{property.Name}: {property.Value.ValueKind.ToString().ToLowerInvariant()}")) + " }";
        }
        private sealed record ProbeJsonProperty(string Name, JsonElement Value);
        private sealed record PortForwardReadProbeResult(bool Success, string ResponseShape, string RuleListField, int? RuleCount);
        public async Task<string> RunPortForwardWriteVerifierAsync()
        {
            const string name = "RouterPilot Verification";
            const string ip = "192.168.1.105";
            const string portA = "57333";
            const string portB = "57334";
            var report = new StringBuilder("Controlled Port Forwarding Verification\n");
            string? ruleId = null;
            IReadOnlyList<JsonElement> baseline = await ReadPortForwardRulesForVerifierAsync();
            report.AppendLine($"Baseline rule count: {baseline.Count}");
            if (baseline.Any(rule => ReadVerifierString(rule, "name") == name || ReadVerifierString(rule, "src_dport") is portA or portB)) return report.Append("ABORTED: temporary rule name or port is already present.").ToString();
            var add = new { name, proto = "tcp", dest = "lan", dest_ip = ip, dest_port = portA, enabled = true, src = "wan", src_dport = portA };
            try
            {
                await CallPortForwardVerifierAsync("add", add);
                var added = await ReadPortForwardRulesForVerifierAsync();
                var match = added.Where(rule => ReadVerifierString(rule, "name") == name && ReadVerifierString(rule, "src_dport") == portA && ReadVerifierString(rule, "dest_ip") == ip && ReadVerifierString(rule, "dest_port") == portA).ToList();
                if (match.Count != 1 || string.IsNullOrWhiteSpace(ReadVerifierString(match[0], "id"))) throw new InvalidOperationException();
                ruleId = ReadVerifierString(match[0], "id");
                report.AppendLine($"Add: SUCCESS; id={ruleId}; schema={DescribeProbeJsonShape(match[0])}");
                var edit = new { id = ruleId, name, proto = "tcp", dest = "lan", dest_ip = ip, dest_port = portA, enabled = true, src = "wan", src_dport = portB };
                await CallPortForwardVerifierAsync("set", edit);
                var edited = await ReadPortForwardRulesForVerifierAsync();
                JsonElement? changed = edited.FirstOrDefault(rule => ReadVerifierString(rule, "id") == ruleId);
                if (changed is null || ReadVerifierString(changed.Value, "src_dport") != portB || ReadVerifierString(changed.Value, "dest_ip") != ip || ReadVerifierString(changed.Value, "dest_port") != portA) throw new InvalidOperationException();
                report.AppendLine("Edit: SUCCESS");
                await CallPortForwardVerifierAsync("remove", new { id = ruleId });
                var final = await ReadPortForwardRulesForVerifierAsync();
                if (final.Any(rule => ReadVerifierString(rule, "id") == ruleId) || final.Count != baseline.Count) throw new InvalidOperationException();
                report.AppendLine("Delete: SUCCESS");
                report.AppendLine($"Final rule count: {final.Count}");
                report.AppendLine("PORT FORWARD WRITE CONTRACT VERIFIED: YES");
            }
            catch
            {
                report.AppendLine("Verification: FAILURE");
                if (!string.IsNullOrWhiteSpace(ruleId))
                {
                    try { await CallPortForwardVerifierAsync("remove", new { id = ruleId }); report.AppendLine("Cleanup: attempted"); } catch { report.AppendLine("Cleanup: could not be confirmed"); }
                }
                report.AppendLine("PORT FORWARD WRITE CONTRACT VERIFIED: NO");
            }
            return report.ToString();
        }
        private async Task CallPortForwardVerifierAsync(string operation, object payload)
        {
            string sid = await _sessionService.GetAdminTokenAsync();
            using JsonDocument _ = await _sessionService.CallPortForwardVerifierAsync(sid, operation, payload);
        }
        private async Task<IReadOnlyList<JsonElement>> ReadPortForwardRulesForVerifierAsync()
        {
            string sid = await _sessionService.GetAdminTokenAsync();
            using JsonDocument document = await _sessionService.CallAsync(sid, "firewall", "get_port_forward_list");
            if (!document.RootElement.TryGetProperty("result", out JsonElement result) || !result.TryGetProperty("res", out JsonElement rules) || rules.ValueKind != JsonValueKind.Array) return Array.Empty<JsonElement>();
            return rules.EnumerateArray().Select(rule => rule.Clone()).ToList();
        }
        private static string ReadVerifierString(JsonElement rule, string name) => rule.TryGetProperty(name, out JsonElement value) ? value.ToString() : string.Empty;
        private async Task<string> SafeFirewallProbeAsync(string command) { try { return await _ssh.RunCommandAsync(command); } catch { return string.Empty; } }
        private async Task<string> SearchProbeAssetsAsync(IEnumerable<string> roots, string commandPrefix)
        {
            var matches = new List<string>();
            foreach (string root in roots.Where(IsSafeProbeAssetRoot))
                matches.AddRange((await SafeFirewallProbeAsync($"{commandPrefix} {root} 2>/dev/null | head -n 80")).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
            return string.Join("\n", matches.Distinct(StringComparer.OrdinalIgnoreCase));
        }
        private async Task<string> SearchProbeFilesAsync(IEnumerable<string> files, string commandPrefix)
        {
            var matches = new List<string>();
            foreach (string file in files.Where(IsSearchableProbeAssetFile))
                matches.AddRange((await SafeFirewallProbeAsync($"{commandPrefix} {file} 2>/dev/null")).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
            return string.Join("\n", matches.Distinct(StringComparer.OrdinalIgnoreCase));
        }
        private async Task<string> SearchProbeBackendFilesAsync(IEnumerable<string> files, string commandPrefix)
        {
            var matches = new List<string>();
            foreach (string file in files.Where(IsSearchableProbeBackendFile))
                matches.AddRange((await SafeFirewallProbeAsync($"{commandPrefix} {file} 2>/dev/null")).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
            return string.Join("\n", matches.Distinct(StringComparer.OrdinalIgnoreCase));
        }
        private static string SummariseProbeOutput(string value) => string.IsNullOrWhiteSpace(value) ? "None found" : string.Join(", ", value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(item => item.Trim()).Where(item => item.Length <= 160).Take(120));
        private static string SummariseProbeContractOutput(string value) => string.IsNullOrWhiteSpace(value) ? "None found" : string.Join(" | ", value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(item => item.Trim()).Where(item => item.Length <= 360).Take(160));
        private static List<string> ExtractPackageDependencies(string status)
        {
            string? dependsLine = status.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(line => line.StartsWith("Depends:", StringComparison.OrdinalIgnoreCase));
            return dependsLine is null ? new List<string>() : dependsLine["Depends:".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim().TrimStart('+')).Where(IsSafePackageIdentifier).ToList();
        }
        private static IEnumerable<string> ExtractProbeAssetRoots(string uhttpdConfig, string nginxRoots, IEnumerable<string> packageFiles)
        {
            var roots = new List<string>();
            foreach (string line in uhttpdConfig.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                if (line.Contains(".home=", StringComparison.Ordinal)) roots.Add(TrimUciValue(line[(line.IndexOf('=') + 1)..]));
            foreach (string line in nginxRoots.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] fields = line.Trim().TrimEnd(';').Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length >= 2) roots.Add(fields[1]);
            }
            foreach (string path in packageFiles.Select(value => value.Trim()).Where(IsSafeFrontendAssetPath))
            {
                string? directory = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/');
                if (!string.IsNullOrWhiteSpace(directory)) roots.Add(directory);
            }
            return roots.Where(IsSafeProbeAssetRoot).Distinct(StringComparer.OrdinalIgnoreCase);
        }
        private static bool IsRelevantPortForwardObject(string name) => IsSafeProbeIdentifier(name) &&
            (name.Equals("firewall", StringComparison.OrdinalIgnoreCase) || name.Equals("service", StringComparison.OrdinalIgnoreCase) ||
             new[] { "firewall", "nat", "redirect", "port", "forward", "gl", "acl", "expose", "dmz", "upnp", "pcp" }.Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase)));
        private static bool IsRelevantPortForwardPackage(string name) => IsSafeProbeIdentifier(name) &&
            (name.Equals("firewall", StringComparison.OrdinalIgnoreCase) || name.Equals("glconfig", StringComparison.OrdinalIgnoreCase) || name.Equals("glinet", StringComparison.OrdinalIgnoreCase) ||
             new[] { "firewall", "nat", "port", "forward", "dmz", "expose", "upnp", "pcp", "gl" }.Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase)));
        private static bool IsSafeProbeIdentifier(string value) => Regex.IsMatch(value, "^[A-Za-z0-9_.-]+$");
        private static bool IsSafePackageIdentifier(string value) => Regex.IsMatch(value, "^[A-Za-z0-9_.-]+$");
        private static bool IsSafeProbeAssetRoot(string value) => Regex.IsMatch(value, "^/(www|usr/share|usr/lib/lua|opt)(/[A-Za-z0-9_./-]+)?$");
        private static bool IsSafeFrontendAssetPath(string value) => IsSafeProbeAssetRoot(System.IO.Path.GetDirectoryName(value)?.Replace('\\', '/') ?? string.Empty) && Regex.IsMatch(value, "^/[A-Za-z0-9_./-]+$");
        private static bool IsSearchableProbeAssetFile(string value) => IsSafeFrontendAssetPath(value) && new[] { ".js", ".json", ".html", ".map", ".lua" }.Contains(System.IO.Path.GetExtension(value), StringComparer.OrdinalIgnoreCase);
        private static bool IsSafeProbeBackendPath(string value) => Regex.IsMatch(value, "^/(usr/lib|usr/share|usr/libexec|etc|www)(/[A-Za-z0-9_./-]+)?$");
        private static bool IsSearchableProbeBackendFile(string value) => IsSafeProbeBackendPath(value) && new[] { ".lua", ".sh", ".json", ".conf" }.Contains(System.IO.Path.GetExtension(value), StringComparer.OrdinalIgnoreCase);
        private static string ClassifyProbeAssetFile(string value)
        {
            string extension = System.IO.Path.GetExtension(value).ToLowerInvariant();
            return extension switch { ".js" => "JavaScript", ".json" => "JSON/manifest", ".html" => "HTML", ".map" => "source map", ".lua" => "Lua", ".gz" => "gzip", ".br" => "Brotli", _ => "resource" };
        }
        private static string ClassifyProbeBackendFile(string value)
        {
            string extension = System.IO.Path.GetExtension(value).ToLowerInvariant();
            return extension switch { ".lua" => "Lua", ".sh" => "shell", ".json" => "JSON/RPC descriptor", ".conf" => "configuration", ".so" => "shared library", _ => "resource/binary" };
        }
        private static bool IsSafeFrontendRpcToken(string value) => value.Length <= 180 && Regex.IsMatch(value, "^(module|func)\\s*:\\s*['\"]?[A-Za-z0-9_.-]+['\"]?$");
        private static IEnumerable<FirewallProbeSection> FindPortForwardCandidates(IEnumerable<FirewallProbeSection> sections) =>
            sections.Where(section => section.Type.Equals("redirect", StringComparison.OrdinalIgnoreCase) ||
                (section.Options.ContainsKey("src_dport") && section.Options.ContainsKey("dest_ip")));
        private static void AppendPortForwardCandidates(StringBuilder report, IEnumerable<FirewallProbeSection> candidates, IReadOnlyList<DhcpNetworkScopeInfo> scopes, IReadOnlyList<WifiClientInfo> clients)
        {
            foreach (FirewallProbeSection section in candidates)
            {
                report.AppendLine();
                report.AppendLine($"Section: {section.Package}.{section.Id}; Type: {section.Type}; Options: {string.Join(", ", section.Options.Keys.OrderBy(x => x))}");
                foreach (string key in new[] { "name", "proto", "src", "src_dport", "dest", "dest_ip", "dest_port", "enabled", "disabled", "family", "target", "src_ip", "reflection" })
                    if (section.Options.TryGetValue(key, out string? value)) report.AppendLine($"{key}: {value}");
                if (section.Options.TryGetValue("dest_ip", out string? destinationIp))
                {
                    WifiClientInfo? client = clients.FirstOrDefault(item => item.IpAddress.Equals(destinationIp, StringComparison.OrdinalIgnoreCase));
                    report.AppendLine($"Device: {(client is null ? "Unknown" : client.Name)}");
                    report.AppendLine($"DHCP scope: {GetProbeScopeDisplay(destinationIp, scopes)}");
                }
            }
        }
        private static string GetProbeScopeDisplay(string ipAddress, IReadOnlyList<DhcpNetworkScopeInfo> scopes)
        {
            if (!IPAddress.TryParse(ipAddress, out IPAddress? address)) return "Unknown";
            var matches = scopes.Where(scope => scope.DhcpEnabled && scope.ContainsAddress(address)).ToList();
            return matches.Count == 1 ? matches[0].DisplayName : matches.Count > 1 ? "Ambiguous" : "Unknown";
        }
        private static IEnumerable<UbusProbeMethod> ExtractUbusMethods(string signature) => Regex.Matches(signature, "^\\s*['\\\"]?([A-Za-z][A-Za-z0-9_-]*)['\\\"]?\\s*[:=]\\s*(.+)$", RegexOptions.Multiline)
            .Select(match => new UbusProbeMethod(match.Groups[1].Value, match.Groups[2].Value.Trim()[..Math.Min(match.Groups[2].Value.Trim().Length, 240)]))
            .GroupBy(method => method.Name, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).Take(20);
        private static bool IsParameterlessReadSignature(string signature) => Regex.IsMatch(signature, "^\\{\\s*\\}$");
        private static bool IsClearlyReadOnlyMethod(string value) => new[] { "get", "list", "status", "dump", "show", "query" }.Contains(value, StringComparer.OrdinalIgnoreCase);
        private static bool IsLikelyWriteMethod(string value) => new[] { "set", "add", "create", "delete", "del", "remove", "update", "apply", "reload", "enable", "disable", "commit" }.Contains(value, StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, FirewallProbeSection> ParseUciProbeSections(string output, string package)
        {
            var sections = new Dictionary<string, FirewallProbeSection>();
            foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int equalsIndex = line.IndexOf('=');
                if (equalsIndex <= 0 || !line.StartsWith(package + ".", StringComparison.Ordinal)) continue;
                string[] path = line[..equalsIndex].Split('.', 3);
                if (path.Length < 2) continue;
                if (!sections.TryGetValue(path[1], out FirewallProbeSection? section)) sections[path[1]] = section = new FirewallProbeSection(package, path[1]);
                if (path.Length == 2) section.Type = TrimUciValue(line[(equalsIndex + 1)..]);
                else section.Options[path[2]] = TrimUciValue(line[(equalsIndex + 1)..]);
            }
            return sections;
        }
        private sealed class FirewallProbeSection(string package, string id)
        {
            public string Package { get; } = package;
            public string Id { get; } = id;
            public string Type { get; set; } = "other";
            public Dictionary<string, string> Options { get; } = new();
        }
        private sealed record UbusProbeMethod(string Name, string Signature);
#endif


        private static (List<DhcpConfigurationInfo> Configurations, List<DhcpReservationInfo> Reservations)
            ParseDhcpConfiguration(string output)
        {
            Dictionary<string, DhcpUciSection> sections = ParseDhcpUciSections(output);
            
            List<DhcpConfigurationInfo> configurations = sections.Values
                .Where(section => section.Type.Equals("dhcp", StringComparison.OrdinalIgnoreCase))
                .Select(section => new DhcpConfigurationInfo
                {
                    Id = section.Id,
                    Interface = GetDhcpOption(section, "interface", section.Id),
                    Enabled = !GetDhcpOption(section, "ignore").Equals("1", StringComparison.Ordinal),
                    Start = GetDhcpOption(section, "start"),
                    Limit = GetDhcpOption(section, "limit"),
                    LeaseTime = GetDhcpOption(section, "leasetime")
                })
                .OrderBy(configuration => configuration.Interface, StringComparer.OrdinalIgnoreCase)
                .ToList();

            List<DhcpReservationInfo> reservations = sections.Values
                .Where(section => section.Type.Equals("host", StringComparison.OrdinalIgnoreCase))
                .Select(section => new DhcpReservationInfo
                {
                    Id = section.Id,
                    Hostname = GetDhcpOption(section, "name", "Unknown device"),
                    MacAddress = GetDhcpOption(section, "mac"),
                    IpAddress = GetDhcpOption(section, "ip"),
                    Enabled = !GetDhcpOption(section, "enabled").Equals("0", StringComparison.Ordinal)
                })
                .OrderBy(reservation => reservation.Hostname, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return (configurations, reservations);
        }

        private static Dictionary<string, DhcpUciSection> ParseDhcpUciSections(string output)
        {
            var sections = new Dictionary<string, DhcpUciSection>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int equalsIndex = line.IndexOf('=');
                if (equalsIndex <= 0 || !line.StartsWith("dhcp.", StringComparison.Ordinal)) continue;

                string left = line[..equalsIndex];
                string value = TrimUciValue(line[(equalsIndex + 1)..]);
                string[] path = left.Split('.', 3);
                if (path.Length < 2) continue;

                string id = path[1];
                if (!sections.TryGetValue(id, out DhcpUciSection? section))
                {
                    section = new DhcpUciSection(id);
                    sections.Add(id, section);
                }

                if (path.Length == 2)
                {
                    section.Type = value;
                }
                else if (path.Length == 3)
                {
                    section.Options[path[2]] = value;
                }
            }
            return sections;
        }

        private static List<DhcpLeaseInfo> ParseDhcpLeaseSnapshot(string output)
        {
            var leases = new List<DhcpLeaseInfo>();
            foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 4 || !long.TryParse(fields[0], out long expirySeconds)) continue;

                bool isStatic = expirySeconds == 0;
                DateTimeOffset? expiry = isStatic ? null : DateTimeOffset.FromUnixTimeSeconds(expirySeconds);
                string hostname = fields[3] == "*" ? "Unknown device" : fields[3];
                leases.Add(new DhcpLeaseInfo
                {
                    Hostname = hostname,
                    ClientName = hostname,
                    MacAddress = fields[1],
                    IpAddress = fields[2],
                    IsStatic = isStatic,
                    Expiry = expiry,
                    RemainingLease = FormatRemainingLease(expiry, isStatic)
                });
            }

            return leases.OrderBy(lease => lease.Hostname, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<string> DetectDhcpConflicts(
            IReadOnlyList<DhcpReservationInfo> reservations,
            IReadOnlyList<DhcpLeaseInfo> leases)
        {
            var warnings = new List<string>();
            foreach (IGrouping<string, DhcpReservationInfo> group in reservations
                         .Where(reservation => HasDhcpValue(reservation.IpAddress))
                         .GroupBy(reservation => reservation.IpAddress, StringComparer.OrdinalIgnoreCase)
                         .Where(group => group.Count() > 1))
            {
                warnings.Add($"Multiple static reservations use {group.Key}.");
            }

            foreach (IGrouping<string, DhcpReservationInfo> group in reservations
                         .Where(reservation => HasDhcpValue(reservation.MacAddress))
                         .GroupBy(reservation => NormaliseMacAddress(reservation.MacAddress))
                         .Where(group => group.Count() > 1))
            {
                warnings.Add("Multiple static reservations use the same MAC address.");
            }

            foreach (DhcpReservationInfo reservation in reservations.Where(reservation => HasDhcpValue(reservation.IpAddress)))
            {
                bool conflict = leases.Any(lease =>
                    lease.IpAddress.Equals(reservation.IpAddress, StringComparison.OrdinalIgnoreCase) &&
                    !NormaliseMacAddress(lease.MacAddress).Equals(NormaliseMacAddress(reservation.MacAddress), StringComparison.OrdinalIgnoreCase));
                if (conflict) warnings.Add($"Active lease conflicts with reservation {reservation.IpAddress}.");
            }

            return warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string GetDhcpOption(DhcpUciSection section, string name, string fallback = "N/A") =>
            section.Options.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

        private static string TrimUciValue(string value) => value.Trim().Trim('\'', '"');

        private static bool HasDhcpValue(string value) =>
            !string.IsNullOrWhiteSpace(value) && value != "-" && !value.Equals("N/A", StringComparison.OrdinalIgnoreCase);

        private static string FormatRemainingLease(DateTimeOffset? expiry, bool isStatic)
        {
            if (isStatic) return "Static";
            if (expiry is null) return "N/A";
            TimeSpan remaining = expiry.Value - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) return "Expired";
            if (remaining.TotalMinutes < 60) return $"{Math.Ceiling(remaining.TotalMinutes):0} min";
            if (remaining.TotalHours < 24) return $"{Math.Ceiling(remaining.TotalHours):0} hr";
            return $"{Math.Ceiling(remaining.TotalDays):0} days";
        }

        private static string RedactMac(string value)
        {
            string normalised = NormaliseMacAddress(value);
            return normalised.Length == 12 ? $"{normalised[..6]}••••••" : "absent";
        }

        private static string RedactIp(string value)
        {
            string[] parts = value.Split('.', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 4 ? $"{parts[0]}.{parts[1]}.{parts[2]}.x" : "absent";
        }

        private sealed class DhcpUciSection(string id)
        {
            public string Id { get; } = id;
            public string Type { get; set; } = string.Empty;
            public Dictionary<string, string> Options { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private async Task<(List<WifiRadioInfo> Networks, string Output)>
            DiscoverWifiRadiosFromHostapdAsync()
        {
            // Retain the discovery path used before per-SSID client mapping was
            // introduced. Some GL.iNet/MediaTek builds do not produce records
            // accepted by the newer iw-based command, but do expose their APs
            // through the hostapd ubus objects.
            string command = """
                found=0
                for s in $(uci show wireless 2>/dev/null | sed -n 's/^wireless\.\([^.=]*\)=wifi-iface$/\1/p'); do
                    mode=$(uci -q get wireless.$s.mode)
                    [ -z "$mode" -o "$mode" = "ap" ] || continue
                    dev=$(uci -q get wireless.$s.device)
                    [ -n "$dev" ] || continue
                    ssid=$(uci -q get wireless.$s.ssid)
                    [ -n "$ssid" ] || ssid='Hidden network'
                    band=$(uci -q get wireless.$dev.band)
                    [ -n "$band" ] || band=$(uci -q get wireless.$dev.hwmode)
                    channel=$(uci -q get wireless.$dev.channel)
                    [ -n "$channel" ] || channel='auto'
                    disabled=$(uci -q get wireless.$s.disabled)
                    rdisabled=$(uci -q get wireless.$dev.disabled)
                    live_ifaces=''

                    case "$band" in
                        *2g*|*11g*|*11b*) wanted_band='2g' ;;
                        *5g*|*11a*|*11ac*|*11ax*) wanted_band='5g' ;;
                        *)
                            if [ "$channel" != 'auto' ] && [ "$channel" -le 14 ] 2>/dev/null; then wanted_band='2g'; else wanted_band='5g'; fi
                            ;;
                    esac

                    for h in $(ubus list 2>/dev/null | awk '/^hostapd\./ { print }'); do
                        status=$(ubus call "$h" get_status 2>/dev/null)
                        hssid=$(printf '%s' "$status" | jsonfilter -e '@.ssid' 2>/dev/null)
                        hfreq=$(printf '%s' "$status" | jsonfilter -e '@.freq' 2>/dev/null)
                        hchan=$(printf '%s' "$status" | jsonfilter -e '@.channel' 2>/dev/null)
                        hband=''
                        if [ -n "$hfreq" ]; then
                            [ "$hfreq" -lt 3000 ] 2>/dev/null && hband='2g' || hband='5g'
                        elif [ -n "$hchan" ]; then
                            [ "$hchan" -le 14 ] 2>/dev/null && hband='2g' || hband='5g'
                        fi

                        if [ "$hssid" = "$ssid" ] || { [ -n "$hband" ] && [ "$hband" = "$wanted_band" ]; }; then
                            iface=${h#hostapd.}
                            case " $live_ifaces " in *" $iface "*) continue ;; esac
                            live_ifaces="$live_ifaces $iface"
                            [ "$hssid" = "$ssid" ] && break
                        fi
                    done

                    state='Online'
                    [ "$disabled" = "1" -o "$rdisabled" = "1" ] && state='Disabled'
                    live_ifaces=$(printf '%s' "$live_ifaces" | sed 's/^ *//')
                    [ -n "$live_ifaces" ] || live_ifaces="$dev"
                    printf 'L|%s|%s|%s|%s|%s|%s\n' "$dev" "$live_ifaces" "$ssid" "$band" "$channel" "$state"
                    found=$((found + 1))
                done

                hostapd_count=0
                if [ "$found" -eq 0 ]; then
                    for object in $(ubus list 2>/dev/null | awk '/^hostapd\./ { print }'); do
                        hostapd_count=$((hostapd_count + 1))
                        iface=${object#hostapd.}
                        status=$(ubus call "$object" get_status 2>/dev/null)
                        ssid=$(printf '%s' "$status" | jsonfilter -e '@.ssid' 2>/dev/null)
                        [ -n "$ssid" ] || ssid=$(iw dev "$iface" info 2>/dev/null | sed -n 's/^[[:space:]]*ssid //p' | head -n1)
                        [ -n "$ssid" ] || ssid='Hidden network'
                        freq=$(printf '%s' "$status" | jsonfilter -e '@.freq' 2>/dev/null)
                        channel=$(printf '%s' "$status" | jsonfilter -e '@.channel' 2>/dev/null)
                        [ -n "$channel" ] || channel='auto'
                        if [ -n "$freq" ] && [ "$freq" -lt 3000 ] 2>/dev/null; then
                            band='2g'
                        elif [ -n "$freq" ]; then
                            band='5g'
                        elif [ "$channel" != 'auto' ] && [ "$channel" -le 14 ] 2>/dev/null; then
                            band='2g'
                        else
                            band='5g'
                        fi
                        printf 'L|%s|%s|%s|%s|%s|Online\n' "$iface" "$iface" "$ssid" "$band" "$channel"
                    done
                fi
                printf 'D|uci|%s|hostapd|%s\n' "$found" "$hostapd_count"
                """;

            string output = await _ssh.RunCommandAsync(command);
            var networks = new List<WifiRadioInfo>();
            LogWifiDiscoveryResult("hostapd-fallback", output);

            foreach (string line in output.Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = line.Split('|');
                if (parts.Length < 7 || parts[0] != "L")
                {
                    continue;
                }

                string rawBand = parts[4].Trim().ToLowerInvariant();
                string band = rawBand.Contains("2g") || rawBand.Contains("11g") || rawBand.Contains("11b")
                    ? "2.4 GHz"
                    : rawBand.Contains("5g") || rawBand.Contains("11a") || rawBand.Contains("11ac") || rawBand.Contains("11ax")
                        ? "5 GHz"
                        : rawBand.Contains("6g")
                            ? "6 GHz"
                            : InferBandFromChannel(parts[5]);

                networks.Add(new WifiRadioInfo
                {
                    Radio = string.IsNullOrWhiteSpace(parts[1]) ? "-" : parts[1].Trim(),
                    Interface = string.IsNullOrWhiteSpace(parts[2]) ? "-" : parts[2].Trim(),
                    Ssid = string.IsNullOrWhiteSpace(parts[3]) ? "Hidden network" : parts[3].Trim(),
                    Band = band,
                    Channel = string.IsNullOrWhiteSpace(parts[5]) ? "auto" : parts[5].Trim(),
                    Status = string.IsNullOrWhiteSpace(parts[6]) ? "Configured" : parts[6].Trim()
                });
            }

            return (networks, output);
        }

        private static void LogWifiDiscoveryResult(string stage, string output)
        {
            string category = output switch
            {
                string value when value.Contains("SSH_AUTH_FAILED", StringComparison.Ordinal) => "authentication-failure",
                string value when value.Contains("SSH_CONNECTION_FAILED", StringComparison.Ordinal) ||
                                  value.Contains("SSH_NETWORK_FAILED", StringComparison.Ordinal) => "connection-failure",
                string value when value.Contains("SSH_ERROR:", StringComparison.Ordinal) => "command-failure",
                string value when value.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                                  value.Contains("unknown command", StringComparison.OrdinalIgnoreCase) => "command-unsupported",
                string value when string.IsNullOrWhiteSpace(value) => "no-output",
                _ => "success"
            };

            Debug.WriteLine($"[WiFiDiscovery] stage={stage} result={category}");
        }

        private static int ReadDiscoveryCount(string output, string name)
        {
            foreach (string line in output.Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = line.Split('|');
                for (int index = 1; index + 1 < parts.Length; index += 2)
                {
                    if (parts[0] == "D" &&
                        parts[index].Equals(name, StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(parts[index + 1], out int count))
                    {
                        return count;
                    }
                }
            }

            return 0;
        }


        private async Task EnrichWifiClientsFromHostapdAsync(
            List<WifiRadioInfo> networks)
        {
            string command = """
                . /usr/share/libubox/jshn.sh 2>/dev/null || exit 0
                for object in $(ubus list 2>/dev/null | awk '/^hostapd\./ { print }'); do
                    iface=${object#hostapd.}
                    ssid=$(iw dev "$iface" info 2>/dev/null | sed -n 's/^[[:space:]]*ssid //p' | head -n1)
                    status=$(ubus call "$object" get_status 2>/dev/null)
                    if [ -z "$ssid" ] && [ -n "$status" ]; then
                        json_load "$status" 2>/dev/null || true
                        json_get_var ssid ssid
                    fi

                    clients=$(ubus call "$object" get_clients 2>/dev/null)
                    [ -n "$clients" ] || continue
                    json_load "$clients" 2>/dev/null || continue
                    json_select clients 2>/dev/null || continue
                    json_get_keys macs
                    for mac in $macs; do
                        json_select "$mac" 2>/dev/null || continue
                        json_get_var signal signal
                        [ -n "$signal" ] || json_get_var signal rssi
                        printf 'H|%s|%s|%s|%s\n' "$iface" "$ssid" "$mac" "$signal"
                        json_select ..
                    done
                    json_select .. 2>/dev/null || true
                done
                """;

            string output = await _ssh.RunCommandAsync(command);
            if (string.IsNullOrWhiteSpace(output))
            {
                return;
            }

            string leaseOutput = await _ssh.RunCommandAsync(
                "cat /tmp/dhcp.leases 2>/dev/null || true");
            var leases = ParseDhcpLeases(leaseOutput);

            foreach (string line in output.Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = line.Split('|');
                if (parts.Length < 5 || parts[0] != "H")
                {
                    continue;
                }

                MergeObservedWifiClient(
                    networks,
                    parts[1].Trim(),
                    parts[2].Trim(),
                    parts[3].Trim(),
                    parts[4].Trim(),
                    leases);
            }
        }

        private async Task EnrichWifiClientsFromStationDumpAsync(
            List<WifiRadioInfo> networks)
        {
            string command = """
                for iface in $(iw dev 2>/dev/null | awk '$1 == "Interface" { print $2 }'); do
                    ssid=$(iw dev "$iface" info 2>/dev/null | sed -n 's/^[[:space:]]*ssid //p' | head -n1)
                    iw dev "$iface" station dump 2>/dev/null | awk -v iface="$iface" -v ssid="$ssid" '
                        /^Station / {
                            if (mac != "") print "W|" iface "|" ssid "|" mac "|" signal;
                            mac=$2; signal=""
                        }
                        /^[[:space:]]*signal:/ { signal=$2 }
                        END { if (mac != "") print "W|" iface "|" ssid "|" mac "|" signal }'
                done
                """;

            string stationOutput = await _ssh.RunCommandAsync(command);
            if (string.IsNullOrWhiteSpace(stationOutput))
            {
                return;
            }

            string leaseOutput = await _ssh.RunCommandAsync(
                "cat /tmp/dhcp.leases 2>/dev/null || true");

            var leases = ParseDhcpLeases(leaseOutput);

            foreach (string line in stationOutput.Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = line.Split('|');
                if (parts.Length < 5 || parts[0] != "W")
                {
                    continue;
                }

                string runtimeInterface = parts[1].Trim();
                string ssid = parts[2].Trim();
                string mac = parts[3].Trim();
                string signal = parts[4].Trim();

                if (mac.Length == 0)
                {
                    continue;
                }

                MergeObservedWifiClient(
                    networks,
                    runtimeInterface,
                    ssid,
                    mac,
                    signal,
                    leases);
            }
        }


        private static void MergeObservedWifiClient(
            List<WifiRadioInfo> networks,
            string runtimeInterface,
            string ssid,
            string mac,
            string signal,
            Dictionary<string, (string Ip, string Name)> leases)
        {
            if (string.IsNullOrWhiteSpace(mac))
            {
                return;
            }

            WifiRadioInfo? network = FindStationNetwork(
                networks,
                runtimeInterface,
                ssid);
            if (network is null)
            {
                return;
            }

            network.Status = "Online";
            string normalisedMac = NormaliseMacAddress(mac);

            WifiClientInfo? existing = networks
                .SelectMany(item => item.Clients)
                .FirstOrDefault(client =>
                    NormaliseMacAddress(client.MacAddress) == normalisedMac);

            if (existing is not null)
            {
                foreach (WifiRadioInfo item in networks)
                {
                    item.Clients.Remove(existing);
                }

                existing.Ssid = network.Ssid;
                existing.Band = network.Band;
                existing.Interface = string.IsNullOrWhiteSpace(runtimeInterface)
                    ? network.Interface
                    : runtimeInterface;
                if (!string.IsNullOrWhiteSpace(signal))
                {
                    existing.Signal = FormatSignal(signal);
                }
                network.Clients.Add(existing);
                return;
            }

            leases.TryGetValue(normalisedMac, out (string Ip, string Name) lease);
            network.Clients.Add(new WifiClientInfo
            {
                Name = string.IsNullOrWhiteSpace(lease.Name) || lease.Name == "*"
                    ? "Unknown device"
                    : lease.Name,
                IpAddress = string.IsNullOrWhiteSpace(lease.Ip) ? "-" : lease.Ip,
                MacAddress = mac,
                Signal = FormatSignal(signal),
                Band = network.Band,
                Interface = string.IsNullOrWhiteSpace(runtimeInterface)
                    ? network.Interface
                    : runtimeInterface,
                Ssid = network.Ssid
            });
        }

        private static WifiRadioInfo? FindStationNetwork(
            List<WifiRadioInfo> networks,
            string runtimeInterface,
            string ssid)
        {
            if (!string.IsNullOrWhiteSpace(ssid))
            {
                WifiRadioInfo? bySsid = networks.FirstOrDefault(network =>
                    network.Ssid.Equals(
                        ssid,
                        StringComparison.OrdinalIgnoreCase));
                if (bySsid is not null)
                {
                    return bySsid;
                }
            }

            if (!string.IsNullOrWhiteSpace(runtimeInterface))
            {
                WifiRadioInfo? byInterface = networks.FirstOrDefault(network =>
                    network.Interface.Equals(
                        runtimeInterface,
                        StringComparison.OrdinalIgnoreCase) ||
                    network.Radio.Equals(
                        runtimeInterface,
                        StringComparison.OrdinalIgnoreCase));
                if (byInterface is not null)
                {
                    return byInterface;
                }
            }

            return null;
        }

        private static Dictionary<string, (string Ip, string Name)>
            ParseDhcpLeases(string output)
        {
            var result = new Dictionary<string, (string Ip, string Name)>(
                StringComparer.OrdinalIgnoreCase);

            foreach (string line in output.Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries))
            {
                string[] fields = line.Split(
                    new[] { ' ', '\t' },
                    StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 4)
                {
                    continue;
                }

                string mac = NormaliseMacAddress(fields[1]);
                if (mac.Length != 12)
                {
                    continue;
                }

                result[mac] = (fields[2], fields[3]);
            }

            return result;
        }

        public async Task<List<WifiClientInfo>> GetGlClientInventoryAsync()
        {
            string clientJson = await _ssh.RunCommandAsync(
                "ubus call gl-clients list 2>/dev/null || true");

            var clients = new List<WifiClientInfo>();
            if (string.IsNullOrWhiteSpace(clientJson))
            {
                return clients;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(clientJson);
                foreach (JsonElement client in EnumerateClientObjects(document.RootElement))
                {
                    if (!GetFlexibleBoolean(client, "online", true))
                    {
                        continue;
                    }

                    string mac = GetFlexibleString(client, "mac", "macaddr", "mac_address");
                    if (string.IsNullOrWhiteSpace(mac) || clients.Any(item =>
                            item.MacAddress.Equals(mac, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    string rawInterface = GetFlexibleString(
                        client, "iface", "interface", "connection", "type");
                    string band = NormaliseClientBand(rawInterface);
                    string ssid = GetFlexibleString(client, "ssid", "wifi", "network");
                    string name = GetFlexibleString(client, "name", "hostname", "host_name");
                    string ip = GetFlexibleString(client, "ip", "ipaddr", "ip_address");
                    string signal = GetFlexibleString(client, "signal", "rssi", "wifi_signal", "signal_strength", "rssi_dbm");

                    clients.Add(new WifiClientInfo
                    {
                        Name = string.IsNullOrWhiteSpace(name) ? "Unknown device" : name,
                        IpAddress = string.IsNullOrWhiteSpace(ip) ? "-" : ip,
                        MacAddress = mac,
                        Signal = FormatSignal(signal),
                        Band = string.IsNullOrWhiteSpace(band) ?
                            (IsExplicitWiredClientConnection(rawInterface) ? "Ethernet" : "Unknown") : band,
                        Interface = string.IsNullOrWhiteSpace(rawInterface) ? "-" : rawInterface,
                        Ssid = string.IsNullOrWhiteSpace(ssid) ? "-" : ssid
                    });
                }
            }
            catch (JsonException)
            {
                // Return an empty inventory while leaving AdGuard client data usable.
            }

            return clients;
        }

        private static IEnumerable<JsonElement> EnumerateClientObjects(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                bool looksLikeClient =
                    HasAnyProperty(element, "mac", "macaddr", "mac_address") &&
                    HasAnyProperty(element, "iface", "interface", "connection", "type");

                if (looksLikeClient)
                {
                    yield return element;
                }

                foreach (JsonProperty property in element.EnumerateObject())
                {
                    foreach (JsonElement child in EnumerateClientObjects(property.Value))
                    {
                        yield return child;
                    }
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray())
                {
                    foreach (JsonElement child in EnumerateClientObjects(item))
                    {
                        yield return child;
                    }
                }
            }
        }

        private static bool HasAnyProperty(JsonElement element, params string[] names)
        {
            return names.Any(name => TryGetPropertyIgnoreCase(element, name, out _));
        }

        private static string GetFlexibleString(JsonElement element, params string[] names)
        {
            foreach (string name in names)
            {
                if (!TryGetPropertyIgnoreCase(element, name, out JsonElement value))
                {
                    continue;
                }

                return value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString()?.Trim() ?? string.Empty,
                    JsonValueKind.Number => value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => string.Empty
                };
            }

            return string.Empty;
        }

        private static bool GetFlexibleBoolean(JsonElement element, string name, bool defaultValue)
        {
            if (!TryGetPropertyIgnoreCase(element, name, out JsonElement value))
            {
                return defaultValue;
            }

            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => value.TryGetInt32(out int number) && number != 0,
                JsonValueKind.String => value.GetString() is string text &&
                    (text == "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                     text.Equals("online", StringComparison.OrdinalIgnoreCase)),
                _ => defaultValue
            };
        }

        private static bool TryGetPropertyIgnoreCase(
            JsonElement element,
            string name,
            out JsonElement value)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = property.Value;
                        return true;
                    }
                }
            }

            value = default;
            return false;
        }

        private static string NormaliseClientBand(string iface)
        {
            string value = iface.Trim().ToLowerInvariant();
            if (value.Contains("2.4") || value.Contains("2g") || value.Contains("24g"))
            {
                return "2.4 GHz";
            }

            if (value.Contains("5g") || value.Contains("5 ghz") || value == "5")
            {
                return "5 GHz";
            }

            if (value.Contains("6g") || value.Contains("6 ghz") || value == "6")
            {
                return "6 GHz";
            }

            return string.Empty;
        }

        // This only interprets the explicit connection value already returned
        // by the normal GL.iNet client inventory. It does not infer a wired
        // link from missing Wi-Fi metadata or diagnostic bridge state.
        private static bool IsExplicitWiredClientConnection(string value) =>
            value.Contains("cable", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("wired", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("ethernet", StringComparison.OrdinalIgnoreCase);

        private static WifiRadioInfo? FindClientNetwork(
            List<WifiRadioInfo> networks,
            JsonElement client,
            string band)
        {
            string ssid = GetFlexibleString(client, "ssid", "wifi_name", "network");
            string runtimeInterface = GetFlexibleString(client, "ifname", "device", "wlan");
            string connectionLabel = GetFlexibleString(
                client,
                "iface",
                "interface",
                "connection",
                "type");

            if (ssid.Length > 0)
            {
                WifiRadioInfo? bySsid = networks.FirstOrDefault(n =>
                    n.Band == band && n.Ssid.Equals(ssid, StringComparison.OrdinalIgnoreCase));
                if (bySsid != null)
                {
                    return bySsid;
                }
            }

            if (runtimeInterface.Length > 0)
            {
                WifiRadioInfo? byInterface = networks.FirstOrDefault(n =>
                    n.Band == band && n.Interface.Equals(runtimeInterface, StringComparison.OrdinalIgnoreCase));
                if (byInterface != null)
                {
                    return byInterface;
                }
            }

            // GL.iNet labels virtual networks in gl-clients as values such as
            // "2.4G_Iot", "5G_Iot", "2.4G_Guest" and "5G_Guest".  These
            // records often omit the SSID and runtime interface, so preserve
            // the role suffix before using the ordinary same-band fallback.
            bool isIot = connectionLabel.Contains(
                "iot",
                StringComparison.OrdinalIgnoreCase);
            bool isGuest = connectionLabel.Contains(
                "guest",
                StringComparison.OrdinalIgnoreCase);

            if (isIot || isGuest)
            {
                string role = isIot ? "iot" : "guest";

                WifiRadioInfo? byRole = networks.FirstOrDefault(n =>
                    n.Band == band &&
                    !n.Status.Equals("Disabled", StringComparison.OrdinalIgnoreCase) &&
                    n.Ssid.Contains(role, StringComparison.OrdinalIgnoreCase));

                if (byRole != null)
                {
                    return byRole;
                }
            }

            // Firmware sometimes reports only "2.4G" or "5G".  In that case
            // use the primary enabled AP for that band.
            return networks.FirstOrDefault(n =>
                n.Band == band &&
                !n.Status.Equals("Disabled", StringComparison.OrdinalIgnoreCase));
        }

        public async Task<WifiClientInfo?> GetWifiClientDetailsAsync(
            string macAddress,
            string ipAddress)
        {
            string normalisedMac = NormaliseMacAddress(macAddress);
            string targetIp = (ipAddress ?? string.Empty).Trim();

            // GL.iNet's own client inventory is the most reliable source for
            // deciding whether a device is on 2.4 GHz, 5 GHz or Ethernet.
            WifiClientInfo? inventoryMatch = null;
            string clientJson = await _ssh.RunCommandAsync(
                "ubus call gl-clients list 2>/dev/null || true");

            if (!string.IsNullOrWhiteSpace(clientJson))
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(clientJson);
                    foreach (JsonElement client in EnumerateClientObjects(document.RootElement))
                    {
                        if (!GetFlexibleBoolean(client, "online", true))
                        {
                            continue;
                        }

                        string candidateMac = GetFlexibleString(
                            client, "mac", "macaddr", "mac_address");
                        string candidateIp = GetFlexibleString(
                            client, "ip", "ipaddr", "ip_address");

                        bool macMatches = normalisedMac.Length == 12 &&
                            NormaliseMacAddress(candidateMac) == normalisedMac;
                        bool ipMatches = targetIp.Length > 0 && targetIp != "-" &&
                            candidateIp.Equals(targetIp, StringComparison.OrdinalIgnoreCase);

                        if (!macMatches && !ipMatches)
                        {
                            continue;
                        }

                        string rawConnection = GetFlexibleString(
                            client, "iface", "interface", "connection", "type");
                        string band = NormaliseClientBand(rawConnection);
                        string ssid = GetFlexibleString(
                            client, "ssid", "wifi_name", "wifi", "network");
                        string runtimeInterface = GetFlexibleString(
                            client, "ifname", "device", "wlan");
                        string signal = GetFlexibleString(
                            client, "signal", "rssi", "wifi_signal",
                            "signal_strength", "rssi_dbm");

                        inventoryMatch = new WifiClientInfo
                        {
                            Name = GetFlexibleString(client, "name", "hostname", "host_name"),
                            IpAddress = string.IsNullOrWhiteSpace(candidateIp) ? targetIp : candidateIp,
                            MacAddress = string.IsNullOrWhiteSpace(candidateMac) ? macAddress : candidateMac,
                            Band = band,
                            Ssid = ssid,
                            Interface = runtimeInterface,
                            Signal = FormatSignal(signal)
                        };
                        break;
                    }
                }
                catch (JsonException)
                {
                    // Continue with the configured-network and driver fallbacks.
                }
            }

            List<WifiRadioInfo> networks = await GetWifiRadiosAsync();

            if (inventoryMatch is not null)
            {
                WifiRadioInfo? mappedNetwork = null;

                if (HasUsefulWifiValue(inventoryMatch.Ssid))
                {
                    mappedNetwork = networks.FirstOrDefault(network =>
                        network.Ssid.Equals(
                            inventoryMatch.Ssid,
                            StringComparison.OrdinalIgnoreCase));
                }

                mappedNetwork ??= networks.FirstOrDefault(network =>
                    network.Clients.Any(client =>
                        (normalisedMac.Length == 12 &&
                         NormaliseMacAddress(client.MacAddress) == normalisedMac) ||
                        (targetIp.Length > 0 && targetIp != "-" &&
                         client.IpAddress.Equals(targetIp, StringComparison.OrdinalIgnoreCase))));

                mappedNetwork ??= networks.FirstOrDefault(network =>
                    HasUsefulWifiValue(inventoryMatch.Band) &&
                    network.Band.Equals(
                        inventoryMatch.Band,
                        StringComparison.OrdinalIgnoreCase));

                if (mappedNetwork is not null)
                {
                    inventoryMatch.Ssid = mappedNetwork.Ssid;
                    inventoryMatch.Band = mappedNetwork.Band;

                    if (!HasUsefulWifiValue(inventoryMatch.Interface))
                    {
                        inventoryMatch.Interface = mappedNetwork.Interface;
                    }

                    WifiClientInfo? mappedClient = mappedNetwork.Clients.FirstOrDefault(client =>
                        (normalisedMac.Length == 12 &&
                         NormaliseMacAddress(client.MacAddress) == normalisedMac) ||
                        (targetIp.Length > 0 && targetIp != "-" &&
                         client.IpAddress.Equals(targetIp, StringComparison.OrdinalIgnoreCase)));

                    if (mappedClient is not null && HasUsefulWifiValue(mappedClient.Signal))
                    {
                        inventoryMatch.Signal = mappedClient.Signal;
                    }
                }
            }

            // Ask the wireless driver for RSSI and the live AP interface. Some
            // Flint 2 firmware builds omit these values, so this is enrichment
            // only and must not discard the GL.iNet inventory result.
            if (normalisedMac.Length == 12)
            {
                string formattedMac = string.Join(":",
                    Enumerable.Range(0, 6)
                        .Select(index => normalisedMac.Substring(index * 2, 2)));

                string command =
                    "target='" + formattedMac + "'; " +
                    "for iface in $(iw dev 2>/dev/null | awk '$1 == \"Interface\" {print $2}'); do " +
                    "station=$(iw dev \"$iface\" station get \"$target\" 2>/dev/null); " +
                    "if [ -n \"$station\" ]; then " +
                    "ssid=$(iw dev \"$iface\" info 2>/dev/null | sed -n 's/^[[:space:]]*ssid //p' | head -n1); " +
                    "signal=$(printf '%s\\n' \"$station\" | awk '/signal:/ {print $2; exit}'); " +
                    "printf '%s|%s|%s\\n' \"$iface\" \"$ssid\" \"$signal\"; exit 0; fi; " +
                    "done";

                string output = await _ssh.RunCommandAsync(command);
                string firstLine = output
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault() ?? string.Empty;
                string[] parts = firstLine.Split('|');

                if (parts.Length >= 2 && !string.IsNullOrWhiteSpace(parts[0]))
                {
                    inventoryMatch ??= new WifiClientInfo
                    {
                        MacAddress = macAddress,
                        IpAddress = string.IsNullOrWhiteSpace(targetIp) ? "-" : targetIp
                    };

                    inventoryMatch.Interface = parts[0].Trim();
                    if (HasUsefulWifiValue(parts[1]))
                    {
                        inventoryMatch.Ssid = parts[1].Trim();
                    }
                    if (parts.Length > 2 && HasUsefulWifiValue(parts[2]))
                    {
                        inventoryMatch.Signal = FormatSignal(parts[2]);
                    }
                }
            }

            if (inventoryMatch is not null && HasUsefulWifiValue(inventoryMatch.Ssid))
            {
                if (!HasUsefulWifiValue(inventoryMatch.Signal))
                {
                    inventoryMatch.Signal = "Not reported";
                }
                return inventoryMatch;
            }

            // Final fallback: use the already-populated per-SSID lists.
            foreach (WifiRadioInfo network in networks)
            {
                WifiClientInfo? client = network.Clients.FirstOrDefault(item =>
                    (normalisedMac.Length == 12 &&
                     NormaliseMacAddress(item.MacAddress) == normalisedMac) ||
                    (targetIp.Length > 0 && targetIp != "-" &&
                     item.IpAddress.Equals(targetIp, StringComparison.OrdinalIgnoreCase)));

                if (client is null)
                {
                    continue;
                }

                client.Ssid = network.Ssid;
                client.Band = network.Band;
                if (!HasUsefulWifiValue(client.Interface))
                {
                    client.Interface = network.Interface;
                }
                if (!HasUsefulWifiValue(client.Signal))
                {
                    client.Signal = "Not reported";
                }
                return client;
            }

            return inventoryMatch;
        }

        private static string NormaliseMacAddress(string? macAddress)
        {
            return new string((macAddress ?? string.Empty)
                .Where(Uri.IsHexDigit)
                .Select(char.ToUpperInvariant)
                .ToArray());
        }

        private static bool HasUsefulWifiValue(string? value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                value != "-" && value != "—" &&
                !value.Equals("Unknown", StringComparison.OrdinalIgnoreCase) &&
                !value.Equals("Not reported", StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatSignal(string signal)
        {
            if (string.IsNullOrWhiteSpace(signal))
            {
                return "-";
            }

            string value = signal.Trim();
            return value.Contains("dbm", StringComparison.OrdinalIgnoreCase)
                ? value
                : $"{value} dBm";
        }

        private static string FormatWifiSecurity(string encryption)
        {
            string value = encryption?.Trim().ToLowerInvariant() ?? string.Empty;
            if (value == "none" || value == "open") return "Open";
            if (value.Contains("sae") && value.Contains("psk")) return "WPA2 / WPA3";
            if (value.Contains("sae")) return "WPA3";
            if (value.Contains("psk2")) return "WPA2";
            if (value.Contains("psk")) return "WPA";
            return string.IsNullOrWhiteSpace(encryption) ? "Unknown" : encryption.Trim();
        }

        private static string FormatWifiChannelWidth(string hardwareMode)
        {
            if (string.IsNullOrWhiteSpace(hardwareMode))
            {
                return "N/A";
            }

            Match match = Regex.Match(hardwareMode, @"(?:HT|VHT|HE|EHT)(20|40|80|160|320)", RegexOptions.IgnoreCase);
            return match.Success ? $"{match.Groups[1].Value} MHz" : "N/A";
        }

        private static WifiGuestClassification ClassifyGuestNetwork(
            string networkAssociation,
            string ssid,
            string interfaceName)
        {
            if (!string.IsNullOrWhiteSpace(networkAssociation) &&
                networkAssociation.Contains("guest", StringComparison.OrdinalIgnoreCase))
            {
                return WifiGuestClassification.VerifiedGuest;
            }

            return ContainsGuestMarker(ssid) || ContainsGuestMarker(interfaceName)
                ? WifiGuestClassification.LikelyGuest
                : WifiGuestClassification.Unknown;
        }

        private static bool ContainsGuestMarker(string value) =>
            !string.IsNullOrWhiteSpace(value) &&
            value.Contains("guest", StringComparison.OrdinalIgnoreCase);

        private static string InferBandFromChannel(string channelValue)
        {
            if (int.TryParse(channelValue?.Trim(), out int channel))
            {
                return channel <= 14 ? "2.4 GHz" : "5 GHz";
            }

            return "Unknown";
        }

        public async Task<string> RestartWifiAsync()
        {
            string result = await _ssh.RunCommandAsync("wifi reload >/tmp/routerpilot_wifi_reload.log 2>&1; rc=$?; echo $rc");
            return result.Trim().EndsWith("0", StringComparison.Ordinal)
                ? "Wi-Fi restart requested successfully."
                : "The router could not restart Wi-Fi.";
        }

        public async Task<string> RestartWanAsync()
        {
            string result = await _ssh.RunCommandAsync("ifdown wan >/dev/null 2>&1; sleep 2; ifup wan >/dev/null 2>&1; echo $?");
            return result.Trim().EndsWith("0", StringComparison.Ordinal)
                ? "WAN reconnect requested successfully."
                : "The router could not reconnect WAN.";
        }

        public async Task<NetworkTrafficSnapshot>
            GetNetworkTrafficSnapshotAsync()
        {
            // Resolve the physical device used by the logical WAN interface,
            // then read the kernel byte counters. The fallbacks cover common
            // GL.iNet/OpenWrt interface layouts.
            string output =
                await _ssh.RunCommandAsync(
                    "dev=$(ubus call network.interface.wan status 2>/dev/null | jsonfilter -e '@.l3_device' 2>/dev/null); " +
                    "[ -n \"$dev\" ] || dev=$(ubus call network.interface.wan status 2>/dev/null | jsonfilter -e '@.device' 2>/dev/null); " +
                    "[ -n \"$dev\" ] || dev=$(ip route show default 2>/dev/null | awk 'NR==1 {print $5}'); " +
                    "[ -n \"$dev\" ] || dev=eth1; " +
                    "rx=$(cat /sys/class/net/$dev/statistics/rx_bytes 2>/dev/null || echo 0); " +
                    "tx=$(cat /sys/class/net/$dev/statistics/tx_bytes 2>/dev/null || echo 0); " +
                    "printf '%s|%s|%s' \"$dev\" \"$rx\" \"$tx\"");

            string[] parts =
                output.Trim().Split('|');

            return new NetworkTrafficSnapshot
            {
                InterfaceName =
                    parts.Length > 0 &&
                    !string.IsNullOrWhiteSpace(parts[0])
                        ? parts[0].Trim()
                        : "-",

                ReceivedBytes =
                    parts.Length > 1 &&
                    long.TryParse(parts[1].Trim(), out long received)
                        ? received
                        : 0,

                TransmittedBytes =
                    parts.Length > 2 &&
                    long.TryParse(parts[2].Trim(), out long transmitted)
                        ? transmitted
                        : 0,

                CapturedAtUtc = DateTime.UtcNow
            };
        }

        //

        private static string NormaliseRouterHost(
            string routerIp)
        {
            string value =
                routerIp.Trim();

            if (Uri.TryCreate(
                    value,
                    UriKind.Absolute,
                    out Uri? uri) &&
                !string.IsNullOrWhiteSpace(
                    uri.Host))
            {
                return uri.Host;
            }

            value =
                value
                    .Replace(
                        "https://",
                        string.Empty,
                        StringComparison.OrdinalIgnoreCase)
                    .Replace(
                        "http://",
                        string.Empty,
                        StringComparison.OrdinalIgnoreCase)
                    .TrimEnd('/');

            int slashIndex =
                value.IndexOf('/');

            if (slashIndex >= 0)
            {
                value =
                    value[..slashIndex];
            }

            int colonIndex =
                value.IndexOf(':');

            if (colonIndex >= 0)
            {
                value =
                    value[..colonIndex];
            }

            if (string.IsNullOrWhiteSpace(
                    value))
            {
                throw new ArgumentException(
                    "The router address is invalid.",
                    nameof(routerIp));
            }

            return value;
        }

        //

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _adGuardClient.Dispose();
            _tokenLock.Dispose();

            if (_sessionService is IDisposable sessionDisposable)
            {
                sessionDisposable.Dispose();
            }

            if (_ssh is IDisposable sshDisposable)
            {
                sshDisposable.Dispose();
            }
        }
    }

}
