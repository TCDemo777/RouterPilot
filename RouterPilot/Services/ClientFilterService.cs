using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using RouterPilot.Models;

namespace RouterPilot.Services;

internal readonly record struct ClientFilterOptions(
    string SearchText,
    bool FavoritesOnly,
    bool HideWithoutIp,
    bool HideUnknown,
    bool OnlineOnly);

/// <summary>Pure projection of the active client snapshot into filtered results.</summary>
internal static class ClientFilterService
{
    internal static List<ClientInfo> Apply(
        IEnumerable<ClientInfo> clients,
        ClientFilterOptions options,
        Func<ClientInfo, bool> isOnline)
    {
        IEnumerable<ClientInfo> query = clients;
        if (options.FavoritesOnly) query = query.Where(client => client.IsFavorite);
        if (options.HideWithoutIp) query = query.Where(client => HasUsableIp(client.IpAddress));
        if (options.HideUnknown) query = query.Where(client => !IsUnknownName(client.Name));
        if (options.OnlineOnly) query = query.Where(isOnline);

        string search = options.SearchText.Trim();
        if (search.Length > 0)
        {
            query = query.Where(client => Contains(client.Name, search) ||
                Contains(client.IpAddress, search) || Contains(client.MacAddress, search) ||
                Contains(client.Manufacturer, search) || Contains(client.DeviceType, search) ||
                Contains(client.HealthText, search));
        }
        return query.ToList();
    }

    internal static bool HasUsableIp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            (!value.Contains('.', StringComparison.Ordinal) && !value.Contains(':', StringComparison.Ordinal))) return false;
        return IPAddress.TryParse(ClientIdentity.NormalizeEndpoint(value), out _);
    }

    internal static bool IsUnknownName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        string name = value.Trim();
        return name.Equals("-", StringComparison.Ordinal) || name.Equals("—", StringComparison.Ordinal) ||
            name.Equals("N/A", StringComparison.OrdinalIgnoreCase) || name.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Unknown device", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Contains(string? value, string search) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains(search, StringComparison.OrdinalIgnoreCase);
}
