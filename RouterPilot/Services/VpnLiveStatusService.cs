using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

public sealed class VpnLiveStatusService : IVpnLiveStatusService
{
    private readonly IRouterManagerProvider _provider;
    private readonly object _sync = new();
    private RouterManager? _manager;
    private Dictionary<int, VpnLiveStatusInfo> _current = new();
    private Dictionary<string, VpnConfigMetadata> _metadata = new();
    public event Action<IReadOnlyList<VpnLiveStatusInfo>>? StatusChanged;
    public VpnLiveStatusService(IRouterManagerProvider provider) => _provider = provider;
    public IReadOnlyList<VpnLiveStatusInfo> Current { get { lock (_sync) return _current.Values.ToList(); } }
    public async Task EnsureSubscribedAsync(CancellationToken token)
    {
        VpnLiveStatusDiagnostics.Record("VpnLiveStatusService.EnsureSubscribedAsync entered: YES");
        RouterManager manager = await _provider.GetRouterManagerAsync(token);
        VpnLiveStatusDiagnostics.Record("VPN live-status service resolved: YES");
        if (!ReferenceEquals(_manager, manager))
        {
            if (_manager is not null) _manager.VpnStatusReceived -= OnStatusReceived;
            _manager = manager;
            Clear();
            _manager.VpnStatusReceived += OnStatusReceived;
        }

        // The socket has no dependency on optional friendly-location metadata.
        // Start it immediately after a current RouterManager/session is available.
        VpnLiveStatusDiagnostics.Record("VPN live-status start invoked: YES");
        await manager.EnsureVpnStatusSubscriptionAsync(token);

        try
        {
            IReadOnlyList<VpnConfigMetadata> metadata = await manager.GetVpnConfigMetadataAsync(token);
            lock (_sync)
            {
                _metadata = metadata
                    .GroupBy(item => $"{item.Protocol.ToLowerInvariant()},{item.GroupId},{item.PeerId}")
                    .ToDictionary(group => group.Key, group => group.First());
            }
        }
        catch
        {
            // Active event values (peer name, virtual IP, endpoint and traffic)
            // remain valid without optional server-location enrichment.
            VpnLiveStatusDiagnostics.Record("VPN profile metadata unavailable; live socket continues");
        }
    }
    public void Clear()
    {
        lock (_sync) _current.Clear();
        StatusChanged?.Invoke([]);
    }
    private void OnStatusReceived(IReadOnlyList<VpnLiveStatusInfo> statuses)
    {
        lock (_sync)
        {
            foreach (VpnLiveStatusInfo status in statuses)
            {
                VpnConfigMetadata? metadata = status.GroupId is int groupId && status.PeerId is int peerId && _metadata.TryGetValue($"{status.Protocol.ToLowerInvariant()},{groupId},{peerId}", out VpnConfigMetadata? match) ? match : null;
                string location = FormatLocation(metadata?.Location);
                string server = !string.IsNullOrWhiteSpace(metadata?.Name) ? metadata.Name : status.PeerName ?? (!string.IsNullOrWhiteSpace(metadata?.GroupName) ? metadata.GroupName : status.Domains.FirstOrDefault() ?? string.Empty);
                _current[status.TunnelId] = new VpnLiveStatusInfo { TunnelId=status.TunnelId, Enabled=status.Enabled, Status=status.Status, Protocol=status.Protocol, RxBytes=status.RxBytes, TxBytes=status.TxBytes, PeerName=status.PeerName, Domains=status.Domains, GroupId=status.GroupId, PeerId=status.PeerId, Via=status.Via, Port=status.Port, TunnelName=status.TunnelName, VirtualIpv4=status.VirtualIpv4, LocationDisplay=location, ServerName=server };
            }
        }
        StatusChanged?.Invoke(Current);
    }

    private static string FormatLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return string.Empty;

        string[] parts = location.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && string.Equals(parts[0], "GB", StringComparison.OrdinalIgnoreCase))
        {
            string second = parts[1];
            if (second.StartsWith("UK ", StringComparison.OrdinalIgnoreCase))
                return "UK / " + second[3..].Trim();
            if (!string.Equals(second, "UK", StringComparison.OrdinalIgnoreCase))
                return "UK / " + second;
        }

        return string.Join(" / ", parts);
    }
}
