using System.Diagnostics;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using RouterPilot.Configuration;
using RouterPilot.Models;
using RouterPilot.Services;
using RouterPilot.ViewModels;

namespace RouterPilot.VpnMutationHarness;

internal interface ITailscaleConfigTransport
{
    Task<JsonObject> ReadSettingsAsync(CancellationToken cancellationToken);
    Task WriteSettingsAsync(JsonObject settings, CancellationToken cancellationToken);
}

internal sealed record ValidationResult(
    bool WriteSucceeded,
    bool ReadBackSucceeded,
    bool RestorationRequired,
    bool RestoreSucceeded,
    bool FinalReadBackSucceeded,
    bool RestorationFailed,
    string? Error,
    string? RpcErrorCode = null,
    string? RpcErrorMessage = null,
    string? RpcErrorDataShape = null);

internal static class TailscaleConfigParser
{
    internal sealed class RpcFailure : Exception
    {
        public string Code { get; }
        public string SafeMessage { get; }
        public string DataShape { get; }

        public RpcFailure(JsonElement error)
            : base("GL.iNet RPC request failed.")
        {
            Code = error.TryGetProperty("code", out JsonElement code)
                ? SafeScalar(code)
                : "<missing>";
            SafeMessage = error.TryGetProperty("message", out JsonElement message) && message.ValueKind == JsonValueKind.String
                ? SafeMessageText(message.GetString())
                : "<redacted>";
            DataShape = error.TryGetProperty("data", out JsonElement data)
                ? DescribeShape(data, "data", 0)
                : "<absent>";
        }

        private static string SafeMessageText(string? message)
        {
            string value = (message ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            string lower = value.ToLowerInvariant();
            return value.Length is 0 or > 200 || new[] { "password", "token", "cookie", "secret", "private", "auth", "session", "url" }.Any(lower.Contains)
                ? "<redacted>"
                : value;
        }

        private static string SafeScalar(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => "<string>",
            JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => "<redacted>"
        };

        private static string DescribeShape(JsonElement value, string path, int depth)
        {
            if (depth > 3) return path + ": " + value.ValueKind;
            if (value.ValueKind == JsonValueKind.Object)
                return string.Join(", ", value.EnumerateObject().Select(property =>
                    DescribeShape(property.Value, path + "." + property.Name, depth + 1)));
            if (value.ValueKind == JsonValueKind.Array)
                return path + ": array";
            return path + ": " + value.ValueKind + " <redacted>";
        }
    }

    public static void EnsureSuccess(JsonElement root)
    {
        if (root.TryGetProperty("error", out JsonElement error))
            throw new RpcFailure(error);
    }

    public static JsonObject ExtractSettings(JsonElement root)
    {
        EnsureSuccess(root);
        if (!root.TryGetProperty("result", out JsonElement result) || result.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("WRITE CONTRACT NOT PROVEN: get_config did not return result.");
        return JsonNode.Parse(result.GetRawText())!.AsObject();
    }
}

internal static class TailscaleConfigRequestBuilder
{
    private static readonly string[] BooleanFields = ["enabled", "lan_enabled", "wan_enabled"];

    public static JsonObject Build(JsonObject current)
    {
        var request = new JsonObject();
        foreach (string field in BooleanFields)
        {
            if (!MutationCoordinator.TryReadBoolean(current, field, out bool value, out _))
                throw new InvalidOperationException($"SET_CONFIG CONTRACT NOT PROVEN: required field {field} is absent or invalid.");
            request[field] = value;
        }

        // The GL.iNet UI includes this optional string when the router reports
        // it. Never invent or clear an exit-node value.
        if (current["exit_node_ip"] is JsonNode exitNode)
        {
            if (exitNode is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out string? value))
                throw new InvalidOperationException("SET_CONFIG CONTRACT NOT PROVEN: exit_node_ip has an unexpected type.");
            request["exit_node_ip"] = value;
        }
        return request;
    }
}

internal sealed class MutationCoordinator
{
    private readonly ITailscaleConfigTransport _transport;
    private readonly Func<long> _generation;
    private readonly Action<string> _log;

    public MutationCoordinator(ITailscaleConfigTransport transport, Func<long> generation, Action<string> log)
    {
        _transport = transport;
        _generation = generation;
        _log = log;
    }

    public Task<ValidationResult> ValidateLanEnabledAsync(CancellationToken cancellationToken) =>
        ValidateFieldAsync("lan_enabled", cancellationToken);

    public Task<ValidationResult> ValidateWanEnabledAsync(CancellationToken cancellationToken) =>
        ValidateFieldAsync("wan_enabled", cancellationToken);

    public async Task<ValidationResult> ValidateFieldAsync(string field, CancellationToken cancellationToken)
    {
        if (field is not ("enabled" or "lan_enabled" or "wan_enabled"))
            throw new ArgumentOutOfRangeException(nameof(field), "Only enabled, lan_enabled and wan_enabled are supported.");
        long capturedGeneration = _generation();
        JsonObject original = await _transport.ReadSettingsAsync(cancellationToken);
        if (!TryReadBoolean(original, field, out bool originalValue, out JsonValueKind valueKind))
            throw new InvalidOperationException($"WRITE CONTRACT NOT PROVEN: {field} was not a boolean/0/1 setting.");

        JsonObject temporary = (JsonObject)original.DeepClone();
        temporary[field] = valueKind == JsonValueKind.String
            ? (JsonNode)(originalValue ? "0" : "1")
            : originalValue ? 0 : 1;
        bool writeSucceeded = false;
        bool readBackSucceeded = false;
        bool restorationRequired = false;
        bool restoreSucceeded = false;
        bool finalReadBackSucceeded = false;
        bool restorationFailed = false;
        string? error = null;
        string? rpcErrorCode = null;
        string? rpcErrorMessage = null;
        string? rpcErrorDataShape = null;

        try
        {
            EnsureGeneration(capturedGeneration);
            await _transport.WriteSettingsAsync(temporary, cancellationToken);
            writeSucceeded = true;
            restorationRequired = true;

            JsonObject changed = await _transport.ReadSettingsAsync(cancellationToken);
            readBackSucceeded = TryReadBoolean(changed, field, out bool changedValue, out _) && changedValue != originalValue;
            if (!readBackSucceeded)
                throw new InvalidOperationException($"Temporary {field} read-back did not match the requested value.");
        }
        catch (TailscaleConfigParser.RpcFailure exception)
        {
            error = exception.Message;
            rpcErrorCode = exception.Code;
            rpcErrorMessage = exception.SafeMessage;
            rpcErrorDataShape = exception.DataShape;
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }
        finally
        {
            if (restorationRequired)
            {
                try
                {
                    EnsureGeneration(capturedGeneration);
                    await _transport.WriteSettingsAsync(original, CancellationToken.None);
                    restoreSucceeded = true;
                    JsonObject restored = await _transport.ReadSettingsAsync(CancellationToken.None);
                    finalReadBackSucceeded = TryReadBoolean(restored, field, out bool finalValue, out _) && finalValue == originalValue;
                    if (!finalReadBackSucceeded)
                        throw new InvalidOperationException($"Final {field} read-back did not match the original value.");
                }
                catch (Exception exception)
                {
                    restorationFailed = true;
                    error = string.IsNullOrWhiteSpace(error) ? exception.Message : error + " Restore: " + exception.Message;
                    _log("RESTORATION FAILED");
                }
            }
        }

        return new ValidationResult(writeSucceeded, readBackSucceeded, restorationRequired, restoreSucceeded, finalReadBackSucceeded, restorationFailed, error, rpcErrorCode, rpcErrorMessage, rpcErrorDataShape);
    }

    private void EnsureGeneration(long capturedGeneration)
    {
        if (_generation() != capturedGeneration)
            throw new InvalidOperationException("Active router/profile generation changed; mutation aborted.");
    }

    internal static bool TryReadBoolean(JsonObject settings, string property, out bool value, out JsonValueKind kind)
    {
        value = false;
        kind = JsonValueKind.Undefined;
        if (settings[property] is not JsonNode node)
            return false;
        if (node is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<bool>(out value)) { kind = JsonValueKind.True; return true; }
            if (jsonValue.TryGetValue<int>(out int number) && number is 0 or 1) { value = number == 1; kind = JsonValueKind.Number; return true; }
            if (jsonValue.TryGetValue<string>(out string? text) && (text == "0" || text == "1")) { value = text == "1"; kind = JsonValueKind.String; return true; }
        }
        return false;
    }
}

internal sealed class FakeTransport : ITailscaleConfigTransport
{
    private JsonObject _settings;
    private readonly bool _mismatchReadBack;
    private readonly bool _failWrite;
    private readonly Action? _afterFirstWrite;
    private readonly bool _failRestore;
    public int WriteCount { get; private set; }
    public List<JsonObject> Writes { get; } = [];

    public FakeTransport(JsonObject settings, bool mismatchReadBack = false, bool failWrite = false, Action? afterFirstWrite = null, bool failRestore = false)
    { _settings = (JsonObject)settings.DeepClone(); _mismatchReadBack = mismatchReadBack; _failWrite = failWrite; _afterFirstWrite = afterFirstWrite; _failRestore = failRestore; }

