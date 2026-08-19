using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

public interface IVpnSummaryService
{
    event Action<VpnSummaryState>? SummaryChanged;
    VpnSummaryState Current { get; }
    IReadOnlyList<VpnTunnelInfo> Tunnels { get; }
    IReadOnlyList<VpnClientProfileInfo> Profiles { get; }
    Task RefreshAsync(CancellationToken cancellationToken);
    void MarkUnavailable();
}
