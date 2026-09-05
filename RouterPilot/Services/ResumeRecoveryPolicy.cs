using System;

namespace RouterPilot.Services;

internal static class ResumeRecoveryPolicy
{
    internal static TimeSpan[] Delays { get; } =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(6),
        TimeSpan.FromSeconds(15)
    ];

    internal static bool IsRecovered(bool routerConnected, bool adGuardAvailable) =>
        routerConnected && adGuardAvailable;
}
