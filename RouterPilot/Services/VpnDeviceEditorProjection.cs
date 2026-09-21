using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>Builds the local, non-mutating VPN device editor working copy from shared inventory plus router assignments.</summary>
internal static class VpnDeviceEditorProjection
{
    internal static IReadOnlyList<VpnDeviceEditorItem> Build(
        IReadOnlyDictionary<string, ClientInfo> inventory,
        IReadOnlyDictionary<string, bool> presence,
        IClientDisplayNameService names,
        IReadOnlyList<string> assignedIdentities)
    {
        HashSet<string> assigned = assignedIdentities.Select(ClientIdentity.NormalizeHexMac)
            .Where(identity => identity.Length == 12)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = new Dictionary<string, ClientInfo>(StringComparer.OrdinalIgnoreCase);
        foreach ((string identity, ClientInfo client) in inventory)
        {
            string normalized = ClientIdentity.NormalizeHexMac(identity);
            if (normalized.Length == 12) candidates.TryAdd(normalized, client);
        }

        List<VpnDeviceEditorItem> items = candidates.OrderBy(pair => DisplayName(pair.Value, names), StringComparer.OrdinalIgnoreCase)
            .Select(pair =>
            {
                string identity = pair.Key;
                string status = presence.TryGetValue(identity, out bool online) ? online ? "Online" : "Offline" : "Unknown";
                return new VpnDeviceEditorItem
                {
                    Identity = identity,
                    DisplayName = DisplayName(pair.Value, names),
                    StatusDisplay = status,
                    IsSelected = assigned.Remove(identity)
                };
            }).ToList();

        int unknown = 0;
        foreach (string identity in assigned.OrderBy(value => value, StringComparer.Ordinal))
        {
            items.Add(new VpnDeviceEditorItem
            {
                Identity = identity,
                DisplayName = $"Unknown device {++unknown}",
                StatusDisplay = "Preserved",
                IsUnknownExistingAssignment = true,
                IsSelected = true
            });
        }
        return items;
    }

    private static string DisplayName(ClientInfo client, IClientDisplayNameService names)
    {
        string displayName = names.Resolve(client);
        return string.IsNullOrWhiteSpace(displayName) || displayName is "-" or "â€”" ? "Unknown device" : displayName;
    }
}