    public Task<JsonObject> ReadSettingsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        JsonObject result = (JsonObject)_settings.DeepClone();
        if (_mismatchReadBack && WriteCount == 1) result["lan_enabled"] = 0;
        return Task.FromResult(result);
    }

    public Task WriteSettingsAsync(JsonObject settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WriteCount++;
        Writes.Add((JsonObject)settings.DeepClone());
        if (_failWrite) throw new InvalidOperationException("simulated write failure");
        if (_failRestore && WriteCount > 1) throw new InvalidOperationException("simulated restore failure");
        _settings = (JsonObject)settings.DeepClone();
        if (WriteCount == 1) _afterFirstWrite?.Invoke();
        return Task.CompletedTask;
    }
}

internal sealed class RouterTransport : ITailscaleConfigTransport
{
    private readonly RouterManager _manager;
    public RouterTransport(RouterManager manager) => _manager = manager;

    public Task<JsonDocument> ReadRawAsync(CancellationToken cancellationToken) =>
        _manager.GetTailscaleConfigAsync(cancellationToken);

    public async Task<JsonObject> ReadSettingsAsync(CancellationToken cancellationToken)
    {
        using JsonDocument document = await _manager.GetTailscaleConfigAsync(cancellationToken);
        return TailscaleConfigParser.ExtractSettings(document.RootElement);
    }

    public async Task WriteSettingsAsync(JsonObject settings, CancellationToken cancellationToken)
    {
        JsonObject request = TailscaleConfigRequestBuilder.Build(settings);
        using JsonDocument document = await _manager.SetTailscaleConfigAsync(
            JsonSerializer.SerializeToElement(request), cancellationToken);
        TailscaleConfigParser.EnsureSuccess(document.RootElement);
    }
}

internal static class Program
{
    private static async Task Main(string[] args)
    {
        Console.WriteLine("RouterPilot VPN mutation harness");
        RunUnitTests();
        Console.WriteLine("Local coordinator tests: PASS");
        if (args.Any(argument => string.Equals(argument, "--self-test", StringComparison.OrdinalIgnoreCase)))
            return;
        if (args.Any(argument => string.Equals(argument, "--tunnel", StringComparison.OrdinalIgnoreCase)))
        {
            await RunTunnelValidationAsync(args);
            return;
        }
        if (args.Any(argument => string.Equals(argument, "--inventory", StringComparison.OrdinalIgnoreCase)))
        {
            await RunInventoryReadAsync();
            return;
        }
        if (args.Any(argument => string.Equals(argument, "--plugins", StringComparison.OrdinalIgnoreCase)))
        {
            await RunPluginDiscoveryAsync();
            return;
        }
        string? pluginMutation = args.FirstOrDefault(argument => argument.StartsWith("--test-plugin-install-remove=", StringComparison.OrdinalIgnoreCase));
        if (pluginMutation is not null)
        {
            await RunPluginInstallRemoveValidationAsync(pluginMutation[("--test-plugin-install-remove=").Length..]);
            return;
        }
        if (args.Any(argument => string.Equals(argument, "--test-plugin-index-refresh", StringComparison.OrdinalIgnoreCase)))
        {
            await RunPluginIndexRefreshValidationAsync();
            return;
        }
        string? pluginUpdate = args.FirstOrDefault(argument => argument.StartsWith("--test-plugin-update=", StringComparison.OrdinalIgnoreCase));
        if (pluginUpdate is not null)
        {
            await RunPluginUpdateValidationAsync(pluginUpdate[("--test-plugin-update=").Length..]);
            return;
        }
        Console.WriteLine("Runtime validation is opt-in and requires the configured RouterPilot profile.");
        string targetField = args.FirstOrDefault(argument => argument.StartsWith("--field=", StringComparison.OrdinalIgnoreCase))?[8..]
            ?? "lan_enabled";
        await RunRuntimeValidationAsync(targetField);
    }

