using System;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

public sealed class DataStatisticsService
{
    private readonly IRouterManagerProvider _routerManagerProvider;
    private readonly IDataStatisticsReader _reader;
    private readonly IActiveRouterContext _activeRouter;
    private readonly SemaphoreSlim _applicationProtectionGate = new(1, 1);

    public DataStatisticsService(IRouterManagerProvider routerManagerProvider, IDataStatisticsReader reader,
        IActiveRouterContext activeRouter)
    {
        _routerManagerProvider = routerManagerProvider;
        _reader = reader;
        _activeRouter = activeRouter;
    }

    public async Task<DataStatisticsReadResult> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        DataStatisticsContextStamp context = new(_activeRouter.CurrentProfileId, _activeRouter.Version);
        try
        {
            IDataStatisticsReadSession reader = await _reader
                .OpenReadSessionAsync(cancellationToken)
                .ConfigureAwait(false);
            NetworkTrafficSnapshot? traffic = null;
            try
            {
                traffic = await reader.GetNetworkTrafficSnapshotAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // DPI statistics remain useful when the optional traffic counter is unavailable.
            }
            DataStatisticsStatus status = await reader
                .GetDataStatisticsStatusAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!status.HasFlowStatisticsState)
            {
                return CreateReadResult(DataStatisticsCapabilitySupport.Unsupported,
                    DataStatisticsOperatingState.Unknown, DataStatisticsReadAvailability.Unknown,
                    context, status, null, traffic);
            }

            if (status.FlowStatisticsEnabled is false)
            {
                return CreateReadResult(DataStatisticsCapabilitySupport.Supported,
                    DataStatisticsOperatingState.Disabled, DataStatisticsReadAvailability.NotReadBecauseDisabled,
                    context, status, null, traffic);
            }

            if (!status.IsDpiActive)
            {
                return CreateReadResult(DataStatisticsCapabilitySupport.Supported,
                    DataStatisticsOperatingState.DpiInactive, DataStatisticsReadAvailability.NotReadBecauseDpiInactive,
                    context, status, null, traffic);
            }

