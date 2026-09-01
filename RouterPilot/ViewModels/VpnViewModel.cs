using System.Collections.ObjectModel;
using System.Collections.Generic;
using System;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using RouterPilot.Models;
using RouterPilot.Services;

namespace RouterPilot.ViewModels;

public sealed partial class VpnViewModel : ObservableObject
{
    private readonly Dictionary<string, bool?> _peerStates = new(StringComparer.OrdinalIgnoreCase);
    public ObservableCollection<string> TailscaleHistory { get; } = new();
    public string TailscaleHistoryText => TailscaleHistory.Count == 0 ? "No Tailscale state changes observed this session." : string.Join("\n", TailscaleHistory);
    public string TailscaleAttention => TailscaleStatus?.State switch { TailscaleState.NeedsLogin => "Tailscale needs login.", TailscaleState.Stopped => "Tailscale daemon is stopped.", TailscaleState.Unavailable => "Tailscale telemetry is unavailable.", _ => string.Empty };
    public ObservableCollection<VpnTunnelInfo> VpnTunnels { get; } = new();
    public ObservableCollection<VpnClientProfileInfo> VpnProfiles { get; } = new();
    [ObservableProperty] private bool vpnIsLoading;
    [ObservableProperty] private bool vpnInventoryLoadCompleted;
    [ObservableProperty] private string vpnStatus = string.Empty;
    [ObservableProperty] private bool vpnSupported;
    [ObservableProperty] private TailscaleStatus? tailscaleStatus;
    [ObservableProperty] private bool tailscaleIsLoading;
    public string TailscaleStateDisplay => TailscaleStatus?.State switch { TailscaleState.Connected => "Connected", TailscaleState.NeedsLogin => "Needs login", TailscaleState.Stopped => "Stopped", TailscaleState.NotInstalled => "Not installed", TailscaleState.Incompatible => "Incompatible", _ => "Unavailable" };
    public string TailscaleAddressDisplay => TailscaleStatus is { Addresses.Count: > 0 } status ? string.Join("\n", status.Addresses) : "—";
    public string TailscaleIPv4Display => string.IsNullOrWhiteSpace(TailscaleStatus?.IPv4) ? "—" : TailscaleStatus.IPv4;
    public string TailscaleIPv6Display => string.IsNullOrWhiteSpace(TailscaleStatus?.IPv6) ? "—" : TailscaleStatus.IPv6;
    public string TailscalePeerCountDisplay => TailscaleStatus?.PeerCount?.ToString() ?? "—";
    public string TailscaleOnlinePeerCountDisplay => TailscaleStatus?.OnlinePeerCount?.ToString() ?? "—";
    public string TailscalePeerSummaryDisplay => TailscaleStatus is { PeerCount: int total, OnlinePeerCount: int online } ? $"{total} / {online}" : "—";
    public void ApplyTailscaleStatus(TailscaleStatus status)
    {
        TailscaleStatus? previous = TailscaleStatus;
        bool usableTransition = previous is not null && status.State is not TailscaleState.Unavailable and not TailscaleState.Incompatible;
        if (usableTransition && previous!.State != status.State)
            AddTailscaleHistory($"Tailscale state changed: {previous.State} -> {status.State}");
        if (previous?.PeerDataAvailable == true && status.PeerDataAvailable)
            foreach (TailscalePeer peer in status.Peers)
                if (_peerStates.TryGetValue(peer.Name, out bool? old) && old != peer.Online)
                    AddTailscaleHistory($"{peer.DisplayName} is now {peer.OnlineDisplay}");
        _peerStates.Clear();
        if (status.PeerDataAvailable)
            foreach (TailscalePeer peer in status.Peers)
                _peerStates[peer.Name] = peer.Online;
        TailscaleStatus = status;
        NotifyTailscale();
    }
    public void ResetTailscale() { TailscaleStatus = null; _peerStates.Clear(); TailscaleHistory.Clear(); NotifyTailscale(); }
    public string BuildTailscaleSummary() { StringBuilder text = new("RouterPilot Tailscale Summary\n"); text.AppendLine($"Status: {TailscaleStateDisplay}"); text.AppendLine($"Version: {TailscaleStatus?.Version ?? "—"}"); text.AppendLine($"IPv4 available: {(string.IsNullOrWhiteSpace(TailscaleStatus?.IPv4) ? "No" : "Yes")}"); text.AppendLine($"IPv6 available: {(string.IsNullOrWhiteSpace(TailscaleStatus?.IPv6) ? "No" : "Yes")}"); text.AppendLine($"Peers: {TailscalePeerSummaryDisplay}"); text.AppendLine("Peer identities and addresses omitted."); return text.ToString(); }
    private void AddTailscaleHistory(string message)
    {
        TailscaleHistory.Insert(0, $"{DateTime.Now:g}  {message}");
        while (TailscaleHistory.Count > 100) TailscaleHistory.RemoveAt(TailscaleHistory.Count - 1);
    }

