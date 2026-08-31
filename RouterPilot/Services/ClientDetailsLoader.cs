using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>
/// Loads the authoritative query-log data used by Client Details.  The
/// loader deliberately returns data rather than mutating presentation state;
/// the ViewModel remains responsible for applying and presenting the result.
/// </summary>
internal sealed class ClientDetailsLoader
{
    private readonly IRouterManagerProvider _routerManagerProvider;

    public ClientDetailsLoader(IRouterManagerProvider routerManagerProvider)
    {
        _routerManagerProvider = routerManagerProvider;
    }

    public async Task<ClientDetailsSnapshot> LoadActivityAsync(
        ClientInfo client,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        RouterManager routerManager =
            await _routerManagerProvider.GetRouterManagerAsync();

        cancellationToken.ThrowIfCancellationRequested();

        AdGuardQueryLogReadResult result =
            await routerManager.GetQueryLogResultAsync();

        cancellationToken.ThrowIfCancellationRequested();

        return new ClientDetailsSnapshot(
            result.IsAvailable,
            result.Entries
                .Where(entry => MatchesClient(entry, client))
                .ToList());
    }

    private static bool MatchesClient(QueryLogEntry entry, ClientInfo client)
    {
        return SameText(entry.ClientAddress, client.IpAddress) ||
               SameText(entry.ClientName, client.Name) ||
               SameText(entry.Client, client.IpAddress) ||
               SameText(entry.Client, client.Name) ||
               ContainsIdentifier(entry.Client, client.IpAddress);
    }

    private static bool SameText(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) ||
            string.IsNullOrWhiteSpace(second) ||
            second == "-")
        {
            return false;
        }

        return string.Equals(
            first.Trim(),
            second.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsIdentifier(string? displayValue, string? identifier)
    {
        if (string.IsNullOrWhiteSpace(displayValue) ||
            string.IsNullOrWhiteSpace(identifier) ||
            identifier == "-")
        {
            return false;
        }

        return displayValue.Contains(
            identifier.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record ClientDetailsSnapshot(
    bool IsAvailable,
    IReadOnlyList<QueryLogEntry> Entries);
