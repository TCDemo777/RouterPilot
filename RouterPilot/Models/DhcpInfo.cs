using System;
using System.Collections.Generic;

namespace RouterPilot.Models
{
    public sealed class DhcpConfigurationInfo
    {
        public string Id { get; init; } = "-";
        public string Interface { get; init; } = "N/A";
        public bool Enabled { get; init; }
        public string Start { get; init; } = "N/A";
        public string Limit { get; init; } = "N/A";
        public string StartAddress { get; init; } = "N/A";
        public string EndAddress { get; init; } = "N/A";
        public string LeaseTime { get; init; } = "N/A";
        public string StatusDisplay => Enabled ? RouterPilotStatusPresentation.Active : RouterPilotStatusPresentation.Disabled;
    }

    public sealed class DhcpLeaseInfo
    {
        public string Hostname { get; init; } = "Unknown device";
        public string IpAddress { get; init; } = "-";
        public string MacAddress { get; init; } = "-";
        public DateTimeOffset? Expiry { get; init; }
        public bool IsStatic { get; init; }
        public string RemainingLease { get; init; } = "N/A";
        public string ClientName { get; set; } = "Unknown device";
        public string DeviceType { get; set; } = "Unknown device";
        public bool IsFavourite { get; set; }
        public string ProfileId { get; set; } = string.Empty;
        public string ScopeDisplay { get; set; } = "Unknown";
        public bool CanViewClient => ClientIdentity.IsMacKey(MacAddress);
    }

    public sealed class DhcpReservationInfo
    {
        public string Id { get; init; } = "-";
        public string Hostname { get; set; } = "Unknown device";
        public string MacAddress { get; init; } = "-";
        public string IpAddress { get; init; } = "-";
        public bool Enabled { get; init; } = true;
        public string Source { get; init; } = "UCI dhcp host";
        public string DeviceType { get; set; } = "Unknown device";
        public bool IsFavourite { get; set; }
        public string ProfileId { get; set; } = string.Empty;
        public string ScopeDisplay { get; set; } = "Unknown";
        public bool CanViewClient => ClientIdentity.IsMacKey(MacAddress);
    }

    public sealed record DhcpReservationIdentity(string MacAddress, string IpAddress, string? FriendlyName = null);

    public sealed class DhcpReservationRequest
    {
        public string MacAddress { get; init; } = string.Empty;
        public string IpAddress { get; init; } = string.Empty;
        // RouterPilot-facing display metadata only. Flint 2 host sections do
        // not require or currently use a UCI name option.
        public string? Hostname { get; init; }
    }

    public sealed class DhcpReservationOperationResult
    {
        public bool Success { get; init; }
        public string Operation { get; init; } = string.Empty;
        public string FailureCategory { get; init; } = string.Empty;
        public DhcpReservationIdentity? VerifiedIdentity { get; init; }
        public string RequestedMac { get; init; } = string.Empty;
        public string RequestedIp { get; init; } = string.Empty;
        public string VerifiedMac { get; init; } = string.Empty;
        public string VerifiedIp { get; init; } = string.Empty;
        public bool RollbackAttempted { get; init; }
        public bool RollbackVerified { get; init; }
        public TimeSpan Duration { get; init; }
    }

    public sealed class DhcpNetworkScopeInfo
    {
        public string ScopeId { get; init; } = "-";
        public string InterfaceName { get; init; } = "-";
        public string DisplayName { get; init; } = "-";
        public bool DhcpEnabled { get; init; }
        public bool InterfaceUp { get; init; }
        public string? IPv4Address { get; init; }
        public int? PrefixLength { get; init; }
        public string? Netmask { get; init; }
        public string? NetworkAddress { get; init; }
        public string? BroadcastAddress { get; init; }
        public string? RouterAddress { get; init; }
        public int? DhcpStart { get; init; }
        public int? DhcpLimit { get; init; }
        public string? DynamicRangeStart { get; init; }
        public string? DynamicRangeEnd { get; init; }
        public string LeaseTime { get; init; } = "N/A";
        public string Status { get; init; } = "Pending";
        public string? FailureCategory { get; init; }
        public string SubnetDisplay => NetworkAddress is not null && PrefixLength is not null ? $"{NetworkAddress}/{PrefixLength}" : "N/A";
        public string DynamicRangeDisplay => DynamicRangeStart is not null && DynamicRangeEnd is not null ? $"{DynamicRangeStart} – {DynamicRangeEnd}" : "N/A";

