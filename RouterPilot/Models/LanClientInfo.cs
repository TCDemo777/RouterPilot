namespace RouterPilot.Models;

public sealed class LanClientInfo
{
    public string Name { get; init; } = "Unknown device";
    public string IpAddress { get; init; } = "—";
    public string MacAddress { get; init; } = "—";
    public string ConnectionType { get; init; } = "Wired";
    public string InterfaceDisplay { get; init; } = "cable";
    public string Interface { get; init; } = "Ethernet";
    public bool IsStaticReservation { get; init; }
    public bool IsOnline { get; init; } = true;
}
