using RouterPilot.Models;

namespace RouterPilot.Services;

// Internal transaction seam for deterministic provider-generation lifecycle
// validation. The production adapter delegates directly to the established
// RouterManager RPC contracts.
internal interface IVpnProviderConfigGenerationGateway
{
    Task<VpnProviderGroupInfo?> GetPiaProviderGroupAsync(CancellationToken token);
    Task<VpnProviderServerCatalogueResult> GetPiaProviderServerCatalogueAsync(int groupId, CancellationToken token);
    Task<RouterManager.VpnWireGuardAssignmentState?> GetTunnelAsync(int tunnelId, CancellationToken token);
    Task<bool> GenerateAsync(int groupId, VpnProviderServerInfo server, CancellationToken token, PiaApplyIdentityTrace? trace = null);
    Task<IReadOnlyList<VpnProviderGeneratedConfigInfo>> GetConfigsAsync(int groupId, CancellationToken token);
    Task<bool> AssignAsync(RouterManager.VpnWireGuardAssignmentState state, int groupId, int configId, CancellationToken token, PiaApplyIdentityTrace? trace = null);
    Task<IReadOnlyList<VpnTunnelInfo>> GetTunnelsAsync(CancellationToken token);
}

internal sealed class RouterManagerVpnProviderConfigGenerationGateway(RouterManager manager) : IVpnProviderConfigGenerationGateway
{
    public Task<VpnProviderGroupInfo?> GetPiaProviderGroupAsync(CancellationToken token) => manager.GetPiaProviderGroupAsync(token);
    public Task<VpnProviderServerCatalogueResult> GetPiaProviderServerCatalogueAsync(int groupId, CancellationToken token) => manager.GetPiaProviderServerCatalogueAsync(groupId, token);
    public Task<RouterManager.VpnWireGuardAssignmentState?> GetTunnelAsync(int tunnelId, CancellationToken token) => manager.GetWireGuardAssignmentStateAsync(tunnelId, token);
    public Task<bool> GenerateAsync(int groupId, VpnProviderServerInfo server, CancellationToken token, PiaApplyIdentityTrace? trace = null) => manager.GeneratePiaProviderConfigAsync(groupId, server, token, trace);
    public Task<IReadOnlyList<VpnProviderGeneratedConfigInfo>> GetConfigsAsync(int groupId, CancellationToken token) => manager.GetPiaGeneratedConfigsAsync(groupId, token);
    public Task<bool> AssignAsync(RouterManager.VpnWireGuardAssignmentState state, int groupId, int configId, CancellationToken token, PiaApplyIdentityTrace? trace = null) => manager.AssignWireGuardProviderConfigAsync(state, groupId, configId, token, trace);
    public Task<IReadOnlyList<VpnTunnelInfo>> GetTunnelsAsync(CancellationToken token) => manager.GetVpnTunnelsAsync(token);
}
