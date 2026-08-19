using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>
/// Translates the existing tunnel inventory and fixed live-status subscription
/// into a small, safe application-wide presentation state.
/// </summary>
public sealed class VpnSummaryService : IVpnSummaryService
{
    private readonly IVpnService _vpnService;
    private readonly IVpnLiveStatusService _liveStatus;
    private readonly object _sync = new();
    private IReadOnlyList<VpnTunnelInfo> _tunnels = [];
    private IReadOnlyList<VpnClientProfileInfo> _profiles = [];
    private IReadOnlyList<VpnLiveStatusInfo> _statuses = [];
    private VpnSummaryState _current = new();

    public VpnSummaryService(IVpnService vpnService, IVpnLiveStatusService liveStatus)
    {
        _vpnService = vpnService;
        _liveStatus = liveStatus;
        _liveStatus.StatusChanged += OnLiveStatusChanged;
    }

    public event Action<VpnSummaryState>? SummaryChanged;
    public VpnSummaryState Current { get { lock (_sync) return _current; } }
    public IReadOnlyList<VpnTunnelInfo> Tunnels { get { lock (_sync) return _tunnels.ToList(); } }
    public IReadOnlyList<VpnClientProfileInfo> Profiles { get { lock (_sync) return _profiles.ToList(); } }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        VpnLiveStatusDiagnostics.Record("VpnSummaryService.RefreshAsync entered: YES");
        try
        {
            IReadOnlyList<VpnTunnelInfo> tunnels = await _vpnService.GetTunnelsAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<VpnClientProfileInfo> profiles = await _vpnService.GetClientProfilesAsync(cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                _tunnels = tunnels;
                _profiles = profiles;
                _statuses = _liveStatus.Current;
            }
            Publish();

            // Socket delivery enriches the already-confirmed tunnel state. A
            // temporary live-status failure must not make a configured VPN look unsupported.
            try { await _liveStatus.EnsureSubscribedAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception exception)
            {
                VpnLiveStatusDiagnostics.SetSocketStartupException(exception, "Awaiting VPN socket startup");
            }
        }
        catch
        {
            MarkUnavailable();
        }
    }

    public void MarkUnavailable()
    {
        lock (_sync)
        {
            _statuses = [];
            _current = new VpnSummaryState { State = "Unavailable" };
        }
        SummaryChanged?.Invoke(Current);
    }

    private void OnLiveStatusChanged(IReadOnlyList<VpnLiveStatusInfo> statuses)
    {
        lock (_sync) _statuses = statuses;
        Publish();
    }

    private void Publish()
    {
        VpnSummaryState summary;
        lock (_sync)
        {
            if (_tunnels.Count == 0)
            {
                _current = new VpnSummaryState { IsAvailable = true, State = "Not configured" };
            }
            else
            {
                VpnTunnelInfo tunnel = _tunnels.FirstOrDefault(item => _statuses.Any(status => status.TunnelId == item.TunnelId && status.IsConnected))
                    ?? _tunnels.FirstOrDefault(item => _statuses.Any(status => status.TunnelId == item.TunnelId && status.Enabled))
                    ?? _tunnels.First();
                VpnLiveStatusInfo? status = _statuses.FirstOrDefault(item => item.TunnelId == tunnel.TunnelId);
                bool connected = status?.IsConnected == true;
                bool connecting = !connected && (status?.Enabled == true || tunnel.Enabled);
                string profile = tunnel.ProfileGroupIds.Select(id => _profiles.FirstOrDefault(item => item.GroupId == id)?.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? string.Empty;
                _current = new VpnSummaryState
                {
                    IsAvailable = true,
                    IsConfigured = true,
                    State = connected ? "Connected" : connecting ? "Connecting" : "Disconnected",
                    Protocol = tunnel.Protocol,
                    TunnelName = tunnel.Name,
                    ProfileName = profile,
                    Location = connected ? status?.LocationDisplay ?? string.Empty : string.Empty,
                    VirtualIp = connected ? status?.VirtualIpv4 ?? string.Empty : string.Empty
                };
            }
            summary = _current;
        }
        VpnLiveStatusDiagnostics.Record("Shared VPN summary updated: YES");
        SummaryChanged?.Invoke(summary);
    }
}
