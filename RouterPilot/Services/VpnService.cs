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
    private readonly ClientInventoryState _clientInventory;
    private readonly IClientDisplayNameService _clientNames;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public VpnService(IRouterManagerProvider provider, TimelineService timeline, ClientInventoryState clientInventory, IClientDisplayNameService clientNames)
    {
        _provider = provider;
        _timeline = timeline;
        _clientInventory = clientInventory;
        _clientNames = clientNames;
    }
    public async Task<VpnInventorySnapshot> GetInventoryAsync(CancellationToken token)
    {
        RouterManager manager = await _provider.GetRouterManagerAsync(token);
        IReadOnlyList<VpnTunnelInfo> tunnels = await manager.GetVpnTunnelsAsync(token);
        IReadOnlyList<VpnClientProfileInfo> profiles;
        VpnProfileInventoryState inventoryState;
        try
        {
            (profiles, inventoryState) = await manager.GetVpnProfilesAsync(tunnels, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"VPN profile inventory unavailable; preserving tunnel inventory ({DiagnosticRedactor.FailureCategory(exception)}).");
            profiles = [];
            inventoryState = VpnProfileInventoryState.Unavailable;
        }
        return new VpnInventorySnapshot { Tunnels = tunnels, Profiles = Correlate(tunnels, profiles), ProfileInventoryState = inventoryState };
    }

    /// <summary>
    /// Optional read-only enrichment. It is deliberately separate from the
    /// primary VPN inventory so an SSH delay or failure can never decide a
    /// tunnel's connection transition.
    /// </summary>
    public async Task<IReadOnlyList<VpnTunnelInfo>> EnrichRoutingPolicyAsync(IReadOnlyList<VpnTunnelInfo> tunnels, CancellationToken token)
    {
        VpnRoutingPolicySnapshot routingPolicy;
        IReadOnlyDictionary<string, string> persistentNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        GlClientPresenceSnapshot presence = GlClientPresenceSnapshot.Unavailable;
        try
        {
            RouterManager manager = await _provider.GetRouterManagerAsync(token).ConfigureAwait(false);
            routingPolicy = await manager.GetVpnRoutingPolicyAsync(token).ConfigureAwait(false);
            if (routingPolicy.Tunnels.Any(policy => policy.DeviceIdentities.Count > 0))
            {
                Task<IReadOnlyDictionary<string, string>> namesTask = manager.GetPersistentClientNamesAsync(token);
                Task<GlClientPresenceSnapshot> presenceTask = manager.GetGlClientPresenceSnapshotAsync(token);
                try
                {
                    persistentNames = await namesTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Debug.WriteLine($"VPN persistent client names unavailable; preserving routing assignments ({DiagnosticRedactor.FailureCategory(exception)}).");
                }
                try
                {
                    presence = await presenceTask.ConfigureAwait(false);
                    if (presence.IsAvailable)
                        _clientInventory.UpdateAuthoritativePresence(presence.Presence);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Debug.WriteLine($"VPN client presence unavailable; preserving routing assignments ({DiagnosticRedactor.FailureCategory(exception)}).");
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"VPN routing policy unavailable; preserving primary tunnel state ({DiagnosticRedactor.FailureCategory(exception)}).");
            routingPolicy = new VpnRoutingPolicySnapshot { State = VpnRoutingPolicyState.Unavailable };
        }
        return ApplyRoutingPolicy(tunnels, routingPolicy,
            new Dictionary<string, ClientInfo>(_clientInventory.Snapshot, StringComparer.OrdinalIgnoreCase), _clientNames,
            persistentNames, presence.IsAvailable ? _clientInventory.PresenceSnapshot : null);
    }
    public async Task<IReadOnlyList<VpnTunnelInfo>> GetTunnelsAsync(CancellationToken token) => await (await _provider.GetRouterManagerAsync(token)).GetVpnTunnelsAsync(token);
    public async Task<IReadOnlyList<VpnClientProfileInfo>> GetClientProfilesAsync(CancellationToken token)
    {
        RouterManager manager = await _provider.GetRouterManagerAsync(token);
        IReadOnlyList<VpnTunnelInfo> tunnels = await manager.GetVpnTunnelsAsync(token);
        IReadOnlyList<VpnClientProfileInfo> profiles;
        try
        {
            (profiles, _) = await manager.GetVpnProfilesAsync(tunnels, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"VPN profile inventory unavailable; preserving tunnel inventory ({DiagnosticRedactor.FailureCategory(exception)}).");
            profiles = [];
        }
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

            // Once the router has accepted the write, restore the exact
            // baseline value regardless of the requested direction.  A
            // failed read-back must never leave a disable mutation applied
            // simply because the old implementation only rolled back
            // enable attempts.
            bool rollbackAttempted = applied;
            bool rollbackVerified = false;
            if (rollbackAttempted)
            {
                bool rollbackApplied = await manager.SetVpnTunnelEnabledAsync(tunnelId, original.Enabled, token);
                rollbackVerified = rollbackApplied && await ReadBackAsync(manager, tunnelId, original.Enabled, token) is not null;
            }
            return await CompleteAsync(new VpnOperationResult { TunnelId = tunnelId, FailureCategory = "VerificationFailed", Message = "RouterPilot could not verify the VPN tunnel state.", RollbackAttempted = rollbackAttempted, RollbackVerified = rollbackVerified }, original, enabled, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
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
        try { await _timeline.AddAsync(new TimelineEvent { Category = TimelineCategory.Router, EventType = result.Success ? TimelineEventType.MaintenanceCompleted : TimelineEventType.MaintenanceFailed, Title = result.Success ? $"VPN tunnel {(enabled ? "enabled" : "disabled")}" : $"Failed to {(enabled ? "enable" : "disable")} VPN tunnel", Message = tunnel?.Name ?? "VPN tunnel", Severity = result.Success ? TimelineSeverity.Success : TimelineSeverity.Warning, Source = "VPN" }, token); } catch (OperationCanceledException) when (token.IsCancellationRequested) { } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Unable to record VPN timeline entry ({DiagnosticRedactor.FailureCategory(ex)})."); }
        return result;
    }

    internal static IReadOnlyList<VpnClientProfileInfo> Correlate(IReadOnlyList<VpnTunnelInfo> tunnels, IReadOnlyList<VpnClientProfileInfo> profiles) => profiles.Select(profile =>
    {
        List<VpnTunnelInfo> usedBy = tunnels.Where(tunnel => tunnel.ProfileGroupIds.Contains(profile.GroupId)).ToList();
        return new VpnClientProfileInfo { GroupId = profile.GroupId, Name = profile.Name, Protocol = profile.Protocol, IsUsedByTunnel = usedBy.Count > 0, TunnelIds = usedBy.Select(tunnel => tunnel.TunnelId).ToList(), UsedByDisplay = usedBy.Count == 0 ? "Not used" : string.Join(", ", usedBy.Select(tunnel => tunnel.Name)), ServerConfigCount = profile.ServerConfigCount, CurrentPeerId = profile.CurrentPeerId, CurrentLocation = profile.CurrentLocation, ActivityState = profile.ActivityState };
    }).ToList();

    internal static IReadOnlyList<VpnTunnelInfo> ApplyRoutingPolicy(
        IReadOnlyList<VpnTunnelInfo> tunnels,
        VpnRoutingPolicySnapshot snapshot,
        IReadOnlyDictionary<string, ClientInfo> clients,
        IClientDisplayNameService names,
        IReadOnlyDictionary<string, string>? persistentNames = null,
        IReadOnlyDictionary<string, bool>? presence = null)
    {
        var policies = snapshot.Tunnels.ToDictionary(policy => policy.TunnelId);
        return tunnels.Select(tunnel =>
        {
            policies.TryGetValue(tunnel.TunnelId, out VpnTunnelRoutingPolicy? policy);
            IReadOnlyList<string> identities = policy?.DeviceIdentities ?? [];
            IReadOnlyList<VpnRoutingDeviceAssignment> devices = identities.Select((identity, index) =>
            {
                string normalizedIdentity = ClientIdentity.NormalizeHexMac(identity);
                bool resolved = clients.TryGetValue(normalizedIdentity, out ClientInfo? client);
                string persistentName = persistentNames is not null && persistentNames.TryGetValue(normalizedIdentity, out string? storedName) ? storedName : string.Empty;
                string displayName = resolved ? names.Resolve(client!) : names.Resolve(normalizedIdentity, null, persistentName);
                bool hasDisplayName = !string.IsNullOrWhiteSpace(displayName) && displayName is not "-" and not "â€”";
                VpnRoutingDevicePresence devicePresence = presence is not null && presence.TryGetValue(normalizedIdentity, out bool online)
                    ? online ? VpnRoutingDevicePresence.Online : VpnRoutingDevicePresence.Offline
                    : VpnRoutingDevicePresence.Unknown;
                VpnRoutingDeviceAssignment assignment = new()
                {
                    ClientIdentity = identity,
                    // Do not expose a raw MAC when a device has not appeared
                    // in the existing bulk inventory. The stable identifier is
                    // retained for correlation but stays out of the UI.
                    DisplayName = hasDisplayName ? displayName : $"Unknown device {index + 1}",
                    IsResolved = resolved || hasDisplayName,
                    Presence = devicePresence
                };
                return VpnRoutingDeviceAssignment.WithTunnelConnection(assignment, tunnel.LiveStatus?.IsConnected == true);
            }).ToList();
            return CopyWithRouting(tunnel, snapshot.State, policy?.Scope ?? VpnInternetRoutingScope.Unknown, identities, devices);
        }).ToList();
    }

    internal static VpnTunnelInfo CopyWithRouting(VpnTunnelInfo tunnel, VpnRoutingPolicyState state, VpnInternetRoutingScope scope,
        IReadOnlyList<string> identities, IReadOnlyList<VpnRoutingDeviceAssignment> devices) => new()
    {
        Id = tunnel.Id, TunnelId = tunnel.TunnelId, Name = tunnel.Name, Enabled = tunnel.Enabled, KillSwitch = tunnel.KillSwitch,
        Protocol = tunnel.Protocol, InterfaceName = tunnel.InterfaceName, ProfileGroupIds = tunnel.ProfileGroupIds,
        SelectedProfileGroupId = tunnel.SelectedProfileGroupId, SelectedProfileGroupExists = tunnel.SelectedProfileGroupExists,
        ActiveProfileName = tunnel.ActiveProfileName, LinkedProfilesDisplay = tunnel.LinkedProfilesDisplay,
        ConfiguredProfileName = tunnel.ConfiguredProfileName, ConfiguredLocation = tunnel.ConfiguredLocation,
        FromType = tunnel.FromType, ToType = tunnel.ToType, Masquerade = tunnel.Masquerade, LocalAccess = tunnel.LocalAccess,
        ServicePolicy = tunnel.ServicePolicy, ServerConfigCount = tunnel.ServerConfigCount, LiveStatus = tunnel.LiveStatus,
        ConfigurationHealth = tunnel.ConfigurationHealth, HasConnectionAttemptFailure = tunnel.HasConnectionAttemptFailure,
        TransitionIntent = tunnel.TransitionIntent, RoutingPolicyState = state, InternetRoutingScope = scope,
        RoutingDeviceIdentities = identities, RoutingDevices = devices
    };
    private static VpnOperationResult Failure(int tunnelId, string category) => new() { TunnelId = tunnelId, FailureCategory = category, Message = "RouterPilot could not update the VPN tunnel." };
}
