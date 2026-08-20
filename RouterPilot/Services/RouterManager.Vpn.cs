using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

public partial class RouterManager
{
    internal event Action<IReadOnlyList<VpnLiveStatusInfo>>? VpnStatusReceived
    {
        add => _sessionService.VpnStatusReceived += value;
        remove => _sessionService.VpnStatusReceived -= value;
    }

    internal async Task EnsureVpnStatusSubscriptionAsync(CancellationToken token)
    {
        VpnLiveStatusDiagnostics.Record("RouterManager.EnsureVpnStatusSubscriptionAsync entered: YES");
        // The preceding VPN read established the current authenticated SID.
        // Do not log in again here: the WebSocket is session-bound.
        await _sessionService.EnsureVpnStatusSocketAsync(token);
    }
    internal async Task<IReadOnlyList<VpnTunnelInfo>> GetVpnTunnelsAsync(CancellationToken token)
    {
        string sid = await _sessionService.GetAdminTokenAsync(token);
        using JsonDocument document = await _sessionService.CallVpnAsync(sid, VpnRpcOperation.GetTunnels, cancellationToken: token);
        if (!TryResultArray(document.RootElement, "tunnels", out JsonElement tunnels)) return [];
        return tunnels.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).Select(ParseTunnel).Where(tunnel => tunnel.TunnelId > 0).ToList();
    }

    internal async Task<IReadOnlyList<VpnClientProfileInfo>> GetVpnProfilesAsync(IReadOnlyList<VpnTunnelInfo> tunnels, CancellationToken token)
    {
        string sid = await _sessionService.GetAdminTokenAsync(token);
        using JsonDocument document = await _sessionService.CallVpnAsync(sid, VpnRpcOperation.GetProfiles, cancellationToken: token);
        if (!TryGet(document.RootElement, out JsonElement configs, "result", "configs") || configs.ValueKind != JsonValueKind.Object) return [];
        var profiles = new List<VpnClientProfileInfo>();
        foreach ((string protocolKey, string protocol) in new[] { ("wireguard", "WireGuard"), ("openvpn", "OpenVPN") })
        {
            if (!configs.TryGetProperty(protocolKey, out JsonElement groups) || groups.ValueKind != JsonValueKind.Array) continue;
            foreach (JsonElement group in groups.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object))
            {
                int groupId = ReadInt(group, "group_id");
                if (groupId <= 0) continue;
                List<VpnTunnelInfo> usedBy = tunnels.Where(tunnel => tunnel.ProfileGroupIds.Contains(groupId)).ToList();
                List<JsonElement> peers = group.TryGetProperty("peers", out JsonElement peerValues) && peerValues.ValueKind == JsonValueKind.Array
                    ? peerValues.EnumerateArray().Where(peer => ReadInt(peer, "peer_id") > 0 || ReadInt(peer, "client_id") > 0).ToList() : [];
                JsonElement currentPeer = peers.Count == 1 ? peers[0] : default;
                int currentPeerId = currentPeer.ValueKind == JsonValueKind.Object ? ReadInt(currentPeer, "peer_id") : 0;
                if (currentPeerId <= 0 && currentPeer.ValueKind == JsonValueKind.Object) currentPeerId = ReadInt(currentPeer, "client_id");
                profiles.Add(new VpnClientProfileInfo { GroupId = groupId, Name = ReadString(group, "group_name", "Unnamed profile"), Protocol = protocol, IsUsedByTunnel = usedBy.Count > 0, TunnelIds = usedBy.Select(tunnel => tunnel.TunnelId).ToList(), UsedByDisplay = usedBy.Count == 0 ? "Not used" : string.Join(", ", usedBy.Select(tunnel => tunnel.Name)), ServerConfigCount = peers.Count, CurrentPeerId = currentPeerId > 0 ? currentPeerId : null, CurrentLocation = currentPeer.ValueKind == JsonValueKind.Object ? ReadString(currentPeer, "location") : string.Empty });
            }
        }
        return profiles;
    }

    internal async Task<IReadOnlyList<VpnConfigMetadata>> GetVpnConfigMetadataAsync(CancellationToken token)
    {
        string sid = await _sessionService.GetAdminTokenAsync(token);
        using JsonDocument document = await _sessionService.CallVpnAsync(sid, VpnRpcOperation.GetProfiles, cancellationToken: token);
        if (!TryGet(document.RootElement, out JsonElement configs, "result", "configs") || configs.ValueKind != JsonValueKind.Object) return [];
        var result = new List<VpnConfigMetadata>();
        foreach ((string protocolKey, string protocol) in new[] { ("wireguard", "WireGuard"), ("openvpn", "OpenVPN") })
        {
            if (!configs.TryGetProperty(protocolKey, out JsonElement groups) || groups.ValueKind != JsonValueKind.Array) continue;
            foreach (JsonElement group in groups.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Object))
            {
                int groupId = ReadInt(group, "group_id");
                string groupName = ReadString(group, "group_name");
                bool isProvider = ReadBool(group, "isProvider") || ReadBool(group, "is_provider");
                if (!group.TryGetProperty("peers", out JsonElement peers) || peers.ValueKind != JsonValueKind.Array) continue;
                foreach (JsonElement peer in peers.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Object))
                {
                    int peerId = ReadInt(peer, "peer_id"); if (peerId <= 0) peerId = ReadInt(peer, "client_id"); if (peerId <= 0) continue;
                    result.Add(new VpnConfigMetadata { Protocol=protocol, GroupId=groupId, PeerId=peerId, GroupName=groupName, Name=ReadString(peer,"name"), Location=ReadString(peer,"location"), IsProvider=isProvider || ReadBool(peer,"isProvider") || ReadBool(peer,"is_provider") });
                }
            }
        }
        return result;
    }