    private void NotifyTailscale() { OnPropertyChanged(nameof(TailscaleStateDisplay)); OnPropertyChanged(nameof(TailscaleAddressDisplay)); OnPropertyChanged(nameof(TailscaleIPv4Display)); OnPropertyChanged(nameof(TailscaleIPv6Display)); OnPropertyChanged(nameof(TailscalePeerCountDisplay)); OnPropertyChanged(nameof(TailscaleOnlinePeerCountDisplay)); OnPropertyChanged(nameof(TailscalePeerSummaryDisplay)); OnPropertyChanged(nameof(TailscaleAttention)); OnPropertyChanged(nameof(TailscaleHistoryText)); }
    [ObservableProperty] private int vpnOperationTunnelId;
    private int? _connectionAttemptTunnelId;
    private int? _connectionAttemptGroupId;
    private string _connectionAttemptLocation = string.Empty;
    private bool _connectionAttemptObservedEnabled;
    private int? _failedConnectionTunnelId;
    private int? _failedConnectionGroupId;
    private string _failedConnectionLocation = string.Empty;
    public bool HasVpnTunnels => VpnTunnels.Count > 0;
    public bool HasVpnProfiles => VpnProfiles.Count > 0;
    public bool IsVpnInventoryLoading => !VpnInventoryLoadCompleted || VpnIsLoading;
    public bool ShowNoVpnTunnels => VpnInventoryLoadCompleted && VpnSupported && !HasVpnTunnels;
    public bool IsTunnelBusy(VpnTunnelInfo tunnel) => VpnOperationTunnelId == tunnel.TunnelId;
    public void Replace(IReadOnlyList<VpnTunnelInfo> tunnels, IReadOnlyList<VpnClientProfileInfo> profiles)
    {
        VpnTunnels.Clear(); foreach (VpnTunnelInfo tunnel in tunnels) VpnTunnels.Add(tunnel);
        VpnProfiles.Clear(); foreach (VpnClientProfileInfo profile in profiles) VpnProfiles.Add(profile);
        OnPropertyChanged(nameof(HasVpnTunnels)); OnPropertyChanged(nameof(HasVpnProfiles));
        OnPropertyChanged(nameof(ShowNoVpnTunnels));
    }

