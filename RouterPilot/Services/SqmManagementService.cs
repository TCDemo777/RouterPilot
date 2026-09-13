using RouterPilot.Models;

namespace RouterPilot.Services;

public interface ISqmManagementService
{
    Task<SqmReadResult> LoadAsync(CancellationToken token = default);
    Task<SqmApplyResult> ApplyAsync(SqmConfiguration requested, CancellationToken token = default);
}

public sealed class SqmManagementService : ISqmManagementService
{
    private readonly IRouterManagerProvider _routers;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public SqmManagementService(IRouterManagerProvider routers) => _routers = routers;

    public async Task<SqmReadResult> LoadAsync(CancellationToken token = default)
    {
        RouterManager router = await _routers.GetRouterManagerAsync(token).ConfigureAwait(false);
        try
        {
            SqmConfiguration configuration = await router.GetNativeSqmConfigurationAsync(token).ConfigureAwait(false);
            if (!ReferenceEquals(router, await _routers.GetRouterManagerAsync(token).ConfigureAwait(false)))
                return new(SqmCapabilityState.Unavailable, null, "The active router changed while SQM was loading.");
            return new(SqmCapabilityState.Native, configuration, "Native GL.iNet SQM Management");
        }
        catch (SqmNativeCapabilityException ex) when (ex.CapabilityAbsent)
        {
            try
            {
                RouterAdvancedSnapshot legacy = await router.GetRouterAdvancedTelemetryAsync(token).ConfigureAwait(false);
                if (legacy.SqmEnabled is null || !int.TryParse(legacy.SqmDownload, out int download) || !int.TryParse(legacy.SqmUpload, out int upload))
                    return new(SqmCapabilityState.Unavailable, null, "SQM configuration is unavailable.");
                return new(SqmCapabilityState.LegacyReadOnly, new(legacy.SqmEnabled.Value, download / 1000, upload / 1000, legacy.SqmQueueDiscipline), "Legacy read-only SQM configuration");
            }
            catch (OperationCanceledException) { throw; }
            catch { return new(SqmCapabilityState.Unavailable, null, "SQM configuration is unavailable."); }
        }
        catch (OperationCanceledException) { throw; }
        catch { return new(SqmCapabilityState.Unavailable, null, "SQM configuration is unavailable."); }
    }

    public async Task<SqmApplyResult> ApplyAsync(SqmConfiguration requested, CancellationToken token = default)
    {
        if (!requested.IsValid) return new(false, false, "Enter whole-number upload and download values from 1 to 10000 Mbps, and select a supported queue rule.");
        if (!await _gate.WaitAsync(0, token).ConfigureAwait(false)) return new(false, false, "An SQM update is already in progress.");
        try
        {
            RouterManager router = await _routers.GetRouterManagerAsync(token).ConfigureAwait(false);
            await router.SetNativeSqmConfigurationAsync(requested, token).ConfigureAwait(false);
            if (!ReferenceEquals(router, await _routers.GetRouterManagerAsync(token).ConfigureAwait(false))) return new(false, false, "The active router changed before SQM could be verified.");
            SqmConfiguration actual = await router.GetNativeSqmConfigurationAsync(token).ConfigureAwait(false);
            if (!ReferenceEquals(router, await _routers.GetRouterManagerAsync(token).ConfigureAwait(false))) return new(false, false, "The active router changed before SQM could be verified.");
            return actual == requested ? new(true, true, "SQM settings applied and verified.") : new(false, false, "The router returned different SQM settings after apply.");
        }
        catch (OperationCanceledException) { return new(false, false, "SQM update was cancelled."); }
        catch { return new(false, false, "RouterPilot could not apply or verify SQM settings."); }
        finally { _gate.Release(); }
    }
}
