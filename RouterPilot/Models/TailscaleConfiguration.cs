namespace RouterPilot.Models;

public enum TailscaleCapabilityState { Supported, Unsupported, Unknown }

public sealed record TailscaleConfigurationSnapshot(
    bool? LanEnabled,
    bool? WanEnabled,
    TailscaleCapabilityState LanCapability,
    TailscaleCapabilityState WanCapability)
{
    public static TailscaleConfigurationSnapshot Unknown => new(null, null, TailscaleCapabilityState.Unknown, TailscaleCapabilityState.Unknown);
    public string LanDisplay => LanEnabled is bool value ? (value ? "On" : "Off") : "—";
    public string WanDisplay => WanEnabled is bool value ? (value ? "On" : "Off") : "—";
}
