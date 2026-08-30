using System.Collections.Generic;
using System.Linq;

namespace RouterPilot.Models;

public enum TailscaleState
{
    Unavailable,
    NotInstalled,
    Incompatible,
    Stopped,
    NeedsLogin,
    Connected
}

public sealed record TailscalePeer(
    string Name,
    string DnsName,
    IReadOnlyList<string> Addresses,
    bool? Online);

public sealed record TailscaleStatus(
    TailscaleState State,
    string Detail,
    string Version,
    string DeviceName,
    string DnsName,
    IReadOnlyList<string> Addresses,
    IReadOnlyList<TailscalePeer> Peers)
{
    public static TailscaleStatus Unavailable(string detail) =>
        new(TailscaleState.Unavailable, detail, string.Empty, string.Empty, string.Empty, [], []);

    public int? PeerCount => State == TailscaleState.Connected ? Peers.Count : null;
    public int? OnlinePeerCount => State == TailscaleState.Connected ? Peers.Count(x => x.Online == true) : null;
}
