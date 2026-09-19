using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using RouterPilot.Models;

namespace RouterPilot.Services;

// Temporary, opt-in incident trace. It emits only safe catalogue/config
// presentation metadata through the local diagnostic trace listener; it does
// not persist data or participate in VPN control flow.
internal sealed class PiaApplyIdentityTrace : IDisposable
{
    private const string EnvironmentVariable = "ROUTERPILOT_PIA_APPLY_IDENTITY_TRACE";
#if DEBUG
    private static int _debugAnnouncementEmitted;
#endif
    private readonly string _selectedCountry;
    private readonly string _selectedCity;
    private readonly string _selectedHostname;
    private string _requestCountry = "<unavailable>";
    private string _requestCity = "<unavailable>";
    private string _requestHostname = "<unavailable>";
    private int _postGenerationMatchCount;
    private int? _postGenerationGroupId;
    private int? _postGenerationConfigId;
    private string _postGenerationName = "<unavailable>";
    private string _postGenerationLocation = "<unavailable>";
    private int? _preAssignmentGroupId;
    private int? _preAssignmentConfigId;
    private int? _assignmentGroupId;
    private int? _assignmentConfigId;
    private int? _postAssignmentGroupId;
    private int? _postAssignmentConfigId;
    private int _postAssignmentResolvedMatchCount;
    private string _postAssignmentResolvedName = "<unavailable>";
    private string _postAssignmentResolvedLocation = "<unavailable>";
    private int _mutationCount;
    private string _preGenerationNextStep = "<unavailable>";
    private string _preGenerationProviderResolved = "NO";
    private string _preGenerationGroupResolved = "NO";
    private string _preGenerationCredentialsAvailable = "<unavailable>";
    private string _preGenerationSelectionValid = "<unavailable>";
    private string _preGenerationOperationCurrent = "<unavailable>";
    private string _generationCallAttempted = "NO";
    private string _generationCallResult = "<unavailable>";
    private string _preGenerationFailureReason = "<unavailable>";
    private string _nextStep = "<unavailable>";
    private string _preAssignmentTunnelReadAttempted = "NO";
    private string _preAssignmentTunnelReadSucceeded = "NO";
    private string _assignmentEligibilityResult = "<unavailable>";
    private string _assignmentEligibilityFailureReason = "<unavailable>";
    private string _assignmentCallAttempted = "NO";
    private bool _emitted;
    private int? _primarySelectedGroupId;
    private int? _primaryGeneratedConfigId;
    private string _primaryPreReadSucceeded = "NO";
    private int? _primaryPreReadTunnelId;
    private string _primaryPreReadViaType = "<unavailable>";
    private int? _primaryPreReadGroupId;
    private int? _primaryPreReadConfigId;
    private string _primaryEligibility = "<unavailable>";
    private string _primaryFailureReason = "<unavailable>";
    private string _primarySerializationValidated = "NO";
    private string _primaryDispatchAttempted = "NO";
    private string _primaryDispatchCompleted = "NO";
    private string _primaryRpcSucceeded = "NO";
    private string _primaryPostReadSucceeded = "NO";
    private string _primaryPostViaPresent = "NO";
    private string _primaryPostViaType = "<unavailable>";
    private int? _primaryPostGroupId;
    private int? _primaryPostConfigId;
    private string _primaryVerification = "<unavailable>";
    private string _primaryVerificationReason = "<unavailable>";
    private string _postGenerationTransitionRecognized = "NO";
    private string _postGenerationTransitionReason = "<unavailable>";
    private string _providerLifecycleBeforeConfigCount = "<unavailable>";
    private string _providerLifecycleBeforeSelectedMatchCount = "<unavailable>";
    private string _providerLifecycleAfterConfigCount = "<unavailable>";
    private string _providerLifecycleAfterSelectedMatchCount = "<unavailable>";
    private string _providerLifecycleOldStillPresent = "UNKNOWN";
    private string _providerLifecycleIdReused = "UNKNOWN";
    private string _providerResetRequired = "YES";
    private string _providerResetAlternateFound = "NO";
    private string _providerResetAlternateCountry = "<unavailable>";
    private string _providerResetAlternateCity = "<unavailable>";
    private string _providerResetAlternateHostname = "<unavailable>";
    private string _providerResetGenerateAttempted = "NO";
    private string _providerResetGenerateSucceeded = "NO";
    private string _providerResetCatalogueRefreshAttempted = "NO";
    private string _providerResetCatalogueRefreshSucceeded = "NO";
    private string _providerResetTargetReresolved = "NO";
    private string _providerCatalogueFetchAttempted = "NO";
    private string _providerCatalogueFetchRpcSucceeded = "UNKNOWN";
    private string _providerCatalogueRawStructuralCount = "<unavailable>";
    private string _providerCatalogueParsedEntryCount = "<unavailable>";
    private string _providerCatalogueValidEntryCount = "<unavailable>";
    private string _providerCatalogueTargetMatchCount = "<unavailable>";
    private string _providerCatalogueTargetFound = "<unavailable>";
    private string _providerCatalogueProviderContextAvailable = "<unavailable>";
    private string _providerCatalogueGroupContextAvailable = "<unavailable>";
    private string _providerCatalogueCredentialsContextAvailable = "<unavailable>";
    private string _providerResetEntryAttempted = "NO";
    private string _providerResetEntryBlockedReason = "<unavailable>";
    private string _logicalTargetCountry = "<unavailable>";
    private string _logicalTargetCity = "<unavailable>";
    private string _logicalTargetOriginalHostname = "<unavailable>";
    private string _providerResetTargetHostname = "<unavailable>";
    private string _initialResolutionCountryCityMatchCount = "<unavailable>";
    private string _initialResolutionUniqueHostnameCount = "<unavailable>";
    private string _initialResolutionResult = "<unavailable>";
    private string _initialResolutionAuthoritativeHostname = "<unavailable>";
    private string _initialResolutionHostnameChanged = "UNKNOWN";
    private string _postResetResolutionCountryCityMatchCount = "<unavailable>";
    private string _postResetResolutionUniqueHostnameCount = "<unavailable>";
    private string _postResetResolutionResult = "<unavailable>";
    private string _postResetResolutionAuthoritativeHostname = "<unavailable>";
    private string _postResetResolutionHostnameChanged = "UNKNOWN";
    private readonly Dictionary<string, string> _providerConfigStages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _providerTunnelStages = new(StringComparer.Ordinal);
    private string _alternateConfigIdEqualsOldPrimary = "UNKNOWN";
    private string _targetConfigIdEqualsAlternate = "UNKNOWN";
    private string _targetConfigIdEqualsOldPrimary = "UNKNOWN";

