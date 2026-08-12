using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

public interface IVpnService
{
    Task<IReadOnlyList<VpnTunnelInfo>> GetTunnelsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<VpnClientProfileInfo>> GetClientProfilesAsync(CancellationToken cancellationToken);
    Task<VpnOperationResult> SetTunnelEnabledAsync(int tunnelId, bool enabled, CancellationToken cancellationToken);
}
