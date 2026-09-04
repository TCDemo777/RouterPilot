using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace RouterPilot.Services;

/// <summary>
/// Interprets the two read-only runtime probes used by GL.iNet's
/// AdGuardHome service.  Some firmware init scripts do not implement a
/// useful <c>status</c> action, so a live process is authoritative evidence
/// that the service is running.
/// </summary>
internal static partial class AdGuardRuntimeStatusParser
{
    private static readonly Regex ProcessLine =
        new(@"^\s*\d+\s+.*AdGuardHome", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool IsRunning(string? serviceOutput, string? processOutput)
    {
        string service = serviceOutput?.Trim() ?? string.Empty;
        bool explicitlyStopped =
            service.Contains("not running", StringComparison.OrdinalIgnoreCase) ||
            service.Contains("stopped", StringComparison.OrdinalIgnoreCase) ||
            service.Contains("inactive", StringComparison.OrdinalIgnoreCase);

        bool serviceReportsRunning =
            !explicitlyStopped &&
            service.Contains("running", StringComparison.OrdinalIgnoreCase);

        bool processReportsRunning =
            !string.IsNullOrWhiteSpace(processOutput) &&
            processOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Any(line => ProcessLine.IsMatch(line.Trim()));

        return serviceReportsRunning || processReportsRunning;
    }

    internal static string ProcessDisplay(string? processOutput) =>
        IsProcessOutputUsable(processOutput)
            ? processOutput!.Trim()
            : "Not Running";

    private static bool IsProcessOutputUsable(string? processOutput) =>
        !string.IsNullOrWhiteSpace(processOutput) &&
        processOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(line => ProcessLine.IsMatch(line.Trim()));
}
