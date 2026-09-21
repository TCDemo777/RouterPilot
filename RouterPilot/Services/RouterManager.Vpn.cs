using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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

    internal async Task<(IReadOnlyList<VpnClientProfileInfo> Profiles, VpnProfileInventoryState State)> GetVpnProfilesAsync(IReadOnlyList<VpnTunnelInfo> tunnels, CancellationToken token)
    {
        string sid = await _sessionService.GetAdminTokenAsync(token);
        using JsonDocument document = await _sessionService.CallVpnAsync(sid, VpnRpcOperation.GetProfiles, cancellationToken: token);
        return ParseVpnProfileInventory(document.RootElement, tunnels);
    }

    // GL.iNet's VPN client UI stores device assignments in the same
    // route_policy UCI configuration it uses for client-policy routing. This
    // is a single, read-only bulk read; it never changes router policy.
    internal async Task<VpnRoutingPolicySnapshot> GetVpnRoutingPolicyAsync(CancellationToken token)
    {
        string output = await RunReadOnlySshCommandAsync("uci -q show route_policy 2>/dev/null", token).ConfigureAwait(false);
        return ParseVpnRoutingPolicy(output);
    }

    // GL.iNet keeps persistent client aliases in gl-client. Unlike the live
    // gl-clients inventory, these labels remain available for an offline
    // device. This stays a single optional aggregate read for VPN policy
    // presentation; it never changes client or route-policy configuration.
    internal async Task<IReadOnlyDictionary<string, string>> GetPersistentClientNamesAsync(CancellationToken token)
    {
        string output = await RunReadOnlySshCommandAsync("uci -q show gl-client 2>/dev/null", token).ConfigureAwait(false);
        return ParsePersistentClientNames(output);
    }

    internal static IReadOnlyDictionary<string, string> ParsePersistentClientNames(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = rawLine.IndexOf('=');
            if (equals <= 0) continue;
            string key = rawLine[..equals].Trim();
            const string prefix = "gl-client.";
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            int propertySeparator = key.LastIndexOf('.');
            if (propertySeparator <= prefix.Length) continue;
            string section = key[..propertySeparator];
            string property = key[(propertySeparator + 1)..];
            if (!sections.TryGetValue(section, out Dictionary<string, string>? values)) sections[section] = values = new(StringComparer.OrdinalIgnoreCase);
            values[property] = UnquoteUci(rawLine[(equals + 1)..].Trim());
        }

        return sections.Values
            .Where(values => values.TryGetValue("mac", out string? mac) &&
                TryGetPersistentClientName(values, out string? name) && !string.IsNullOrWhiteSpace(name))
            .Select(values => (Mac: ClientIdentity.NormalizeHexMac(values["mac"]), Name: GetPersistentClientName(values)))
            .Where(item => ClientIdentity.IsMacKey(item.Mac))
            .GroupBy(item => item.Mac, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Name, StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryGetPersistentClientName(IReadOnlyDictionary<string, string> values, out string? name)
    {
        name = GetPersistentClientName(values);
        return !string.IsNullOrWhiteSpace(name);
    }

    private static string GetPersistentClientName(IReadOnlyDictionary<string, string> values)
    {
        string alias = Read(values, "alias");
        return string.IsNullOrWhiteSpace(alias) ? Read(values, "name") : alias;
    }

    internal static VpnRoutingPolicySnapshot ParseVpnRoutingPolicy(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return new VpnRoutingPolicySnapshot { State = VpnRoutingPolicyState.Unavailable };

        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = rawLine.IndexOf('=');
            if (equals <= 0) continue;
            string key = rawLine[..equals].Trim();
            const string prefix = "route_policy.";
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            int propertySeparator = key.LastIndexOf('.');
            if (propertySeparator <= prefix.Length) continue;
            string section = key[..propertySeparator];
            string property = key[(propertySeparator + 1)..];
            if (!sections.TryGetValue(section, out Dictionary<string, string>? values)) sections[section] = values = new(StringComparer.OrdinalIgnoreCase);
            values[property] = UnquoteUci(output: rawLine[(equals + 1)..].Trim());
        }

        var policies = new Dictionary<int, VpnTunnelRoutingPolicy>();
        foreach ((string section, Dictionary<string, string> values) in sections)
        {
            if (!TryReadPositiveInt(values, "tunnel_id", out int tunnelId)) continue;
            bool isDefault = section.Contains("@default[", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Read(values, "type"), "default", StringComparison.OrdinalIgnoreCase) && IsEnabled(values);
            // On the live Flint 2, GL.iNet's selected-device VPN rule uses
            // from_mac plus via_type=wireguard and references the tunnel,
            // group and peer. Do not treat a generic MAC-based policy (such
            // as a bypass rule) as a VPN inclusion without that proven via.
            bool hasSelectedDevices = values.ContainsKey("from_mac") && IsVpnVia(Read(values, "via_type"));
            if (!isDefault && !hasSelectedDevices) continue;

            IReadOnlyList<string> devices = hasSelectedDevices
                ? Regex.Matches(Read(values, "from_mac"), "(?i)(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}")
                    .Select(match => ClientIdentity.NormalizeHexMac(match.Value)).Where(mac => mac.Length == 12).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : [];
            VpnInternetRoutingScope scope = hasSelectedDevices ? VpnInternetRoutingScope.SelectedDevices : VpnInternetRoutingScope.DefaultInternet;
            // A tunnel can have more than one selected-device rule. Union the
            // stable identities without allowing a default rule to override it.
            if (policies.TryGetValue(tunnelId, out VpnTunnelRoutingPolicy? existing))
            {
                scope = existing.Scope == VpnInternetRoutingScope.SelectedDevices || scope == VpnInternetRoutingScope.SelectedDevices
                    ? VpnInternetRoutingScope.SelectedDevices : VpnInternetRoutingScope.DefaultInternet;
                devices = existing.DeviceIdentities.Concat(devices).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            policies[tunnelId] = new VpnTunnelRoutingPolicy
            {
                TunnelId = tunnelId,
                GroupId = TryReadPositiveInt(values, "group_id", out int groupId) ? groupId : null,
                PeerId = TryReadPositiveInt(values, "peer_id", out int peerId) ? peerId : null,
                Enabled = TryReadBoolean(values, "enabled"),
                Scope = scope,
                DeviceIdentities = devices
            };
        }
        return new VpnRoutingPolicySnapshot { State = VpnRoutingPolicyState.Available, Tunnels = policies.Values.ToList() };
    }

    private static bool TryReadPositiveInt(IReadOnlyDictionary<string, string> values, string key, out int value) => int.TryParse(Read(values, key), out value) && value > 0;
    private static string Read(IReadOnlyDictionary<string, string> values, string key) => values.TryGetValue(key, out string? value) ? value : string.Empty;
    private static bool IsEnabled(IReadOnlyDictionary<string, string> values) => !values.TryGetValue("enabled", out string? value) || value == "1";
    private static bool? TryReadBoolean(IReadOnlyDictionary<string, string> values, string key) => values.TryGetValue(key, out string? value)
        ? value == "1" ? true : value == "0" ? false : null : null;
    private static bool IsVpnVia(string viaType) => viaType.Equals("wireguard", StringComparison.OrdinalIgnoreCase) || viaType.Equals("openvpn", StringComparison.OrdinalIgnoreCase);
    private static string UnquoteUci(string output) => output.Length >= 2 && output[0] == '\'' && output[^1] == '\'' ? output[1..^1].Replace("'\\''", "'", StringComparison.Ordinal) : output;

    // The profile read is a bulk configured-profile inventory.  Do not use a
    // tunnel's live connection state to decide whether a group exists.
    internal static (IReadOnlyList<VpnClientProfileInfo> Profiles, VpnProfileInventoryState State) ParseVpnProfileInventory(JsonElement root, IReadOnlyList<VpnTunnelInfo> tunnels)
    {
        if (!TryGet(root, out JsonElement configs, "result", "configs") || configs.ValueKind != JsonValueKind.Object)
            return ([], VpnProfileInventoryState.Unavailable);
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
        return (profiles, VpnProfileInventoryState.Available);
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
    // Manual incident capture. This method intentionally performs exactly two
    // read RPCs when IDs are available: wg-client.get_config_list and
    // vpn-client.get_tunnel. It contains no mutation-capable call path.
    internal async Task<PiaManualStateSnapshot> CapturePiaManualStateAsync(int piaGroupId, int primaryTunnelId, CancellationToken token)
    {
        IReadOnlyList<PiaManualConfigSnapshot> configs = [];
        bool configReadSucceeded = false;
        if (piaGroupId > 0)
        {
            try
            {
                configs = (await GetPiaGeneratedConfigsAsync(piaGroupId, token).ConfigureAwait(false))
                    .Select(config => new PiaManualConfigSnapshot { ConfigId = config.PeerId, Name = config.Name, Location = config.Location })
                    .ToList();
                configReadSucceeded = true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { }
        }

        VpnTunnelStructuralSnapshot? tunnel = null;
        bool tunnelReadSucceeded = false;
        if (primaryTunnelId > 0)
        {
            try
            {
                string sid = await _sessionService.GetAdminTokenAsync(token).ConfigureAwait(false);
                using JsonDocument document = await _sessionService.CallVpnAsync(sid, VpnRpcOperation.GetTunnels, cancellationToken: token).ConfigureAwait(false);
                if (TryResultArray(document.RootElement, "tunnels", out JsonElement tunnels))
                {
                    List<JsonElement> matches = tunnels.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object && ReadInt(item, "tunnel_id") == primaryTunnelId).ToList();
                    tunnel = matches.Count == 1 ? ParseVpnTunnelStructuralSnapshot(matches[0]) : null;
                    tunnelReadSucceeded = true;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { }
        }
        return new PiaManualStateSnapshot { ConfigReadSucceeded = configReadSucceeded, Configs = configs, TunnelReadSucceeded = tunnelReadSucceeded, Tunnel = tunnel };
    }

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

    // CONNECT-only provider WireGuard contract captured from the stock UI.
    // The association is read afresh from get_tunnel and is never retained as
    // a durable server/peer identity.
    internal async Task<VpnWireGuardConnectAssociation?> GetWireGuardConnectAssociationAsync(int tunnelId, CancellationToken token)
    {
        string sid = await _sessionService.GetAdminTokenAsync(token).ConfigureAwait(false);
        using JsonDocument document = await _sessionService.CallVpnAsync(sid, VpnRpcOperation.GetTunnels, cancellationToken: token).ConfigureAwait(false);
        if (!TryResultArray(document.RootElement, "tunnels", out JsonElement tunnels)) return null;
        List<JsonElement> matches = tunnels.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object && ReadInt(item, "tunnel_id") == tunnelId).ToList();
        return matches.Count == 1 ? ParseWireGuardConnectAssociation(matches[0]) : null;
    }

    internal async Task<bool> ConnectWireGuardProviderTunnelAsync(int tunnelId, VpnWireGuardConnectAssociation association, CancellationToken token, VpnConnectTrace? trace = null)
    {
        string sid = await _sessionService.GetAdminTokenAsync(token).ConfigureAwait(false);
        object request = BuildWireGuardProviderConnectRequest(tunnelId, association);
        trace?.Serialization(true);
        trace?.DispatchAttempted();
        try
        {
            using JsonDocument document = await _sessionService.CallAsync(sid, "vpn-client", "set_tunnel", request, token).ConfigureAwait(false);
            bool success = !document.RootElement.TryGetProperty("error", out _) && document.RootElement.TryGetProperty("result", out _);
            trace?.DispatchCompleted(success);
            return success;
        }
        catch
        {
            trace?.DispatchCompleted(false);
            throw;
        }
    }

    internal static object BuildWireGuardProviderConnectRequest(int tunnelId, VpnWireGuardConnectAssociation association) => new
    {
        enabled = true,
        tunnel_id = tunnelId,
        via = new { type = "wireguard", configs = new[] { new { group_id = association.GroupId, id_list = new[] { association.ConfigId } } } }
    };

    internal static VpnWireGuardConnectAssociation? ParseWireGuardConnectAssociation(JsonElement tunnel)
    {
        if (ReadBool(tunnel, "enabled") || !tunnel.TryGetProperty("via", out JsonElement via) || via.ValueKind != JsonValueKind.Object ||
            !string.Equals(ReadString(via, "type"), "wireguard", StringComparison.OrdinalIgnoreCase) ||
            !via.TryGetProperty("configs", out JsonElement configs) || configs.ValueKind != JsonValueKind.Array || configs.GetArrayLength() != 1)
            return null;
        JsonElement config = configs[0];
        if (config.ValueKind != JsonValueKind.Object || ReadInt(config, "group_id") <= 0 ||
            !config.TryGetProperty("id_list", out JsonElement ids) || ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() != 1 ||
            !ids[0].TryGetInt32(out int configId) || configId <= 0)
            return null;
        return new VpnWireGuardConnectAssociation(ReadInt(config, "group_id"), configId);
    }

    internal sealed record VpnWireGuardConnectAssociation(int GroupId, int ConfigId);

    // Explicit, narrowly-scoped stock assignment DTO. MACs are retained only
    // in this internal request while preserving the router's selected-device
    // policy; they never enter a public model, log, or diagnostic export.
    internal async Task<VpnWireGuardAssignmentState?> GetWireGuardAssignmentStateAsync(int tunnelId, CancellationToken token)
    {
        string sid = await _sessionService.GetAdminTokenAsync(token).ConfigureAwait(false);
        using JsonDocument document = await _sessionService.CallVpnAsync(sid, VpnRpcOperation.GetTunnels, cancellationToken: token).ConfigureAwait(false);
        if (!TryResultArray(document.RootElement, "tunnels", out JsonElement tunnels)) return null;
        List<JsonElement> matches = tunnels.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object && ReadInt(item, "tunnel_id") == tunnelId).ToList();
        return matches.Count == 1 ? ParseWireGuardAssignmentState(matches[0]) : null;
    }

#if DEBUG
    internal async Task<VpnTunnelStructuralSnapshot?> GetVpnTunnelStructuralSnapshotAsync(int tunnelId, CancellationToken token)
    {
        try
        {
            string sid = await _sessionService.GetAdminTokenAsync(token).ConfigureAwait(false);
            using JsonDocument document = await _sessionService.CallVpnAsync(sid, VpnRpcOperation.GetTunnels, cancellationToken: token).ConfigureAwait(false);
            if (!TryResultArray(document.RootElement, "tunnels", out JsonElement tunnels)) return null;
            List<JsonElement> matches = tunnels.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object && ReadInt(item, "tunnel_id") == tunnelId).ToList();
            return matches.Count == 1 ? ParseVpnTunnelStructuralSnapshot(matches[0]) : null;
        }
        catch { return null; }
    }

    private static VpnTunnelStructuralSnapshot ParseVpnTunnelStructuralSnapshot(JsonElement tunnel)
    {
        bool viaPresent = tunnel.TryGetProperty("via", out JsonElement via);
        bool viaObject = viaPresent && via.ValueKind == JsonValueKind.Object;
        string viaType = viaObject ? ReadString(via, "type") : string.Empty;
        JsonElement configs = default;
        bool configsPresent = viaObject && via.TryGetProperty("configs", out configs);
        int viaConfigCount = configsPresent && configs.ValueKind == JsonValueKind.Array ? configs.GetArrayLength() : 0;
        JsonElement? firstConfig = configsPresent && configs.ValueKind == JsonValueKind.Array && viaConfigCount == 1 ? configs[0] : null;
        int? groupId = firstConfig is { ValueKind: JsonValueKind.Object } config ? ReadInt(config, "group_id") : null;
        int? configId = null;
        if (firstConfig is { ValueKind: JsonValueKind.Object } configObject && configObject.TryGetProperty("id_list", out JsonElement ids) && ids.ValueKind == JsonValueKind.Array && ids.GetArrayLength() == 1 && ids[0].TryGetInt32(out int parsedId)) configId = parsedId;
        bool fromPresent = tunnel.TryGetProperty("from", out JsonElement from) && from.ValueKind == JsonValueKind.Object;
        string fromType = fromPresent ? ReadString(from, "type") : string.Empty;
        int macCount = fromPresent && from.TryGetProperty("mac_list", out JsonElement macs) && macs.ValueKind == JsonValueKind.Array ? macs.GetArrayLength() : 0;
        bool toPresent = tunnel.TryGetProperty("to", out JsonElement to) && to.ValueKind == JsonValueKind.Object;
        return new VpnTunnelStructuralSnapshot(ReadInt(tunnel, "tunnel_id"), ReadBool(tunnel, "enabled"), viaPresent, viaType, viaConfigCount, groupId, configId, fromType, macCount, toPresent ? ReadString(to, "type") : string.Empty);
    }

    public sealed record VpnTunnelStructuralSnapshot(int TunnelId, bool Enabled, bool ViaPresent, string ViaType, int ViaConfigCount,
        int? ViaGroupId, int? ViaConfigId, string FromType, int MacListCount, string ToType);
#endif

    internal async Task<bool> AssignWireGuardProviderConfigAsync(VpnWireGuardAssignmentState state, int groupId, int configId, CancellationToken token, PiaApplyIdentityTrace? trace = null)
    {
        if (!state.IsSupportedRouting || state.Enabled || groupId <= 0 || configId <= 0) return false;
        WireGuardProviderAssignmentRequest request = BuildWireGuardProviderAssignmentRequest(state, groupId, configId);
        trace?.PrimarySerializationValidated(request.Via.Type == "wireguard" && request.Via.Configs.Length == 1 && request.Via.Configs[0].GroupId == groupId && request.Via.Configs[0].IdList.Length == 1 && request.Via.Configs[0].IdList[0] == configId && request.From.Type == "mac" && request.To.Type == "default");
        trace?.AssignmentIntended(request.Via.Configs[0].GroupId, request.Via.Configs[0].IdList[0]);
        trace?.AssignmentMutationIssued();
        string sid = await _sessionService.GetAdminTokenAsync(token).ConfigureAwait(false);
        trace?.PrimaryDispatchAttempted();
        trace?.AssignmentCallAttempted();
        try
        {
            using JsonDocument document = await _sessionService.CallAsync(sid, "vpn-client", "set_tunnel", request, token).ConfigureAwait(false);
            trace?.PrimaryDispatchCompleted();
            bool succeeded = !document.RootElement.TryGetProperty("error", out _) && document.RootElement.TryGetProperty("result", out _);
            trace?.PrimaryRpcSucceeded(succeeded);
            return succeeded;
        }
        catch
        {
            trace?.PrimaryDispatchCompleted();
            trace?.PrimaryRpcSucceeded(false);
            throw;
        }
    }

    internal static WireGuardProviderAssignmentRequest BuildWireGuardProviderAssignmentRequest(VpnWireGuardAssignmentState state, int groupId, int configId) => new(
        new WireGuardAssignmentFrom("mac", state.MacList.ToArray()),
        new WireGuardAssignmentTo("default"),
        state.TunnelId,
        new WireGuardAssignmentVia("wireguard", [new WireGuardAssignmentConfig(groupId, [configId])]));

    internal static VpnWireGuardAssignmentState? ParseWireGuardAssignmentState(JsonElement tunnel)
    {
        int tunnelId = ReadInt(tunnel, "tunnel_id");
        JsonElement via = tunnel.TryGetProperty("via", out JsonElement viaValue) && viaValue.ValueKind == JsonValueKind.Object ? viaValue : default;
        JsonElement from = tunnel.TryGetProperty("from", out JsonElement fromValue) && fromValue.ValueKind == JsonValueKind.Object ? fromValue : default;
        JsonElement to = tunnel.TryGetProperty("to", out JsonElement toValue) && toValue.ValueKind == JsonValueKind.Object ? toValue : default;
        string viaType = ReadString(via, "type");
        bool hasViaConfigs = via.TryGetProperty("configs", out JsonElement existingConfigs) && existingConfigs.ValueKind == JsonValueKind.Array && existingConfigs.GetArrayLength() > 0;
        bool clearedPrimaryVia = string.IsNullOrWhiteSpace(viaType) && !hasViaConfigs;
        if (tunnelId <= 0 || (!string.Equals(viaType, "wireguard", StringComparison.OrdinalIgnoreCase) && !clearedPrimaryVia)) return null;
        IReadOnlyList<VpnWireGuardConnectAssociation> references = via.TryGetProperty("configs", out JsonElement configs) && configs.ValueKind == JsonValueKind.Array
            ? configs.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).Select(item =>
                item.TryGetProperty("id_list", out JsonElement ids) && ids.ValueKind == JsonValueKind.Array && ids.GetArrayLength() == 1 && ids[0].TryGetInt32(out int id) && id > 0
                    ? new VpnWireGuardConnectAssociation(ReadInt(item, "group_id"), id) : null).Where(item => item is not null).Cast<VpnWireGuardConnectAssociation>().Where(item => item.GroupId > 0).ToList() : [];
        IReadOnlyList<string> macs = string.Equals(ReadString(from, "type"), "mac", StringComparison.OrdinalIgnoreCase) && from.TryGetProperty("mac_list", out JsonElement values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!.Trim()).Where(value => value.Length > 0).ToList() : [];
        // Stock GL.iNet leaves via empty when the disconnected Primary has no
        // current provider config.  Its mac/default routing is still an
        // authoritative, safe base for an explicit Primary assignment.
        bool supported = !ReadBool(tunnel, "enabled") && macs.Count > 0 &&
            string.Equals(ReadString(to, "type"), "default", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(viaType, "wireguard", StringComparison.OrdinalIgnoreCase) || clearedPrimaryVia);
        return new VpnWireGuardAssignmentState(tunnelId, ReadBool(tunnel, "enabled"), references, macs, "mac", "default", supported);
    }

    internal static bool VerifyWireGuardProviderAssignment(VpnWireGuardAssignmentState? before, VpnWireGuardAssignmentState? after, int expectedGroupId, int expectedConfigId) =>
        before is not null && after is not null && !after.Enabled && after.TunnelId == before.TunnelId && after.IsSupportedRouting &&
        before.FromType == after.FromType && before.ToType == after.ToType && before.MacList.SequenceEqual(after.MacList, StringComparer.OrdinalIgnoreCase) &&
        after.References.Count == 1 && after.References[0].GroupId == expectedGroupId && after.References[0].ConfigId == expectedConfigId;

    internal sealed record VpnWireGuardAssignmentState(int TunnelId, bool Enabled, IReadOnlyList<VpnWireGuardConnectAssociation> References, IReadOnlyList<string> MacList, string FromType, string ToType, bool IsSupportedRouting);

    internal sealed record WireGuardProviderAssignmentRequest(
        [property: JsonPropertyName("from")] WireGuardAssignmentFrom From,
        [property: JsonPropertyName("to")] WireGuardAssignmentTo To,
        [property: JsonPropertyName("tunnel_id")] int TunnelId,
        [property: JsonPropertyName("via")] WireGuardAssignmentVia Via);
    internal sealed record WireGuardAssignmentFrom([property: JsonPropertyName("type")] string Type, [property: JsonPropertyName("mac_list")] string[] MacList);
    internal sealed record WireGuardAssignmentTo([property: JsonPropertyName("type")] string Type);
    internal sealed record WireGuardAssignmentVia([property: JsonPropertyName("type")] string Type, [property: JsonPropertyName("configs")] WireGuardAssignmentConfig[] Configs);
    internal sealed record WireGuardAssignmentConfig([property: JsonPropertyName("group_id")] int GroupId, [property: JsonPropertyName("id_list")] int[] IdList);

    // Generic selected-device routing mutation. It intentionally supports
    // only the already-proven WireGuard/mac/default structural contract and
    // preserves the current via/config association read from get_tunnel.
    internal async Task<VpnWireGuardAssignmentState?> GetSelectedDeviceAssignmentStateAsync(int tunnelId, CancellationToken token) =>
        await GetWireGuardAssignmentStateAsync(tunnelId, token).ConfigureAwait(false);

    internal async Task<bool> SetSelectedDeviceAssignmentAsync(VpnWireGuardAssignmentState state, IReadOnlyList<string> macs, CancellationToken token)
    {
        if (state.Enabled || !state.IsSupportedRouting || state.References.Count == 0 || macs.Count == 0) return false;
        WireGuardProviderAssignmentRequest request = new(
            new WireGuardAssignmentFrom("mac", macs.ToArray()),
            new WireGuardAssignmentTo(state.ToType),
            state.TunnelId,
            new WireGuardAssignmentVia("wireguard", state.References.Select(reference => new WireGuardAssignmentConfig(reference.GroupId, [reference.ConfigId])).ToArray()));
        string sid = await _sessionService.GetAdminTokenAsync(token).ConfigureAwait(false);
        using JsonDocument document = await _sessionService.CallAsync(sid, "vpn-client", "set_tunnel", request, token).ConfigureAwait(false);
        return !document.RootElement.TryGetProperty("error", out _) && document.RootElement.TryGetProperty("result", out _);
    }

    // Temporary harness-only incident projection. It exposes structural kinds,
    // counts, and safe numeric config references only; raw tunnel JSON and all
    // sensitive routing/configuration values remain inside this method.
    internal async Task<IReadOnlyList<string>> GetVpnTunnelShapeLinesAsync(int tunnelId, CancellationToken token)
    {
        string sid = await _sessionService.GetAdminTokenAsync(token).ConfigureAwait(false);
        using JsonDocument document = await _sessionService.CallVpnAsync(sid, VpnRpcOperation.GetTunnels, cancellationToken: token).ConfigureAwait(false);
        if (!TryResultArray(document.RootElement, "tunnels", out JsonElement tunnels)) return ["TunnelList=UNAVAILABLE"];
        List<JsonElement> matches = tunnels.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object && ReadInt(item, "tunnel_id") == tunnelId).ToList();
        if (matches.Count != 1) return [$"TunnelId={tunnelId}", $"TunnelMatchCount={matches.Count}"];
        return DescribeTunnelShape(matches[0]);
    }

    // Temporary harness-only read-only projection. The current numeric tunnel
    // association is resolved only against this one get_config_list snapshot;
    // no raw config response or sensitive material leaves this method.
    internal async Task<IReadOnlyList<string>> GetVpnTunnelConfigResolutionLinesAsync(int tunnelId, CancellationToken token)
    {
        string sid = await _sessionService.GetAdminTokenAsync(token).ConfigureAwait(false);
        using JsonDocument document = await _sessionService.CallVpnAsync(sid, VpnRpcOperation.GetTunnels, cancellationToken: token).ConfigureAwait(false);
        if (!TryResultArray(document.RootElement, "tunnels", out JsonElement tunnels)) return ["GroupId=<unavailable>", "ConfigId=<unavailable>", "Name=<unavailable>", "Location=<unavailable>", "MATCH_COUNT=0"];
        List<JsonElement> matches = tunnels.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object && ReadInt(item, "tunnel_id") == tunnelId).ToList();
        if (matches.Count != 1 || !TryGetCurrentWireGuardReference(matches[0], out int groupId, out int configId))
            return ["GroupId=<unavailable>", "ConfigId=<unavailable>", "Name=<unavailable>", "Location=<unavailable>", "MATCH_COUNT=0"];

        IReadOnlyList<VpnProviderGeneratedConfigInfo> configs = await GetPiaGeneratedConfigsAsync(groupId, token).ConfigureAwait(false);
        List<VpnProviderGeneratedConfigInfo> configMatches = configs.Where(config => config.PeerId == configId).ToList();
        if (configMatches.Count != 1)
            return [$"GroupId={groupId}", $"ConfigId={configId}", "Name=<unavailable>", "Location=<unavailable>", $"MATCH_COUNT={configMatches.Count}"];
        return [$"GroupId={groupId}", $"ConfigId={configId}", $"Name={SafeConfigResolutionText(configMatches[0].Name)}", $"Location={SafeConfigResolutionText(configMatches[0].Location)}", "MATCH_COUNT=1"];
    }

    private static bool TryGetCurrentWireGuardReference(JsonElement tunnel, out int groupId, out int configId)
    {
        groupId = 0;
        configId = 0;
        if (!string.Equals(ReadNestedString(tunnel, "via", "type"), "wireguard", StringComparison.OrdinalIgnoreCase) ||
            !TryGet(tunnel, out JsonElement references, "via", "configs") || references.ValueKind != JsonValueKind.Array || references.GetArrayLength() != 1)
            return false;
        JsonElement reference = references[0];
        if (reference.ValueKind != JsonValueKind.Object || !reference.TryGetProperty("id_list", out JsonElement ids) ||
            ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() != 1 || !ids[0].TryGetInt32(out configId) || configId <= 0)
            return false;
        groupId = ReadInt(reference, "group_id");
        return groupId > 0;
    }

    private static string SafeConfigResolutionText(string value)
    {
        string normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length is > 0 and <= 256 ? normalized : "<unavailable>";
    }

    internal static IReadOnlyList<string> DescribeTunnelShape(JsonElement tunnel)
    {
        var lines = new List<string> { $"TunnelId={ReadInt(tunnel, "tunnel_id")}", $"Enabled={ReadBool(tunnel, "enabled")}" };
        DescribeObjectProperty(lines, tunnel, "via", "Via", includeType: true, includeMacList: false, includeConfigs: true);
        DescribeObjectProperty(lines, tunnel, "from", "From", includeType: true, includeMacList: true, includeConfigs: false);
        DescribeObjectProperty(lines, tunnel, "to", "To", includeType: true, includeMacList: false, includeConfigs: false);
        return lines;
    }

    private static void DescribeObjectProperty(List<string> lines, JsonElement parent, string property, string label, bool includeType, bool includeMacList, bool includeConfigs)
    {
        bool present = parent.TryGetProperty(property, out JsonElement value);
        lines.Add($"{label}Present={present}");
        if (!present) return;
        lines.Add($"{label}Kind={value.ValueKind}");
        if (value.ValueKind != JsonValueKind.Object) return;
        if (includeType)
        {
            bool typePresent = value.TryGetProperty("type", out JsonElement type);
            lines.Add($"{label}TypePresent={typePresent}");
            if (typePresent)
            {
                lines.Add($"{label}TypeKind={type.ValueKind}");
                if (type.ValueKind == JsonValueKind.String) lines.Add($"{label}Type={SafeTunnelType(type.GetString())}");
            }
        }
        if (includeMacList) DescribeArray(lines, value, "mac_list", "MacList", includeNumericValues: false);
        if (includeConfigs)
        {
            bool configsPresent = value.TryGetProperty("configs", out JsonElement configs);
            lines.Add($"ConfigsPresent={configsPresent}");
            if (!configsPresent) return;
            lines.Add($"ConfigsKind={configs.ValueKind}");
            if (configs.ValueKind != JsonValueKind.Array) return;
            lines.Add($"ConfigsCount={configs.GetArrayLength()}");
            int index = 0;
            foreach (JsonElement config in configs.EnumerateArray())
            {
                lines.Add($"Config[{index}].Kind={config.ValueKind}");
                if (config.ValueKind == JsonValueKind.Object)
                {
                    DescribeSafeNumber(lines, config, "group_id", $"Config[{index}].GroupId");
                    DescribeArray(lines, config, "id_list", $"Config[{index}].IdList", includeNumericValues: true);
                }
                index++;
            }
        }
    }

    private static void DescribeSafeNumber(List<string> lines, JsonElement parent, string property, string label)
    {
        bool present = parent.TryGetProperty(property, out JsonElement value);
        lines.Add($"{label}Present={present}");
        if (!present) return;
        lines.Add($"{label}Kind={value.ValueKind}");
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)) lines.Add($"{label}={number}");
    }

    private static void DescribeArray(List<string> lines, JsonElement parent, string property, string label, bool includeNumericValues)
    {
        bool present = parent.TryGetProperty(property, out JsonElement value);
        lines.Add($"{label}Present={present}");
        if (!present) return;
        lines.Add($"{label}Kind={value.ValueKind}");
        if (value.ValueKind != JsonValueKind.Array) return;
        lines.Add($"{label}Count={value.GetArrayLength()}");
        if (!includeNumericValues) return;
        int index = 0;
        foreach (JsonElement item in value.EnumerateArray())
        {
            lines.Add($"{label}[{index}].Kind={item.ValueKind}");
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out int number)) lines.Add($"{label}[{index}]={number}");
            index++;
        }
    }

    private static string SafeTunnelType(string? value) => value?.ToLowerInvariant() switch { "wireguard" => "wireguard", "mac" => "mac", "default" => "default", _ => "<unrecognized>" };

    // These are the captured stock-frontend contracts.  Credentials are held
    // only in PiaProviderCredentials while get_provider_server_list is in
    // flight; no public model, diagnostic, or exception receives them.
    internal async Task<VpnProviderGroupInfo?> GetPiaProviderGroupAsync(CancellationToken token)
    {
        string sid = await _sessionService.GetAdminTokenAsync(token).ConfigureAwait(false);
        using JsonDocument document = await _sessionService.CallAsync(sid, "wg-client", "get_group_list", new { }, token).ConfigureAwait(false);
        return ParsePiaProviderGroup(document.RootElement);
    }

    internal async Task<VpnProviderServerCatalogueResult> GetPiaProviderServerCatalogueAsync(int expectedGroupId, CancellationToken token)
    {
        PiaProviderCredentials? credentials = await ReadPiaProviderCredentialsAsync(token).ConfigureAwait(false);
        if (credentials is null || credentials.GroupId != expectedGroupId)
            return new VpnProviderServerCatalogueResult { GroupId = expectedGroupId, Message = "The PIA provider group is no longer available." };
        if (string.IsNullOrWhiteSpace(credentials.Username) || string.IsNullOrEmpty(credentials.Password))
            return new VpnProviderServerCatalogueResult { GroupId = expectedGroupId, Message = "Provider credentials are unavailable on this router." };

        string sid = await _sessionService.GetAdminTokenAsync(token).ConfigureAwait(false);
        using JsonDocument document = await _sessionService.CallAsync(sid, "wg_client", "get_provider_server_list",
            BuildProviderServerListRequest(credentials.GroupId, credentials.Username, credentials.Password), token).ConfigureAwait(false);
        if (document.RootElement.TryGetProperty("error", out _))
            return new VpnProviderServerCatalogueResult { GroupId = expectedGroupId, Message = "The router could not refresh the PIA server catalogue." };
        IReadOnlyList<VpnProviderServerInfo> servers = ParseProviderServerCatalogue(document.RootElement, expectedGroupId);
        return servers.Count == 0
            ? new VpnProviderServerCatalogueResult { GroupId = expectedGroupId, Message = "The router returned an empty or incomplete PIA server catalogue." }
            : new VpnProviderServerCatalogueResult { Success = true, GroupId = expectedGroupId, Servers = servers };
    }

    internal async Task<bool> GeneratePiaProviderConfigAsync(int groupId, VpnProviderServerInfo server, CancellationToken token, PiaApplyIdentityTrace? trace = null)
    {
        object request = BuildGenerateProviderConfigRequest(groupId, server);
        trace?.GenerateRequest(server.CountryName, server.CityName, server.Hostname);
        string sid = await _sessionService.GetAdminTokenAsync(token).ConfigureAwait(false);
        trace?.GenerationCallAttempted();
        using JsonDocument document = await _sessionService.CallAsync(sid, "wg_client", "generate_provider_config", request, token).ConfigureAwait(false);
        // The observed successful response is result: [].  Presence of result,
        // rather than its shape or count, is the proven success signal.
        bool succeeded = !document.RootElement.TryGetProperty("error", out _) && document.RootElement.TryGetProperty("result", out _);
        trace?.GenerationCallResult(succeeded);
        return succeeded;
    }

    internal async Task<IReadOnlyList<VpnProviderGeneratedConfigInfo>> GetPiaGeneratedConfigsAsync(int groupId, CancellationToken token)
    {
        string sid = await _sessionService.GetAdminTokenAsync(token).ConfigureAwait(false);
        using JsonDocument document = await _sessionService.CallAsync(sid, "wg-client", "get_config_list", new { group_id = groupId }, token).ConfigureAwait(false);
        return ParseGeneratedProviderConfigs(document.RootElement);
    }

    // Read-side reconciliation only. A provider-generated peer ID is used
    // solely to resolve this one authoritative tunnel snapshot against this
    // one authoritative get_config_list response; it is never cached as a
    // durable server identity.
    internal async Task<IReadOnlyList<VpnPiaTunnelConfigResolution>> GetCurrentPiaTunnelConfigResolutionsAsync(int groupId, CancellationToken token)
    {
        string sid = await _sessionService.GetAdminTokenAsync(token).ConfigureAwait(false);
        using JsonDocument document = await _sessionService.CallVpnAsync(sid, VpnRpcOperation.GetTunnels, cancellationToken: token).ConfigureAwait(false);
        if (!TryResultArray(document.RootElement, "tunnels", out JsonElement tunnels)) return [];
        IReadOnlyList<VpnProviderGeneratedConfigInfo> configs = await GetPiaGeneratedConfigsAsync(groupId, token).ConfigureAwait(false);
        var resolutions = new List<VpnPiaTunnelConfigResolution>();
        foreach (JsonElement tunnel in tunnels.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object))
        {
            if (!string.Equals(ReadNestedString(tunnel, "via", "type"), "wireguard", StringComparison.OrdinalIgnoreCase)) continue;
            if (!TryGet(tunnel, out JsonElement references, "via", "configs") || references.ValueKind != JsonValueKind.Array || references.GetArrayLength() != 1) continue;
            JsonElement reference = references[0];
            if (reference.ValueKind != JsonValueKind.Object || ReadInt(reference, "group_id") != groupId ||
                !reference.TryGetProperty("id_list", out JsonElement ids) || ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() != 1 || !ids[0].TryGetInt32(out int configId)) continue;
            List<VpnProviderGeneratedConfigInfo> matches = configs.Where(config => config.PeerId == configId).ToList();
            if (matches.Count == 1)
                resolutions.Add(new VpnPiaTunnelConfigResolution(ReadInt(tunnel, "tunnel_id"), groupId, matches[0].PeerId, matches[0].Name, matches[0].Location));
        }
        return resolutions;
    }

    // The configured candidates and the currently referenced candidate are
    // deliberately separate. A tunnel with several candidates but no single
    // authoritative reference is an actionable SelectionRequired state.
    internal async Task<IReadOnlyList<VpnPiaTunnelConfigSelection>> GetPiaTunnelConfigSelectionsAsync(int groupId, CancellationToken token)
    {
        string sid = await _sessionService.GetAdminTokenAsync(token).ConfigureAwait(false);
        using JsonDocument document = await _sessionService.CallVpnAsync(sid, VpnRpcOperation.GetTunnels, cancellationToken: token).ConfigureAwait(false);
        IReadOnlyList<VpnProviderGeneratedConfigInfo> configs = await GetPiaGeneratedConfigsAsync(groupId, token).ConfigureAwait(false);
        return ParsePiaTunnelConfigSelections(document.RootElement, groupId, configs);
    }

    // Temporary read-only incident projection. Values and identifiers are
    // intentionally omitted: this exists only to establish response shape
    // and count boundaries for the multiple-selection state.
    internal async Task<IReadOnlyList<string>> GetVpnConfigCandidateShapeLinesAsync(CancellationToken token)
    {
        VpnProviderGroupInfo? pia = await GetPiaProviderGroupAsync(token).ConfigureAwait(false);
        var lines = new List<string> { $"piaGroupResolved={(pia is not null ? "true" : "false")}" };
        if (pia is null) return lines;
        string sid = await _sessionService.GetAdminTokenAsync(token).ConfigureAwait(false);
        using JsonDocument tunnelDocument = await _sessionService.CallVpnAsync(sid, VpnRpcOperation.GetTunnels, cancellationToken: token).ConfigureAwait(false);
        if (!TryResultArray(tunnelDocument.RootElement, "tunnels", out JsonElement tunnels)) return [.. lines, "tunnelAvailable=false", "configListEntryCount=0", "candidateCount=0", "resolution=Unavailable", "currentResolved=false"];
        List<JsonElement> wireGuard = tunnels.EnumerateArray().Where(tunnel => tunnel.ValueKind == JsonValueKind.Object && string.Equals(ReadNestedString(tunnel, "via", "type"), "wireguard", StringComparison.OrdinalIgnoreCase)).ToList();
        lines.Add($"tunnelCount={tunnels.GetArrayLength()}");
        lines.Add($"wireGuardTunnelCount={wireGuard.Count}");
        int tunnelIndex = 0;
        foreach (JsonElement item in tunnels.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object))
        {
            string viaType = (ReadNestedString(item, "via", "type") ?? string.Empty).ToLowerInvariant();
            lines.Add($"tunnel[{tunnelIndex}].viaType={(viaType == "wireguard" ? "wireguard" : string.IsNullOrEmpty(viaType) ? "missing" : "other")}");
            lines.Add($"tunnel[{tunnelIndex}].enabled={ReadBool(item, "enabled").ToString().ToLowerInvariant()}");
            tunnelIndex++;
        }
        JsonElement? relevant = wireGuard.FirstOrDefault(tunnel => tunnel.TryGetProperty("via", out JsonElement via) && via.ValueKind == JsonValueKind.Object &&
            via.TryGetProperty("configs", out JsonElement refs) && refs.ValueKind == JsonValueKind.Array && refs.EnumerateArray().Any(reference => reference.ValueKind == JsonValueKind.Object && ReadInt(reference, "group_id") == pia.GroupId));
        JsonElement? tunnel = relevant ?? wireGuard.FirstOrDefault();
        bool available = tunnel is { ValueKind: JsonValueKind.Object };
        JsonElement primaryTunnel = available ? tunnel!.Value : tunnels.EnumerateArray().FirstOrDefault(item => item.ValueKind == JsonValueKind.Object);
        bool primaryAvailable = primaryTunnel.ValueKind == JsonValueKind.Object;
        int referenceCount = primaryAvailable && primaryTunnel.TryGetProperty("via", out JsonElement tunnelVia) && tunnelVia.ValueKind == JsonValueKind.Object && tunnelVia.TryGetProperty("configs", out JsonElement refs) && refs.ValueKind == JsonValueKind.Array ? refs.GetArrayLength() : 0;
        bool enabled = primaryAvailable && ReadBool(primaryTunnel, "enabled");
        string primaryVia = primaryAvailable ? (ReadNestedString(primaryTunnel, "via", "type") ?? string.Empty).ToLowerInvariant() : string.Empty;
        using JsonDocument configDocument = await _sessionService.CallAsync(sid, "wg-client", "get_config_list", new { group_id = pia.GroupId }, token).ConfigureAwait(false);
        int rawCount = TryGet(configDocument.RootElement, out JsonElement result, "result") && result.TryGetProperty("peers", out JsonElement peers) && peers.ValueKind == JsonValueKind.Array ? peers.GetArrayLength() : 0;
        IReadOnlyList<VpnProviderGeneratedConfigInfo> parsed = ParseGeneratedProviderConfigs(configDocument.RootElement);
        IReadOnlyList<VpnPiaTunnelConfigSelection> selections = ParsePiaTunnelConfigSelections(tunnelDocument.RootElement, pia.GroupId, parsed);
        VpnPiaTunnelConfigSelection? selection = selections.FirstOrDefault();
        lines.Add($"tunnelAvailable={primaryAvailable.ToString().ToLowerInvariant()}");
        lines.Add($"tunnelEnabled={enabled.ToString().ToLowerInvariant()}");
        lines.Add("tunnelViaType=" + (primaryVia == "wireguard" ? "wireguard" : string.IsNullOrEmpty(primaryVia) ? "missing" : "other"));
        lines.Add($"tunnelConfigReferenceCount={referenceCount}");
        lines.Add($"configListEntryCount={rawCount}");
        lines.Add($"parsedEntryCount={parsed.Count}");
        lines.Add($"validCandidateCount={parsed.Count}");
        lines.Add($"candidateCount={selection?.Candidates.Count ?? 0}");
        lines.Add($"resolution={selection?.State.ToString() ?? "Unavailable"}");
        lines.Add($"currentResolved={(selection?.State == VpnServerConfigResolutionState.Resolved ? "true" : "false")}");
        return lines;
    }

    internal static IReadOnlyList<VpnPiaTunnelConfigSelection> ParsePiaTunnelConfigSelections(JsonElement root, int groupId, IReadOnlyList<VpnProviderGeneratedConfigInfo> configs)
    {
        if (groupId <= 0 || !TryResultArray(root, "tunnels", out JsonElement tunnels)) return [];
        IReadOnlyList<VpnProviderServerInfo> candidates = configs
            .Select(config => new VpnProviderServerInfo
            {
                GroupId = groupId,
                CountryName = config.Location,
                CityName = config.Name,
                ExistingConfigId = config.PeerId
            })
            .ToList();
        var selections = new List<VpnPiaTunnelConfigSelection>();
        foreach (JsonElement tunnel in tunnels.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object))
        {
            if (!string.Equals(ReadNestedString(tunnel, "via", "type"), "wireguard", StringComparison.OrdinalIgnoreCase) ||
                !TryGet(tunnel, out JsonElement references, "via", "configs") || references.ValueKind != JsonValueKind.Array)
                continue;
            List<JsonElement> groupReferences = references.EnumerateArray()
                .Where(reference => reference.ValueKind == JsonValueKind.Object && ReadInt(reference, "group_id") == groupId).ToList();
            if (groupReferences.Count == 0) continue;
            List<int> referenceIds = groupReferences
                .SelectMany(reference => reference.TryGetProperty("id_list", out JsonElement ids) && ids.ValueKind == JsonValueKind.Array
                    ? ids.EnumerateArray().Where(id => id.TryGetInt32(out int value) && value > 0).Select(id => id.GetInt32()) : [])
                .Distinct().ToList();
            List<VpnProviderGeneratedConfigInfo> currentMatches = configs.Where(config => referenceIds.Contains(config.PeerId)).ToList();
            VpnProviderGeneratedConfigInfo? current = referenceIds.Count == 1 && currentMatches.Count == 1 ? currentMatches[0] : null;
            VpnServerConfigResolutionState state = current is not null
                ? VpnServerConfigResolutionState.Resolved
                : candidates.Count > 0 ? VpnServerConfigResolutionState.SelectionRequired : VpnServerConfigResolutionState.Unavailable;
            selections.Add(new VpnPiaTunnelConfigSelection
            {
                TunnelId = ReadInt(tunnel, "tunnel_id"), GroupId = groupId, State = state, Candidates = candidates,
                CurrentServer = current is null ? null : new VpnProviderServerInfo
                {
                    GroupId = groupId, CountryName = current.Location, CityName = current.Name, ExistingConfigId = current.PeerId
                }
            });
        }
        // Stock GL.iNet can clear the Primary tunnel's via object while it
        // retains multiple unapplied provider configs. With one authoritative
        // disconnected tunnel and one uniquely identified PIA group, that is
        // sufficient evidence for an actionable selection requirement, but
        // not for a current-config inference.
        if (selections.Count == 0 && candidates.Count > 0)
        {
            List<JsonElement> allTunnels = tunnels.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).ToList();
            if (allTunnels.Count == 1 && !ReadBool(allTunnels[0], "enabled") && string.IsNullOrWhiteSpace(ReadNestedString(allTunnels[0], "via", "type")))
            {
                selections.Add(new VpnPiaTunnelConfigSelection
                {
                    TunnelId = ReadInt(allTunnels[0], "tunnel_id"), GroupId = groupId,
                    State = VpnServerConfigResolutionState.SelectionRequired, Candidates = candidates
                });
            }
        }
        return selections;
    }

    internal sealed record VpnPiaTunnelConfigResolution(int TunnelId, int GroupId, int ConfigId, string Name, string Location);

    private async Task<PiaProviderCredentials?> ReadPiaProviderCredentialsAsync(CancellationToken token)
    {
        string sid = await _sessionService.GetAdminTokenAsync(token).ConfigureAwait(false);
        using JsonDocument document = await _sessionService.CallAsync(sid, "wg-client", "get_group_list", new { }, token).ConfigureAwait(false);
        if (!TryGet(document.RootElement, out JsonElement groups, "result", "groups") || groups.ValueKind != JsonValueKind.Array) return null;
        List<JsonElement> matches = groups.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.Object && IsPiaProviderGroup(value))
            .ToList();
        return matches.Count == 1
            ? new PiaProviderCredentials(ReadInt(matches[0], "group_id"), ReadString(matches[0], "username"), ReadString(matches[0], "password"))
            : null;
    }

    internal static VpnProviderGroupInfo? ParsePiaProviderGroup(JsonElement root)
    {
        if (!TryGet(root, out JsonElement groups, "result", "groups") || groups.ValueKind != JsonValueKind.Array) return null;
        List<JsonElement> matches = groups.EnumerateArray()
            .Where(group => group.ValueKind == JsonValueKind.Object && IsPiaProviderGroup(group))
            .ToList();
        return matches.Count == 1 ? new VpnProviderGroupInfo { GroupId = ReadInt(matches[0], "group_id") } : null;
    }

    private static bool IsPiaProviderGroup(JsonElement group) =>
        string.Equals(ReadString(group, "group_name"), "PIA", StringComparison.OrdinalIgnoreCase) && ReadInt(group, "group_type") == 1 && ReadInt(group, "group_id") > 0;

    internal static object BuildProviderServerListRequest(int groupId, string username, string password) => new { group_id = groupId, username, password };
    internal static object BuildGenerateProviderConfigRequest(int groupId, VpnProviderServerInfo server) => new
    {
        group_id = groupId,
        server_info = new[] { new { country_name = server.CountryName, cities = new[] { new { city_name = server.CityName, hostname = new[] { server.Hostname } } } } }
    };

    internal static IReadOnlyList<VpnProviderServerInfo> ParseProviderServerCatalogue(JsonElement root, int groupId)
    {
        JsonElement value = TryGet(root, out JsonElement result, "result") ? result : root;
        if (!TryGet(value, out JsonElement countries, "server_info") || countries.ValueKind != JsonValueKind.Array) return [];
        var servers = new List<VpnProviderServerInfo>();
        foreach (JsonElement country in countries.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object))
        {
            string countryName = ReadString(country, "country_name");
            if (string.IsNullOrWhiteSpace(countryName) || !country.TryGetProperty("cities", out JsonElement cities) || cities.ValueKind != JsonValueKind.Array) continue;
            foreach (JsonElement city in cities.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object))
            {
                string cityName = ReadString(city, "city_name");
                if (string.IsNullOrWhiteSpace(cityName) || !city.TryGetProperty("hostname", out JsonElement hosts) || hosts.ValueKind != JsonValueKind.Array) continue;
                foreach (JsonElement host in hosts.EnumerateArray())
                    if (host.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(host.GetString()))
                        servers.Add(new VpnProviderServerInfo { GroupId = groupId, CountryName = countryName, CityName = cityName, Hostname = host.GetString()!.Trim() });
            }
        }
        return servers.GroupBy(server => server.CatalogueIdentity, StringComparer.Ordinal).Select(group => group.First()).OrderBy(server => server.CountryName).ThenBy(server => server.CityName).ThenBy(server => server.Hostname).ToList();
    }

    internal static IReadOnlyList<VpnProviderGeneratedConfigInfo> ParseGeneratedProviderConfigs(JsonElement root)
    {
        if (!TryGet(root, out JsonElement result, "result") || !TryGet(result, out JsonElement peers, "peers") || peers.ValueKind != JsonValueKind.Array) return [];
        return peers.EnumerateArray().Where(peer => peer.ValueKind == JsonValueKind.Object).Select(peer => new VpnProviderGeneratedConfigInfo
        {
            PeerId = ReadInt(peer, "peer_id"), Name = ReadString(peer, "name"), Location = ReadString(peer, "location"), Endpoint = ReadString(peer, "end_point")
        }).Where(peer => peer.PeerId > 0 && !string.IsNullOrWhiteSpace(peer.Name)).ToList();
    }

    internal static bool GeneratedConfigMatchesSelection(VpnProviderGeneratedConfigInfo config, VpnProviderServerInfo selection) =>
        string.Equals(config.Name, selection.Hostname, StringComparison.OrdinalIgnoreCase) &&
        (!string.IsNullOrWhiteSpace(config.Location) && (config.Location.Contains(selection.CityName, StringComparison.OrdinalIgnoreCase) || config.Location.Contains(selection.CountryName, StringComparison.OrdinalIgnoreCase)));

    private sealed record PiaProviderCredentials(int GroupId, string Username, string Password);

    // This intentionally requests only the latest-handshake counters. It
    // never reads WireGuard configuration, private keys, or peer details.
    // The command itself is limited to six seconds; a new SSH connection can
    // additionally consume the existing five-second connection timeout.
    internal async Task<VpnWireGuardHandshakeSnapshot> GetWireGuardHandshakeSnapshotAsync(string? interfaceName, CancellationToken token)
    {
        if (!IsSafeWireGuardInterfaceName(interfaceName)) return new VpnWireGuardHandshakeSnapshot();

        string output = await RunReadOnlySshCommandAsync(
            $"wg show {interfaceName} latest-handshakes 2>/dev/null", TimeSpan.FromSeconds(6), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return ParseWireGuardHandshakeSnapshot(output);
    }

    internal static bool IsSafeWireGuardInterfaceName(string? interfaceName) =>
        !string.IsNullOrWhiteSpace(interfaceName) && Regex.IsMatch(interfaceName, "\\A[A-Za-z0-9_.-]{1,32}\\z");

    internal static VpnWireGuardHandshakeSnapshot ParseWireGuardHandshakeSnapshot(string? output)
    {
        if (string.IsNullOrWhiteSpace(output) || output.StartsWith("SSH_", StringComparison.Ordinal))
            return new VpnWireGuardHandshakeSnapshot();

        var timestamps = new List<long>();
        foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = line.LastIndexOf('\t');
            if (separator < 0 || !long.TryParse(line[(separator + 1)..].Trim(), out long latestHandshake) || latestHandshake < 0)
                return new VpnWireGuardHandshakeSnapshot();
            timestamps.Add(latestHandshake);
        }

        return timestamps.Count == 0
            ? new VpnWireGuardHandshakeSnapshot()
            : new VpnWireGuardHandshakeSnapshot { IsAvailable = true, LatestHandshakeTimestamps = timestamps.OrderBy(value => value).ToList() };
    }

    internal static VpnWireGuardHandshakeState CompareWireGuardHandshakeSnapshots(
        VpnWireGuardHandshakeSnapshot baseline, VpnWireGuardHandshakeSnapshot current)
    {
        if (!baseline.IsAvailable || !current.IsAvailable || baseline.LatestHandshakeTimestamps.Count != current.LatestHandshakeTimestamps.Count)
            return VpnWireGuardHandshakeState.Unavailable;

        bool advanced = false;
        for (int index = 0; index < baseline.LatestHandshakeTimestamps.Count; index++)
        {
            long before = baseline.LatestHandshakeTimestamps[index];
            long after = current.LatestHandshakeTimestamps[index];
            if (after < before) return VpnWireGuardHandshakeState.Unavailable;
            if (after > before) advanced = true;
        }
        return advanced ? VpnWireGuardHandshakeState.Successful : VpnWireGuardHandshakeState.NoHandshake;
    }

    private static VpnTunnelInfo ParseTunnel(JsonElement tunnel)
    {
        JsonElement via = tunnel.TryGetProperty("via", out JsonElement viaValue) && viaValue.ValueKind == JsonValueKind.Object ? viaValue : default;
        JsonElement options = tunnel.TryGetProperty("options", out JsonElement optionsValue) && optionsValue.ValueKind == JsonValueKind.Object ? optionsValue : default;
        List<int> groups = via.ValueKind == JsonValueKind.Object && via.TryGetProperty("configs", out JsonElement configs) && configs.ValueKind == JsonValueKind.Array
            ? configs.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).Select(item => ReadInt(item, "group_id")).Where(id => id > 0).Distinct().ToList() : [];
        string protocolRaw = ReadString(via, "type");
        IReadOnlyList<string> routingIdentities = ReadNestedStringArray(tunnel, "from", "mac_list")
            .Select(ClientIdentity.NormalizeHexMac)
            .Where(identity => identity.Length == 12)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new VpnTunnelInfo { Id = ReadString(tunnel, "id"), TunnelId = ReadInt(tunnel, "tunnel_id"), Name = ReadString(tunnel, "name", "Unnamed tunnel"), Enabled = ReadBool(tunnel, "enabled"), KillSwitch = ReadBool(tunnel, "killswitch"), Protocol = protocolRaw.Equals("wireguard", StringComparison.OrdinalIgnoreCase) ? "WireGuard" : protocolRaw.Equals("openvpn", StringComparison.OrdinalIgnoreCase) ? "OpenVPN" : "Unknown", InterfaceName = ReadString(via, "via"), ProfileGroupIds = groups, FromType = ReadNestedString(tunnel, "from", "type"), ToType = ReadNestedString(tunnel, "to", "type"), RoutingDeviceIdentities = routingIdentities, Masquerade = ReadNullableBool(options, "masq"), LocalAccess = ReadNullableBool(options, "local_access"), ServicePolicy = ReadString(options, "service_policy") };
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
    private static IReadOnlyList<string> ReadNestedStringArray(JsonElement value, string parent, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(parent, out JsonElement nested) && nested.ValueKind == JsonValueKind.Object &&
        nested.TryGetProperty(property, out JsonElement array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString() ?? string.Empty).ToList()
            : [];
}
