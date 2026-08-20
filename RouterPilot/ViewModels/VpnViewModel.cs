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

    public void ApplyLiveStatuses(IReadOnlyList<VpnLiveStatusInfo> statuses, bool vpnInventoryAuthoritative)
    {
        var statusMap = statuses.ToDictionary(status => status.TunnelId);
        var profilesByGroup = VpnProfiles.ToDictionary(profile => profile.GroupId);
        int matched = VpnTunnels.Count(tunnel => statusMap.ContainsKey(tunnel.TunnelId));
        VpnLiveStatusDiagnostics.Record($"VPN tunnel_id matched: {(matched > 0 ? "YES" : "NO")}; matching tunnel(s): {matched}");
        if (statuses.Any(status => status.Status == 1))
            VpnLiveStatusDiagnostics.Record("VPN status=1 mapped to Connected: YES");
        var updated = VpnTunnels.Select(tunnel =>
        {
            VpnLiveStatusInfo? selectedStatus = statusMap.TryGetValue(tunnel.TunnelId, out VpnLiveStatusInfo? status) ? status : null;
            // Current tunnel configuration is authoritative when it identifies
            // one profile group. Live status is only a fallback for a proven
            // unlinked tunnel, where it is the remaining safe correlation.
            int? selectedGroupId = tunnel.ProfileGroupIds.Count == 1 && profilesByGroup.ContainsKey(tunnel.ProfileGroupIds[0])
                ? tunnel.ProfileGroupIds[0]
                : selectedStatus?.GroupId;
            VpnClientProfileInfo? configuredProfile = null;
            bool selectedGroupExists = selectedGroupId is int groupId && profilesByGroup.TryGetValue(groupId, out configuredProfile);
            VpnConfigurationHealth configurationHealth = !vpnInventoryAuthoritative || !selectedGroupExists
                ? VpnConfigurationHealth.Unknown
                : tunnel.ProfileGroupIds.Contains(selectedGroupId!.Value)
                    ? VpnConfigurationHealth.Healthy
                    : VpnConfigurationHealth.Unlinked;
            return new VpnTunnelInfo
            {
                Id=tunnel.Id, TunnelId=tunnel.TunnelId, Name=tunnel.Name, Enabled=tunnel.Enabled, KillSwitch=tunnel.KillSwitch,
                Protocol=tunnel.Protocol, InterfaceName=tunnel.InterfaceName, ProfileGroupIds=tunnel.ProfileGroupIds,
                SelectedProfileGroupId=selectedGroupId, SelectedProfileGroupExists=selectedGroupExists, ConfigurationHealth=configurationHealth,
                ActiveProfileName=tunnel.ActiveProfileName, LinkedProfilesDisplay=tunnel.LinkedProfilesDisplay, FromType=tunnel.FromType,
                ConfiguredProfileName=configuredProfile?.Name ?? string.Empty, ConfiguredLocation=configuredProfile?.CurrentLocation ?? string.Empty,
                ToType=tunnel.ToType, Masquerade=tunnel.Masquerade, LocalAccess=tunnel.LocalAccess, ServicePolicy=tunnel.ServicePolicy,
                ServerConfigCount=tunnel.ServerConfigCount,
                // A disconnected status can still carry the authoritative group
                // association needed to recognise an unlinked profile. It is not
                // presented as a live connection unless Status == Connected.
                LiveStatus=selectedStatus
            };
        }).ToList();
        VpnTunnels.Clear(); foreach (VpnTunnelInfo tunnel in updated) VpnTunnels.Add(tunnel);
        VpnLiveStatusDiagnostics.Record("VpnTunnel live properties updated: YES");
    }
}
