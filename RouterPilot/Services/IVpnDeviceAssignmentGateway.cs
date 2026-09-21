using RouterPilot.Models;

namespace RouterPilot.Services;

internal interface IVpnDeviceAssignmentGateway
{
    Task<RouterManager.VpnWireGuardAssignmentState?> GetTunnelAsync(int tunnelId, CancellationToken token);
    Task<bool> SetTunnelAsync(RouterManager.VpnWireGuardAssignmentState state, IReadOnlyList<string> macs, CancellationToken token);
}

internal sealed class RouterManagerVpnDeviceAssignmentGateway(IRouterManagerProvider provider) : IVpnDeviceAssignmentGateway
{
    public async Task<RouterManager.VpnWireGuardAssignmentState?> GetTunnelAsync(int tunnelId, CancellationToken token) =>
        await (await provider.GetRouterManagerAsync(token).ConfigureAwait(false)).GetSelectedDeviceAssignmentStateAsync(tunnelId, token).ConfigureAwait(false);
    public async Task<bool> SetTunnelAsync(RouterManager.VpnWireGuardAssignmentState state, IReadOnlyList<string> macs, CancellationToken token) =>
        await (await provider.GetRouterManagerAsync(token).ConfigureAwait(false)).SetSelectedDeviceAssignmentAsync(state, macs, token).ConfigureAwait(false);
}