    private PiaApplyIdentityTrace(string country, string city, string hostname)
    {
        _selectedCountry = Safe(country);
        _selectedCity = Safe(city);
        _selectedHostname = Safe(hostname);
    }

    internal static PiaApplyIdentityTrace? Start(string country, string city, string hostname)
    {
#if DEBUG
        if (Interlocked.Exchange(ref _debugAnnouncementEmitted, 1) == 0)
            Debug.WriteLine("PIA_APPLY_IDENTITY_TRACE_ENABLED=YES");
        return new PiaApplyIdentityTrace(country, city, hostname);
#else
        return string.Equals(Environment.GetEnvironmentVariable(EnvironmentVariable), "1", StringComparison.Ordinal)
            ? new PiaApplyIdentityTrace(country, city, hostname)
            : null;
#endif
    }

    internal void GenerateRequest(string country, string city, string hostname)
    {
        _requestCountry = Safe(country);
        _requestCity = Safe(city);
        _requestHostname = Safe(hostname);
    }

    internal void PreGenerationNextStep(string value) => _preGenerationNextStep = Safe(value);
    internal void ProviderResolved() => _preGenerationProviderResolved = "YES";
    internal void GroupResolved(bool resolved) => _preGenerationGroupResolved = resolved ? "YES" : "NO";
    internal void CredentialsAvailable(bool available) => _preGenerationCredentialsAvailable = available ? "YES" : "NO";
    internal void SelectionValid(bool valid) => _preGenerationSelectionValid = valid ? "YES" : "NO";
    internal void OperationCurrent(bool current) => _preGenerationOperationCurrent = current ? "YES" : "NO";
    internal void GenerationCallAttempted() => _generationCallAttempted = "YES";
    internal void GenerationCallResult(bool succeeded) => _generationCallResult = succeeded ? "SUCCESS" : "FAILURE";
    internal void PreGenerationFailure(string reason) => _preGenerationFailureReason = Safe(reason);

    internal void PostGenerationMatch(int count, int groupId, int? configId, string? name, string? location)
    {
        _postGenerationMatchCount = count;
        _postGenerationGroupId = groupId > 0 ? groupId : null;
        _postGenerationConfigId = configId is > 0 ? configId : null;
        _postGenerationName = Safe(name);
        _postGenerationLocation = Safe(location);
    }

