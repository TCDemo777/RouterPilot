namespace RouterPilot.Models;

public enum RouterCapabilityState
{
    Supported,
    Unsupported,
    Unknown
}

/// <summary>Read-only capability evidence for the active router.</summary>
public sealed record RouterCapabilitySnapshot(
    RouterCapabilityState Temperature,
    RouterCapabilityState EthernetPortTelemetry,
    RouterCapabilityState WifiRadioTelemetry,
    RouterCapabilityState MultiWan,
    RouterCapabilityState DnsTelemetry,
    RouterCapabilityState PerformanceTelemetry,
    RouterCapabilityState FanTelemetry,
    RouterCapabilityState ZramTelemetry,
    RouterCapabilityState HardwareCryptoTelemetry,
    RouterCapabilityState VpnTelemetry)
{
    public static RouterCapabilitySnapshot Unknown { get; } = new(
        RouterCapabilityState.Unknown,
        RouterCapabilityState.Unknown,
        RouterCapabilityState.Unknown,
        RouterCapabilityState.Unknown,
        RouterCapabilityState.Unknown,
        RouterCapabilityState.Unknown,
        RouterCapabilityState.Unknown,
        RouterCapabilityState.Unknown,
        RouterCapabilityState.Unknown,
        RouterCapabilityState.Unknown);

    public static RouterCapabilityState FromEvidence(bool available) =>
        available ? RouterCapabilityState.Supported : RouterCapabilityState.Unknown;
}
