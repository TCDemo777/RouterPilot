using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>Coordinates best-effort identity enrichment without owning client state.</summary>
internal sealed class ClientIdentityEnrichmentCoordinator
{
    private readonly IDeviceIdentityResolver _resolver;
    private readonly IMdnsIdentityService _mdns;

    internal ClientIdentityEnrichmentCoordinator(IDeviceIdentityResolver resolver, IMdnsIdentityService mdns)
    {
        _resolver = resolver;
        _mdns = mdns;
    }

    internal Task<List<(ClientInfo Client, string Manufacturer)>> ResolveManufacturersAsync(
        IReadOnlyList<ClientInfo> clients, CancellationToken cancellationToken = default) =>
        Task.WhenAll(clients.Select(async client =>
            (client, await _resolver.ResolveManufacturerAsync(client.MacAddress, client.Name, client.Manufacturer, cancellationToken))))
            .ContinueWith(task => task.Result.ToList(), cancellationToken, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    internal async Task<List<(ClientInfo Client, string Hostname)>> ResolveMdnsAsync(
        IReadOnlyList<ClientInfo> clients, Func<string?, bool> hasUsableIp, CancellationToken cancellationToken = default)
    {
        using SemaphoreSlim gate = new(4, 4);
        var results = new List<(ClientInfo Client, string Hostname)>();
        foreach (ClientInfo client in clients)
        {
            if (!hasUsableIp(client.IpAddress)) continue;
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string? hostname = await _mdns.ResolveHostnameAsync(client.IpAddress, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(hostname)) results.Add((client, hostname));
            }
            finally { gate.Release(); }
        }
        return results;
    }
}
