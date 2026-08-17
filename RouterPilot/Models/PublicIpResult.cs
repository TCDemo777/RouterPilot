using System;

namespace RouterPilot.Models;

public enum PublicIpStatus
{
    Unknown,
    Loading,
    Available,
    Unavailable,
    TimedOut
}

public sealed record PublicIpResult(
    string? PublicIp,
    DateTimeOffset CheckedAt,
    PublicIpStatus Status,
    string? FailureReason)
{
    public static PublicIpResult Initial { get; } = new(null, DateTimeOffset.MinValue, PublicIpStatus.Unknown, null);
}