    internal void NextStep(string value) => _nextStep = Safe(value);

    internal void PreAssignmentTunnelReadAttempted() => _preAssignmentTunnelReadAttempted = "YES";

    internal void PreAssignmentTunnelReadResult(bool succeeded) => _preAssignmentTunnelReadSucceeded = succeeded ? "YES" : "NO";

    internal void AssignmentEligibility(bool eligible, string? failureReason = null)
    {
        _assignmentEligibilityResult = eligible ? "PASS" : "FAIL";
        _assignmentEligibilityFailureReason = eligible ? "NONE" : Safe(failureReason);
    }

    internal void AssignmentCallAttempted() => _assignmentCallAttempted = "YES";

    internal void PreAssignmentTunnel(int? groupId, int? configId)
    {
        _preAssignmentGroupId = groupId is > 0 ? groupId : null;
        _preAssignmentConfigId = configId is > 0 ? configId : null;
    }

    internal void AssignmentIntended(int groupId, int configId)
    {
        _assignmentGroupId = groupId > 0 ? groupId : null;
        _assignmentConfigId = configId > 0 ? configId : null;
    }

    internal void AssignmentMutationIssued() => _mutationCount++;

    internal void PrimarySelected(int groupId, int configId)
    {
        _primarySelectedGroupId = groupId > 0 ? groupId : null;
        _primaryGeneratedConfigId = configId > 0 ? configId : null;
    }

    internal void PrimaryPreRead(RouterManager.VpnWireGuardAssignmentState? state)
    {
        _primaryPreReadSucceeded = state is null ? "NO" : "YES";
        _primaryPreReadTunnelId = state?.TunnelId;
        _primaryPreReadViaType = state is null ? "<unavailable>" : "wireguard";
        RouterManager.VpnWireGuardConnectAssociation? reference = state?.References.Count == 1 ? state.References[0] : null;
        _primaryPreReadGroupId = reference?.GroupId;
        _primaryPreReadConfigId = reference?.ConfigId;
    }

    internal void PrimaryEligibility(bool pass, string? reason)
    {
        _primaryEligibility = pass ? "PASS" : "FAIL";
        _primaryFailureReason = pass ? "NONE" : Safe(reason);
    }

    internal void PrimarySerializationValidated(bool valid) => _primarySerializationValidated = valid ? "YES" : "NO";
    internal void PrimaryDispatchAttempted() => _primaryDispatchAttempted = "YES";
    internal void PrimaryDispatchCompleted() => _primaryDispatchCompleted = "YES";
    internal void PrimaryRpcSucceeded(bool succeeded) => _primaryRpcSucceeded = succeeded ? "YES" : "NO";

    internal void PrimaryPostRead(RouterManager.VpnWireGuardAssignmentState? state)
    {
        _primaryPostReadSucceeded = state is null ? "NO" : "YES";
        _primaryPostViaPresent = state is null ? "NO" : "YES";
        _primaryPostViaType = state is null ? "<unavailable>" : "wireguard";
        RouterManager.VpnWireGuardConnectAssociation? reference = state?.References.Count == 1 ? state.References[0] : null;
        _primaryPostGroupId = reference?.GroupId;
        _primaryPostConfigId = reference?.ConfigId;
    }

    internal void PrimaryVerification(bool pass, string? reason)
    {
        _primaryVerification = pass ? "PASS" : "FAIL";
        _primaryVerificationReason = pass ? "NONE" : Safe(reason);
    }

    internal void PostGenerationTransition(bool recognized, string reason)
    {
        _postGenerationTransitionRecognized = recognized ? "YES" : "NO";
        _postGenerationTransitionReason = Safe(reason);
    }

    internal void ProviderLifecycleBefore(int? currentConfigId)
    {
        _providerLifecycleBeforeConfigCount = "<unavailable>";
        _providerLifecycleBeforeSelectedMatchCount = "<unavailable>";
        _preAssignmentConfigId = currentConfigId is > 0 ? currentConfigId : null;
    }

