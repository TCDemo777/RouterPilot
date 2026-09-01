using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;
using RouterPilot.Configuration;

namespace RouterPilot.Services
{
    public partial class RouterManager
    {
        // Client diagnostics
        //

        public async Task<string> PingClientAsync(string ipAddress)
        {
            if (!IPAddress.TryParse(ipAddress, out IPAddress? parsedAddress))
            {
                throw new ArgumentException(
                    "The client IP address is invalid.",
                    nameof(ipAddress));
            }

            string safeAddress = parsedAddress.ToString();

            string output = await _ssh.RunCommandAsync(
                $"ping -c 3 -W 2 {safeAddress} 2>&1");

            if (output.Contains("0% packet loss", StringComparison.OrdinalIgnoreCase))
            {
                string latency = "reachable";
                int marker = output.IndexOf("min/avg/max", StringComparison.OrdinalIgnoreCase);

                if (marker >= 0)
                {
                    int equals = output.IndexOf('=', marker);
                    int ms = output.IndexOf(" ms", equals, StringComparison.OrdinalIgnoreCase);

                    if (equals >= 0 && ms > equals)
                    {
                        string[] values = output[(equals + 1)..ms]
                            .Trim()
                            .Split('/');

                        if (values.Length >= 2)
                        {
                            latency = $"{values[1]} ms average";
                        }
                    }
                }

                return $"{safeAddress} is online ({latency}).";
            }

            return $"{safeAddress} did not respond to ping.";
        }

        public async Task<string> WakeClientAsync(string macAddress)
        {
            string normalized = (macAddress ?? string.Empty)
                .Trim()
                .Replace('-', ':')
                .ToUpperInvariant();

            if (!System.Text.RegularExpressions.Regex.IsMatch(
                    normalized,
                    "^([0-9A-F]{2}:){5}[0-9A-F]{2}$"))
            {
                throw new ArgumentException(
                    "The client MAC address is invalid.",
                    nameof(macAddress));
            }

            string command =
                "if command -v etherwake >/dev/null 2>&1; then " +
                $"etherwake -i br-lan {normalized} 2>&1; " +
                "elif command -v wol >/dev/null 2>&1; then " +
                $"wol {normalized} 2>&1; " +
                "else echo '__WOL_TOOL_MISSING__'; fi";

            string output = await _ssh.RunCommandAsync(command);

            if (output.Contains(
                    "__WOL_TOOL_MISSING__",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "Wake-on-LAN is not available on this router. Install etherwake to enable it.";
            }

            if (output.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("invalid", StringComparison.OrdinalIgnoreCase))
            {
                return "The router could not send the Wake-on-LAN packet: " +
                       output.Trim();
            }

            return $"Wake-on-LAN packet sent to {normalized}.";
        }

        //
        // Controls
        //

        public Task StartAdGuardAsync()
        {
            return _ssh.RunCommandAsync(
                "/etc/init.d/adguardhome start");
        }

        public Task StopAdGuardAsync()
        {
            return _ssh.RunCommandAsync(
                "/etc/init.d/adguardhome stop");
        }

        public Task RestartAdGuardAsync()
        {
            return _ssh.RunCommandAsync(
                "/etc/init.d/adguardhome restart");
        }

        public Task EnableAdGuardAsync()
        {
            return _ssh.RunCommandAsync(
                "/etc/init.d/adguardhome enable");
        }

        public Task DisableAdGuardAsync()
        {
            return _ssh.RunCommandAsync(
                "/etc/init.d/adguardhome disable");
        }

        //
        // Logs
        //

        public Task<string> GetLogsAsync()
        {
            return _ssh.RunCommandAsync(
                "logread -e AdGuardHome");
        }

        //
        // Router diagnostic tools
        //

        public Task<string> PingAsync(string target, CancellationToken cancellationToken = default)
        {
            string safeTarget = ValidateDiagnosticTarget(target);

            return _ssh.RunCommandAsync(
                $"ping -c 4 -W 2 {safeTarget}", cancellationToken);
        }

        public Task<string> TracerouteAsync(string target)
        {
            string safeTarget = ValidateDiagnosticTarget(target);

            return _ssh.RunCommandAsync(
                $"traceroute -m 12 -w 2 {safeTarget}");
        }

        public Task<string> DnsLookupAsync(string target, CancellationToken cancellationToken = default)
        {
            string safeTarget = ValidateDiagnosticTarget(target);

            return _ssh.RunCommandAsync(
                $"nslookup {safeTarget}", cancellationToken);
        }

        private static string ValidateDiagnosticTarget(string target)
        {
            string value = (target ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    "Enter a hostname or IP address.",
                    nameof(target));
            }

            if (value.Length > 253 ||
                value.Any(character =>
                    !(char.IsLetterOrDigit(character) ||
                      character == '.' ||
                      character == '-' ||
                      character == ':' ||
                      character == '_')))
            {
                throw new ArgumentException(
                    "The diagnostic target contains unsupported characters.",
                    nameof(target));
            }

            return value;
        }

        //
    }

}