            DataStatisticsSnapshot snapshot = await reader
                .GetTopAppFlowStatisticsAsync(cancellationToken)
                .ConfigureAwait(false);
            return CreateReadResult(DataStatisticsCapabilitySupport.Supported,
                DataStatisticsOperatingState.EnabledAndDpiActive, DataStatisticsReadAvailability.Available,
                context, status, snapshot, traffic);
        }
        catch (DataStatisticsRpcException exception) when (exception.IsMethodOrServiceUnavailable)
        {
            return CreateReadResult(DataStatisticsCapabilitySupport.Unsupported,
                DataStatisticsOperatingState.Unknown, DataStatisticsReadAvailability.Unknown,
                context, null, null, null);
        }
        catch (DataStatisticsRpcException)
        {
            return CreateReadResult(DataStatisticsCapabilitySupport.Unknown,
                DataStatisticsOperatingState.Unknown, DataStatisticsReadAvailability.TemporarilyUnavailable,
                context, null, null, null);
        }
    }

    private static DataStatisticsReadResult CreateReadResult(
        DataStatisticsCapabilitySupport support,
        DataStatisticsOperatingState operatingState,
        DataStatisticsReadAvailability readAvailability,
        DataStatisticsContextStamp context,
        DataStatisticsStatus? status,
        DataStatisticsSnapshot? snapshot,
        NetworkTrafficSnapshot? trafficSnapshot)
    {
        var capabilityFact = new DataStatisticsCapabilityReadFact(
            support, operatingState, readAvailability, context, status, snapshot, trafficSnapshot);
        return new DataStatisticsReadResult
        {
            Availability = capabilityFact.ToLegacyAvailability(),
            Status = status,
            Snapshot = snapshot,
            TrafficSnapshot = trafficSnapshot,
            CapabilityFact = capabilityFact
        };
    }

    public async Task<FullApplicationStatisticsReadResult> ReadFullApplicationsAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            RouterManager routerManager = await _routerManagerProvider
                .GetRouterManagerAsync(cancellationToken)
                .ConfigureAwait(false);
            FullApplicationStatisticsSnapshot snapshot = await routerManager
                .GetFlowStatisticsAsync(cancellationToken)
                .ConfigureAwait(false);
            return new FullApplicationStatisticsReadResult
            {
                Availability = FullApplicationStatisticsAvailability.Available,
                Snapshot = snapshot
            };
        }
        catch (DataStatisticsRpcException exception) when (exception.IsMethodOrServiceUnavailable)
        {
            return new FullApplicationStatisticsReadResult
            {
                Availability = FullApplicationStatisticsAvailability.Unsupported
            };
        }
        catch (DataStatisticsRpcException)
        {
            return new FullApplicationStatisticsReadResult
            {
                Availability = FullApplicationStatisticsAvailability.TemporarilyUnavailable
            };
        }
    }

    public async Task<ApplicationTrafficDetailReadResult> ReadApplicationDetailAsync(
        string applicationId, string applicationName, CancellationToken cancellationToken = default)
    {
        try
        {
            RouterManager routerManager = await _routerManagerProvider.GetRouterManagerAsync(cancellationToken).ConfigureAwait(false);
            ApplicationTrafficDetail detail = await routerManager
                .GetAppFlowStatisticsAsync(applicationId, applicationName, cancellationToken).ConfigureAwait(false);
            return new ApplicationTrafficDetailReadResult
            {
                Availability = ApplicationTrafficDetailAvailability.Available,
                Detail = detail
            };
        }
        catch (DataStatisticsRpcException exception) when (exception.IsMethodOrServiceUnavailable)
        {
            return new ApplicationTrafficDetailReadResult { Availability = ApplicationTrafficDetailAvailability.Unsupported };
        }
        catch (DataStatisticsRpcException)
        {
            return new ApplicationTrafficDetailReadResult { Availability = ApplicationTrafficDetailAvailability.TemporarilyUnavailable };
        }
    }

    public async Task<ApplicationProtectionMutationResult> SetApplicationContentProtectionAsync(
        string applicationId, string applicationName, bool blocked, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(applicationId) || string.IsNullOrWhiteSpace(applicationName))
            return new ApplicationProtectionMutationResult { Availability = ApplicationProtectionMutationAvailability.InvalidApplication };

        if (!await _applicationProtectionGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return new ApplicationProtectionMutationResult { Availability = ApplicationProtectionMutationAvailability.Busy };

        bool writeAccepted = false;
        try
        {
            RouterManager routerManager = await _routerManagerProvider.GetRouterManagerAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await routerManager.SetApplicationContentProtectionAsync(applicationName, blocked, cancellationToken).ConfigureAwait(false);
                writeAccepted = true;
            }
            catch (DataStatisticsRpcException exception) when (exception.IsMethodOrServiceUnavailable)
            {
                return new ApplicationProtectionMutationResult { Availability = ApplicationProtectionMutationAvailability.Unsupported };
            }
            catch (DataStatisticsRpcException)
            {
                return new ApplicationProtectionMutationResult { Availability = ApplicationProtectionMutationAvailability.WriteFailed };
            }

            ApplicationTrafficDetail detail;
            try
            {
                detail = await routerManager.GetAppFlowStatisticsAsync(applicationId, applicationName, cancellationToken).ConfigureAwait(false);
            }
            catch (DataStatisticsRpcException)
            {
                return new ApplicationProtectionMutationResult { Availability = ApplicationProtectionMutationAvailability.VerificationFailed };
            }
            return ApplicationProtectionVerification.Matches(detail, blocked)
                ? new ApplicationProtectionMutationResult { Availability = ApplicationProtectionMutationAvailability.Succeeded, VerifiedDetail = detail }
                : new ApplicationProtectionMutationResult { Availability = ApplicationProtectionMutationAvailability.VerificationFailed };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new ApplicationProtectionMutationResult
            {
                Availability = writeAccepted
                    ? ApplicationProtectionMutationAvailability.VerificationFailed
                    : ApplicationProtectionMutationAvailability.WriteFailed
            };
        }
        finally
        {
            _applicationProtectionGate.Release();
        }
    }
}
