using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
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
                    printf 'N|%s|%s|%s|%s|%s|%s|%s|%s\n' "$s" "$dev" "$display_iface" "$ssid" "$band" "$channel" "$encryption" "$state"
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
                if (parts.Length < 9 || parts[0] != "N")
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
                    Status = string.IsNullOrWhiteSpace(parts[8]) ? "Configured" : parts[8].Trim()
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
                            (rawInterface.Contains("cable", StringComparison.OrdinalIgnoreCase) ? "Ethernet" : "Unknown") : band,
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