    private static async Task RunInventoryReadAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        try
        {
            var settings = new SettingsService();
            var profiles = new RouterProfileService(settings);
            var active = new ActiveRouterContext(profiles);
            await using var provider = new RouterManagerProvider(
                settings, active, new SshHostKeyTrustService(settings),
                new RouterCertificateTrustService(settings),
                new AdGuardTransportSecurityService(), new SshConnectionFactory());
            RouterManager manager = await provider.GetRouterManagerAsync(timeout.Token);
            RouterInfo identity = await manager.GetRouterInfoAsync();
            IReadOnlyList<VpnTunnelInfo> tunnels = await manager.GetVpnTunnelsAsync(timeout.Token);
            Console.WriteLine($"Router: {Sanitize(identity.Model)}");
            Console.WriteLine($"GET_TUNNEL_RAW_RPC: PASS; RAW_TUNNEL_COUNT={tunnels.Count}");
            (IReadOnlyList<VpnClientProfileInfo> profilesRead, VpnProfileInventoryState inventoryState) = await manager.GetVpnProfilesAsync(tunnels, timeout.Token);
            Console.WriteLine($"GET_ALL_CONFIG_LIST_RAW_RPC: PASS; INVENTORY_STATE={inventoryState}; RAW_PROFILE_COUNT={profilesRead.Count}");
            Console.WriteLine($"PARSED_TUNNEL_COUNT={tunnels.Count}; PARSED_PROFILE_COUNT={profilesRead.Count}");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"READ_ONLY_INVENTORY_FAILURE: {Sanitize(exception.Message)}");
        }
    }

    private static async Task RunPluginDiscoveryAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        try
        {
            var settings = new SettingsService();
            var profiles = new RouterProfileService(settings);
            var active = new ActiveRouterContext(profiles);
            await using var provider = new RouterManagerProvider(
                settings, active, new SshHostKeyTrustService(settings),
                new RouterCertificateTrustService(settings),
                new AdGuardTransportSecurityService(), new SshConnectionFactory());
            RouterManager manager = await provider.GetRouterManagerAsync(timeout.Token);
            RouterInfo identity = await manager.GetRouterInfoAsync();
            Console.WriteLine("ROUTERPILOT PLUGIN DISCOVERY (READ-ONLY)");
            Console.WriteLine($"Router: {Sanitize(identity.Model)}");

            Stopwatch timer = Stopwatch.StartNew();
            string executable = await manager.RunReadOnlySshCommandAsync("command -v opkg 2>/dev/null", timeout.Token);
            Console.WriteLine($"OPKG_AVAILABLE={(IsCommandSuccess(executable) ? "YES" : "NO")}");
            if (!IsCommandSuccess(executable)) return;

            Console.WriteLine($"OPKG_VERSION={SafeFirstLine(await manager.RunReadOnlySshCommandAsync("opkg --version 2>/dev/null", timeout.Token))}");
            Console.WriteLine($"ARCHITECTURES={CountMeaningfulLines(await manager.RunReadOnlySshCommandAsync("opkg print-architecture 2>/dev/null", timeout.Token))}");

            string statusPath = await manager.RunReadOnlySshCommandAsync(
                "for p in /opt/lib/opkg/status /usr/lib/opkg/status /var/lib/opkg/status; do [ -f \"$p\" ] && { printf '%s\\n' \"$p\"; break; }; done", timeout.Token);
            Console.WriteLine($"STATUS_DATABASE={SafeFirstLine(statusPath) ?? "UNAVAILABLE"}");

            string installed = await manager.RunReadOnlySshCommandAsync("opkg list-installed 2>/dev/null", timeout.Token);
            string available = await manager.RunReadOnlySshCommandAsync("opkg list 2>/dev/null", timeout.Token);
            string upgradable = await manager.RunReadOnlySshCommandAsync("opkg list-upgradable 2>/dev/null", timeout.Token);
            Console.WriteLine($"INSTALLED_COUNT={CountPackageLines(installed)}");
            Console.WriteLine($"AVAILABLE_COUNT={CountPackageLines(available)}");
            Console.WriteLine($"UPGRADABLE_COUNT={(HasSafeCommandResult(upgradable) ? CountPackageLines(upgradable).ToString() : "UNAVAILABLE")}");
            Console.WriteLine($"UPGRADABLE_PACKAGES={string.Join(",", SafeLines(upgradable).Where(line => line.Contains(" - ", StringComparison.Ordinal)).Select(line => Sanitize(line.Split(" - ", 2)[0])).Take(10))}");
            string upgradeMetadata = await manager.RunReadOnlySshCommandAsync(
                "for p in iperf3 dnsmasq-full; do printf 'UPGRADE_CANDIDATE %s\\n' \"$p\"; opkg status \"$p\" 2>/dev/null | sed -n 's/^\\(Package\\|Version\\|Architecture\\|Depends\\|Description\\):/\\1:/p'; opkg info \"$p\" 2>/dev/null | sed -n 's/^\\(Package\\|Version\\|Architecture\\|Depends\\|Description\\):/available-\\1:/p' | head -n 6; done", timeout.Token);
            Console.WriteLine($"UPGRADABLE_METADATA={string.Join(" || ", SafeLines(upgradeMetadata).Take(30).Select(Sanitize))}");

            string feeds = await manager.RunReadOnlySshCommandAsync(
                "for f in /etc/opkg.conf /etc/opkg/*.conf /etc/opkg/customfeeds.conf; do [ -f \"$f\" ] && cat \"$f\"; done 2>/dev/null", timeout.Token);
            (int official, int custom) = CountFeeds(feeds);
            Console.WriteLine($"OFFICIAL_FEEDS={official}");
            Console.WriteLine($"CUSTOM_FEEDS={custom}");

            string lists = await manager.RunReadOnlySshCommandAsync(
                "for d in /var/opkg-lists /usr/lib/opkg/lists /opt/lib/opkg/lists; do [ -d \"$d\" ] && find \"$d\" -maxdepth 1 -type f 2>/dev/null; done", timeout.Token);
            Console.WriteLine($"PACKAGE_INDEX_FILES={CountMeaningfulLines(lists)}");
            string detailFields = await manager.RunReadOnlySshCommandAsync(
                "pkg=$(opkg list-installed 2>/dev/null | awk 'NR==1 {print $1}'); [ -n \"$pkg\" ] && opkg status \"$pkg\" 2>/dev/null | sed -n 's/:.*/:/p' | sort -u", timeout.Token);
            Console.WriteLine($"PACKAGE_DETAIL_FIELDS={string.Join(",", SafeLines(detailFields).Take(20))}");
            string guiEvidence = await manager.RunReadOnlySshCommandAsync(
                "find /www /www-static /usr/lib/lua -type f 2>/dev/null | while read f; do grep -qiE 'opkg|plugin[s]?|package manager' \"$f\" 2>/dev/null && printf '%s\\n' \"$f\"; done | head -n 30", timeout.Token);
            Console.WriteLine($"GUI_EVIDENCE_FILES={CountMeaningfulLines(guiEvidence)}");
            string guiEvidenceNames = await manager.RunReadOnlySshCommandAsync(
                "find /www /www-static /usr/lib/lua -type f 2>/dev/null | while read f; do grep -qiE 'opkg|plugin[s]?|package manager' \"$f\" 2>/dev/null && basename \"$f\"; done | sort -u | head -n 20", timeout.Token);
            Console.WriteLine($"GUI_EVIDENCE_NAMES={string.Join(",", SafeLines(guiEvidenceNames).Take(20))}");
            string guiOpkgTokens = await manager.RunReadOnlySshCommandAsync(
                "grep -RhoE 'opkg([A-Za-z0-9_.:/-]*)' /www /www-static /usr/lib/lua 2>/dev/null | sort -u | head -n 20", timeout.Token);
            Console.WriteLine($"GUI_OPKG_TOKENS={string.Join(",", SafeLines(guiOpkgTokens).Take(20))}");
            string guiCallFiles = await manager.RunReadOnlySshCommandAsync(
                "grep -RIlE 'opkg-call|opkg_package' /www /www-static /usr/lib/lua 2>/dev/null | while read f; do basename \"$f\"; done | sort -u | head -n 20", timeout.Token);
            Console.WriteLine($"GUI_PACKAGE_CALL_FILES={string.Join(",", SafeLines(guiCallFiles).Take(20))}");
            string opkgJsPaths = await manager.RunReadOnlySshCommandAsync(
                "find /www /www-static /usr/lib/lua -name opkg.js -type f 2>/dev/null | head -n 10", timeout.Token);
            Console.WriteLine($"GUI_OPKG_JS_PATHS={string.Join(",", SafeLines(opkgJsPaths).Take(10).Select(System.IO.Path.GetFileName))}");
            string opkgMethods = await manager.RunReadOnlySshCommandAsync(
                "grep -RhoE 'opkg_[A-Za-z0-9_]+|opkg-call|opkg\\.[A-Za-z0-9_]+' /www /www-static /usr/lib/lua 2>/dev/null | sort -u | head -n 40", timeout.Token);
            Console.WriteLine($"GUI_PACKAGE_METHOD_TOKENS={string.Join(",", SafeLines(opkgMethods).Take(40))}");
            string opkgCallEvidence = await manager.RunReadOnlySshCommandAsync(
                "grep -RInE 'opkg-call|opkg_package' /www /www-static /usr/lib/lua 2>/dev/null | head -n 12 | cut -c1-240", timeout.Token);
            Console.WriteLine($"GUI_PACKAGE_CALL_EVIDENCE={string.Join(" || ", SafeLines(opkgCallEvidence).Take(12))}");
            string opkgCallActions = await manager.RunReadOnlySshCommandAsync(
                "grep -oE \"opkg-call[^'\\\"]*|exec_direct\\([^)]*\" /www/luci-static/resources/view/opkg.js 2>/dev/null | head -n 30", timeout.Token);
            Console.WriteLine($"GUI_PACKAGE_ACTIONS={string.Join(" || ", SafeLines(opkgCallActions).Take(30))}");
            string helperEvidence = await manager.RunReadOnlySshCommandAsync(
                "grep -nEi 'case|update|install|remove|upgrade|list-' /usr/libexec/opkg-call 2>/dev/null | head -n 80 | cut -c1-240", timeout.Token);
            Console.WriteLine($"OPKG_HELPER_EVIDENCE={string.Join(" || ", SafeLines(helperEvidence).Take(80))}");
            string helperContract = await manager.RunReadOnlySshCommandAsync(
                "sed -n '1,75p' /usr/libexec/opkg-call 2>/dev/null | sed -E 's/(password|token|secret|key)[[:space:]]*=[^ ]+ /\\1=<redacted> /Ig' | cut -c1-240", timeout.Token);
            Console.WriteLine($"OPKG_HELPER_CONTRACT={string.Join(" || ", SafeLines(helperContract).Take(75))}");
            string candidates = await manager.RunReadOnlySshCommandAsync(
                "for p in jq tree htop nano file less bc; do line=$(opkg list 2>/dev/null | awk -v p=\"$p\" '$1==p {print; exit}'); installed=$(opkg status \"$p\" 2>/dev/null | sed -n 's/^Status: //p'); [ -n \"$line\" ] && printf '%s | available=%s | installed=%s\\n' \"$p\" \"$line\" \"${installed:-not-installed}\"; done", timeout.Token);
            Console.WriteLine($"SAFE_CANDIDATE_SHORTLIST={string.Join(" || ", SafeLines(candidates).Take(12).Select(Sanitize))}");
            string candidateMetadata = await manager.RunReadOnlySshCommandAsync(
                "for p in tree file less bc htop nano; do printf 'CANDIDATE %s\\n' \"$p\"; opkg info \"$p\" 2>/dev/null | sed -n 's/^\\(Package\\|Version\\|Architecture\\|Installed-Size\\|Depends\\|Provides\\|Description\\):/\\1:/p' | head -n 8; opkg whatdepends \"$p\" 2>/dev/null | head -n 4; done", timeout.Token);
            Console.WriteLine($"SAFE_CANDIDATE_METADATA={string.Join(" || ", SafeLines(candidateMetadata).Take(60).Select(Sanitize))}");
            string guiInstalled = await manager.RunReadOnlySshCommandAsync("/usr/libexec/opkg-call list-installed 2>/dev/null", timeout.Token);
            string guiAvailable = await manager.RunReadOnlySshCommandAsync("/usr/libexec/opkg-call list-available 2>/dev/null", timeout.Token);
            Console.WriteLine($"GUI_INSTALLED_COUNT={CountPackageRecords(guiInstalled)}");
            Console.WriteLine($"GUI_AVAILABLE_COUNT={CountPackageRecords(guiAvailable)}");
            Console.WriteLine($"GUI_INSTALLED_SHAPE={OutputShape(guiInstalled)}");
            Console.WriteLine($"GUI_AVAILABLE_SHAPE={OutputShape(guiAvailable)}");
            string storage = await manager.RunReadOnlySshCommandAsync("df -k /overlay /opt 2>/dev/null", timeout.Token);
            Console.WriteLine($"PACKAGE_STORAGE_LINES={CountMeaningfulLines(storage)}");
            string glServices = await manager.RunReadOnlySshCommandAsync(
                "ubus list 2>/dev/null | grep -Ei 'gl|package|plugin|app' | head -n 40", timeout.Token);
            Console.WriteLine($"GL_PACKAGE_RPC_CANDIDATES={string.Join(",", SafeLines(glServices).Take(40))}");
            string packageServices = await manager.RunReadOnlySshCommandAsync(
                "ubus list 2>/dev/null | grep -Ei 'opkg|package|plugin' | head -n 30", timeout.Token);
            Console.WriteLine($"PACKAGE_RPC_SERVICES={string.Join(",", SafeLines(packageServices).Take(20))}");
            Console.WriteLine("OPKG_UPDATE_EXECUTED=NO");
            Console.WriteLine("OPKG_INSTALL_EXECUTED=NO");
            Console.WriteLine("OPKG_REMOVE_EXECUTED=NO");
            Console.WriteLine("OPKG_UPGRADE_EXECUTED=NO");
            timer.Stop();
            Console.WriteLine($"RETRIEVAL_MS={timer.ElapsedMilliseconds}");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"PLUGIN_DISCOVERY_FAILURE={Sanitize(exception.Message)}");
        }
    }

    private static async Task RunPluginInstallRemoveValidationAsync(string package)
    {
        if (!IsSafePackageName(package)) { Console.WriteLine("PLUGIN_MUTATION_REFUSED=invalid-package-name"); return; }
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(3));
        try
        {
            var settings = new SettingsService();
            var profiles = new RouterProfileService(settings);
            var active = new ActiveRouterContext(profiles);
            await using var provider = new RouterManagerProvider(settings, active, new SshHostKeyTrustService(settings), new RouterCertificateTrustService(settings), new AdGuardTransportSecurityService(), new SshConnectionFactory());
            RouterManager manager = await provider.GetRouterManagerAsync(timeout.Token);
            string beforeStatus = await manager.RunReadOnlySshCommandAsync($"opkg status {package} 2>/dev/null", timeout.Token);
            bool initiallyInstalled = IsInstalledStatus(beforeStatus);
            string beforeCountText = await manager.RunReadOnlySshCommandAsync("opkg list-installed 2>/dev/null | wc -l", timeout.Token);
            Console.WriteLine($"PLUGIN={package};ORIGINAL_INSTALLED={(initiallyInstalled ? "YES" : "NO")};BASELINE_COUNT={SafeFirstLine(beforeCountText) ?? "UNKNOWN"}");
            if (initiallyInstalled) { Console.WriteLine("PLUGIN_MUTATION_REFUSED=package-already-installed"); return; }

            string install = await manager.RunReadOnlySshCommandAsync($"/usr/libexec/opkg-call install {package} 2>/dev/null", timeout.Token);
            bool installCommand = IsHelperSuccess(install);
            bool installedAfter = IsInstalledStatus(await manager.RunReadOnlySshCommandAsync($"opkg status {package} 2>/dev/null", timeout.Token));
            Console.WriteLine($"INSTALL_COMMAND={(installCommand ? "PASS" : "FAIL")};INSTALL_READBACK={(installedAfter ? "PASS" : "FAIL")}");

            bool removeCommand = false, removedAfter = false;
            try
            {
                if (installedAfter)
                {
                    string remove = await manager.RunReadOnlySshCommandAsync($"/usr/libexec/opkg-call remove {package} 2>/dev/null", timeout.Token);
                    removeCommand = IsHelperSuccess(remove);
                    removedAfter = !IsInstalledStatus(await manager.RunReadOnlySshCommandAsync($"opkg status {package} 2>/dev/null", timeout.Token));
                }
            }
            finally
            {
                bool finalRemoved = !IsInstalledStatus(await manager.RunReadOnlySshCommandAsync($"opkg status {package} 2>/dev/null", timeout.Token));
                string finalCount = await manager.RunReadOnlySshCommandAsync("opkg list-installed 2>/dev/null | wc -l", timeout.Token);
                Console.WriteLine($"REMOVE_COMMAND={(removeCommand ? "PASS" : "FAIL")};REMOVE_READBACK={(removedAfter ? "PASS" : "FAIL")};FINAL_COUNT={SafeFirstLine(finalCount) ?? "UNKNOWN"};ROUTER_RESTORED={(finalRemoved ? "YES" : "NO")}");
            }
        }
        catch (Exception exception) { Console.WriteLine($"PLUGIN_MUTATION_FAILURE={Sanitize(exception.Message)}"); }
    }

    private static async Task RunPluginIndexRefreshValidationAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(3));
        try
        {
            var settings = new SettingsService(); var profiles = new RouterProfileService(settings); var active = new ActiveRouterContext(profiles);
            await using var provider = new RouterManagerProvider(settings, active, new SshHostKeyTrustService(settings), new RouterCertificateTrustService(settings), new AdGuardTransportSecurityService(), new SshConnectionFactory());
            RouterManager manager = await provider.GetRouterManagerAsync(timeout.Token);
            string beforeAvailable = await manager.RunReadOnlySshCommandAsync("opkg list 2>/dev/null | wc -l", timeout.Token);
            string beforeUpdates = await manager.RunReadOnlySshCommandAsync("opkg list-upgradable 2>/dev/null | wc -l", timeout.Token);
            string beforeIndexes = await manager.RunReadOnlySshCommandAsync("for f in /usr/lib/opkg/lists/* /var/opkg-lists/*; do [ -f \"$f\" ] && stat -c '%n %Y' \"$f\" 2>/dev/null; done", timeout.Token);
            string refresh = await manager.RunReadOnlySshCommandAsync("/usr/libexec/opkg-call update 2>/dev/null", timeout.Token);
            string afterAvailable = await manager.RunReadOnlySshCommandAsync("opkg list 2>/dev/null | wc -l", timeout.Token);
            string afterUpdates = await manager.RunReadOnlySshCommandAsync("opkg list-upgradable 2>/dev/null | wc -l", timeout.Token);
            string afterIndexes = await manager.RunReadOnlySshCommandAsync("for f in /usr/lib/opkg/lists/* /var/opkg-lists/*; do [ -f \"$f\" ] && stat -c '%n %Y' \"$f\" 2>/dev/null; done", timeout.Token);
            Console.WriteLine($"INDEX_REFRESH_COMMAND={(IsHelperSuccess(refresh) ? "PASS" : "FAIL")};AVAILABLE_BEFORE={SafeFirstLine(beforeAvailable) ?? "UNKNOWN"};AVAILABLE_AFTER={SafeFirstLine(afterAvailable) ?? "UNKNOWN"};UPDATES_BEFORE={SafeFirstLine(beforeUpdates) ?? "UNKNOWN"};UPDATES_AFTER={SafeFirstLine(afterUpdates) ?? "UNKNOWN"}");
            Console.WriteLine($"INDEX_TIMESTAMPS_CHANGED={(Sanitize(beforeIndexes) == Sanitize(afterIndexes) ? "NO" : "YES")};INDEX_REFRESH_READBACK={(IsHelperSuccess(refresh) ? "PASS" : "FAIL")}");
        }
        catch (Exception exception) { Console.WriteLine($"PLUGIN_INDEX_REFRESH_FAILURE={Sanitize(exception.Message)}"); }
    }

    private static async Task RunPluginUpdateValidationAsync(string package)
    {
        if (!IsSafePackageName(package)) { Console.WriteLine("PLUGIN_UPDATE_REFUSED=invalid-package-name"); return; }
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(3));
        try
        {
            var settings = new SettingsService(); var profiles = new RouterProfileService(settings); var active = new ActiveRouterContext(profiles);
            await using var provider = new RouterManagerProvider(settings, active, new SshHostKeyTrustService(settings), new RouterCertificateTrustService(settings), new AdGuardTransportSecurityService(), new SshConnectionFactory());
            RouterManager manager = await provider.GetRouterManagerAsync(timeout.Token);
            string before = await manager.RunReadOnlySshCommandAsync($"opkg status {package} 2>/dev/null | sed -n 's/^Version: //p'", timeout.Token);
            string pendingBefore = await manager.RunReadOnlySshCommandAsync("opkg list-upgradable 2>/dev/null", timeout.Token);
            string result = await manager.RunReadOnlySshCommandAsync($"/usr/libexec/opkg-call install {package} 2>/dev/null", timeout.Token);
            string after = await manager.RunReadOnlySshCommandAsync($"opkg status {package} 2>/dev/null | sed -n 's/^Version: //p'", timeout.Token);
            string pendingAfter = await manager.RunReadOnlySshCommandAsync("opkg list-upgradable 2>/dev/null", timeout.Token);
            bool noLongerPending = !SafeLines(pendingAfter).Any(line => line.StartsWith(package + " ", StringComparison.OrdinalIgnoreCase));
            Console.WriteLine($"PLUGIN={package};BEFORE={SafeFirstLine(before) ?? "UNKNOWN"};AFTER={SafeFirstLine(after) ?? "UNKNOWN"};INSTALL_CONTRACT_RESULT={(IsHelperSuccess(result) ? "PASS" : "FAIL")};VERSION_CHANGED={(SafeFirstLine(before) != SafeFirstLine(after) ? "YES" : "NO")};NO_LONGER_UPGRADABLE={(noLongerPending ? "YES" : "NO")};PENDING_BEFORE={(SafeLines(pendingBefore).Count(line => line.Contains(" - ", StringComparison.Ordinal)))};PENDING_AFTER={(SafeLines(pendingAfter).Count(line => line.Contains(" - ", StringComparison.Ordinal)))}");
        }
        catch (Exception exception) { Console.WriteLine($"PLUGIN_UPDATE_FAILURE={Sanitize(exception.Message)}"); }
    }

    private static bool IsSafePackageName(string value) => value.Length is > 0 and <= 80 && value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.' or '+');
    private static bool IsInstalledStatus(string output) => output.Contains("Status: install", StringComparison.OrdinalIgnoreCase) && output.Contains("installed", StringComparison.OrdinalIgnoreCase) && !output.Contains("SSH_", StringComparison.OrdinalIgnoreCase);
    private static bool IsHelperSuccess(string output) => output.Contains("\"code\":0", StringComparison.OrdinalIgnoreCase) || output.Contains("\"code\": 0", StringComparison.OrdinalIgnoreCase);

    private static bool IsCommandSuccess(string output) => HasSafeCommandResult(output) && !string.IsNullOrWhiteSpace(SafeFirstLine(output));

    private static bool HasSafeCommandResult(string output) =>
        !string.IsNullOrWhiteSpace(output) && !output.Contains("SSH_", StringComparison.OrdinalIgnoreCase);

    private static string? SafeFirstLine(string output) => output
        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Trim())
        .FirstOrDefault(line => !line.StartsWith("SSH_", StringComparison.OrdinalIgnoreCase));

    private static int CountMeaningfulLines(string output) => output
        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Count(line => !line.Trim().StartsWith("SSH_", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> SafeLines(string output) => output
        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Trim())
        .Where(line => !line.StartsWith("SSH_", StringComparison.OrdinalIgnoreCase));

    private static int CountPackageLines(string output) => output
        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Count(line => line.Contains(" - ", StringComparison.Ordinal) && !line.StartsWith("Collected errors", StringComparison.OrdinalIgnoreCase));

    private static int CountPackageRecords(string output) => output
        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Count(line => line.TrimStart().StartsWith("Package:", StringComparison.OrdinalIgnoreCase));

    private static string OutputShape(string output)
    {
        string value = output.Trim();
        return value.Length == 0 ? "empty" : $"first={value[0]};last={value[^1]};chars={value.Length};lines={CountMeaningfulLines(value)}";
    }

    private static (int Official, int Custom) CountFeeds(string output)
    {
        int official = 0;
        int custom = 0;
        foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string value = line.Trim();
            if (!value.StartsWith("src", StringComparison.OrdinalIgnoreCase)) continue;
            if (value.Contains("gl-inet", StringComparison.OrdinalIgnoreCase) || value.Contains("openwrt", StringComparison.OrdinalIgnoreCase)) official++;
            else custom++;
        }
        return (official, custom);
    }

    private static async Task RunTunnelValidationAsync(string[] args)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(45));
        try
        {
            var settings = new SettingsService();
            var profiles = new RouterProfileService(settings);
            var active = new ActiveRouterContext(profiles);
            RouterProfile profile = active.CurrentProfile;
            long generation = active.Version;
            await using var provider = new RouterManagerProvider(
                settings, active, new SshHostKeyTrustService(settings),
                new RouterCertificateTrustService(settings),
                new AdGuardTransportSecurityService(), new SshConnectionFactory());
            RouterManager manager = await provider.GetRouterManagerAsync(timeout.Token);
            RouterInfo identity = await manager.GetRouterInfoAsync();
            if (!identity.Model.Contains("GL-MT6000", StringComparison.OrdinalIgnoreCase) &&
                !identity.Model.Contains("Flint 2", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"ABORT: router identity rejected ({Sanitize(identity.Model)}).");
                return;
            }

            IReadOnlyList<VpnTunnelInfo> tunnels = await manager.GetVpnTunnelsAsync(timeout.Token);
            int requestedId = ParseTunnelId(args);
            VpnTunnelInfo? selected = requestedId > 0
                ? tunnels.SingleOrDefault(tunnel => tunnel.TunnelId == requestedId)
                : tunnels.FirstOrDefault();
            if (selected is null)
            {
                Console.WriteLine("ABORT: no unique configured VPN tunnel was found.");
                return;
            }

            Console.WriteLine("ROUTERPILOT VPN CLIENT TUNNEL VALIDATION");
            Console.WriteLine($"Router: {Sanitize(identity.Model)}");
            Console.WriteLine($"Profile: {Sanitize(profile.DisplayName)}");
            Console.WriteLine($"Tunnel id: {selected.TunnelId}");
            Console.WriteLine($"Protocol: {Sanitize(selected.Protocol)}");
            Console.WriteLine($"Before enabled: {selected.Enabled}");
            Console.WriteLine($"Before kill switch: {selected.KillSwitch}");
            Console.WriteLine($"Before local access: {selected.LocalAccess?.ToString() ?? "unknown"}");
            Console.WriteLine($"Before masquerade: {selected.Masquerade?.ToString() ?? "unknown"}");
            Console.WriteLine("Contract: vpn-client.set_tunnel { tunnel_id, enabled }");
            Console.WriteLine("This will invert one existing tunnel and restore it immediately.");
            Console.Write("Type VALIDATE to continue: ");
            if (!string.Equals(Console.ReadLine(), "VALIDATE", StringComparison.Ordinal))
            {
                Console.WriteLine("ABORT");
                return;
            }
            if (active.Version != generation || active.CurrentProfileId != profile.Id)
            {
                Console.WriteLine("ABORT: active router/profile generation changed.");
                return;
            }

            bool original = selected.Enabled;
            VpnLiveStatusService live = new(provider);
            await live.EnsureSubscribedAsync(timeout.Token);
            VpnLiveStatusInfo? baselineRuntime = await WaitForTunnelRuntimeAsync(live, selected.TunnelId, original, timeout.Token, allowCurrent: true);
            Console.WriteLine($"Before runtime: {DescribeRuntime(baselineRuntime)}");
            if (baselineRuntime is null)
            {
                Console.WriteLine("ABORT: authoritative live runtime state was not observed.");
                return;
            }

            bool restore = false;
            bool final = false;
            bool allRuntimeChecks = true;
            bool[] sequence = original ? [false, true] : [true, false];
            try
            {
                foreach (bool target in sequence)
                {
                    bool write = await manager.SetVpnTunnelEnabledAsync(selected.TunnelId, target, timeout.Token);
                    Console.WriteLine($"{(target ? "Enable" : "Disable")} write: {(write ? "PASS" : "FAIL")}");
                    if (!write) { allRuntimeChecks = false; break; }
                    IReadOnlyList<VpnTunnelInfo> changed = await manager.GetVpnTunnelsAsync(timeout.Token);
                    bool readBack = changed.SingleOrDefault(tunnel => tunnel.TunnelId == selected.TunnelId)?.Enabled == target;
                    VpnLiveStatusInfo? runtime = await WaitForTunnelRuntimeAsync(live, selected.TunnelId, target, timeout.Token);
                    bool runtimePass = runtime is not null;
                    allRuntimeChecks &= readBack && runtimePass;
                    Console.WriteLine($"{(target ? "Enable" : "Disable")} inventory read-back: {(readBack ? "PASS" : "FAIL")}");
                    Console.WriteLine($"{(target ? "Enable" : "Disable")} runtime: {(runtimePass ? "PASS" : "FAIL")} ({DescribeRuntime(runtime)})");
                    if (!readBack || !runtimePass) break;
                }
            }
            finally
            {
                IReadOnlyList<VpnTunnelInfo> current = await manager.GetVpnTunnelsAsync(CancellationToken.None);
                bool currentValue = current.SingleOrDefault(tunnel => tunnel.TunnelId == selected.TunnelId)?.Enabled ?? original;
                if (currentValue != original)
                {
                    restore = await manager.SetVpnTunnelEnabledAsync(selected.TunnelId, original, CancellationToken.None);
                    IReadOnlyList<VpnTunnelInfo> restored = await manager.GetVpnTunnelsAsync(CancellationToken.None);
                    final = restored.SingleOrDefault(tunnel => tunnel.TunnelId == selected.TunnelId)?.Enabled == original;
                    final &= await WaitForTunnelRuntimeAsync(live, selected.TunnelId, original, CancellationToken.None) is not null;
                }
                else
                {
                    restore = true;
                    final = await WaitForTunnelRuntimeAsync(live, selected.TunnelId, original, CancellationToken.None) is not null;
                }
            }
            Console.WriteLine($"Runtime connected proven: {(allRuntimeChecks && sequence.Contains(true) ? "YES" : "NO")}");
            Console.WriteLine($"Runtime disconnected proven: {(allRuntimeChecks && sequence.Contains(false) ? "YES" : "NO")}");
            Console.WriteLine($"Restore: {(restore ? "PASS" : "FAIL")}");
            Console.WriteLine($"Final read-back and runtime: {(final ? "PASS" : "FAIL")}");
            Console.WriteLine($"ROUTER RESTORED: {(restore && final ? "YES" : "NO")}");
            if (!(restore && final)) Console.WriteLine("STOP: restoration was not proven.");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("ABORT: cancelled or timed out.");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Runtime validation unavailable: {Sanitize(exception.Message)}");
        }
    }

    private static async Task<VpnLiveStatusInfo?> WaitForTunnelRuntimeAsync(
        VpnLiveStatusService live,
        int tunnelId,
        bool enabled,
        CancellationToken cancellationToken,
        bool allowCurrent = false)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(12);
        do
        {
            VpnLiveStatusInfo? status = live.Current.SingleOrDefault(item => item.TunnelId == tunnelId);
            if (status is not null && status.Enabled == enabled && (allowCurrent || (enabled ? status.IsConnected : !status.IsConnected)))
                return status;
            await Task.Delay(250, cancellationToken);
        }
        while (DateTime.UtcNow < deadline);
        return null;
    }

    private static string DescribeRuntime(VpnLiveStatusInfo? status) => status is null
        ? "unobserved"
        : $"enabled={status.Enabled}; status={status.Status}; state={status.ConnectionState}";

    private static int ParseTunnelId(string[] args)
    {
        string? value = args.FirstOrDefault(argument => argument.StartsWith("--tunnel-id=", StringComparison.OrdinalIgnoreCase));
        return value is not null && int.TryParse(value[12..], out int tunnelId) ? tunnelId : 0;
    }

    private static void RunUnitTests()
    {
        RunVpnControlPresentationTests();
        RunVpnProfileInventoryTests();
        RunRouterCertificateTrustPolicyTests();
        static JsonObject Settings(int value) => new() { ["lan_enabled"] = value, ["wan_enabled"] = value, ["enabled"] = 0, ["masq"] = 0 };

        using (JsonDocument valid = JsonDocument.Parse("{\"jsonrpc\":\"2.0\",\"id\":3,\"result\":{\"enabled\":false,\"lan_enabled\":false,\"wan_enabled\":false,\"run_exit_node\":false,\"masq\":false,\"extra\":\"ignored\"}}"))
        {
            JsonObject parsed = TailscaleConfigParser.ExtractSettings(valid.RootElement);
            Require(parsed["lan_enabled"]!.GetValue<bool>() == false && parsed["extra"]!.GetValue<string>() == "ignored", "live result envelope parses and preserves extras");
        }
        using (JsonDocument error = JsonDocument.Parse("{\"error\":{\"code\":-1,\"message\":\"failure\"}}"))
        {
            try { _ = TailscaleConfigParser.ExtractSettings(error.RootElement); }
            catch (TailscaleConfigParser.RpcFailure failure)
            {
                Require(failure.Code == "-1" && failure.SafeMessage == "failure" && failure.DataShape == "<absent>", "safe RPC error details are extracted");
            }
        }
        using (JsonDocument secretError = JsonDocument.Parse("{\"error\":{\"code\":-2,\"message\":\"token rejected\",\"data\":{\"auth_key\":\"secret-value\",\"reason\":\"bad\"}}}"))
        {
            try { _ = TailscaleConfigParser.ExtractSettings(secretError.RootElement); }
            catch (TailscaleConfigParser.RpcFailure failure)
            {
                Require(failure.SafeMessage == "<redacted>" && !failure.DataShape.Contains("secret-value", StringComparison.Ordinal) && failure.DataShape.Contains("data.auth_key", StringComparison.Ordinal), "secret-bearing RPC errors are redacted");
            }
        }
        using (JsonDocument missing = JsonDocument.Parse("{\"result\":{\"enabled\":false}}"))
            RequireThrows(() => new MutationCoordinator(new FakeTransport(TailscaleConfigParser.ExtractSettings(missing.RootElement)), () => 1, _ => { }).ValidateLanEnabledAsync(CancellationToken.None).GetAwaiter().GetResult(), "missing target field is rejected");
        using (JsonDocument wrongType = JsonDocument.Parse("{\"result\":{\"lan_enabled\":\"maybe\"}}"))
            RequireThrows(() => new MutationCoordinator(new FakeTransport(TailscaleConfigParser.ExtractSettings(wrongType.RootElement)), () => 1, _ => { }).ValidateLanEnabledAsync(CancellationToken.None).GetAwaiter().GetResult(), "unexpected target type is rejected");

        var fake = new FakeTransport(Settings(0));
        var coordinator = new MutationCoordinator(fake, () => 1, _ => { });
        ValidationResult result = coordinator.ValidateLanEnabledAsync(CancellationToken.None).GetAwaiter().GetResult();
        Require(result.WriteSucceeded && result.ReadBackSucceeded && result.RestorationRequired && result.RestoreSucceeded && result.FinalReadBackSucceeded, "happy path");
        Require(fake.Writes[0]["enabled"]!.GetValue<int>() == 0 && fake.Writes[1]["enabled"]!.GetValue<int>() == 0, "unrelated fields preserved");

        var wanFake = new FakeTransport(Settings(0));
        result = new MutationCoordinator(wanFake, () => 1, _ => { }).ValidateWanEnabledAsync(CancellationToken.None).GetAwaiter().GetResult();
        Require(result.WriteSucceeded && result.ReadBackSucceeded && result.RestorationRequired && result.RestoreSucceeded && result.FinalReadBackSucceeded &&
            wanFake.Writes[0]["lan_enabled"]!.GetValue<int>() == 0 && wanFake.Writes[0]["wan_enabled"]!.GetValue<int>() == 1,
            "WAN-only validation preserves LAN state");

        var failed = new FakeTransport(Settings(0), failWrite: true);
        result = new MutationCoordinator(failed, () => 1, _ => { }).ValidateLanEnabledAsync(CancellationToken.None).GetAwaiter().GetResult();
        Require(!result.WriteSucceeded && !result.RestorationRequired && failed.WriteCount == 1, "write failure is not reported as success");

        var mismatch = new FakeTransport(Settings(0), mismatchReadBack: true);
        result = new MutationCoordinator(mismatch, () => 1, _ => { }).ValidateLanEnabledAsync(CancellationToken.None).GetAwaiter().GetResult();
        Require(!result.ReadBackSucceeded && result.RestorationRequired && result.RestoreSucceeded && result.FinalReadBackSucceeded, "read-back mismatch restores");

        long generation = 1;
        var generationChanged = new FakeTransport(Settings(0));
        result = new MutationCoordinator(generationChanged, () => ++generation, _ => { }).ValidateLanEnabledAsync(CancellationToken.None).GetAwaiter().GetResult();
        Require(!result.WriteSucceeded && generationChanged.WriteCount == 0, "generation change prevents mutation");

        var cancellation = new CancellationTokenSource();
        var cancelledBeforeWrite = new FakeTransport(Settings(0));
        cancellation.Cancel();
        bool cancelledThrown = false;
        try { _ = new MutationCoordinator(cancelledBeforeWrite, () => 1, _ => { }).ValidateLanEnabledAsync(cancellation.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { cancelledThrown = true; }
        Require(cancelledThrown && cancelledBeforeWrite.WriteCount == 0, "pre-write cancellation prevents mutation");

        using var cancelAfterWrite = new CancellationTokenSource();
        var cancelledAfterWrite = new FakeTransport(Settings(0), afterFirstWrite: cancelAfterWrite.Cancel);
        result = new MutationCoordinator(cancelledAfterWrite, () => 1, _ => { }).ValidateLanEnabledAsync(cancelAfterWrite.Token).GetAwaiter().GetResult();
        Require(cancelledAfterWrite.WriteCount == 2 && result.RestoreSucceeded && result.FinalReadBackSucceeded, "post-write cancellation restores");

        var restoreFailure = new FakeTransport(Settings(0), failRestore: true);
        result = new MutationCoordinator(restoreFailure, () => 1, _ => { }).ValidateLanEnabledAsync(CancellationToken.None).GetAwaiter().GetResult();
        Require(result.RestorationFailed, "restore failure is surfaced");

        string logged = string.Empty;
        var secrets = new FakeTransport(new JsonObject { ["lan_enabled"] = 0, ["auth_key"] = "never-log" });
        _ = new MutationCoordinator(secrets, () => 1, message => logged += message).ValidateLanEnabledAsync(CancellationToken.None).GetAwaiter().GetResult();
        Require(!logged.Contains("never-log", StringComparison.Ordinal), "secret values never enter diagnostics");

        JsonObject request = TailscaleConfigRequestBuilder.Build(new JsonObject
        {
            ["enabled"] = false, ["lan_enabled"] = true, ["wan_enabled"] = false,
            ["masq"] = false, ["run_exit_node"] = false, ["lan_ip"] = "192.0.2.1",
            ["auth_key"] = "never-send"
        });
        Require(request.Count == 3 && request["lan_enabled"]!.GetValue<bool>() && request["lan_ip"] is null && request["auth_key"] is null, "set_config envelope excludes derived and secret fields");
    }

    private static void RunRouterCertificateTrustPolicyTests()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using X509Certificate2 selfSigned = CreateCertificate(now.AddDays(-1), now.AddDays(30));
        const SslPolicyErrors chainError = SslPolicyErrors.RemoteCertificateChainErrors;
        Require(
            RouterCertificateTrustPolicy.Determine(selfSigned, chainError, null, now) == RouterCertificateTrustState.TrustRequired,
            "a valid self-signed certificate reaches explicit trust rather than silent acceptance");

        string fingerprint = RouterCertificateTrustPolicy.BuildFingerprint(selfSigned);
        Require(
            RouterCertificateTrustPolicy.Determine(selfSigned, chainError, fingerprint, now) == RouterCertificateTrustState.Trusted,
            "an explicitly pinned certificate is accepted through the existing trust mechanism");

        using X509Certificate2 replacement = CreateCertificate(now.AddDays(-1), now.AddDays(30));
        Require(
            RouterCertificateTrustPolicy.Determine(replacement, chainError, fingerprint, now) == RouterCertificateTrustState.CertificateChanged,
            "a replacement certificate is never silently authorized by a previous pin");

        using X509Certificate2 expired = CreateCertificate(now.AddDays(-30), now.AddDays(-1));
        Require(
            RouterCertificateTrustPolicy.Determine(expired, chainError, null, now) == RouterCertificateTrustState.Expired,
            "expired certificates have a distinct validity classification");

        using X509Certificate2 notYetValid = CreateCertificate(now.AddDays(1), now.AddDays(30));
        Require(
            RouterCertificateTrustPolicy.Determine(notYetValid, chainError, null, now) == RouterCertificateTrustState.NotYetValid,
            "not-yet-valid certificates have a distinct validity classification");

        Require(
            RouterCertificateTrustPolicy.Determine(selfSigned, SslPolicyErrors.RemoteCertificateNameMismatch, null, now) == RouterCertificateTrustState.TrustRequired &&
            RouterCertificateTrustPolicy.DescribeValidationErrors(SslPolicyErrors.RemoteCertificateNameMismatch).Contains("hostname mismatch", StringComparison.Ordinal),
            "hostname mismatch is accurately identified for the explicit trust decision");

        Require(
            RouterCertificateTrustPolicy.DescribeValidationErrors((SslPolicyErrors)16) == "unknown TLS validation failure",
            "unknown TLS failures do not fabricate certificate or clock diagnoses");
        Require(
            RouterCertificateTrustPolicy.Determine(selfSigned, SslPolicyErrors.RemoteCertificateNotAvailable, null, now) == RouterCertificateTrustState.CertificateUnavailable,
            "a missing certificate is not eligible for trust");

        static X509Certificate2 CreateCertificate(DateTimeOffset notBefore, DateTimeOffset notAfter)
        {
            using RSA key = RSA.Create(2048);
            var request = new CertificateRequest("CN=RouterPilot test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using X509Certificate2 certificate = request.CreateSelfSigned(notBefore, notAfter);
            return X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
        }
    }

    private static void RunVpnControlPresentationTests()
    {
        VpnTunnelInfo disabled = new() { TunnelId = 38, Name = "Test tunnel", Protocol = "WireGuard", Enabled = false,
            LiveStatus = new VpnLiveStatusInfo { TunnelId = 38, Enabled = false, Status = 0 } };
        Require(disabled.ConnectionState == "Disconnected" && disabled.ActionDisplay == "Connect" && disabled.CanToggle,
            "disabled tunnel presents Connect");

        VpnTunnelInfo connecting = new() { TunnelId = 38, Name = "Test tunnel", Protocol = "WireGuard", Enabled = true,
            TransitionIntent = VpnTransitionIntent.Connecting,
            LiveStatus = new VpnLiveStatusInfo { TunnelId = 38, Enabled = true, Status = 0 } };
        Require(connecting.ConnectionState == "Connecting" && connecting.ActionDisplay == "Connecting…" && !connecting.CanToggle,
            "connect operation presents bounded Connecting state");

        VpnTunnelInfo connected = new() { TunnelId = 38, Name = "Test tunnel", Protocol = "WireGuard", Enabled = true,
            LiveStatus = new VpnLiveStatusInfo { TunnelId = 38, Enabled = true, Status = 1 } };
        Require(connected.ConnectionState == "Connected" && connected.ActionDisplay == "Disconnect" && connected.CanToggle,
            "authoritative Connected state presents Disconnect");

        VpnTunnelInfo disconnecting = new() { TunnelId = 38, Name = "Test tunnel", Protocol = "WireGuard", Enabled = true,
            TransitionIntent = VpnTransitionIntent.Disconnecting,
            LiveStatus = new VpnLiveStatusInfo { TunnelId = 38, Enabled = true, Status = 1 } };
        Require(disconnecting.ConnectionState == "Disconnecting" && disconnecting.ActionDisplay == "Disconnecting…" && !disconnecting.CanToggle,
            "disconnect operation never presents Connecting");

        Require(connecting.ConnectionState == "Connecting", "slow connect remains truthful while runtime is pending");

        VpnOperationIntentService intents = new();
        long connectGeneration = intents.Begin(38, connecting: true);
        long disconnectGeneration = intents.Begin(38, connecting: false);
        intents.Clear(38, connectGeneration);
        Require(intents.GetIntent(38) == VpnTransitionIntent.Disconnecting,
            "stale connect completion cannot clear newer disconnect intent");
        intents.Clear(38, disconnectGeneration);
        Require(intents.GetIntent(38) == VpnTransitionIntent.None,
            "current operation completion clears its intent");

        TailscaleStatus authoritativeTailscale = new(
            TailscaleState.Connected,
            "connected",
            "1.2.3",
            "router",
            "router.tailnet",
            ["192.0.2.10"],
            []);
        VpnViewModel page = new();
        page.ApplyTailscaleStatus(authoritativeTailscale);
        page.Replace([disabled], []);
        page.ApplyLiveStatuses([], vpnInventoryAuthoritative: false);
        Require(ReferenceEquals(page.TailscaleStatus, authoritativeTailscale) && page.VpnTunnels.Count == 1,
            "Unified VPN partial refresh preserves authoritative Tailscale state");
        page.ApplyTailscaleStatus(TailscaleStatus.Unavailable("temporary read failure"));
        Require(page.VpnTunnels.Count == 1, "Tailscale refresh does not clear Unified VPN inventory");

        RunVpnConnectionFailureLifecycleTests();
    }

    private static void RunVpnConnectionFailureLifecycleTests()
    {
        VpnClientProfileInfo profileA = new() { GroupId = 101, Name = "Same name", Protocol = "WireGuard", CurrentLocation = "A" };
        VpnClientProfileInfo profileB = new() { GroupId = 202, Name = "Same name", Protocol = "WireGuard", CurrentLocation = "B" };
        VpnTunnelInfo tunnelA = new() { TunnelId = 10, Name = "Primary", Enabled = false, ProfileGroupIds = [101] };
        VpnTunnelInfo tunnelB = new() { TunnelId = 10, Name = "Primary", Enabled = false, ProfileGroupIds = [202] };

        static VpnLiveStatusInfo Status(int groupId, bool enabled, int state) => new() { TunnelId = 10, GroupId = groupId, Enabled = enabled, Status = state };
        static VpnTunnelInfo Current(VpnViewModel page) => page.VpnTunnels.Single();
        static void CreateTransientFailure(VpnViewModel page)
        {
            page.ApplyLiveStatuses([Status(101, enabled: false, state: 0)], vpnInventoryAuthoritative: true);
            page.BeginConnectionAttempt(Current(page));
            page.ApplyLiveStatuses([Status(101, enabled: true, state: 0)], vpnInventoryAuthoritative: true, fromLiveStatusEvent: true);
            page.ApplyLiveStatuses([Status(101, enabled: false, state: 0)], vpnInventoryAuthoritative: true, fromLiveStatusEvent: true);
            Require(Current(page).HasConnectionAttemptFailure, "a failed connection attempt is presented while its live failure state is current");
        }

        var retry = new VpnViewModel();
        retry.Replace([tunnelA], [profileA], VpnProfileInventoryState.Available);
        CreateTransientFailure(retry);
        retry.BeginConnectionAttempt(Current(retry));
        Require(!Current(retry).HasConnectionAttemptFailure, "retry clears the previous transient connection failure before the new attempt");

        var connected = new VpnViewModel();
        connected.Replace([tunnelA], [profileA], VpnProfileInventoryState.Available);
        CreateTransientFailure(connected);
        connected.ApplyLiveStatuses([Status(101, enabled: true, state: 1)], vpnInventoryAuthoritative: true);
        Require(!Current(connected).HasConnectionAttemptFailure && Current(connected).ConnectionState == "Connected",
            "authoritative connected refresh clears a previous transient failure");

        var changedProfile = new VpnViewModel();
        changedProfile.Replace([tunnelA], [profileA], VpnProfileInventoryState.Available);
        CreateTransientFailure(changedProfile);
        changedProfile.Replace([tunnelB], [profileB], VpnProfileInventoryState.Available);
        changedProfile.ApplyLiveStatuses([Status(202, enabled: false, state: 0)], vpnInventoryAuthoritative: true);
        Require(!Current(changedProfile).HasConnectionAttemptFailure && Current(changedProfile).SelectedProfileGroupId == 202,
            "a profile change cannot inherit another profile's transient failure even when names match");

        var normalDisconnect = new VpnViewModel();
        normalDisconnect.Replace([tunnelA], [profileA], VpnProfileInventoryState.Available);
        CreateTransientFailure(normalDisconnect);
        normalDisconnect.ApplyLiveStatuses([Status(101, enabled: false, state: 0)], vpnInventoryAuthoritative: true);
        Require(!Current(normalDisconnect).HasConnectionAttemptFailure && Current(normalDisconnect).ConnectionState == "Disconnected",
            "authoritative normal disconnected refresh clears a previous transient failure");

        var currentConfigurationError = new VpnViewModel();
        currentConfigurationError.Replace([tunnelB], [profileA], VpnProfileInventoryState.Available);
        currentConfigurationError.ApplyLiveStatuses([Status(101, enabled: false, state: 0)], vpnInventoryAuthoritative: true);
        Require(Current(currentConfigurationError).HasConfigurationAttention && Current(currentConfigurationError).ConnectionState == "Configuration needs attention",
            "current authoritative configuration errors remain visible after reconciliation");
    }

    private static void RunVpnProfileInventoryTests()
    {
        VpnTunnelInfo tunnel = new() { TunnelId = 10, Name = "Primary", ProfileGroupIds = [101] };
        using JsonDocument configured = JsonDocument.Parse("""
            {"result":{"configs":{"wireguard":[{"group_id":101,"group_name":"Surfshark London","peers":[{"peer_id":501,"location":"GB,London"}]}],"openvpn":[{"group_id":202,"group_name":"Backup OpenVPN","peers":[{"client_id":601}]}]}}}
            """);
        (IReadOnlyList<VpnClientProfileInfo> profiles, VpnProfileInventoryState state) = RouterManager.ParseVpnProfileInventory(configured.RootElement, [tunnel]);
        Require(state == VpnProfileInventoryState.Available && profiles.Count == 2 && profiles.Single(profile => profile.GroupId == 101).Name == "Surfshark London",
            "configured Surfshark profile is listed by the bulk inventory without requiring active state");
        Require(profiles.Single(profile => profile.GroupId == 101).IsUsedByTunnel && !profiles.Single(profile => profile.GroupId == 202).IsUsedByTunnel,
            "tunnel association is configuration correlation, not profile existence");

        using JsonDocument zero = JsonDocument.Parse("{\"result\":{\"configs\":{\"wireguard\":[],\"openvpn\":[]}}}");
        (IReadOnlyList<VpnClientProfileInfo> emptyProfiles, VpnProfileInventoryState emptyState) = RouterManager.ParseVpnProfileInventory(zero.RootElement, []);
        Require(emptyState == VpnProfileInventoryState.Available && emptyProfiles.Count == 0,
            "available inventory with zero profiles remains a truthful empty state");

        using JsonDocument unavailable = JsonDocument.Parse("{\"result\":{}}");
        (_, VpnProfileInventoryState unavailableState) = RouterManager.ParseVpnProfileInventory(unavailable.RootElement, []);
        Require(unavailableState == VpnProfileInventoryState.Unavailable,
            "missing profile inventory schema is unavailable rather than falsely empty or unsupported");

        VpnViewModel page = new() { VpnInventoryLoadCompleted = true };
        page.Replace([tunnel], profiles, VpnProfileInventoryState.Available);
        page.ApplyLiveStatuses([new VpnLiveStatusInfo { TunnelId = 10, Enabled = true, Status = 1 }], vpnInventoryAuthoritative: true);
        Require(page.VpnProfiles.Single(profile => profile.GroupId == 101).ActivityState == VpnProfileActivityState.Active,
            "configured and active profile is visible and Active");
        Require(page.VpnProfiles.Single(profile => profile.GroupId == 202).ActivityState == VpnProfileActivityState.Inactive,
            "configured profile not assigned to an active tunnel remains visible and Inactive");

        page.Replace([tunnel], profiles, VpnProfileInventoryState.Available);
        page.ApplyLiveStatuses([new VpnLiveStatusInfo { TunnelId = 10, Enabled = false, Status = 0 }], vpnInventoryAuthoritative: true);
        Require(page.VpnProfiles.Single(profile => profile.GroupId == 101).ActivityState == VpnProfileActivityState.Inactive,
            "configured but inactive Surfshark profile remains visible and Inactive");

        page.Replace([tunnel], profiles, VpnProfileInventoryState.Available);
        page.ApplyLiveStatuses([], vpnInventoryAuthoritative: true);
        Require(page.VpnProfiles.Single(profile => profile.GroupId == 101).ActivityState == VpnProfileActivityState.Unknown,
            "configured profile survives unavailable active state with an unknown state");

        page.Replace([], [], VpnProfileInventoryState.Available);
        Require(page.ShowNoVpnProfiles && !page.ShowVpnProfilesUnavailable,
            "empty configured-profile inventory has distinct UI semantics");
        page.Replace([], [], VpnProfileInventoryState.Unavailable);
        Require(!page.ShowNoVpnProfiles && page.ShowVpnProfilesUnavailable,
            "unavailable configured-profile inventory has distinct UI semantics");
    }

    private static async Task RunRuntimeValidationAsync(string targetField)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; timeout.Cancel(); };

        try
        {
            var settings = new SettingsService();
            var profiles = new RouterProfileService(settings);
            var active = new ActiveRouterContext(profiles);
            RouterProfile profile = active.CurrentProfile;
            long generation = active.Version;
            await using var provider = new RouterManagerProvider(
                settings,
                active,
                new SshHostKeyTrustService(settings),
                new RouterCertificateTrustService(settings),
                new AdGuardTransportSecurityService(),
                new SshConnectionFactory());
            RouterManager manager = await provider.GetRouterManagerAsync(timeout.Token);
            RouterInfo identity = await manager.GetRouterInfoAsync();
            if (!identity.Model.Contains("GL-MT6000", StringComparison.OrdinalIgnoreCase) &&
                !identity.Model.Contains("Flint 2", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"Router identity rejected: {Sanitize(identity.Model)}");
                return;
            }

            var transport = new RouterTransport(manager);
            using (JsonDocument rawConfig = await transport.ReadRawAsync(timeout.Token))
            {
                Console.WriteLine("GET_CONFIG: PASS");
                PrintSanitizedShape(rawConfig.RootElement, "root", 0);
            }
            JsonObject original = await transport.ReadSettingsAsync(timeout.Token);
            if (targetField is not ("enabled" or "lan_enabled" or "wan_enabled"))
            {
                Console.WriteLine("ABORT: field must be enabled, lan_enabled or wan_enabled.");
                return;
            }
            if (!MutationCoordinator.TryReadBoolean(original, targetField, out bool originalValue, out _))
            {
                Console.WriteLine("WRITE CONTRACT NOT PROVEN");
                return;
            }

            Console.WriteLine("ROUTERPILOT VPN MUTATION VALIDATION");
            Console.WriteLine($"Router: {Sanitize(identity.Model)}");
            Console.WriteLine($"Profile: {Sanitize(profile.DisplayName)}");
            Console.WriteLine($"Target field: {targetField}");
            Console.WriteLine($"Current value: {(originalValue ? 1 : 0)}");
            Console.WriteLine($"Temporary value: {(originalValue ? 0 : 1)}");
            Console.WriteLine("The harness will restore the original value immediately after verification.");
            Console.Write("Type VALIDATE to continue: ");
            if (!string.Equals(Console.ReadLine(), "VALIDATE", StringComparison.Ordinal))
            {
                Console.WriteLine("ABORT");
                return;
            }

            if (active.Version != generation || active.CurrentProfileId != profile.Id)
            {
                Console.WriteLine("ABORT: active router/profile generation changed.");
                return;
            }

            ValidationResult result = await new MutationCoordinator(transport, () => active.Version, Console.WriteLine)
                .ValidateFieldAsync(targetField, timeout.Token);
            Console.WriteLine($"Router: {Sanitize(identity.Model)}");
            Console.WriteLine("Method: tailscale.set_config");
            Console.WriteLine($"Field: {targetField}");
            Console.WriteLine($"Original: {(originalValue ? 1 : 0)}");
            Console.WriteLine($"Temporary: {(originalValue ? 0 : 1)}");
            Console.WriteLine($"Write: {(result.WriteSucceeded ? "PASS" : "FAIL")}");
            Console.WriteLine($"Read-back: {(result.ReadBackSucceeded ? "PASS" : "FAIL")}");
            if (result.RpcErrorCode is not null)
            {
                Console.WriteLine($"RPC error code: {result.RpcErrorCode}");
                Console.WriteLine($"RPC error message: {result.RpcErrorMessage}");
                Console.WriteLine($"RPC error data shape: {result.RpcErrorDataShape}");
                Console.WriteLine("Mutation confirmed applied: NO");
            }
            Console.WriteLine($"Restoration required: {(result.RestorationRequired ? "YES" : "NO")}");
            Console.WriteLine("Backend/UCI consistency: NOT RUN");
            Console.WriteLine($"Restore: {(result.RestorationRequired ? (result.RestoreSucceeded ? "PASS" : "FAIL") : "NOT REQUIRED")}");
            Console.WriteLine($"Final read-back: {(result.FinalReadBackSucceeded ? "PASS" : "FAIL")}");
            Console.WriteLine($"ROUTER RESTORED: {(result.FinalReadBackSucceeded ? "YES" : "NO")}");
            if (result.RestorationFailed)
            {
                Console.WriteLine("RESTORATION FAILED");
                Console.WriteLine($"Field: {targetField}");
                Console.WriteLine($"Expected original: {(originalValue ? 1 : 0)}");
                Console.WriteLine("Current read-back: UNKNOWN");
            }
            Console.WriteLine($"WRITE CONTRACT: {(result.WriteSucceeded && result.ReadBackSucceeded && result.RestoreSucceeded && result.FinalReadBackSucceeded ? "PROVEN" : "UNPROVEN")}");
            if (!string.IsNullOrWhiteSpace(result.Error)) Console.WriteLine($"Result: {Sanitize(result.Error)}");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("ABORT: cancelled or timed out; no unbounded retry was attempted.");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Runtime validation unavailable: {Sanitize(exception.Message)}");
        }
    }

    private static string Sanitize(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static void PrintSanitizedShape(JsonElement value, string path, int depth)
    {
        if (depth > 5)
        {
            Console.WriteLine($"{path}: {value.ValueKind} <redacted>");
            return;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            Console.WriteLine($"{path}: object");
            foreach (JsonProperty property in value.EnumerateObject())
            {
                string childPath = path + "." + property.Name;
                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    PrintSanitizedShape(property.Value, childPath, depth + 1);
                else if (property.Name is "enabled" or "lan_enabled" or "wan_enabled" or "run_exit_node" or "masq")
                    Console.WriteLine($"{childPath}: {property.Value.ValueKind} {property.Value.GetRawText()}");
                else
                    Console.WriteLine($"{childPath}: {property.Value.ValueKind} <redacted>");
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            Console.WriteLine($"{path}: array");
            Console.WriteLine($"{path}.length: {value.GetArrayLength()}");
        }
        else
        {
            Console.WriteLine($"{path}: {value.ValueKind} <redacted>");
        }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException($"Harness test failed: {message}"); }

    private static void RequireThrows(Action action, string message)
    {
        try { action(); }
        catch (Exception) { return; }
        throw new InvalidOperationException($"Harness test failed: {message}");
    }
}
