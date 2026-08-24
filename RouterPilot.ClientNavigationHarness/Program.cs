using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;
using RouterPilot.Services;

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static ClientInfo Client(string mac, string name, string ip) => new()
{
    MacAddress = mac,
    Name = name,
    RouterName = name,
    IpAddress = ip
};

static ClientProfile Profile(string mac, string name) => new()
{
    Key = ClientIdentity.NormalizeMac(mac),
    Nickname = name
};

const string targetMac = "AA:BB:CC:DD:EE:01";
ClientInfo target = Client(targetMac, "Office laptop", "192.168.8.31");

async Task<ClientDetailsNavigationTarget?> ResolveColdAsync(
    ClientInventoryState inventory,
    ClientInventoryCoordinator coordinator,
    IReadOnlyDictionary<string, ClientProfile>? profiles = null,
    string identity = targetMac) =>
    await ClientDetailsNavigationPreparation.ResolveAsync(
        identity,
        inventory,
        coordinator,
        profiles ?? new Dictionary<string, ClientProfile>(StringComparer.OrdinalIgnoreCase));

foreach (string source in new[] { "ColdAnalyticsDeepLink", "ColdNetworkDeepLink" })
{
    var inventory = new ClientInventoryState();
    int reconciliationCount = 0;
    var coordinator = new ClientInventoryCoordinator(inventory, async _ =>
    {
        reconciliationCount++;
        await Task.Yield();
        return new[] { target };
    });

    ClientDetailsNavigationTarget? result = await ResolveColdAsync(inventory, coordinator);
    Require(reconciliationCount == 1, $"{source} did not perform exactly one shared reconciliation.");
    Require(ReferenceEquals(result?.LiveClient, target), $"{source} did not return the authoritative client object.");
}

var wiredInventory = new ClientInventoryState();
ClientInfo wired = Client("AA:BB:CC:DD:EE:02", "Desk switch", "192.168.8.42");
var wiredCoordinator = new ClientInventoryCoordinator(wiredInventory, _ =>
    Task.FromResult<IReadOnlyList<ClientInfo>>(new[] { wired }));
ClientDetailsNavigationTarget? wiredResult = await ResolveColdAsync(
    wiredInventory, wiredCoordinator, identity: wired.MacAddress);
Require(ReferenceEquals(wiredResult?.LiveClient, wired), "Wired inventory-only client was not resolved.");

const string profileMac = "AA:BB:CC:DD:EE:03";
var profileInventory = new ClientInventoryState();
int profileLoadCount = 0;
var profileCoordinator = new ClientInventoryCoordinator(profileInventory, _ =>
{
    profileLoadCount++;
    return Task.FromResult<IReadOnlyList<ClientInfo>>(Array.Empty<ClientInfo>());
});
var profiles = new Dictionary<string, ClientProfile>(StringComparer.OrdinalIgnoreCase)
{
    [ClientIdentity.NormalizeMac(profileMac)] = Profile(profileMac, "Offline camera")
};
ClientDetailsNavigationTarget? profileResult = await ResolveColdAsync(
    profileInventory, profileCoordinator, profiles, profileMac);
Require(profileResult?.Profile is not null && profileResult.LiveClient is null, "Profile-only client did not use the offline target.");
Require(profileLoadCount == 0, "Profile-only client unnecessarily reconciled live inventory.");

var unknownInventory = new ClientInventoryState();
var unknownCoordinator = new ClientInventoryCoordinator(unknownInventory, _ =>
    Task.FromResult<IReadOnlyList<ClientInfo>>(Array.Empty<ClientInfo>()));
Require(await ResolveColdAsync(unknownInventory, unknownCoordinator, identity: "AA:BB:CC:DD:EE:99") is null,
    "Unknown MAC produced a navigation target.");

var identityInventory = new ClientInventoryState();
ClientInfo sameNameA = Client("AA:BB:CC:DD:EE:04", "Shared name", "192.168.8.50");
ClientInfo sameNameB = Client("AA:BB:CC:DD:EE:05", "Shared name", "192.168.8.51");
identityInventory.Update(new[] { sameNameA, sameNameB });
var identityCoordinator = new ClientInventoryCoordinator(identityInventory, _ =>
    Task.FromResult<IReadOnlyList<ClientInfo>>(new[] { sameNameA, sameNameB }));
ClientDetailsNavigationTarget? normalized = await ResolveColdAsync(
    identityInventory, identityCoordinator, identity: "aa:bb:cc:dd:ee:04");
Require(ReferenceEquals(normalized?.LiveClient, sameNameA), "MAC normalization or duplicate-name resolution selected the wrong client.");
Require(normalized?.LiveClient?.IpAddress == "192.168.8.50", "A stale or unrelated IP changed MAC-backed resolution.");

var warmInventory = new ClientInventoryState();
int warmLoadCount = 0;
var warmCoordinator = new ClientInventoryCoordinator(warmInventory, _ =>
{
    warmLoadCount++;
    return Task.FromResult<IReadOnlyList<ClientInfo>>(new[] { target });
});
ClientDetailsNavigationTarget? cold = await ResolveColdAsync(warmInventory, warmCoordinator);
ClientDetailsNavigationTarget? warm = await ResolveColdAsync(warmInventory, warmCoordinator);
Require(ReferenceEquals(cold?.LiveClient, warm?.LiveClient) && warmLoadCount == 1,
    "Cold and warm deep links did not reuse the same authoritative client state.");

var concurrentInventory = new ClientInventoryState();
int concurrentLoadCount = 0;
var concurrentCoordinator = new ClientInventoryCoordinator(concurrentInventory, async _ =>
{
    Interlocked.Increment(ref concurrentLoadCount);
    await Task.Delay(25);
    return new[] { target };
});
ClientDetailsNavigationTarget?[] concurrent = await Task.WhenAll(
    ResolveColdAsync(concurrentInventory, concurrentCoordinator),
    ResolveColdAsync(concurrentInventory, concurrentCoordinator));
Require(concurrentLoadCount == 1 && concurrent.All(result => ReferenceEquals(result?.LiveClient, target)),
    "Concurrent deep links did not coalesce authoritative reconciliation.");

Console.WriteLine("Client Details deep-link regression fixtures passed: 8/8.");
