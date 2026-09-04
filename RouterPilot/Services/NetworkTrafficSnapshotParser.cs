using System;
using RouterPilot.Models;

namespace RouterPilot.Services
{
    /// <summary>
    /// Converts the compact WAN counter response into the existing snapshot model.
    /// This parser is deliberately stateless; transport and router lifecycle remain
    /// owned by RouterManager.
    /// </summary>
    internal static class NetworkTrafficSnapshotParser
    {
        public static NetworkTrafficSnapshot Parse(string? output, DateTime capturedAtUtc)
        {
            string[] parts = (output ?? string.Empty).Trim().Split('|');

            bool valid = parts.Length >= 3 && long.TryParse(parts[1].Trim(), out long received) &&
                         long.TryParse(parts[2].Trim(), out long transmitted) && received >= 0 && transmitted >= 0 &&
                         !string.IsNullOrWhiteSpace(parts[0]) && !parts[0].StartsWith("SSH_", StringComparison.OrdinalIgnoreCase);
            return new NetworkTrafficSnapshot
            {
                InterfaceName = parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0])
                    ? parts[0].Trim()
                    : "-",
                ReceivedBytes = parts.Length > 1 && long.TryParse(parts[1].Trim(), out received) ? received : 0,
                TransmittedBytes = parts.Length > 2 && long.TryParse(parts[2].Trim(), out transmitted) ? transmitted : 0,
                IsValid = valid,
                CapturedAtUtc = capturedAtUtc
            };
        }
    }
}
