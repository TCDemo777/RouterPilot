using System.Threading;

namespace RouterPilot.Services;

/// <summary>
/// Orders AdGuard UI refreshes across a connection lifecycle change. Results
/// started before a suspend/resume boundary must not replace newer state.
/// </summary>
internal sealed class AdGuardRefreshEpoch
{
    private long _value;

    internal long Capture() => Interlocked.Read(ref _value);

    internal long Advance() => Interlocked.Increment(ref _value);

    internal bool IsCurrent(long value) => value == Interlocked.Read(ref _value);
}
