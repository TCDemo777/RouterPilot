using System;
using RouterPilot.Models;

namespace RouterPilot.Services;

internal static class ResumeRecoveryPolicy
{
    // Matches the existing freshness re-establishment window. Resume recovery
    // must finish or cancel within this bounded lifecycle period.
    internal static TimeSpan MaximumRecoveryWindow { get; } = TimeSpan.FromMinutes(2);

    // The initial short delays preserve prompt recovery for routers that are
    // immediately reachable.  The later spacing keeps trying throughout the
    // existing two-minute re-establishment window instead of giving up after
    // the first 23 seconds of a Windows network restore.
    internal static TimeSpan[] Delays { get; } =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(6),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(30)
    ];

    internal static bool IsRecovered(bool routerConnected, bool adGuardAvailable) =>
        routerConnected && adGuardAvailable;

    internal static bool ShouldContinue(AdGuardAvailabilityState availability) =>
        availability != AdGuardAvailabilityState.NotConfigured;
}
