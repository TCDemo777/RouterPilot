using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

public sealed class VpnService : IVpnService
{
    private readonly IRouterManagerProvider _provider;
    private readonly TimelineService _timeline;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public VpnService(IRouterManagerProvider provider, TimelineService timeline) { _provider = provider; _timeline = timeline; }
    public async Task<IReadOnlyList<VpnTunnelInfo>> GetTunnelsAsync(CancellationToken token) => await (await _provider.GetRouterManagerAsync(token)).GetVpnTunnelsAsync(token);
    public async Task<IReadOnlyList<VpnClientProfileInfo>> GetClientProfilesAsync(CancellationToken token)
    {
        RouterManager manager = await _provider.GetRouterManagerAsync(token);
        IReadOnlyList<VpnTunnelInfo> tunnels = await manager.GetVpnTunnelsAsync(token);
        IReadOnlyList<VpnClientProfileInfo> profiles = await manager.GetVpnProfilesAsync(tunnels, token);
        return Correlate(tunnels, profiles);
    }

#if DEBUG
    public async Task<VpnStateCaptureSnapshot> GetDebugStateCaptureAsync(CancellationToken token) =>
        await (await _provider.GetRouterManagerAsync(token)).GetVpnStateCaptureAsync(token);
#endif

    public async Task<VpnOperationResult> SetTunnelEnabledAsync(int tunnelId, bool enabled, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            RouterManager manager = await _provider.GetRouterManagerAsync(token);
            List<VpnTunnelInfo> before = (await manager.GetVpnTunnelsAsync(token)).Where(tunnel => tunnel.TunnelId == tunnelId).ToList();
            if (before.Count != 1) return await CompleteAsync(Failure(tunnelId, "TunnelIdentityAmbiguous"), null, enabled, token);
            VpnTunnelInfo original = before[0];
            if (original.Enabled == enabled) return await CompleteAsync(new VpnOperationResult { Success = true, TunnelId = tunnelId }, original, enabled, token);

            bool applied = await manager.SetVpnTunnelEnabledAsync(tunnelId, enabled, token);
            VpnTunnelInfo? verified = await ReadBackAsync(manager, tunnelId, enabled, token);
            if (applied && verified is not null) return await CompleteAsync(new VpnOperationResult { Success = true, TunnelId = tunnelId }, verified, enabled, token);

            bool rollbackAttempted = enabled;
            bool rollbackVerified = false;
            if (rollbackAttempted)
            {
                bool rollbackApplied = await manager.SetVpnTunnelEnabledAsync(tunnelId, false, token);
                rollbackVerified = rollbackApplied && await ReadBackAsync(manager, tunnelId, false, token) is not null;
            }
            return await CompleteAsync(new VpnOperationResult { TunnelId = tunnelId, FailureCategory = "VerificationFailed", Message = "RouterPilot could not verify the VPN tunnel state.", RollbackAttempted = rollbackAttempted, RollbackVerified = rollbackVerified }, original, enabled, token);
        }
        catch { return await CompleteAsync(Failure(tunnelId, "RemoteApplyFailed"), null, enabled, token); }
        finally { _gate.Release(); }
    }

    private static async Task<VpnTunnelInfo?> ReadBackAsync(RouterManager manager, int tunnelId, bool expected, CancellationToken token)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            List<VpnTunnelInfo> matches = (await manager.GetVpnTunnelsAsync(token)).Where(tunnel => tunnel.TunnelId == tunnelId).ToList();
            if (matches.Count == 1 && matches[0].Enabled == expected) return matches[0];
            if (attempt < 4) await Task.Delay(TimeSpan.FromSeconds(1), token);
        }
        return null;
    }

    private async Task<VpnOperationResult> CompleteAsync(VpnOperationResult result, VpnTunnelInfo? tunnel, bool enabled, CancellationToken token)
    {
        try { await _timeline.AddAsync(new TimelineEvent { Category = TimelineCategory.Router, EventType = result.Success ? TimelineEventType.MaintenanceCompleted : TimelineEventType.MaintenanceFailed, Title = result.Success ? $"VPN tunnel {(enabled ? "enabled" : "disabled")}" : $"Failed to {(enabled ? "enable" : "disable")} VPN tunnel", Message = tunnel?.Name ?? "VPN tunnel", Severity = result.Success ? TimelineSeverity.Success : TimelineSeverity.Warning, Source = "VPN" }, token); } catch { }
        return result;
    }

    internal static IReadOnlyList<VpnClientProfileInfo> Correlate(IReadOnlyList<VpnTunnelInfo> tunnels, IReadOnlyList<VpnClientProfileInfo> profiles) => profiles.Select(profile =>
    {
        List<VpnTunnelInfo> usedBy = tunnels.Where(tunnel => tunnel.ProfileGroupIds.Contains(profile.GroupId)).ToList();
        return new VpnClientProfileInfo { GroupId = profile.GroupId, Name = profile.Name, Protocol = profile.Protocol, IsUsedByTunnel = usedBy.Count > 0, TunnelIds = usedBy.Select(tunnel => tunnel.TunnelId).ToList(), UsedByDisplay = usedBy.Count == 0 ? "Not used" : string.Join(", ", usedBy.Select(tunnel => tunnel.Name)), ServerConfigCount = profile.ServerConfigCount, CurrentPeerId = profile.CurrentPeerId, CurrentLocation = profile.CurrentLocation };
    }).ToList();
    private static VpnOperationResult Failure(int tunnelId, string category) => new() { TunnelId = tunnelId, FailureCategory = category, Message = "RouterPilot could not update the VPN tunnel." };
}