    internal void ProviderLifecycleAfter(int configCount, int selectedMatchCount, int? selectedConfigId, int? oldConfigId, bool? oldConfigStillPresent)
    {
        _providerLifecycleAfterConfigCount = configCount.ToString();
        _providerLifecycleAfterSelectedMatchCount = selectedMatchCount.ToString();
        _postGenerationConfigId = selectedConfigId is > 0 ? selectedConfigId : null;
        _providerLifecycleOldStillPresent = oldConfigStillPresent.HasValue ? (oldConfigStillPresent.Value ? "YES" : "NO") : "UNKNOWN";
        _providerLifecycleIdReused = oldConfigId is > 0 && selectedConfigId == oldConfigId ? "UNKNOWN" : "UNKNOWN";
    }

    internal void ProviderResetAlternate(VpnProviderServerInfo? alternate)
    {
        _providerResetAlternateFound = alternate is null ? "NO" : "YES";
        _providerResetAlternateCountry = Safe(alternate?.CountryName);
        _providerResetAlternateCity = Safe(alternate?.CityName);
        _providerResetAlternateHostname = Safe(alternate?.Hostname);
    }

    internal void LogicalTarget(string country, string city, string originalHostname)
    {
        _logicalTargetCountry = Safe(country);
        _logicalTargetCity = Safe(city);
        _logicalTargetOriginalHostname = Safe(originalHostname);
    }

    internal void ProviderResetTargetHostname(string hostname) => _providerResetTargetHostname = Safe(hostname);

    internal void InitialResolution(int countryCityMatches, int uniqueHostnames, string result, string? hostname, bool? changed)
    {
        _initialResolutionCountryCityMatchCount = countryCityMatches.ToString();
        _initialResolutionUniqueHostnameCount = uniqueHostnames.ToString();
        _initialResolutionResult = Safe(result);
        _initialResolutionAuthoritativeHostname = Safe(hostname);
        _initialResolutionHostnameChanged = changed.HasValue ? (changed.Value ? "YES" : "NO") : "UNKNOWN";
    }

    internal void PostResetResolution(int countryCityMatches, int uniqueHostnames, string result, string? hostname, bool? changed)
    {
        _postResetResolutionCountryCityMatchCount = countryCityMatches.ToString();
        _postResetResolutionUniqueHostnameCount = uniqueHostnames.ToString();
        _postResetResolutionResult = Safe(result);
        _postResetResolutionAuthoritativeHostname = Safe(hostname);
        _postResetResolutionHostnameChanged = changed.HasValue ? (changed.Value ? "YES" : "NO") : "UNKNOWN";
    }