        public bool ContainsUsableHost(string? value)
        {
            if (!System.Net.IPAddress.TryParse(value, out var address) || NetworkAddress is null || BroadcastAddress is null || RouterAddress is null) return false;
            return ContainsAddress(address) && value != NetworkAddress && value != BroadcastAddress && value != RouterAddress;
        }

        public bool ContainsAddress(System.Net.IPAddress address)
        {
            if (NetworkAddress is null || BroadcastAddress is null || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
            if (!System.Net.IPAddress.TryParse(NetworkAddress, out var network) || !System.Net.IPAddress.TryParse(BroadcastAddress, out var broadcast) ||
                network.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || broadcast.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
            byte[] v = address.GetAddressBytes(), n = network.GetAddressBytes(), b = broadcast.GetAddressBytes();
            uint x = ((uint)v[0]<<24)|((uint)v[1]<<16)|((uint)v[2]<<8)|v[3], lo = ((uint)n[0]<<24)|((uint)n[1]<<16)|((uint)n[2]<<8)|n[3], hi = ((uint)b[0]<<24)|((uint)b[1]<<16)|((uint)b[2]<<8)|b[3];
            return x >= lo && x <= hi;
        }
    }

    /// <summary>
    /// Read-only presentation derived from one DHCP scope and the current
    /// lease/reservation snapshot. Reservations remain contextual: a
    /// reservation can be outside the dynamic pool, so it is never subtracted
    /// from available dynamic addresses.
    /// </summary>
    public sealed class DhcpScopeCapacityInfo
    {
        public required DhcpNetworkScopeInfo Scope { get; init; }
        public int ActiveLeaseCount { get; init; }
        public int EnabledReservationCount { get; init; }
        public int EnabledReservationsInDynamicRangeCount { get; init; }

        public bool HasCapacity => Scope.DhcpLimit is > 0 &&
            !string.IsNullOrWhiteSpace(Scope.DynamicRangeStart) &&
            !string.IsNullOrWhiteSpace(Scope.DynamicRangeEnd);

        public int? TotalPoolCapacity => HasCapacity ? Scope.DhcpLimit : null;
        public int? AvailableAddressCount => TotalPoolCapacity is int capacity
            ? Math.Max(0, capacity - ActiveLeaseCount)
            : null;
        public int? UtilisationPercent => TotalPoolCapacity is int capacity && capacity > 0
            ? Math.Clamp((int)Math.Round(ActiveLeaseCount * 100d / capacity, MidpointRounding.AwayFromZero), 0, 100)
            : null;

        public string CapacitySummary => TotalPoolCapacity is int capacity
            ? $"{ActiveLeaseCount} / {capacity} addresses in use"
            : "Pool capacity unavailable";

        public string AvailableAddressDisplay => AvailableAddressCount is int available
            ? available.ToString()
            : "Unavailable";

        public string UtilisationDisplay => UtilisationPercent is int percent
            ? $"{percent}% in use"
            : "Unavailable";

        public string CapacityState => !HasCapacity ? "Unavailable" : UtilisationPercent switch
        {
            >= 100 => "Full",
            >= 90 => "Low capacity",
            >= 70 => "Getting full",
            _ => "Healthy"
        };
    }

    public sealed class DhcpSnapshot
    {
        public IReadOnlyList<DhcpConfigurationInfo> Configurations { get; init; } = Array.Empty<DhcpConfigurationInfo>();
        public IReadOnlyList<DhcpLeaseInfo> Leases { get; init; } = Array.Empty<DhcpLeaseInfo>();
        public IReadOnlyList<DhcpReservationInfo> Reservations { get; init; } = Array.Empty<DhcpReservationInfo>();
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
        public IReadOnlyList<DhcpNetworkScopeInfo> Scopes { get; init; } = Array.Empty<DhcpNetworkScopeInfo>();
    }
}