    partial void OnVpnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsVpnInventoryLoading));
    partial void OnVpnInventoryLoadCompletedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsVpnInventoryLoading));
        OnPropertyChanged(nameof(ShowNoVpnTunnels));
    }
    partial void OnVpnSupportedChanged(bool value) => OnPropertyChanged(nameof(ShowNoVpnTunnels));

    public void BeginConnectionAttempt(VpnTunnelInfo tunnel)
    {
        _connectionAttemptTunnelId = tunnel.TunnelId;
        _connectionAttemptGroupId = tunnel.SelectedProfileGroupId;
        _connectionAttemptLocation = tunnel.ConfiguredLocation;
        _connectionAttemptObservedEnabled = false;
        ClearFailedAttempt(tunnel.TunnelId);
    }

    public void MarkExplicitDisconnect(int tunnelId)
    {
        if (_connectionAttemptTunnelId == tunnelId) ClearConnectionAttempt();
        ClearFailedAttempt(tunnelId);
    }

    public void ApplyLiveStatuses(IReadOnlyList<VpnLiveStatusInfo> statuses, bool vpnInventoryAuthoritative, bool fromLiveStatusEvent = false)
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
            bool hasConnectionFailure = UpdateConnectionAttemptState(tunnel.TunnelId, selectedGroupId, configuredProfile?.CurrentLocation ?? string.Empty, configurationHealth, selectedStatus, fromLiveStatusEvent);
            return new VpnTunnelInfo
            {
                Id=tunnel.Id, TunnelId=tunnel.TunnelId, Name=tunnel.Name, Enabled=tunnel.Enabled, KillSwitch=tunnel.KillSwitch,
                Protocol=tunnel.Protocol, InterfaceName=tunnel.InterfaceName, ProfileGroupIds=tunnel.ProfileGroupIds,
                SelectedProfileGroupId=selectedGroupId, SelectedProfileGroupExists=selectedGroupExists, ConfigurationHealth=configurationHealth,
                ActiveProfileName=tunnel.ActiveProfileName, LinkedProfilesDisplay=tunnel.LinkedProfilesDisplay, FromType=tunnel.FromType,
                ConfiguredProfileName=configuredProfile?.Name ?? string.Empty, ConfiguredLocation=configuredProfile?.CurrentLocation ?? string.Empty,
                ToType=tunnel.ToType, Masquerade=tunnel.Masquerade, LocalAccess=tunnel.LocalAccess, ServicePolicy=tunnel.ServicePolicy,
                ServerConfigCount=tunnel.ServerConfigCount,
                HasConnectionAttemptFailure=hasConnectionFailure,
                // A disconnected status can still carry the authoritative group
                // association needed to recognise an unlinked profile. It is not
                // presented as a live connection unless Status == Connected.
                LiveStatus=selectedStatus
            };
        }).ToList();
        VpnTunnels.Clear(); foreach (VpnTunnelInfo tunnel in updated) VpnTunnels.Add(tunnel);
        VpnLiveStatusDiagnostics.Record("VpnTunnel live properties updated: YES");
    }

    private bool UpdateConnectionAttemptState(int tunnelId, int? groupId, string location, VpnConfigurationHealth configurationHealth, VpnLiveStatusInfo? status, bool fromLiveStatusEvent)
    {
        if (configurationHealth == VpnConfigurationHealth.Unlinked)
        {
            if (_connectionAttemptTunnelId == tunnelId) ClearConnectionAttempt();
            ClearFailedAttempt(tunnelId);
            return false;
        }

        if (_connectionAttemptTunnelId == tunnelId && (_connectionAttemptGroupId != groupId || !string.Equals(_connectionAttemptLocation, location, StringComparison.Ordinal)))
            ClearConnectionAttempt();

        if (_failedConnectionTunnelId == tunnelId && (_failedConnectionGroupId != groupId || !string.Equals(_failedConnectionLocation, location, StringComparison.Ordinal)))
            ClearFailedAttempt(tunnelId);

        if (status?.IsConnected == true)
        {
            if (_connectionAttemptTunnelId == tunnelId) ClearConnectionAttempt();
            ClearFailedAttempt(tunnelId);
            return false;
        }

        if (fromLiveStatusEvent && _connectionAttemptTunnelId == tunnelId)
        {
            if (status?.Enabled == true)
            {
                _connectionAttemptObservedEnabled = true;
            }
            else if (configurationHealth == VpnConfigurationHealth.Healthy && _connectionAttemptObservedEnabled && status is not null)
            {
                _failedConnectionTunnelId = tunnelId;
                _failedConnectionGroupId = groupId;
                _failedConnectionLocation = location;
                ClearConnectionAttempt();
            }
        }

        return _failedConnectionTunnelId == tunnelId;
    }

    private void ClearConnectionAttempt()
    {
        _connectionAttemptTunnelId = null;
        _connectionAttemptGroupId = null;
        _connectionAttemptLocation = string.Empty;
        _connectionAttemptObservedEnabled = false;
    }

    private void ClearFailedAttempt(int tunnelId)
    {
        if (_failedConnectionTunnelId != tunnelId) return;
        _failedConnectionTunnelId = null;
        _failedConnectionGroupId = null;
        _failedConnectionLocation = string.Empty;
    }
}
