using System;

namespace RouterPilot.Models;

public enum DataFreshnessState
{
    Loading,
    Fresh,
    Stale,
    Unavailable
}

public sealed record DataFreshnessInfo(
    string Source,
    DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? LastAttemptUtc,
    DataFreshnessState State,
    TimeSpan ExpectedRefreshInterval);
