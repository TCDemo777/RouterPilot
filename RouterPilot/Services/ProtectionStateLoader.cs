using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

internal sealed record ProtectionStateSnapshot(
    AdGuardProtectionStatus Status,
    AdGuardStatistics Statistics,
    AdGuardProtectionOptions Options,
    bool CatalogueRefreshed,
    AdGuardBlockedServicesConfig BlockedConfig,
    IReadOnlyList<CustomFilteringRule> Rules,
    IReadOnlyList<DnsRewriteRule> Rewrites,
    IReadOnlyList<QueryLogEntry> QueryLog);

/// <summary>Loads a coherent Protection snapshot without mutating UI state.</summary>
internal sealed class ProtectionStateLoader
{
    private readonly IRouterManagerProvider _routers;
    private readonly IAdGuardServiceCatalogueProvider _catalogue;

    internal ProtectionStateLoader(IRouterManagerProvider routers, IAdGuardServiceCatalogueProvider catalogue)
    {
        _routers = routers;
        _catalogue = catalogue;
    }

    internal async Task<ProtectionStateSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        RouterManager router = await _routers.GetRouterManagerAsync();
        AdGuardProtectionStatus status = await router.GetAdGuardProtectionStatusAsync();
        AdGuardStatistics statistics = await router.GetAdGuardStatisticsAsync();
        AdGuardProtectionOptions options = await router.GetProtectionOptionsAsync();
        bool catalogueRefreshed = await _catalogue.RefreshAsync(router, cancellationToken);
        AdGuardBlockedServicesConfig blockedConfig = await router.GetBlockedServicesConfigAsync();
        IReadOnlyList<CustomFilteringRule> rules = await router.GetCustomFilteringRulesAsync();
        IReadOnlyList<DnsRewriteRule> rewrites = await router.GetDnsRewritesAsync();
        IReadOnlyList<QueryLogEntry> queryLog = await router.GetQueryLogAsync();
        return new(status, statistics, options, catalogueRefreshed, blockedConfig, rules, rewrites, queryLog);
    }
}
