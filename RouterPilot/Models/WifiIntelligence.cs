namespace RouterPilot.Models;

public sealed class WifiSignalQualitySummary
{
    public string Quality { get; init; } = string.Empty;
    public int Count { get; init; }
}

public sealed class WifiWeakClientInfo
{
    public string Name { get; init; } = "Unknown device";
    public string Band { get; init; } = "-";
    public string Ssid { get; init; } = "-";
    public string Signal { get; init; } = "-";
    public string SignalQuality { get; init; } = "-";
    public int SignalDbm { get; init; }
}