    internal void ProviderConfigStage(string stage, bool readSucceeded, int? configCount, int? targetLogicalMatchCount, int? alternateExactMatchCount,
        int? targetExactMatchCount, int? currentPrimaryConfigId, int? alternateConfigId, int? targetConfigId, int? oldPrimaryConfigId)
    {
        string Prefix = stage + ".";
        _providerConfigStages[Prefix + "ReadSucceeded"] = readSucceeded ? "YES" : "NO";
        _providerConfigStages[Prefix + "ConfigCount"] = Number(configCount);
        _providerConfigStages[Prefix + "TargetLogicalMatchCount"] = Number(targetLogicalMatchCount);
        _providerConfigStages[Prefix + "AlternateExactMatchCount"] = Number(alternateExactMatchCount);
        _providerConfigStages[Prefix + "TargetExactMatchCount"] = Number(targetExactMatchCount);
        _providerConfigStages[Prefix + "CurrentPrimaryConfigId"] = Number(currentPrimaryConfigId);
        _providerConfigStages[Prefix + "AlternateConfigId"] = Number(alternateConfigId);
        _providerConfigStages[Prefix + "TargetConfigId"] = Number(targetConfigId);
        string alternateEqualsOld = Equal(alternateConfigId, oldPrimaryConfigId);
        string targetEqualsAlternate = Equal(targetConfigId, alternateConfigId);
        string targetEqualsOld = Equal(targetConfigId, oldPrimaryConfigId);
        _providerConfigStages[Prefix + "AlternateConfigIdEqualsOldPrimary"] = alternateEqualsOld;
        _providerConfigStages[Prefix + "TargetConfigIdEqualsAlternate"] = targetEqualsAlternate;
        _providerConfigStages[Prefix + "TargetConfigIdEqualsOldPrimary"] = targetEqualsOld;
        if (alternateEqualsOld != "UNKNOWN") _alternateConfigIdEqualsOldPrimary = alternateEqualsOld;
        if (targetEqualsAlternate != "UNKNOWN") _targetConfigIdEqualsAlternate = targetEqualsAlternate;
        if (targetEqualsOld != "UNKNOWN") _targetConfigIdEqualsOldPrimary = targetEqualsOld;
    }

#if DEBUG
    internal void ProviderTunnelStage(string stage, RouterManager.VpnTunnelStructuralSnapshot? snapshot,
        int? oldPrimaryConfigId, int? alternateConfigId, int? targetConfigId)
    {
        string prefix = stage + ".";
        if (snapshot is null)
        {
            _providerTunnelStages[prefix + "TunnelReadSucceeded"] = "NO";
            _providerTunnelStages[prefix + "TunnelId"] = "<unavailable>";
            _providerTunnelStages[prefix + "Enabled"] = "<unavailable>";
            _providerTunnelStages[prefix + "ViaPresent"] = "<unavailable>";
            _providerTunnelStages[prefix + "ViaType"] = "<unavailable>";
            _providerTunnelStages[prefix + "ViaConfigCount"] = "<unavailable>";
            _providerTunnelStages[prefix + "ViaGroupId"] = "<unavailable>";
            _providerTunnelStages[prefix + "ViaConfigId"] = "<unavailable>";
            _providerTunnelStages[prefix + "FromType"] = "<unavailable>";
            _providerTunnelStages[prefix + "MacListCount"] = "<unavailable>";
            _providerTunnelStages[prefix + "ToType"] = "<unavailable>";
            _providerTunnelStages[prefix + "ViaMatchesOldPrimary"] = "UNKNOWN";
            _providerTunnelStages[prefix + "ViaMatchesAlternate"] = "UNKNOWN";
            _providerTunnelStages[prefix + "ViaMatchesTarget"] = "UNKNOWN";
            return;
        }

        _providerTunnelStages[prefix + "TunnelReadSucceeded"] = "YES";
        _providerTunnelStages[prefix + "TunnelId"] = snapshot.TunnelId.ToString();
        _providerTunnelStages[prefix + "Enabled"] = snapshot.Enabled ? "true" : "false";
        _providerTunnelStages[prefix + "ViaPresent"] = snapshot.ViaPresent ? "YES" : "NO";
        _providerTunnelStages[prefix + "ViaType"] = Safe(snapshot.ViaType);
        _providerTunnelStages[prefix + "ViaConfigCount"] = snapshot.ViaConfigCount.ToString();
        _providerTunnelStages[prefix + "ViaGroupId"] = Number(snapshot.ViaGroupId);
        _providerTunnelStages[prefix + "ViaConfigId"] = Number(snapshot.ViaConfigId);
        _providerTunnelStages[prefix + "FromType"] = Safe(snapshot.FromType);
        _providerTunnelStages[prefix + "MacListCount"] = snapshot.MacListCount.ToString();
        _providerTunnelStages[prefix + "ToType"] = Safe(snapshot.ToType);
        _providerTunnelStages[prefix + "ViaMatchesOldPrimary"] = Equal(snapshot.ViaConfigId, oldPrimaryConfigId);
        _providerTunnelStages[prefix + "ViaMatchesAlternate"] = Equal(snapshot.ViaConfigId, alternateConfigId);
        _providerTunnelStages[prefix + "ViaMatchesTarget"] = Equal(snapshot.ViaConfigId, targetConfigId);
    }
#endif

    internal void ProviderResetGenerationAttempted() => _providerResetGenerateAttempted = "YES";
    internal void ProviderResetGenerationResult(bool succeeded) => _providerResetGenerateSucceeded = succeeded ? "YES" : "NO";
    internal void ProviderResetCatalogueRefreshAttempted() => _providerResetCatalogueRefreshAttempted = "YES";
    internal void ProviderResetCatalogueRefreshResult(bool succeeded) => _providerResetCatalogueRefreshSucceeded = succeeded ? "YES" : "NO";
    internal void ProviderResetTargetReresolved(bool resolved) => _providerResetTargetReresolved = resolved ? "YES" : "NO";

