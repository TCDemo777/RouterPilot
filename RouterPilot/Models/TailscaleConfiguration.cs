namespace RouterPilot.Models;

public enum TailscaleCapabilityState { Supported, Unsupported, Unknown }

public sealed record TailscaleConfigurationSnapshot(
    bool? Enabled,
    bool? LanEnabled,
    bool? WanEnabled,
    TailscaleCapabilityState EnabledCapability,
    TailscaleCapabilityState LanCapability,
    TailscaleCapabilityState WanCapability)
{
    public static TailscaleConfigurationSnapshot Unknown => new(null, null, null, TailscaleCapabilityState.Unknown, TailscaleCapabilityState.Unknown, TailscaleCapabilityState.Unknown);
    public string EnabledDisplay => Enabled is bool value ? (value ? "Enabled" : "Disabled") : "—";
    public string LanDisplay => LanEnabled is bool value ? (value ? "Enabled" : "Disabled") : "—";
    public string WanDisplay => WanEnabled is bool value ? (value ? "Enabled" : "Disabled") : "—";
}
