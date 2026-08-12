using System;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

public interface IVpnSummaryService
{
    event Action<VpnSummaryState>? SummaryChanged;
    VpnSummaryState Current { get; }
    Task RefreshAsync(CancellationToken cancellationToken);
    void MarkUnavailable();
}
