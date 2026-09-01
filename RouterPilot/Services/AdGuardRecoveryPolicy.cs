using System;
using System.Net.Http;

namespace RouterPilot.Services;

internal static class AdGuardRecoveryPolicy
{
    internal static bool ShouldRetryTransport(Exception exception, bool cancellationRequested, bool alreadyRetried) =>
        exception is HttpRequestException && !cancellationRequested && !alreadyRetried;
}
