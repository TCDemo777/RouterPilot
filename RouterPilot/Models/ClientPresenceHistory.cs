using System;

namespace RouterPilot.Models;

public enum ClientPresenceState { Online, Offline }

public sealed class ClientPresencePeriod
{
    public string NormalizedMac { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public DateTimeOffset LastObservedAt { get; set; }
    public ClientPresenceState State { get; set; }
}
