using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using RouterPilot.Models;

namespace RouterPilot.Services;

public enum DhcpReservationValidationCode
{
    Valid, InvalidMac, BroadcastMac, MulticastMac, InvalidIp,
    OutsideKnownDhcpSubnet, NetworkAddress, BroadcastAddress, RouterAddress,
    AmbiguousScope, DuplicateExactReservation, ConflictingReservedIp,
    ConflictingMacReservation, ScopeUnavailable
}

public sealed record DhcpReservationValidationResult(
    DhcpReservationValidationCode Code,
    string NormalizedMac,
    string? IpAddress,
    DhcpNetworkScopeInfo? Scope)
{
    public bool IsValid => Code == DhcpReservationValidationCode.Valid;
}

public enum DhcpExistingReservationState { NotReserved, ExactReservation, SameMacDifferentIp, IpReservedToDifferentMac, Ambiguous }

public sealed record DhcpReservationEligibility(
    bool Eligible, DhcpNetworkScopeInfo? Scope, string MacAddress, string IpAddress,
    DhcpExistingReservationState ExistingReservationState, string FailureReason);

public sealed class DhcpReservationValidator
{
    public DhcpReservationValidationResult Validate(
        string? macAddress, string? ipAddress,
        IReadOnlyList<DhcpNetworkScopeInfo> scopes,
        IReadOnlyList<DhcpReservationInfo> reservations,
        IReadOnlyList<DhcpLeaseInfo>? leases = null)
    {
        if (!TryNormalizeMac(macAddress, out string mac, out var macFailure))
            return new(macFailure, string.Empty, ipAddress, null);
        if (!IPAddress.TryParse(ipAddress, out IPAddress? ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return new(DhcpReservationValidationCode.InvalidIp, mac, ipAddress, null);
        var matching = scopes.Where(s => s.DhcpEnabled && s.Status == "Active" && s.ContainsAddress(ip)).ToList();
        if (matching.Count == 0) return new(DhcpReservationValidationCode.OutsideKnownDhcpSubnet, mac, ip.ToString(), null);
        if (matching.Count > 1) return new(DhcpReservationValidationCode.AmbiguousScope, mac, ip.ToString(), null);
        DhcpNetworkScopeInfo scope = matching[0];
        if (ip.ToString() == scope.NetworkAddress) return new(DhcpReservationValidationCode.NetworkAddress, mac, ip.ToString(), scope);
        if (ip.ToString() == scope.BroadcastAddress) return new(DhcpReservationValidationCode.BroadcastAddress, mac, ip.ToString(), scope);
        if (ip.ToString() == scope.RouterAddress) return new(DhcpReservationValidationCode.RouterAddress, mac, ip.ToString(), scope);
        bool exact = reservations.Any(r => SameMac(r.MacAddress, mac) && r.IpAddress == ip.ToString());
        if (exact) return new(DhcpReservationValidationCode.DuplicateExactReservation, mac, ip.ToString(), scope);
        if (reservations.Any(r => r.IpAddress == ip.ToString() && !SameMac(r.MacAddress, mac))) return new(DhcpReservationValidationCode.ConflictingReservedIp, mac, ip.ToString(), scope);
        if (reservations.Any(r => SameMac(r.MacAddress, mac) && r.IpAddress != ip.ToString())) return new(DhcpReservationValidationCode.ConflictingMacReservation, mac, ip.ToString(), scope);
        if (leases?.Any(l => l.IpAddress == ip.ToString() && !SameMac(l.MacAddress, mac)) == true) return new(DhcpReservationValidationCode.ConflictingReservedIp, mac, ip.ToString(), scope);
        return new(DhcpReservationValidationCode.Valid, mac, ip.ToString(), scope);
    }

    public DhcpReservationEligibility GetEligibility(string? mac, string? ip, IReadOnlyList<DhcpNetworkScopeInfo> scopes, IReadOnlyList<DhcpReservationInfo> reservations, IReadOnlyList<DhcpLeaseInfo>? leases = null)
    {
        var result = Validate(mac, ip, scopes, reservations, leases);
        DhcpExistingReservationState state = result.Code switch { DhcpReservationValidationCode.DuplicateExactReservation => DhcpExistingReservationState.ExactReservation, DhcpReservationValidationCode.ConflictingMacReservation => DhcpExistingReservationState.SameMacDifferentIp, DhcpReservationValidationCode.ConflictingReservedIp => DhcpExistingReservationState.IpReservedToDifferentMac, DhcpReservationValidationCode.AmbiguousScope => DhcpExistingReservationState.Ambiguous, _ => DhcpExistingReservationState.NotReserved };
        return new(result.IsValid, result.Scope, result.NormalizedMac, result.IpAddress ?? string.Empty, state, result.Code.ToString());
    }

    private static bool SameMac(string value, string normalized) => TryNormalizeMac(value, out string current, out _) && current == normalized;
    private static bool TryNormalizeMac(string? value, out string normalized, out DhcpReservationValidationCode failure)
    {
        normalized = new string((value ?? string.Empty).Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (normalized.Length != 12) { failure = DhcpReservationValidationCode.InvalidMac; return false; }
        byte first = Convert.ToByte(normalized[..2], 16);
        if (normalized == "FFFFFFFFFFFF") { failure = DhcpReservationValidationCode.BroadcastMac; return false; }
        if ((first & 1) != 0) { failure = DhcpReservationValidationCode.MulticastMac; return false; }
        string compact = normalized;
        normalized = string.Join(":", Enumerable.Range(0,6).Select(i => compact.Substring(i*2,2)));
        failure = DhcpReservationValidationCode.Valid; return true;
    }
}
