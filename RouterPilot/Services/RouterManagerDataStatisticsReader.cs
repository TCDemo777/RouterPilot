using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>Production read adapter retaining the established RouterManager lifecycle.</summary>
internal sealed class RouterManagerDataStatisticsReader(IRouterManagerProvider routerManagerProvider)
    : IDataStatisticsReader
{
    public async Task<IDataStatisticsReadSession> OpenReadSessionAsync(
        CancellationToken cancellationToken = default)
    {
        RouterManager routerManager = await routerManagerProvider
            .GetRouterManagerAsync(cancellationToken)
            .ConfigureAwait(false);
        return new RouterManagerDataStatisticsReadSession(routerManager);
    }

    private sealed class RouterManagerDataStatisticsReadSession(RouterManager routerManager)
        : IDataStatisticsReadSession
    {
        public Task<NetworkTrafficSnapshot> GetNetworkTrafficSnapshotAsync(
            CancellationToken cancellationToken = default) =>
            routerManager.GetNetworkTrafficSnapshotAsync(cancellationToken);

        public Task<DataStatisticsStatus> GetDataStatisticsStatusAsync(
            CancellationToken cancellationToken = default) =>
            routerManager.GetDataStatisticsStatusAsync(cancellationToken);

        public Task<DataStatisticsSnapshot> GetTopAppFlowStatisticsAsync(
            CancellationToken cancellationToken = default) =>
            routerManager.GetTopAppFlowStatisticsAsync(cancellationToken);
    }
}
