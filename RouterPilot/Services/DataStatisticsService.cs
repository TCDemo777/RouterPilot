using System;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

public sealed class DataStatisticsService
{
    private readonly IRouterManagerProvider _routerManagerProvider;
    private readonly SemaphoreSlim _applicationProtectionGate = new(1, 1);

    public DataStatisticsService(IRouterManagerProvider routerManagerProvider)
    {
        _routerManagerProvider = routerManagerProvider;
    }

    public async Task<DataStatisticsReadResult> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            RouterManager routerManager = await _routerManagerProvider
                .GetRouterManagerAsync(cancellationToken)
                .ConfigureAwait(false);
            DataStatisticsStatus status = await routerManager
                .GetDataStatisticsStatusAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!status.HasFlowStatisticsState)
            {
                return new DataStatisticsReadResult
                {
                    Availability = DataStatisticsAvailability.Unsupported,
                    Status = status
                };
            }

            if (status.FlowStatisticsEnabled is false)
            {
                return new DataStatisticsReadResult
                {
                    Availability = DataStatisticsAvailability.Disabled,
                    Status = status
                };
            }

            if (!status.IsDpiActive)
            {
                return new DataStatisticsReadResult
                {
                    Availability = DataStatisticsAvailability.DpiInactive,
                    Status = status
                };
            }

            DataStatisticsSnapshot snapshot = await routerManager
                .GetTopAppFlowStatisticsAsync(cancellationToken)
                .ConfigureAwait(false);
            return new DataStatisticsReadResult
            {
                Availability = DataStatisticsAvailability.Available,
                Status = status,
                Snapshot = snapshot
            };
        }
        catch (DataStatisticsRpcException exception) when (exception.IsMethodOrServiceUnavailable)
        {
            return new DataStatisticsReadResult
            {
                Availability = DataStatisticsAvailability.Unsupported
            };
        }
        catch (DataStatisticsRpcException)
        {
            return new DataStatisticsReadResult
            {
                Availability = DataStatisticsAvailability.TemporarilyUnavailable
            };
        }
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
