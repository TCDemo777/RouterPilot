using System;
using System.Collections.Generic;

namespace RouterPilot.Services;

/// <summary>
/// Bounded, privacy-safe resume breadcrumbs included in the existing
/// diagnostic report so a failed wake can be inspected without restarting.
/// </summary>
internal static class ResumeRecoveryDiagnostics
{
    private static readonly object Sync = new();
    private static readonly Queue<string> Entries = new();

    internal static void Record(string message)
    {
        lock (Sync)
        {
            Entries.Enqueue($"{DateTimeOffset.UtcNow:O} {message}");
            while (Entries.Count > 80) Entries.Dequeue();
        }
    }

    internal static string Report()
    {
        lock (Sync)
        {
            return Entries.Count == 0
                ? "No resume recovery events recorded."
                : string.Join(Environment.NewLine, Entries);
        }
    }
}
