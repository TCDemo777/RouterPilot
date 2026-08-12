using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RouterPilot.Models;
using RouterPilot.Services;

namespace RouterPilot.ViewModels;

public sealed partial class VpnViewModel : ObservableObject
{
    public ObservableCollection<VpnTunnelInfo> VpnTunnels { get; } = new();
    public ObservableCollection<VpnClientProfileInfo> VpnProfiles { get; } = new();
    [ObservableProperty] private bool vpnIsLoading;
    [ObservableProperty] private string vpnStatus = string.Empty;
    [ObservableProperty] private bool vpnSupported;
    [ObservableProperty] private int vpnOperationTunnelId;
    public bool HasVpnTunnels => VpnTunnels.Count > 0;
    public bool HasVpnProfiles => VpnProfiles.Count > 0;
    public bool IsTunnelBusy(VpnTunnelInfo tunnel) => VpnOperationTunnelId == tunnel.TunnelId;
    public void Replace(IReadOnlyList<VpnTunnelInfo> tunnels, IReadOnlyList<VpnClientProfileInfo> profiles)
    {
        VpnTunnels.Clear(); foreach (VpnTunnelInfo tunnel in tunnels) VpnTunnels.Add(tunnel);
        VpnProfiles.Clear(); foreach (VpnClientProfileInfo profile in profiles) VpnProfiles.Add(profile);
        OnPropertyChanged(nameof(HasVpnTunnels)); OnPropertyChanged(nameof(HasVpnProfiles));
    }

    public void ApplyLiveStatuses(IReadOnlyList<VpnLiveStatusInfo> statuses)
    {
        var statusMap = statuses.ToDictionary(status => status.TunnelId);
        int matched = VpnTunnels.Count(tunnel => statusMap.ContainsKey(tunnel.TunnelId));
        VpnLiveStatusDiagnostics.Record($"VPN tunnel_id matched: {(matched > 0 ? "YES" : "NO")}; matching tunnel(s): {matched}");
        if (statuses.Any(status => status.Status == 1))
            VpnLiveStatusDiagnostics.Record("VPN status=1 mapped to Connected: YES");
        var updated = VpnTunnels.Select(tunnel => new VpnTunnelInfo
        {
            Id=tunnel.Id, TunnelId=tunnel.TunnelId, Name=tunnel.Name, Enabled=tunnel.Enabled, KillSwitch=tunnel.KillSwitch,
            Protocol=tunnel.Protocol, InterfaceName=tunnel.InterfaceName, ProfileGroupIds=tunnel.ProfileGroupIds,
            ActiveProfileName=tunnel.ActiveProfileName, LinkedProfilesDisplay=tunnel.LinkedProfilesDisplay, FromType=tunnel.FromType,
            ToType=tunnel.ToType, Masquerade=tunnel.Masquerade, LocalAccess=tunnel.LocalAccess, ServicePolicy=tunnel.ServicePolicy,
            LiveStatus=statusMap.TryGetValue(tunnel.TunnelId, out VpnLiveStatusInfo? status) && status.Enabled ? status : null
        }).ToList();
        VpnTunnels.Clear(); foreach (VpnTunnelInfo tunnel in updated) VpnTunnels.Add(tunnel);
        VpnLiveStatusDiagnostics.Record("VpnTunnel live properties updated: YES");
    }
}
