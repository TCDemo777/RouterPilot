using System.Collections.Generic;
using System.Linq;
using System.Net;

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
    bool? Online)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "Unnamed device" : Name;
    public string AddressDisplay => Addresses.Count == 0 ? "—" : string.Join(", ", Addresses);
    public string OnlineDisplay => Online switch { true => "Online", false => "Offline", _ => "—" };
}

public sealed record TailscaleStatus(
    TailscaleState State,
    string Detail,
    string Version,
    string DeviceName,
    string DnsName,
    IReadOnlyList<string> Addresses,
    IReadOnlyList<TailscalePeer> Peers)
{
    public bool PeerDataAvailable { get; init; }
    public static TailscaleStatus Unavailable(string detail) =>
        new(TailscaleState.Unavailable, detail, string.Empty, string.Empty, string.Empty, [], []);

    public int? PeerCount => State == TailscaleState.Connected && PeerDataAvailable ? Peers.Count : null;
    public int? OnlinePeerCount => State == TailscaleState.Connected && PeerDataAvailable ? Peers.Count(x => x.Online == true) : null;
    public string IPv4 => Addresses.FirstOrDefault(x => IPAddress.TryParse(x, out IPAddress? address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? string.Empty;
    public string IPv6 => Addresses.FirstOrDefault(x => IPAddress.TryParse(x, out IPAddress? address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6) ?? string.Empty;
}
