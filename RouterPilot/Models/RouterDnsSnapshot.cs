using System;
using System.Collections.Generic;

namespace RouterPilot.Models;

public enum RouterDnsMode { Automatic, Manual, AdGuard, DoH, DoT, Encrypted, Vpn, Mixed, Unknown }
public enum RouterDnsRuntimeState { Running, Stopped, Unknown }
public enum RouterDnsEncryptionMode { Plain, DoH, DoT, Encrypted, Unknown }

public sealed record RouterDnsSnapshot(
    RouterCapabilityState CapabilityState,
    RouterDnsMode Mode,
    RouterDnsRuntimeState RuntimeState,
    RouterDnsEncryptionMode EncryptionMode,
    IReadOnlyList<string> UpstreamResolvers,
    bool? AdGuardHandlesClientRequests,
    string? VpnDnsState,
    DateTimeOffset CapturedAt)
{
    public static RouterDnsSnapshot Unknown { get; } = new(
        RouterCapabilityState.Unknown,
        RouterDnsMode.Unknown,
        RouterDnsRuntimeState.Unknown,
        RouterDnsEncryptionMode.Unknown,
        Array.Empty<string>(),
        null,
        null,
        DateTimeOffset.UtcNow);
}
