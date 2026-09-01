using System.Collections.Generic;

namespace RouterPilot.Models;

public enum RouterInterfaceType
{
    PhysicalEthernet,
    Bridge,
    Logical,
    Vlan,
    Wireless,
    Vpn,
    Loopback,
    Virtual,
    Unknown
}

public sealed record RouterPortSnapshot(
    string Id,
    string InterfaceName,
    string FriendlyName,
    string Role,
    RouterInterfaceType InterfaceType,
    bool? Carrier,
    string LinkState,
    int? NegotiatedSpeedMbps,
    string Duplex,
    string MacAddress,
    IReadOnlyList<string> IPv4Addresses,
    IReadOnlyList<string> IPv6Addresses,
    long? RxBytes,
    long? TxBytes,
    long? RxErrors,
    long? TxErrors,
    long? RxDropped,
    long? TxDropped,
    string? Bridge,
    bool IsPhysical,
    bool IsVirtual)
{
    public string SpeedDisplay => NegotiatedSpeedMbps switch
    {
        >= 1000 => $"{NegotiatedSpeedMbps / 1000.0:0.0} Gbps",
        > 0 => $"{NegotiatedSpeedMbps} Mbps",
        _ => "—"
    };

    public string RxBytesDisplay => FormatBytes(RxBytes);
    public string TxBytesDisplay => FormatBytes(TxBytes);
    public string ErrorsDisplay => RxErrors is { } rx && TxErrors is { } tx ? $"{rx} / {tx}" : "—";
    public string DropsDisplay => RxDropped is { } rx && TxDropped is { } tx ? $"{rx} / {tx}" : "—";

    private static string FormatBytes(long? value)
    {
        if (value is not { } bytes || bytes < 0) return "—";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double amount = bytes;
        int unit = 0;
        while (amount >= 1024 && unit < units.Length - 1) { amount /= 1024; unit++; }
        return unit == 0 ? $"{bytes} B" : $"{amount:0.0} {units[unit]}";
    }
}
