using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

public interface IVpnService
{
    Task<VpnInventorySnapshot> GetInventoryAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<VpnTunnelInfo>> EnrichRoutingPolicyAsync(IReadOnlyList<VpnTunnelInfo> tunnels, CancellationToken cancellationToken);
    Task<IReadOnlyList<VpnTunnelInfo>> GetTunnelsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<VpnClientProfileInfo>> GetClientProfilesAsync(CancellationToken cancellationToken);
    Task<VpnWireGuardHandshakeSnapshot> GetWireGuardHandshakeSnapshotAsync(VpnTunnelInfo tunnel, CancellationToken cancellationToken);
    Task<VpnProviderServerCatalogueResult> RefreshPiaProviderServersAsync(int tunnelId, int groupId, CancellationToken cancellationToken);
    Task<VpnProviderConfigGenerationResult> GeneratePiaProviderConfigAsync(int tunnelId, int groupId, VpnProviderServerInfo selection, Func<bool> operationStillCurrent, CancellationToken cancellationToken);
#if DEBUG
    Task<VpnStateCaptureSnapshot> GetDebugStateCaptureAsync(CancellationToken cancellationToken);
    Task<PiaManualStateSnapshot> CapturePiaManualStateAsync(int piaGroupId, int primaryTunnelId, CancellationToken cancellationToken);
#endif
    Task<VpnOperationResult> SetTunnelEnabledAsync(int tunnelId, bool enabled, CancellationToken cancellationToken, VpnConnectTrace? trace = null);
}