    internal void ProviderCatalogueStage(bool fetchAttempted, bool? rpcSucceeded, int parsedEntryCount, int validEntryCount, int targetMatchCount,
        bool providerContextAvailable, bool groupContextAvailable, bool credentialsContextAvailable, string? blockedReason)
    {
        _providerCatalogueFetchAttempted = fetchAttempted ? "YES" : "NO";
        _providerCatalogueFetchRpcSucceeded = rpcSucceeded.HasValue ? (rpcSucceeded.Value ? "YES" : "NO") : "UNKNOWN";
        _providerCatalogueParsedEntryCount = parsedEntryCount.ToString();
        _providerCatalogueValidEntryCount = validEntryCount.ToString();
        _providerCatalogueTargetMatchCount = targetMatchCount.ToString();
        _providerCatalogueTargetFound = targetMatchCount == 1 ? "YES" : "NO";
        _providerCatalogueProviderContextAvailable = providerContextAvailable ? "YES" : "NO";
        _providerCatalogueGroupContextAvailable = groupContextAvailable ? "YES" : "NO";
        _providerCatalogueCredentialsContextAvailable = credentialsContextAvailable ? "YES" : "NO";
        _providerResetEntryAttempted = "YES";
        _providerResetEntryBlockedReason = Safe(blockedReason);
    }

    internal void PostAssignmentTunnel(int? groupId, int? configId)
    {
        _postAssignmentGroupId = groupId is > 0 ? groupId : null;
        _postAssignmentConfigId = configId is > 0 ? configId : null;
    }

    internal void PostAssignmentResolvedConfig(int count, string? name, string? location)
    {
        _postAssignmentResolvedMatchCount = count;
        _postAssignmentResolvedName = Safe(name);
        _postAssignmentResolvedLocation = Safe(location);
    }

