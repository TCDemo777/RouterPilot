using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>Small seam for the existing-router-config Primary assignment contract.</summary>
internal interface IVpnExistingConfigAssignmentGateway
{
    Task<RouterManager.VpnWireGuardAssignmentState?> GetTunnelAsync(int tunnelId, CancellationToken token);
    Task<IReadOnlyList<VpnProviderGeneratedConfigInfo>> GetConfigsAsync(int groupId, CancellationToken token);
    Task<bool> AssignAsync(RouterManager.VpnWireGuardAssignmentState state, int groupId, int configId, CancellationToken token);
}

internal sealed class RouterManagerVpnExistingConfigAssignmentGateway(IRouterManagerProvider provider) : IVpnExistingConfigAssignmentGateway
{
    public async Task<RouterManager.VpnWireGuardAssignmentState?> GetTunnelAsync(int tunnelId, CancellationToken token) =>
        await (await provider.GetRouterManagerAsync(token).ConfigureAwait(false)).GetWireGuardAssignmentStateAsync(tunnelId, token).ConfigureAwait(false);
    public async Task<IReadOnlyList<VpnProviderGeneratedConfigInfo>> GetConfigsAsync(int groupId, CancellationToken token) =>
        await (await provider.GetRouterManagerAsync(token).ConfigureAwait(false)).GetPiaGeneratedConfigsAsync(groupId, token).ConfigureAwait(false);
    public async Task<bool> AssignAsync(RouterManager.VpnWireGuardAssignmentState state, int groupId, int configId, CancellationToken token) =>
        await (await provider.GetRouterManagerAsync(token).ConfigureAwait(false)).AssignWireGuardProviderConfigAsync(state, groupId, configId, token).ConfigureAwait(false);
}
