using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private readonly IRouterPilotDevLog _devLog;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public VpnService(IRouterManagerProvider provider, TimelineService timeline, ClientInventoryState clientInventory, IClientDisplayNameService clientNames, IRouterPilotDevLog devLog)
    {
        _provider = provider;
        _timeline = timeline;
        _clientInventory = clientInventory;
        _clientNames = clientNames;
        _devLog = devLog;
    }
    public async Task<VpnInventorySnapshot> GetInventoryAsync(CancellationToken token)
    {
        string operation = _devLog.CreateOperationId("VPN-REFRESH");
        Stopwatch timing = Stopwatch.StartNew();
        _devLog.Write(RouterPilotDevLogCategory.VPN, operation, "Refresh.Start", RouterPilotDevLogLevel.Info);
#if DEBUG
        Debug.WriteLine("PIA_LINK_DIAGNOSTIC_VERSION=1");
#endif
        RouterManager manager = await _provider.GetRouterManagerAsync(token);
        IReadOnlyList<VpnTunnelInfo> tunnels = await manager.GetVpnTunnelsAsync(token);
#if DEBUG
        VpnTunnelInfo? initialPrimary = tunnels.SingleOrDefault(tunnel => tunnel.TunnelId == 38);
        Debug.WriteLine($"PIA_LINK.InitialTunnelCount={tunnels.Count}");
        Debug.WriteLine($"PIA_LINK.InitialPrimaryFound={(initialPrimary is null ? "NO" : "YES")}");
        Debug.WriteLine($"PIA_LINK.InitialProtocol={initialPrimary?.Protocol ?? "unavailable"}");
        Debug.WriteLine($"PIA_LINK.InitialGroupCount={initialPrimary?.ProfileGroupIds.Count ?? 0}");
#endif
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
        VpnProviderGroupInfo? piaGroup = null;
        _devLog.Write(RouterPilotDevLogCategory.VPN, operation, "ProviderDiscovery.Start", RouterPilotDevLogLevel.Debug);
        try { piaGroup = await manager.GetPiaProviderGroupAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine($"PIA provider discovery unavailable ({DiagnosticRedactor.FailureCategory(exception)})."); }
        _devLog.Write(RouterPilotDevLogCategory.VPN, operation,
            $"ProviderDiscovery.Result piaDetected={(piaGroup is not null ? "true" : "false")}", RouterPilotDevLogLevel.Debug);
        _devLog.Write(piaGroup is null ? RouterPilotDevLogCategory.VPN : RouterPilotDevLogCategory.PIA, operation,
            piaGroup is null ? "PiaServerManagement.Unavailable reason=PiaProviderNotDetected" : $"ProviderDetected serverManagement=true group={piaGroup.GroupId}",
            piaGroup is null ? RouterPilotDevLogLevel.Debug : RouterPilotDevLogLevel.Info);
        if (piaGroup is not null)
        {
#if DEBUG
            Debug.WriteLine("PIA_LINK.PiaGroupInventoryFound=YES");
            Debug.WriteLine($"PIA_LINK.InitialHasPiaGroup={(tunnels.Any(tunnel => tunnel.ProfileGroupIds.Contains(piaGroup.GroupId)) ? "YES" : "NO")}");
            Debug.WriteLine("PIA_LINK.ResolutionAttempted=YES");
#endif
            try
            {
                IReadOnlyList<RouterManager.VpnPiaTunnelConfigResolution> resolutions = await manager.GetCurrentPiaTunnelConfigResolutionsAsync(piaGroup.GroupId, token).ConfigureAwait(false);
#if DEBUG
                Debug.WriteLine($"PIA_LINK.ResolutionSucceeded={(resolutions.Count > 0 ? "YES" : "NO")}");
                RouterManager.VpnPiaTunnelConfigResolution? firstResolution = resolutions.Count == 1 ? resolutions[0] : null;
                Debug.WriteLine($"PIA_LINK.ResolvedTunnelId={(firstResolution?.TunnelId.ToString() ?? "unavailable")}");
                Debug.WriteLine($"PIA_LINK.ResolvedGroupId={(firstResolution?.GroupId.ToString() ?? "unavailable")}");
                Debug.WriteLine($"PIA_LINK.ResolvedConfigId={(firstResolution?.ConfigId.ToString() ?? "unavailable")}");
#endif
                // The router has been observed returning an incomplete via
                // object transiently. If the independent authoritative
                // resolution read proves the PIA association but the initial
                // tunnel snapshot did not, accept a same-sized fresh snapshot
                // only when it explicitly contains that provider group.
                if (resolutions.Count > 0 && !tunnels.Any(tunnel => tunnel.ProfileGroupIds.Contains(piaGroup.GroupId)))
                {
#if DEBUG
                    Debug.WriteLine("PIA_LINK.RecoveryReadRequired=YES");
                    Debug.WriteLine("PIA_LINK.RecoveryReadAttempted=YES");
#endif
                    IReadOnlyList<VpnTunnelInfo> refreshedTunnels;
                    try
                    {
                        refreshedTunnels = await manager.GetVpnTunnelsAsync(token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch
                    {
#if DEBUG
                        Debug.WriteLine("PIA_LINK.RecoveryReadSucceeded=NO");
                        Debug.WriteLine("PIA_LINK.RecoveryTunnelCount=0");
                        Debug.WriteLine("PIA_LINK.RecoveryPrimaryFound=NO");
                        Debug.WriteLine("PIA_LINK.RecoveryProtocol=unavailable");
                        Debug.WriteLine("PIA_LINK.RecoveryGroupCount=0");
                        Debug.WriteLine("PIA_LINK.RecoveryHasPiaGroup=NO");
                        Debug.WriteLine("PIA_LINK.RecoveryAccepted=NO");
                        Debug.WriteLine("PIA_LINK.RecoveryRejectReason=RecoveryReadFailed");
#endif
                        refreshedTunnels = [];
                    }
#if DEBUG
                    VpnTunnelInfo? recoveryPrimary = refreshedTunnels.SingleOrDefault(tunnel => tunnel.TunnelId == 38);
                    Debug.WriteLine("PIA_LINK.RecoveryReadSucceeded=YES");
                    Debug.WriteLine($"PIA_LINK.RecoveryTunnelCount={refreshedTunnels.Count}");
                    Debug.WriteLine($"PIA_LINK.RecoveryPrimaryFound={(recoveryPrimary is null ? "NO" : "YES")}");
                    Debug.WriteLine($"PIA_LINK.RecoveryProtocol={recoveryPrimary?.Protocol ?? "unavailable"}");
                    Debug.WriteLine($"PIA_LINK.RecoveryGroupCount={recoveryPrimary?.ProfileGroupIds.Count ?? 0}");
                    Debug.WriteLine($"PIA_LINK.RecoveryHasPiaGroup={(recoveryPrimary?.ProfileGroupIds.Contains(piaGroup.GroupId) == true ? "YES" : "NO")}");
#endif
                    tunnels = PreferPiaLinkedTunnelInventory(tunnels, refreshedTunnels, piaGroup.GroupId, resolutions.Select(resolution => resolution.TunnelId).ToHashSet());
#if DEBUG
                    Debug.WriteLine($"PIA_LINK.RecoveryAccepted={(ReferenceEquals(tunnels, refreshedTunnels) ? "YES" : "NO")}");
                    Debug.WriteLine($"PIA_LINK.RecoveryRejectReason={(ReferenceEquals(tunnels, refreshedTunnels) ? "None" : "RecoveryReadFailed")}");
#endif
                }
#if DEBUG
                else
                {
                    Debug.WriteLine("PIA_LINK.RecoveryReadRequired=NO");
                    Debug.WriteLine("PIA_LINK.RecoveryReadAttempted=NO");
                    Debug.WriteLine("PIA_LINK.RecoveryReadSucceeded=NO");
                    Debug.WriteLine("PIA_LINK.RecoveryAccepted=NO");
                    Debug.WriteLine("PIA_LINK.RecoveryRejectReason=None");
                }
#endif
                profiles = ReconcilePiaProfileInventory(profiles, tunnels, piaGroup, resolutions);
#if DEBUG
                VpnTunnelInfo? finalPrimary = tunnels.SingleOrDefault(tunnel => tunnel.TunnelId == 38);
                Debug.WriteLine($"PIA_LINK.FinalPrimaryFound={(finalPrimary is null ? "NO" : "YES")}");
                Debug.WriteLine($"PIA_LINK.FinalProtocol={finalPrimary?.Protocol ?? "unavailable"}");
                Debug.WriteLine($"PIA_LINK.FinalGroupCount={finalPrimary?.ProfileGroupIds.Count ?? 0}");
                Debug.WriteLine($"PIA_LINK.FinalHasPiaGroup={(finalPrimary?.ProfileGroupIds.Contains(piaGroup.GroupId) == true ? "YES" : "NO")}");
                Debug.WriteLine($"PIA_LINK.ReconcileAttempted=YES");
                Debug.WriteLine($"PIA_LINK.ReconcileLinked={(profiles.Any(profile => profile.GroupId == piaGroup.GroupId && profile.IsUsedByTunnel) ? "YES" : "NO")}");
                Debug.WriteLine($"PIA_LINK.ReconcileFailureReason={(profiles.Any(profile => profile.GroupId == piaGroup.GroupId && profile.IsUsedByTunnel) ? "None" : "ReconcileRejected")}");
#endif
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
#if DEBUG
                Debug.WriteLine("PIA_LINK.ResolutionSucceeded=NO");
                Debug.WriteLine("PIA_LINK.ResolvedTunnelId=unavailable");
                Debug.WriteLine("PIA_LINK.ResolvedGroupId=unavailable");
                Debug.WriteLine("PIA_LINK.ResolvedConfigId=unavailable");
                Debug.WriteLine("PIA_LINK.RecoveryReadRequired=NO");
                Debug.WriteLine("PIA_LINK.RecoveryReadAttempted=NO");
                Debug.WriteLine("PIA_LINK.RecoveryReadSucceeded=NO");
                Debug.WriteLine("PIA_LINK.RecoveryAccepted=NO");
                Debug.WriteLine("PIA_LINK.RecoveryRejectReason=ResolutionUnavailable");
                Debug.WriteLine("PIA_LINK.ReconcileAttempted=NO");
                Debug.WriteLine("PIA_LINK.ReconcileLinked=NO");
                Debug.WriteLine("PIA_LINK.ReconcileFailureReason=ResolutionUnavailable");
#endif
                System.Diagnostics.Debug.WriteLine($"PIA current config resolution unavailable ({DiagnosticRedactor.FailureCategory(exception)}).");
            }
        }
#if DEBUG
        else
        {
            Debug.WriteLine("PIA_LINK.PiaGroupInventoryFound=NO");
            Debug.WriteLine("PIA_LINK.InitialHasPiaGroup=NO");
            Debug.WriteLine("PIA_LINK.ResolutionAttempted=NO");
            Debug.WriteLine("PIA_LINK.ResolutionSucceeded=NO");
            Debug.WriteLine("PIA_LINK.RecoveryReadRequired=NO");
            Debug.WriteLine("PIA_LINK.RecoveryReadAttempted=NO");
            Debug.WriteLine("PIA_LINK.RecoveryReadSucceeded=NO");
            Debug.WriteLine("PIA_LINK.RecoveryAccepted=NO");
            Debug.WriteLine("PIA_LINK.RecoveryRejectReason=PiaGroupInventoryMissing");
            Debug.WriteLine("PIA_LINK.ReconcileAttempted=NO");
            Debug.WriteLine("PIA_LINK.ReconcileLinked=NO");
            Debug.WriteLine("PIA_LINK.ReconcileFailureReason=PiaGroupInventoryMissing");
        }
#endif
        _devLog.Write(RouterPilotDevLogCategory.VPN, operation, $"TunnelInventory.Parsed tunnels={tunnels.Count}", RouterPilotDevLogLevel.Debug);
        _devLog.Write(RouterPilotDevLogCategory.PIA, operation, $"Link.Result linked={(piaGroup is not null ? "true" : "false")}{(piaGroup is null ? string.Empty : $" group={piaGroup.GroupId}")}", RouterPilotDevLogLevel.Debug);
        _devLog.Write(RouterPilotDevLogCategory.VPN, operation, "Refresh.Completed", RouterPilotDevLogLevel.Info, timing.ElapsedMilliseconds, "Success");
        return new VpnInventorySnapshot { Tunnels = tunnels, Profiles = Correlate(tunnels, profiles), ProfileInventoryState = inventoryState, PiaProviderGroup = piaGroup };
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

    public async Task<VpnWireGuardHandshakeSnapshot> GetWireGuardHandshakeSnapshotAsync(VpnTunnelInfo tunnel, CancellationToken token)
    {
        if (!string.Equals(tunnel.Protocol, "WireGuard", StringComparison.OrdinalIgnoreCase))
            return new VpnWireGuardHandshakeSnapshot();
        try
        {
            RouterManager manager = await _provider.GetRouterManagerAsync(token).ConfigureAwait(false);
            return await manager.GetWireGuardHandshakeSnapshotAsync(tunnel.InterfaceName, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"WireGuard handshake diagnostic unavailable ({DiagnosticRedactor.FailureCategory(exception)}).");
            return new VpnWireGuardHandshakeSnapshot();
        }
    }

    public async Task<VpnProviderServerCatalogueResult> RefreshPiaProviderServersAsync(int tunnelId, int groupId, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            RouterManager manager = await _provider.GetRouterManagerAsync(token).ConfigureAwait(false);
            VpnProviderGroupInfo? piaGroup = await manager.GetPiaProviderGroupAsync(token).ConfigureAwait(false);
            if (piaGroup is null || piaGroup.GroupId != groupId)
                return new VpnProviderServerCatalogueResult { TunnelId = tunnelId, GroupId = groupId, Message = "PIA provider management is unavailable for the current router." };
            VpnTunnelInfo? tunnel = await ReadDisconnectedPiaTunnelAsync(manager, tunnelId, groupId, token).ConfigureAwait(false);
            if (tunnel is null) return new VpnProviderServerCatalogueResult { TunnelId = tunnelId, GroupId = groupId, Message = "Disconnect this WireGuard tunnel before refreshing provider servers." };
            _devLog.Write(RouterPilotDevLogCategory.PIA, $"Server catalogue request started; group={groupId}");
            VpnProviderServerCatalogueResult result = await manager.GetPiaProviderServerCatalogueAsync(groupId, token).ConfigureAwait(false);
            _devLog.Write(RouterPilotDevLogCategory.PIA, $"Catalogue received: {result.Servers.Count} servers", result.Success ? RouterPilotDevLogLevel.Info : RouterPilotDevLogLevel.Warn);
            return new VpnProviderServerCatalogueResult { Success = result.Success, TunnelId = tunnelId, GroupId = groupId, Servers = result.Servers, Message = result.Message };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) { return new VpnProviderServerCatalogueResult { TunnelId = tunnelId, GroupId = groupId, Message = $"Provider server refresh is unavailable ({DiagnosticRedactor.FailureCategory(exception)})." }; }
        finally { _gate.Release(); }
    }

    public async Task<VpnProviderConfigGenerationResult> GeneratePiaProviderConfigAsync(int tunnelId, int groupId, VpnProviderServerInfo selection, Func<bool> operationStillCurrent, CancellationToken token)
    {
#if DEBUG
        Debug.WriteLine("PIA_APPLY_SERVICE_ENTRY=YES");
#endif
        await _gate.WaitAsync(token).ConfigureAwait(false);
        using PiaApplyIdentityTrace? trace = PiaApplyIdentityTrace.Start(selection.CountryName, selection.CityName, selection.Hostname);
        _devLog.Write(RouterPilotDevLogCategory.PIA, $"Apply requested: {selection.CountryName}/{selection.CityName} / {selection.Hostname}");
        try
        {
            trace?.PreGenerationNextStep("ProviderResolve");
            bool initialOperationCurrent = operationStillCurrent();
            trace?.OperationCurrent(initialOperationCurrent);
            if (!initialOperationCurrent)
            {
                trace?.PreGenerationFailure("OperationInvalidated");
                return new VpnProviderConfigGenerationResult { TunnelId = tunnelId, Message = "The router profile changed before provider configuration generation." };
            }
            RouterManager manager = await _provider.GetRouterManagerAsync(token).ConfigureAwait(false);
            VpnProviderGroupInfo? authoritativePiaGroup = await manager.GetPiaProviderGroupAsync(token).ConfigureAwait(false);
            if (authoritativePiaGroup is null || authoritativePiaGroup.GroupId != groupId)
                return new VpnProviderConfigGenerationResult { TunnelId = tunnelId, Message = "PIA provider management is unavailable for the current router." };
            trace?.ProviderResolved();
            bool selectionValid = selection.GroupId == groupId && !string.IsNullOrWhiteSpace(selection.CountryName) && !string.IsNullOrWhiteSpace(selection.CityName) && !string.IsNullOrWhiteSpace(selection.Hostname);
            trace?.SelectionValid(selectionValid);
            trace?.GroupResolved(groupId > 0 && selection.GroupId == groupId);
            trace?.LogicalTarget(selection.CountryName, selection.CityName, selection.Hostname);
            if (!selectionValid)
            {
                trace?.PreGenerationFailure("SelectionInvalid");
                return new VpnProviderConfigGenerationResult { TunnelId = tunnelId, Message = "Choose a server from the current PIA catalogue." };
            }
            // Re-fetch the authoritative catalogue.  A selected item from an old
            // refresh cannot be used after provider or profile state changed.
            trace?.PreGenerationNextStep("ProviderCatalogue");
            trace?.ProviderCatalogueStage(true, null, 0, 0, 0, providerContextAvailable: true, groupContextAvailable: groupId > 0, credentialsContextAvailable: false, "Unknown");
            VpnProviderServerCatalogueResult catalogue;
            try
            {
                catalogue = await manager.GetPiaProviderServerCatalogueAsync(groupId, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch
            {
                trace?.ProviderCatalogueStage(true, false, 0, 0, 0, providerContextAvailable: true, groupContextAvailable: groupId > 0, credentialsContextAvailable: false, "ProviderCatalogueUnavailable");
                trace?.CredentialsAvailable(false);
                trace?.PreGenerationFailure("ProviderCatalogueUnavailable");
                return new VpnProviderConfigGenerationResult { TunnelId = tunnelId, Message = "The current PIA provider catalogue is unavailable. Refresh servers and try again." };
            }
            bool credentialsAvailable = !catalogue.Message.Contains("credentials", StringComparison.OrdinalIgnoreCase);
            int validCatalogueEntryCount = catalogue.Servers.Count(server => !string.IsNullOrWhiteSpace(server.CountryName) && !string.IsNullOrWhiteSpace(server.CityName) && !string.IsNullOrWhiteSpace(server.Hostname));
            VpnProviderServerResolution initialResolution = ResolveLogicalProviderServer(catalogue.Servers, selection.CountryName, selection.CityName);
            trace?.InitialResolution(initialResolution.CountryCityMatchCount, initialResolution.UniqueHostnameCount, initialResolution.Result, initialResolution.Server?.Hostname,
                initialResolution.Server is null ? null : !string.Equals(initialResolution.Server.Hostname, selection.Hostname, StringComparison.OrdinalIgnoreCase));
            string? catalogueFailureReason = !catalogue.Success ? (credentialsAvailable ? "ProviderCatalogueUnavailable" : "CredentialsUnavailable") : catalogue.Servers.Count == 0 ? "ProviderCatalogueEmpty" : initialResolution.Result == "NOT_FOUND" ? "TargetLocationNotInCatalogue" : initialResolution.Result == "AMBIGUOUS" ? "TargetLocationAmbiguous" : null;
            trace?.ProviderCatalogueStage(true, catalogue.Success, catalogue.Servers.Count, validCatalogueEntryCount, initialResolution.CountryCityMatchCount, providerContextAvailable: true, groupContextAvailable: groupId > 0, credentialsContextAvailable: credentialsAvailable, catalogueFailureReason);
            trace?.CredentialsAvailable(credentialsAvailable);
            if (catalogueFailureReason is not null)
            {
                trace?.PreGenerationFailure(catalogueFailureReason);
                string message = catalogueFailureReason switch
                {
                    "CredentialsUnavailable" => "PIA provider credentials are unavailable on the router.",
                    "ProviderCatalogueEmpty" => "The router returned an empty PIA provider catalogue. Refresh servers and try again.",
                    "ProviderCatalogueUnavailable" => "The current PIA provider catalogue is unavailable. Refresh servers and try again.",
                    "TargetLocationNotInCatalogue" => "The selected PIA location is no longer in the current catalogue. Refresh and choose it again.",
                    "TargetLocationAmbiguous" => "The selected PIA location has multiple current hostnames. Choose it again after refreshing servers.",
                    _ => "The selected server is no longer in the current PIA catalogue. Refresh and choose it again."
                };
                return new VpnProviderConfigGenerationResult { TunnelId = tunnelId, Message = message };
            }
            VpnProviderServerInfo targetSelection = initialResolution.Server!;
            if (!operationStillCurrent())
            {
                trace?.PreGenerationFailure("OperationInvalidated");
                return new VpnProviderConfigGenerationResult { TunnelId = tunnelId, Message = "The router profile changed before Primary Tunnel preparation." };
            }
            trace?.PreGenerationNextStep("PreGenerationAssignmentSnapshot");
            trace?.PreAssignmentTunnelReadAttempted();
            _devLog.Write(RouterPilotDevLogCategory.VPN, "Primary assignment pre-generation read started", RouterPilotDevLogLevel.Trace);
            RouterManager.VpnWireGuardAssignmentState? beforeAssignment = await manager.GetWireGuardAssignmentStateAsync(tunnelId, token).ConfigureAwait(false);
            trace?.PreAssignmentTunnelReadResult(beforeAssignment is not null);
            trace?.PrimaryPreRead(beforeAssignment);
            List<RouterManager.VpnWireGuardConnectAssociation> preAssignmentReferences = beforeAssignment?.References.Where(reference => reference.GroupId == groupId).ToList() ?? [];
            RouterManager.VpnWireGuardConnectAssociation? preAssignmentReference = preAssignmentReferences.Count == 1 ? preAssignmentReferences[0] : null;
            trace?.PreAssignmentTunnel(preAssignmentReference?.GroupId, preAssignmentReference?.ConfigId);
            trace?.ProviderLifecycleBefore(preAssignmentReference?.ConfigId);
            string? preGenerationFailure = beforeAssignment is null ? "TunnelReadFailed" : beforeAssignment.Enabled ? "TunnelEnabled" : !beforeAssignment.IsSupportedRouting ? "RoutingPolicyUnsupported" : preAssignmentReferences.Count != 1 ? "GroupMismatch" : null;
            trace?.PrimaryEligibility(preGenerationFailure is null, preGenerationFailure);
            if (preGenerationFailure is not null)
            {
                trace?.PreGenerationFailure(preGenerationFailure);
                return new VpnProviderConfigGenerationResult { TunnelId = tunnelId, Message = "Disconnect this WireGuard tunnel before generating provider configuration." };
            }

#if DEBUG
            await RecordProviderConfigStageAsync(manager, groupId, "BeforeReset", selection.CountryName, selection.CityName, targetSelection: targetSelection,
                alternate: null, currentPrimaryConfigId: preAssignmentReference?.ConfigId, oldPrimaryConfigId: preAssignmentReference?.ConfigId, token: token, trace: trace!).ConfigureAwait(false);
            await RecordProviderTunnelStageAsync(manager, "BeforeReset", tunnelId, preAssignmentReference?.ConfigId, null, null, token, trace!).ConfigureAwait(false);
#endif

            // Stock same-location reselection proves one target generation is
            // sufficient.  Do not generate an alternate or force a second
            // catalogue refresh: the authoritative logical resolution above
            // supplies this operation's current concrete hostname.
#if DEBUG
            // Retained diagnostic stage schema records no alternate for the
            // simplified one-generation lifecycle.
            VpnProviderServerInfo? resetServer = null;
#endif

            trace?.PreGenerationNextStep("GenerationCall");
            _devLog.Write(RouterPilotDevLogCategory.PIA, "Provider config generation started");
            bool generationOperationCurrent = operationStillCurrent();
            trace?.OperationCurrent(generationOperationCurrent);
            if (!generationOperationCurrent)
            {
                trace?.PreGenerationFailure("OperationInvalidated");
                return new VpnProviderConfigGenerationResult { TunnelId = tunnelId, Message = "The router profile changed before provider configuration generation." };
            }
            if (!await manager.GeneratePiaProviderConfigAsync(groupId, targetSelection, token, trace).ConfigureAwait(false))
            {
                trace?.GenerationCallResult(false);
                trace?.PreGenerationFailure("GenerationCallFailed");
                return new VpnProviderConfigGenerationResult { TunnelId = tunnelId, Message = "The router did not accept provider configuration generation." };
            }
            trace?.GenerationCallResult(true);
            _devLog.Write(RouterPilotDevLogCategory.PIA, "Provider config generation completed");
            IReadOnlyList<VpnProviderGeneratedConfigInfo> generated = await manager.GetPiaGeneratedConfigsAsync(groupId, token).ConfigureAwait(false);
            List<VpnProviderGeneratedConfigInfo> matches = generated.Where(config => RouterManager.GeneratedConfigMatchesSelection(config, targetSelection)).ToList();
            VpnProviderGeneratedConfigInfo? selectedMatch = matches.Count == 1 ? matches[0] : null;
            trace?.PostGenerationMatch(matches.Count, groupId, selectedMatch?.PeerId, selectedMatch?.Name, selectedMatch?.Location);
            trace?.ProviderLifecycleAfter(generated.Count, matches.Count, selectedMatch?.PeerId, preAssignmentReference?.ConfigId,
                preAssignmentReference is { ConfigId: > 0 } && generated.Any(config => config.PeerId == preAssignmentReference.ConfigId));
#if DEBUG
            trace?.ProviderConfigStage("AfterTargetGenerate", true, generated.Count,
                generated.Count(config => GeneratedConfigMatchesLogicalLocation(config, selection.CountryName, selection.CityName)),
                resetServer is null ? null : generated.Count(config => RouterManager.GeneratedConfigMatchesSelection(config, resetServer)),
                matches.Count, preAssignmentReference?.ConfigId,
                resetServer is null ? null : generated.Where(config => RouterManager.GeneratedConfigMatchesSelection(config, resetServer)).Select(config => (int?)config.PeerId).SingleOrDefault(),
                selectedMatch?.PeerId, preAssignmentReference?.ConfigId);
            await RecordProviderTunnelStageAsync(manager, "AfterTargetGenerate", tunnelId, preAssignmentReference?.ConfigId,
                resetServer is null ? null : generated.Where(config => RouterManager.GeneratedConfigMatchesSelection(config, resetServer)).Select(config => (int?)config.PeerId).SingleOrDefault(),
                selectedMatch?.PeerId, token, trace!).ConfigureAwait(false);
#endif
            if (matches.Count != 1)
            {
                trace?.NextStep("PreAssignmentTunnelRead");
                trace?.AssignmentEligibility(false, "GeneratedConfigUnavailable");
                return new VpnProviderConfigGenerationResult { TunnelId = tunnelId, Message = "The router generated configuration, but RouterPilot could not uniquely verify the selected server." };
            }
            int currentConfigId = matches[0].PeerId;
            _devLog.Write(RouterPilotDevLogCategory.PIA, $"Selected config match count: {matches.Count}; generated config ID: {currentConfigId}");
            trace?.PrimarySelected(groupId, currentConfigId);
            if (!operationStillCurrent())
            {
                trace?.NextStep("PreAssignmentTunnelRead");
                trace?.AssignmentEligibility(false, "OperationInvalidated");
                return new VpnProviderConfigGenerationResult { TunnelId = tunnelId, Message = "The router profile changed before Primary Tunnel assignment." };
            }

            // The router is proven to clear only the provider via association
            // during generation. The pre-generation state remains authoritative
            // for the assignment policy and old config identity is never reused.
            trace?.NextStep("PostGenerationTunnelRead");
            RouterManager.VpnWireGuardAssignmentState? postGenerationState = await manager.GetWireGuardAssignmentStateAsync(tunnelId, token).ConfigureAwait(false);
            if (!operationStillCurrent())
            {
                trace?.PostGenerationTransition(false, "OperationInvalidated");
                trace?.AssignmentEligibility(false, "OperationInvalidated");
                return new VpnProviderConfigGenerationResult { TunnelId = tunnelId, Message = "The router profile changed during provider generation." };
            }
            if (postGenerationState is null)
            {
                IReadOnlyList<VpnTunnelInfo> transitionalTunnels = await manager.GetVpnTunnelsAsync(token).ConfigureAwait(false);
                if (!operationStillCurrent())
                {
                    trace?.PostGenerationTransition(false, "OperationInvalidated");
                    trace?.AssignmentEligibility(false, "OperationInvalidated");
                    return new VpnProviderConfigGenerationResult { TunnelId = tunnelId, Message = "The router profile changed during provider generation." };
                }
                bool transitionAccepted = IsExpectedProviderGenerationTransition(transitionalTunnels, tunnelId);
                trace?.PostGenerationTransition(transitionAccepted, transitionAccepted ? "ViaClearedByGeneration" : "UnexpectedPostGenerationState");
                if (!transitionAccepted)
                {
                    trace?.AssignmentEligibility(false, "TunnelReadFailed");
                    return new VpnProviderConfigGenerationResult { TunnelId = tunnelId, Message = "RouterPilot could not safely verify the Primary Tunnel after provider generation." };
                }
            }
            else if (postGenerationState.TunnelId != beforeAssignment!.TunnelId || postGenerationState.Enabled || !postGenerationState.IsSupportedRouting ||
                     postGenerationState.FromType != beforeAssignment.FromType || postGenerationState.ToType != beforeAssignment.ToType ||
                     !postGenerationState.MacList.SequenceEqual(beforeAssignment.MacList, StringComparer.OrdinalIgnoreCase) ||
                     (postGenerationState.References.Count > 0 && postGenerationState.References.Any(reference => reference.GroupId != groupId)))
            {
                trace?.PostGenerationTransition(false, "UnexpectedPostGenerationState");
                trace?.AssignmentEligibility(false, "UnexpectedPostGenerationState");
                return new VpnProviderConfigGenerationResult { TunnelId = tunnelId, Message = "The Primary Tunnel changed unexpectedly during provider generation." };
            }
            trace?.PostGenerationTransition(true, postGenerationState is null ? "ViaClearedByGeneration" : "AssociationPreserved");
            trace?.AssignmentEligibility(true, null);
            _devLog.Write(RouterPilotDevLogCategory.VPN, "Primary assignment eligibility: PASS");
            if (!operationStillCurrent())
            {
                trace?.NextStep("Assignment");
                trace?.AssignmentEligibility(false, "OperationInvalidated");
                return new VpnProviderConfigGenerationResult { TunnelId = tunnelId, Message = "The router profile changed before Primary Tunnel assignment." };
            }
            trace?.NextStep("Assignment");
#if DEBUG
            await RecordProviderTunnelStageAsync(manager, "BeforeFinalAssignment", tunnelId, preAssignmentReference?.ConfigId,
                resetServer is null ? null : generated.Where(config => RouterManager.GeneratedConfigMatchesSelection(config, resetServer)).Select(config => (int?)config.PeerId).SingleOrDefault(),
                currentConfigId, token, trace!).ConfigureAwait(false);
#endif
            _devLog.Write(RouterPilotDevLogCategory.VPN, "Primary assignment dispatch started");
            if (!await manager.AssignWireGuardProviderConfigAsync(beforeAssignment!, groupId, currentConfigId, token, trace).ConfigureAwait(false))
            {
                trace?.AssignmentEligibility(false, "AssignmentCallFailed");
                trace?.PrimaryVerification(false, "RpcFailed");
                return new VpnProviderConfigGenerationResult { TunnelId = tunnelId, Message = "RouterPilot could not assign the generated server configuration to the Primary Tunnel." };
            }
            RouterManager.VpnWireGuardAssignmentState? afterAssignment = await manager.GetWireGuardAssignmentStateAsync(tunnelId, token).ConfigureAwait(false);
            trace?.PrimaryPostRead(afterAssignment);
            _devLog.Write(RouterPilotDevLogCategory.VPN, "Primary assignment post-read completed", RouterPilotDevLogLevel.Trace);
            List<RouterManager.VpnWireGuardConnectAssociation> postAssignmentReferences = afterAssignment?.References.Where(reference => reference.GroupId == groupId).ToList() ?? [];
            RouterManager.VpnWireGuardConnectAssociation? postAssignmentReference = postAssignmentReferences.Count == 1 ? postAssignmentReferences[0] : null;
            trace?.PostAssignmentTunnel(postAssignmentReference?.GroupId, postAssignmentReference?.ConfigId);
            if (trace is not null)
            {
                try
                {
                    IReadOnlyList<VpnProviderGeneratedConfigInfo> postAssignmentConfigs = await manager.GetPiaGeneratedConfigsAsync(groupId, token).ConfigureAwait(false);
                    List<VpnProviderGeneratedConfigInfo> postAssignmentMatches = postAssignmentReference is null
                        ? [] : postAssignmentConfigs.Where(config => config.PeerId == postAssignmentReference.ConfigId).ToList();
                    VpnProviderGeneratedConfigInfo? postAssignmentMatch = postAssignmentMatches.Count == 1 ? postAssignmentMatches[0] : null;
                    trace.PostAssignmentResolvedConfig(postAssignmentMatches.Count, postAssignmentMatch?.Name, postAssignmentMatch?.Location);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { trace.PostAssignmentResolvedConfig(0, null, null); }
            }
            bool assignmentVerified = RouterManager.VerifyWireGuardProviderAssignment(beforeAssignment, afterAssignment, groupId, currentConfigId);
            trace?.PrimaryVerification(assignmentVerified, assignmentVerified ? "None" : afterAssignment is null ? "PostReadFailed" : "PostReadMismatch");
            _devLog.Write(RouterPilotDevLogCategory.VPN, $"Primary assignment verification: {(assignmentVerified ? "PASS" : "FAIL")}", assignmentVerified ? RouterPilotDevLogLevel.Info : RouterPilotDevLogLevel.Error);
            if (!assignmentVerified)
                return new VpnProviderConfigGenerationResult { TunnelId = tunnelId, Message = "RouterPilot could not verify the Primary Tunnel assignment. Check the router VPN configuration before connecting." };
            return new VpnProviderConfigGenerationResult { Success = true, TunnelId = tunnelId, GeneratedConfigVerified = true, AuthoritativeServer = targetSelection, Message = "Provider server configuration was generated and assigned to the Primary Tunnel. Connect when ready." };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            trace?.NextStep("Cancelled");
            trace?.AssignmentEligibility(false, "Cancelled");
            throw;
        }
        catch (Exception exception)
        {
            trace?.NextStep("UnexpectedFailure");
            trace?.AssignmentEligibility(false, "UnexpectedFailure");
            return new VpnProviderConfigGenerationResult { TunnelId = tunnelId, Message = $"Provider configuration generation is unavailable ({DiagnosticRedactor.FailureCategory(exception)})." };
        }
        finally { _gate.Release(); }
    }

    private static async Task<VpnTunnelInfo?> ReadDisconnectedPiaTunnelAsync(RouterManager manager, int tunnelId, int groupId, CancellationToken token)
    {
        List<VpnTunnelInfo> tunnels = (await manager.GetVpnTunnelsAsync(token).ConfigureAwait(false)).Where(tunnel => tunnel.TunnelId == tunnelId).ToList();
        return tunnels.Count == 1 && !tunnels[0].Enabled && string.Equals(tunnels[0].Protocol, "WireGuard", StringComparison.OrdinalIgnoreCase) && tunnels[0].ProfileGroupIds.Contains(groupId)
            ? tunnels[0] : null;
    }

    internal static VpnProviderServerResolution ResolveLogicalProviderServer(
        IReadOnlyList<VpnProviderServerInfo> servers, string country, string city)
    {
        List<VpnProviderServerInfo> matches = servers
            .Where(server => !string.IsNullOrWhiteSpace(server.CountryName) &&
                !string.IsNullOrWhiteSpace(server.CityName) &&
                !string.IsNullOrWhiteSpace(server.Hostname) &&
                string.Equals(server.CountryName.Trim(), country.Trim(), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(server.CityName.Trim(), city.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        List<VpnProviderServerInfo> unique = matches
            .GroupBy(server => server.Hostname.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(server => server.CountryName, StringComparer.Ordinal).ThenBy(server => server.CityName, StringComparer.Ordinal).ThenBy(server => server.Hostname, StringComparer.Ordinal).First())
            .OrderBy(server => server.Hostname, StringComparer.Ordinal)
            .ToList();
        return unique.Count switch
        {
            0 => new VpnProviderServerResolution("NOT_FOUND", matches.Count, 0, null),
            1 => new VpnProviderServerResolution("FOUND", matches.Count, 1, unique[0]),
            _ => new VpnProviderServerResolution("AMBIGUOUS", matches.Count, unique.Count, null)
        };
    }

    internal sealed record VpnProviderServerResolution(string Result, int CountryCityMatchCount, int UniqueHostnameCount, VpnProviderServerInfo? Server);

    internal static bool GeneratedConfigMatchesLogicalLocation(VpnProviderGeneratedConfigInfo config, string country, string city) =>
        !string.IsNullOrWhiteSpace(config.Location) &&
        (config.Location.Contains(city, StringComparison.OrdinalIgnoreCase) || config.Location.Contains(country, StringComparison.OrdinalIgnoreCase));

#if DEBUG
    private static async Task<int?> RecordProviderConfigStageAsync(RouterManager manager, int groupId, string stage, string country, string city,
        VpnProviderServerInfo targetSelection, VpnProviderServerInfo? alternate, int? currentPrimaryConfigId, int? oldPrimaryConfigId,
        CancellationToken token, PiaApplyIdentityTrace trace)
    {
        try
        {
            IReadOnlyList<VpnProviderGeneratedConfigInfo> configs = await manager.GetPiaGeneratedConfigsAsync(groupId, token).ConfigureAwait(false);
            List<VpnProviderGeneratedConfigInfo> logicalTarget = configs.Where(config => GeneratedConfigMatchesLogicalLocation(config, country, city)).ToList();
            List<VpnProviderGeneratedConfigInfo> alternateMatches = alternate is null ? [] : configs.Where(config => RouterManager.GeneratedConfigMatchesSelection(config, alternate)).ToList();
            List<VpnProviderGeneratedConfigInfo> targetMatches = configs.Where(config => RouterManager.GeneratedConfigMatchesSelection(config, targetSelection)).ToList();
            trace.ProviderConfigStage(stage, true, configs.Count, logicalTarget.Count, alternate is null ? null : alternateMatches.Count, targetMatches.Count,
                currentPrimaryConfigId, alternateMatches.Count == 1 ? alternateMatches[0].PeerId : null,
                targetMatches.Count == 1 ? targetMatches[0].PeerId : null, oldPrimaryConfigId);
            return alternateMatches.Count == 1 ? alternateMatches[0].PeerId : null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch
        {
            trace.ProviderConfigStage(stage, false, null, null, null, null, currentPrimaryConfigId, null, null, oldPrimaryConfigId);
            return null;
        }
    }

    private static async Task RecordProviderTunnelStageAsync(RouterManager manager, string stage, int tunnelId,
        int? oldPrimaryConfigId, int? alternateConfigId, int? targetConfigId, CancellationToken token, PiaApplyIdentityTrace trace)
    {
        try
        {
            RouterManager.VpnTunnelStructuralSnapshot? snapshot = await manager.GetVpnTunnelStructuralSnapshotAsync(tunnelId, token).ConfigureAwait(false);
            trace.ProviderTunnelStage(stage, snapshot, oldPrimaryConfigId, alternateConfigId, targetConfigId);
        }
        catch
        {
            trace.ProviderTunnelStage(stage, null, oldPrimaryConfigId, alternateConfigId, targetConfigId);
        }
    }
#endif

    internal static bool IsExpectedProviderGenerationTransition(IReadOnlyList<VpnTunnelInfo> tunnels, int tunnelId)
    {
        if (tunnels.Count(item => item.TunnelId == tunnelId) != 1) return false;
        VpnTunnelInfo tunnel = tunnels.Single(item => item.TunnelId == tunnelId);
        return !tunnel.Enabled && string.Equals(tunnel.Protocol, "Unknown", StringComparison.OrdinalIgnoreCase) &&
               tunnel.ProfileGroupIds.Count == 0 && string.Equals(tunnel.FromType, "mac", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(tunnel.ToType, "default", StringComparison.OrdinalIgnoreCase);
    }

    internal static IReadOnlyList<VpnClientProfileInfo> ReconcilePiaProfileInventory(
        IReadOnlyList<VpnClientProfileInfo> profiles,
        IReadOnlyList<VpnTunnelInfo> tunnels,
        VpnProviderGroupInfo piaGroup,
        IReadOnlyList<RouterManager.VpnPiaTunnelConfigResolution> resolutions)
    {
        List<RouterManager.VpnPiaTunnelConfigResolution> groupResolutions = resolutions.Where(item => item.GroupId == piaGroup.GroupId).ToList();
        if (groupResolutions.Count == 0) return profiles;
        VpnClientProfileInfo? existing = profiles.SingleOrDefault(profile => profile.GroupId == piaGroup.GroupId);
        if (existing is null && groupResolutions.Count != 1) return profiles;
        RouterManager.VpnPiaTunnelConfigResolution? resolution = groupResolutions.Count == 1 ? groupResolutions[0] : null;
        if (resolution is null) return profiles;
        List<VpnTunnelInfo> linkedTunnels = tunnels.Where(tunnel => tunnel.TunnelId == resolution.TunnelId && tunnel.ProfileGroupIds.Contains(piaGroup.GroupId)).ToList();
        if (linkedTunnels.Count != 1) return profiles;
        VpnClientProfileInfo reconciled = new()
        {
            GroupId = piaGroup.GroupId,
            Name = existing?.Name is { Length: > 0 } name ? name : "PIA",
            Protocol = "WireGuard",
            IsUsedByTunnel = true,
            TunnelIds = [resolution.TunnelId],
            UsedByDisplay = linkedTunnels[0].Name,
            ServerConfigCount = existing?.ServerConfigCount ?? 1,
            CurrentPeerId = resolution.ConfigId,
            CurrentLocation = resolution.Location,
            ActivityState = existing?.ActivityState ?? VpnProfileActivityState.Unknown
        };
        return profiles.Where(profile => profile.GroupId != piaGroup.GroupId).Append(reconciled).ToList();
    }

    internal static IReadOnlyList<VpnTunnelInfo> PreferPiaLinkedTunnelInventory(
        IReadOnlyList<VpnTunnelInfo> original,
        IReadOnlyList<VpnTunnelInfo> refreshed,
        int piaGroupId,
        IReadOnlySet<int> provenTunnelIds)
    {
        if (refreshed.Count != original.Count || !refreshed.Any(tunnel => provenTunnelIds.Contains(tunnel.TunnelId) && tunnel.ProfileGroupIds.Contains(piaGroupId)))
            return original;
        return refreshed;
    }

#if DEBUG
    public async Task<VpnStateCaptureSnapshot> GetDebugStateCaptureAsync(CancellationToken token) =>
        await (await _provider.GetRouterManagerAsync(token)).GetVpnStateCaptureAsync(token);

    public async Task<PiaManualStateSnapshot> CapturePiaManualStateAsync(int piaGroupId, int primaryTunnelId, CancellationToken token) =>
        await (await _provider.GetRouterManagerAsync(token)).CapturePiaManualStateAsync(piaGroupId, primaryTunnelId, token);
#endif

    public async Task<VpnOperationResult> SetTunnelEnabledAsync(int tunnelId, bool enabled, CancellationToken token, VpnConnectTrace? trace = null)
    {
        await _gate.WaitAsync(token);
        try
        {
            RouterManager manager = await _provider.GetRouterManagerAsync(token);
            List<VpnTunnelInfo> before = (await manager.GetVpnTunnelsAsync(token)).Where(tunnel => tunnel.TunnelId == tunnelId).ToList();
            if (before.Count != 1) return await CompleteAsync(Failure(tunnelId, "TunnelIdentityAmbiguous"), null, enabled, token);
            VpnTunnelInfo original = before[0];
            if (original.Enabled == enabled) return await CompleteAsync(new VpnOperationResult { Success = true, TunnelId = tunnelId }, original, enabled, token);

            bool applied;
            if (enabled && string.Equals(original.Protocol, "WireGuard", StringComparison.OrdinalIgnoreCase))
            {
                VpnProviderGroupInfo? piaGroup = await manager.GetPiaProviderGroupAsync(token).ConfigureAwait(false);
                RouterManager.VpnWireGuardConnectAssociation? association = await manager.GetWireGuardConnectAssociationAsync(tunnelId, token).ConfigureAwait(false);
                trace?.EmitIdentity(tunnelId, association is not null, original.Enabled, original.Protocol, association?.GroupId, association?.ConfigId);
                bool isLinkedPiaProvider = piaGroup is not null && (original.ProfileGroupIds.Contains(piaGroup.GroupId) || association?.GroupId == piaGroup.GroupId);
                if (isLinkedPiaProvider)
                {
                    if (association is null || association.GroupId != piaGroup!.GroupId)
                        return await CompleteAsync(new VpnOperationResult { TunnelId = tunnelId, FailureCategory = "ProviderConfigAssociationUnavailable", Message = "RouterPilot could not verify the current WireGuard provider configuration before connecting." }, original, enabled, token);
                    applied = await manager.ConnectWireGuardProviderTunnelAsync(tunnelId, association, token, trace).ConfigureAwait(false);
                }
                else
                {
                    applied = await manager.SetVpnTunnelEnabledAsync(tunnelId, true, token).ConfigureAwait(false);
                }
            }
            else
            {
                // Stock disconnect is intentionally the existing minimal
                // { tunnel_id, enabled:false } request, with no via object.
                applied = await manager.SetVpnTunnelEnabledAsync(tunnelId, enabled, token).ConfigureAwait(false);
            }
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
        HasWireGuardHandshakeFailure = tunnel.HasWireGuardHandshakeFailure,
        TransitionIntent = tunnel.TransitionIntent, RoutingPolicyState = state, InternetRoutingScope = scope,
        RoutingDeviceIdentities = identities, RoutingDevices = devices
    };
    private static VpnOperationResult Failure(int tunnelId, string category) => new() { TunnelId = tunnelId, FailureCategory = category, Message = "RouterPilot could not update the VPN tunnel." };
}