    public void Dispose()
    {
        if (_emitted) return;
        _emitted = true;
        Emit($"""
            PIA_APPLY_IDENTITY_TRACE

            SelectedCatalogue.Country={_selectedCountry}
            SelectedCatalogue.City={_selectedCity}
            SelectedCatalogue.Hostname={_selectedHostname}

            GenerateRequest.Country={_requestCountry}
            GenerateRequest.City={_requestCity}
            GenerateRequest.Hostname={_requestHostname}

            PreGeneration.NextStep={_preGenerationNextStep}
            PreGeneration.ProviderResolved={_preGenerationProviderResolved}
            PreGeneration.GroupResolved={_preGenerationGroupResolved}
            PreGeneration.CredentialsAvailable={_preGenerationCredentialsAvailable}
            PreGeneration.SelectionValid={_preGenerationSelectionValid}
            PreGeneration.OperationCurrent={_preGenerationOperationCurrent}
            GenerationCall.Attempted={_generationCallAttempted}
            GenerationCall.Result={_generationCallResult}
            PreGeneration.FailureReason={_preGenerationFailureReason}

            PostGenerationMatch.MatchCount={_postGenerationMatchCount}
            PostGenerationMatch.GroupId={Number(_postGenerationGroupId)}
            PostGenerationMatch.ConfigId={Number(_postGenerationConfigId)}
            PostGenerationMatch.Name={_postGenerationName}
            PostGenerationMatch.Location={_postGenerationLocation}

            PostGeneration.NextStep={_nextStep}
            PreAssignmentTunnelRead.Attempted={_preAssignmentTunnelReadAttempted}
            PreAssignmentTunnelRead.Succeeded={_preAssignmentTunnelReadSucceeded}
            AssignmentEligibility.Result={_assignmentEligibilityResult}
            AssignmentEligibility.FailureReason={_assignmentEligibilityFailureReason}
            AssignmentCall.Attempted={_assignmentCallAttempted}

            PreAssignmentTunnel.GroupId={Number(_preAssignmentGroupId)}
            PreAssignmentTunnel.ConfigId={Number(_preAssignmentConfigId)}

            AssignmentIntended.GroupId={Number(_assignmentGroupId)}
            AssignmentIntended.ConfigId={Number(_assignmentConfigId)}

            PostAssignmentTunnel.GroupId={Number(_postAssignmentGroupId)}
            PostAssignmentTunnel.ConfigId={Number(_postAssignmentConfigId)}

            PostAssignmentResolvedConfig.MatchCount={_postAssignmentResolvedMatchCount}
            PostAssignmentResolvedConfig.Name={_postAssignmentResolvedName}
            PostAssignmentResolvedConfig.Location={_postAssignmentResolvedLocation}

            MUTATION_COUNT={_mutationCount}
            CONNECT_MUTATIONS=0

            PRIMARY_ASSIGN.SelectedGroupId={Number(_primarySelectedGroupId)}
            PRIMARY_ASSIGN.GeneratedConfigId={Number(_primaryGeneratedConfigId)}
            PRIMARY_ASSIGN.PreReadSucceeded={_primaryPreReadSucceeded}
            PRIMARY_ASSIGN.PreReadViaType={_primaryPreReadViaType}
            PRIMARY_ASSIGN.PreReadGroupId={Number(_primaryPreReadGroupId)}
            PRIMARY_ASSIGN.PreReadConfigId={Number(_primaryPreReadConfigId)}
            PRIMARY_ASSIGN.Eligibility={_primaryEligibility}
            PRIMARY_ASSIGN.FailureReason={_primaryFailureReason}
            PRIMARY_ASSIGN.SerializationValidated={_primarySerializationValidated}
            PRIMARY_ASSIGN.DispatchAttempted={_primaryDispatchAttempted}
            PRIMARY_ASSIGN.DispatchCompleted={_primaryDispatchCompleted}
            PRIMARY_ASSIGN.RpcSucceeded={_primaryRpcSucceeded}
            PRIMARY_ASSIGN.PostReadSucceeded={_primaryPostReadSucceeded}
            PRIMARY_ASSIGN.PostViaPresent={_primaryPostViaPresent}
            PRIMARY_ASSIGN.PostViaType={_primaryPostViaType}
            PRIMARY_ASSIGN.PostGroupId={Number(_primaryPostGroupId)}
            PRIMARY_ASSIGN.PostConfigId={Number(_primaryPostConfigId)}
            PRIMARY_ASSIGN.Verification={_primaryVerification}
            PRIMARY_ASSIGN.VerificationReason={_primaryVerificationReason}
            PRIMARY_ASSIGN.PreGenerationSnapshot={_primaryPreReadSucceeded}
            PRIMARY_ASSIGN.PreGenerationTunnelId={Number(_primaryPreReadTunnelId)}
            PRIMARY_ASSIGN.PreGenerationGroupId={Number(_preAssignmentGroupId)}
            PRIMARY_ASSIGN.PreGenerationConfigId={Number(_preAssignmentConfigId)}
            PRIMARY_ASSIGN.GenerationSucceeded={_generationCallResult}
            PRIMARY_ASSIGN.GeneratedConfigId={Number(_primaryGeneratedConfigId)}
            PRIMARY_ASSIGN.PostGenerationReadSucceeded={(_postGenerationTransitionRecognized == "YES" ? "YES" : "NO")}
            PRIMARY_ASSIGN.PostGenerationViaPresent={(_postGenerationTransitionReason == "AssociationPreserved" ? "YES" : "NO")}
            PRIMARY_ASSIGN.PostGenerationTransitionRecognized={_postGenerationTransitionRecognized}
            PRIMARY_ASSIGN.PostGenerationTransitionReason={_postGenerationTransitionReason}
            PIA_PROVIDER_LIFECYCLE_TRACE
            BeforeGenerate.GroupId={Number(_primarySelectedGroupId)}
            BeforeGenerate.ConfigCount={_providerLifecycleBeforeConfigCount}
            BeforeGenerate.SelectedHostnameMatchCount={_providerLifecycleBeforeSelectedMatchCount}
            BeforeGenerate.CurrentTunnelConfigId={Number(_preAssignmentConfigId)}
            Generate.SelectedCountry={_selectedCountry}
            Generate.SelectedCity={_selectedCity}
            Generate.SelectedHostname={_selectedHostname}
            Generate.RpcSucceeded={_generationCallResult}
            AfterGenerate.ConfigCount={_providerLifecycleAfterConfigCount}
            AfterGenerate.SelectedHostnameMatchCount={_providerLifecycleAfterSelectedMatchCount}
            AfterGenerate.GeneratedConfigId={Number(_postGenerationConfigId)}
            AfterGenerate.OldConfigStillPresent={_providerLifecycleOldStillPresent}
            AfterGenerate.ConfigIdReused={_providerLifecycleIdReused}
            ProviderReset.Required={_providerResetRequired}
            ProviderReset.TargetCountry={_selectedCountry}
            ProviderReset.TargetCity={_selectedCity}
            ProviderReset.TargetHostname={_providerResetTargetHostname}
            ProviderReset.AlternateFound={_providerResetAlternateFound}
            ProviderReset.AlternateCountry={_providerResetAlternateCountry}
            ProviderReset.AlternateCity={_providerResetAlternateCity}
            ProviderReset.AlternateHostname={_providerResetAlternateHostname}
            ProviderReset.GenerateAttempted={_providerResetGenerateAttempted}
            ProviderReset.GenerateSucceeded={_providerResetGenerateSucceeded}
            ProviderReset.CatalogueRefreshAttempted={_providerResetCatalogueRefreshAttempted}
            ProviderReset.CatalogueRefreshSucceeded={_providerResetCatalogueRefreshSucceeded}
            ProviderReset.TargetReResolved={_providerResetTargetReresolved}
            ProviderCatalogue.FetchAttempted={_providerCatalogueFetchAttempted}
            ProviderCatalogue.FetchRpcSucceeded={_providerCatalogueFetchRpcSucceeded}
            ProviderCatalogue.RawStructuralCount={_providerCatalogueRawStructuralCount}
            ProviderCatalogue.ParsedEntryCount={_providerCatalogueParsedEntryCount}
            ProviderCatalogue.ValidEntryCount={_providerCatalogueValidEntryCount}
            ProviderCatalogue.TargetMatchCount={_providerCatalogueTargetMatchCount}
            ProviderCatalogue.TargetFound={_providerCatalogueTargetFound}
            ProviderCatalogue.ProviderContextAvailable={_providerCatalogueProviderContextAvailable}
            ProviderCatalogue.GroupContextAvailable={_providerCatalogueGroupContextAvailable}
            ProviderCatalogue.CredentialsContextAvailable={_providerCatalogueCredentialsContextAvailable}
            ProviderReset.EntryAttempted={_providerResetEntryAttempted}
            ProviderReset.EntryBlockedReason={_providerResetEntryBlockedReason}
            LogicalTarget.Country={_logicalTargetCountry}
            LogicalTarget.City={_logicalTargetCity}
            LogicalTarget.OriginalHostname={_logicalTargetOriginalHostname}
            InitialResolution.CountryCityMatchCount={_initialResolutionCountryCityMatchCount}
            InitialResolution.UniqueHostnameCount={_initialResolutionUniqueHostnameCount}
            InitialResolution.Result={_initialResolutionResult}
            InitialResolution.AuthoritativeHostname={_initialResolutionAuthoritativeHostname}
            InitialResolution.HostnameChanged={_initialResolutionHostnameChanged}
            PostResetResolution.CountryCityMatchCount={_postResetResolutionCountryCityMatchCount}
            PostResetResolution.UniqueHostnameCount={_postResetResolutionUniqueHostnameCount}
            PostResetResolution.Result={_postResetResolutionResult}
            PostResetResolution.AuthoritativeHostname={_postResetResolutionAuthoritativeHostname}
            PostResetResolution.HostnameChanged={_postResetResolutionHostnameChanged}
            {_providerConfigStagesText}
            {_providerTunnelStagesText}
            AlternateConfigIdEqualsOldPrimary={_alternateConfigIdEqualsOldPrimary}
            TargetConfigIdEqualsAlternate={_targetConfigIdEqualsAlternate}
            TargetConfigIdEqualsOldPrimary={_targetConfigIdEqualsOldPrimary}
            TargetGenerate.Attempted={_generationCallAttempted}
            TargetGenerate.Succeeded={_generationCallResult}
            TargetGenerate.MatchCount={_postGenerationMatchCount}
            TargetGenerate.ConfigId={Number(_postGenerationConfigId)}
            PrimaryAssignment.Attempted={_assignmentCallAttempted}
            PrimaryAssignment.ConfigId={Number(_assignmentConfigId)}
            PrimaryAssignment.Verified={(_primaryVerification == "PASS" ? "YES" : "NO")}
            """);
    }

    private static void Emit(string message)
    {
#if DEBUG
        Debug.WriteLine(message);
#else
        Trace.WriteLine(message);
#endif
    }

    private static string Number(int? value) => value?.ToString() ?? "<unavailable>";

    private string _providerConfigStagesText => _providerConfigStages.Count == 0
        ? string.Empty
        : "PIA_PROVIDER_CONFIG_LIFECYCLE_TRACE\n" + string.Join("\n", _providerConfigStages.Select(item => item.Key + "=" + item.Value));

    private string _providerTunnelStagesText => _providerTunnelStages.Count == 0
        ? string.Empty
        : "PIA_PROVIDER_TUNNEL_LIFECYCLE_TRACE\n" + string.Join("\n", _providerTunnelStages.Select(item => item.Key + "=" + item.Value));

    private static string Equal(int? left, int? right) => left.HasValue && right.HasValue ? (left == right ? "YES" : "NO") : "UNKNOWN";

    private static string Safe(string? value)
    {
        string normalized = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length is > 0 and <= 256 ? normalized : "<unavailable>";
    }
}
