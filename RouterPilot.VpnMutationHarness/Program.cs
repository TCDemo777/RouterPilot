using System.Text.Json;
using System.Text.Json.Nodes;
using RouterPilot.Configuration;
using RouterPilot.Models;
using RouterPilot.Services;

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
        Console.WriteLine("Runtime validation is opt-in and requires the configured RouterPilot profile.");
        string targetField = args.FirstOrDefault(argument => argument.StartsWith("--field=", StringComparison.OrdinalIgnoreCase))?[8..]
            ?? "lan_enabled";
        await RunRuntimeValidationAsync(targetField);
    }

    private static void RunUnitTests()
    {
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