#if DEBUG
    // One-shot DEBUG diagnostic read. Only IDs and display metadata already
    // consumed by RouterPilot are projected; raw VPN configuration is excluded.
    internal async Task<VpnStateCaptureSnapshot> GetVpnStateCaptureAsync(CancellationToken token)
    {
        IReadOnlyList<VpnTunnelInfo> tunnels = await GetVpnTunnelsAsync(token);
        string sid = await _sessionService.GetAdminTokenAsync(token);
        using JsonDocument document = await _sessionService.CallVpnAsync(sid, VpnRpcOperation.GetProfiles, cancellationToken: token);
        if (!TryGet(document.RootElement, out JsonElement configs, "result", "configs") || configs.ValueKind != JsonValueKind.Object)
            return new VpnStateCaptureSnapshot { Tunnels = tunnels };

        var groups = new List<VpnProfileGroupCapture>();
        foreach ((string protocolKey, string protocol) in new[] { ("wireguard", "WireGuard"), ("openvpn", "OpenVPN") })
        {
            if (!configs.TryGetProperty(protocolKey, out JsonElement groupValues) || groupValues.ValueKind != JsonValueKind.Array) continue;
            foreach (JsonElement group in groupValues.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Object))
            {
                int groupId = ReadInt(group, "group_id");
                if (groupId <= 0) continue;
                bool provider = ReadBool(group, "isProvider") || ReadBool(group, "is_provider");
                var peers = new List<VpnPeerCapture>();
                if (group.TryGetProperty("peers", out JsonElement peerValues) && peerValues.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement peer in peerValues.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Object))
                    {
                        int peerId = ReadInt(peer, "peer_id");
                        if (peerId <= 0) peerId = ReadInt(peer, "client_id");
                        if (peerId <= 0) continue;
                        peers.Add(new VpnPeerCapture
                        {
                            PeerId = peerId,
                            Name = ReadString(peer, "name"),
                            Location = ReadString(peer, "location"),
                            IsProvider = provider || ReadBool(peer, "isProvider") || ReadBool(peer, "is_provider")
                        });
                    }
                }
                groups.Add(new VpnProfileGroupCapture { Protocol = protocol, GroupId = groupId, GroupName = ReadString(group, "group_name"), IsProvider = provider, Peers = peers });
            }
        }
        return new VpnStateCaptureSnapshot { ProfileGroups = groups, Tunnels = tunnels };
    }
#endif

    internal async Task<bool> SetVpnTunnelEnabledAsync(int tunnelId, bool enabled, CancellationToken token)
    {
        string sid = await _sessionService.GetAdminTokenAsync(token);
        using JsonDocument document = await _sessionService.CallVpnAsync(sid, VpnRpcOperation.SetTunnelEnabled, tunnelId, enabled, cancellationToken: token);
        return !document.RootElement.TryGetProperty("error", out _) && document.RootElement.TryGetProperty("result", out _);
    }

    private static VpnTunnelInfo ParseTunnel(JsonElement tunnel)
    {
        JsonElement via = tunnel.TryGetProperty("via", out JsonElement viaValue) && viaValue.ValueKind == JsonValueKind.Object ? viaValue : default;
        JsonElement options = tunnel.TryGetProperty("options", out JsonElement optionsValue) && optionsValue.ValueKind == JsonValueKind.Object ? optionsValue : default;
        List<int> groups = via.ValueKind == JsonValueKind.Object && via.TryGetProperty("configs", out JsonElement configs) && configs.ValueKind == JsonValueKind.Array
            ? configs.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).Select(item => ReadInt(item, "group_id")).Where(id => id > 0).Distinct().ToList() : [];
        string protocolRaw = ReadString(via, "type");
        return new VpnTunnelInfo { Id = ReadString(tunnel, "id"), TunnelId = ReadInt(tunnel, "tunnel_id"), Name = ReadString(tunnel, "name", "Unnamed tunnel"), Enabled = ReadBool(tunnel, "enabled"), KillSwitch = ReadBool(tunnel, "killswitch"), Protocol = protocolRaw.Equals("wireguard", StringComparison.OrdinalIgnoreCase) ? "WireGuard" : protocolRaw.Equals("openvpn", StringComparison.OrdinalIgnoreCase) ? "OpenVPN" : "Unknown", InterfaceName = ReadString(via, "via"), ProfileGroupIds = groups, FromType = ReadNestedString(tunnel, "from", "type"), ToType = ReadNestedString(tunnel, "to", "type"), Masquerade = ReadNullableBool(options, "masq"), LocalAccess = ReadNullableBool(options, "local_access"), ServicePolicy = ReadString(options, "service_policy") };
    }

    private static bool TryResultArray(JsonElement root, string property, out JsonElement array)
    {
        array = default;
        return TryGet(root, out JsonElement result, "result") && result.TryGetProperty(property, out array) && array.ValueKind == JsonValueKind.Array;
    }
    private static bool TryGet(JsonElement root, out JsonElement value, params string[] path) { value = root; foreach (string segment in path) if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value)) return false; return true; }
    private static int ReadInt(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out JsonElement result) && result.TryGetInt32(out int number) ? number : 0;
    private static bool ReadBool(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out JsonElement result) && result.ValueKind == JsonValueKind.True;
    private static bool? ReadNullableBool(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out JsonElement result) ? result.ValueKind == JsonValueKind.True ? true : result.ValueKind == JsonValueKind.False ? false : null : null;
    private static string ReadString(JsonElement value, string property, string fallback = "") => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out JsonElement result) && result.ValueKind == JsonValueKind.String ? result.GetString() ?? fallback : fallback;
    private static string? ReadNestedString(JsonElement value, string parent, string property) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(parent, out JsonElement nested) ? ReadString(nested, property) : null;
}
