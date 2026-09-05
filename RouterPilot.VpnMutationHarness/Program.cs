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
    bool RestoreSucceeded,
    bool FinalReadBackSucceeded,
    bool RestorationFailed,
    string? Error);

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

    public async Task<ValidationResult> ValidateLanEnabledAsync(CancellationToken cancellationToken)
    {
        long capturedGeneration = _generation();
        JsonObject original = await _transport.ReadSettingsAsync(cancellationToken);
        if (!TryReadBoolean(original, "lan_enabled", out bool originalValue, out JsonValueKind valueKind))
            throw new InvalidOperationException("WRITE CONTRACT NOT PROVEN: lan_enabled was not a boolean/0/1 setting.");

        JsonObject temporary = (JsonObject)original.DeepClone();
        temporary["lan_enabled"] = valueKind == JsonValueKind.String
            ? (JsonNode)(originalValue ? "0" : "1")
            : originalValue ? 0 : 1;
        bool mutationAttempted = false;
        bool writeSucceeded = false;
        bool readBackSucceeded = false;
        bool restoreSucceeded = false;
        bool finalReadBackSucceeded = false;
        bool restorationFailed = false;
        string? error = null;

        try
        {
            EnsureGeneration(capturedGeneration);
            mutationAttempted = true;
            await _transport.WriteSettingsAsync(temporary, cancellationToken);
            writeSucceeded = true;

            JsonObject changed = await _transport.ReadSettingsAsync(cancellationToken);
            readBackSucceeded = TryReadBoolean(changed, "lan_enabled", out bool changedValue, out _) && changedValue != originalValue;
            if (!readBackSucceeded)
                throw new InvalidOperationException("Temporary lan_enabled read-back did not match the requested value.");
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }
        finally
        {
            if (mutationAttempted)
            {
                try
                {
                    EnsureGeneration(capturedGeneration);
                    await _transport.WriteSettingsAsync(original, CancellationToken.None);
                    restoreSucceeded = true;
                    JsonObject restored = await _transport.ReadSettingsAsync(CancellationToken.None);
                    finalReadBackSucceeded = TryReadBoolean(restored, "lan_enabled", out bool finalValue, out _) && finalValue == originalValue;
                    if (!finalReadBackSucceeded)
                        throw new InvalidOperationException("Final lan_enabled read-back did not match the original value.");
                }
                catch (Exception exception)
                {
                    restorationFailed = true;
                    error = string.IsNullOrWhiteSpace(error) ? exception.Message : error + " Restore: " + exception.Message;
                    _log("RESTORATION FAILED");
                }
            }
        }

        return new ValidationResult(writeSucceeded, readBackSucceeded, restoreSucceeded, finalReadBackSucceeded, restorationFailed, error);
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

    public async Task<JsonObject> ReadSettingsAsync(CancellationToken cancellationToken)
    {
        using JsonDocument document = await _manager.GetTailscaleConfigAsync(cancellationToken);
        ThrowIfError(document.RootElement);
        if (!document.RootElement.TryGetProperty("result", out JsonElement result) ||
            !result.TryGetProperty("settings", out JsonElement settings) || settings.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("WRITE CONTRACT NOT PROVEN: get_config did not return result.settings.");
        return JsonNode.Parse(settings.GetRawText())!.AsObject();
    }

    public async Task WriteSettingsAsync(JsonObject settings, CancellationToken cancellationToken)
    {
        using JsonDocument document = await _manager.SetTailscaleConfigAsync(
            JsonSerializer.SerializeToElement(settings), cancellationToken);
        ThrowIfError(document.RootElement);
    }

    private static void ThrowIfError(JsonElement root)
    {
        if (root.TryGetProperty("error", out JsonElement error))
            throw new InvalidOperationException($"GL.iNet RPC error ({error.GetRawText().Length} bytes).");
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
        await RunRuntimeValidationAsync();
    }

    private static void RunUnitTests()
    {
        static JsonObject Settings(int value) => new() { ["lan_enabled"] = value, ["enabled"] = 0, ["masq"] = 0 };

        var fake = new FakeTransport(Settings(0));
        var coordinator = new MutationCoordinator(fake, () => 1, _ => { });
        ValidationResult result = coordinator.ValidateLanEnabledAsync(CancellationToken.None).GetAwaiter().GetResult();
        Require(result.WriteSucceeded && result.ReadBackSucceeded && result.RestoreSucceeded && result.FinalReadBackSucceeded, "happy path");
        Require(fake.Writes[0]["enabled"]!.GetValue<int>() == 0 && fake.Writes[1]["enabled"]!.GetValue<int>() == 0, "unrelated fields preserved");

        var failed = new FakeTransport(Settings(0), failWrite: true);
        result = new MutationCoordinator(failed, () => 1, _ => { }).ValidateLanEnabledAsync(CancellationToken.None).GetAwaiter().GetResult();
        Require(!result.WriteSucceeded && failed.WriteCount == 2, "write failure is not reported as success");

        var mismatch = new FakeTransport(Settings(0), mismatchReadBack: true);
        result = new MutationCoordinator(mismatch, () => 1, _ => { }).ValidateLanEnabledAsync(CancellationToken.None).GetAwaiter().GetResult();
        Require(!result.ReadBackSucceeded && result.RestoreSucceeded && result.FinalReadBackSucceeded, "read-back mismatch restores");

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
    }

    private static async Task RunRuntimeValidationAsync()
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
            JsonObject original = await transport.ReadSettingsAsync(timeout.Token);
            if (!MutationCoordinator.TryReadBoolean(original, "lan_enabled", out bool originalValue, out _))
            {
                Console.WriteLine("WRITE CONTRACT NOT PROVEN");
                return;
            }

            Console.WriteLine("ROUTERPILOT VPN MUTATION VALIDATION");
            Console.WriteLine($"Router: {Sanitize(identity.Model)}");
            Console.WriteLine($"Profile: {Sanitize(profile.DisplayName)}");
            Console.WriteLine("Target field: lan_enabled");
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
                .ValidateLanEnabledAsync(timeout.Token);
            Console.WriteLine($"Router: {Sanitize(identity.Model)}");
            Console.WriteLine("Method: tailscale.set_config");
            Console.WriteLine("Field: lan_enabled");
            Console.WriteLine($"Original: {(originalValue ? 1 : 0)}");
            Console.WriteLine($"Temporary: {(originalValue ? 0 : 1)}");
            Console.WriteLine($"Write: {(result.WriteSucceeded ? "PASS" : "FAIL")}");
            Console.WriteLine($"Read-back: {(result.ReadBackSucceeded ? "PASS" : "FAIL")}");
            Console.WriteLine("Backend/UCI consistency: NOT RUN");
            Console.WriteLine($"Restore: {(result.RestoreSucceeded ? "PASS" : "FAIL")}");
            Console.WriteLine($"Final read-back: {(result.FinalReadBackSucceeded ? "PASS" : "FAIL")}");
            Console.WriteLine($"ROUTER RESTORED: {(result.FinalReadBackSucceeded ? "YES" : "NO")}");
            if (result.RestorationFailed)
            {
                Console.WriteLine("RESTORATION FAILED");
                Console.WriteLine("Field: lan_enabled");
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
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException($"Harness test failed: {message}"); }
}
