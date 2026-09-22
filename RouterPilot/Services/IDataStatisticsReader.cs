using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>
/// Read-only operations needed by the normal Data Statistics refresh path.
/// A session keeps the existing single RouterManager acquisition for one read.
/// </summary>
public interface IDataStatisticsReader
{
    Task<IDataStatisticsReadSession> OpenReadSessionAsync(
        CancellationToken cancellationToken = default);
}

public interface IDataStatisticsReadSession
{
    Task<NetworkTrafficSnapshot> GetNetworkTrafficSnapshotAsync(
        CancellationToken cancellationToken = default);

    Task<DataStatisticsStatus> GetDataStatisticsStatusAsync(
        CancellationToken cancellationToken = default);

    Task<DataStatisticsSnapshot> GetTopAppFlowStatisticsAsync(
        CancellationToken cancellationToken = default);
}
