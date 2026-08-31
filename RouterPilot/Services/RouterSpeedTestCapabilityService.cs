using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>
/// Performs the read-only inventory of speed-test executables available on a
/// router. It intentionally does not execute any detected tool.
/// </summary>
internal sealed class RouterSpeedTestCapabilityService
{
    private readonly GLInetSshService _ssh;

    public RouterSpeedTestCapabilityService(GLInetSshService ssh)
    {
        _ssh = ssh;
    }

    public async Task<RouterSpeedTestCapability> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        const string discoveryCommand =
            "for tool in speedtest speedtest-cli speedtest-netperf speedtestpp librespeed-cli iperf3 netperf; do " +
            "if command -v \"$tool\" >/dev/null 2>&1; then printf '%s\\n' \"$tool\"; fi; done";

        string output = await _ssh.RunCommandAsync(discoveryCommand, cancellationToken);
        if (output.StartsWith("SSH_", StringComparison.OrdinalIgnoreCase))
            return new RouterSpeedTestCapability { SafeStatus = "ssh-unavailable" };

        string? detected = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .FirstOrDefault(value => value is "speedtest" or "speedtest-cli" or "speedtest-netperf" or
                "speedtestpp" or "librespeed-cli" or "iperf3" or "netperf");

        return new RouterSpeedTestCapability
        {
            // iperf3/netperf alone are not Internet tests without a verified
            // remote server, and no installed CLI is assumed safe to invoke.
            IsSupported = false,
            DetectedBinary = detected,
            SafeStatus = detected is null ? "unavailable" : "unverified-backend"
        };
    }
}
