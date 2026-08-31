using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>Applies AdGuard top-client totals to the authoritative client snapshot.</summary>
internal static class AdGuardClientCorrelationService
{
    internal static int ApplyTopClientTotals(List<ClientInfo> clients, string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;
        var byIdentifier = new Dictionary<string, ClientInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (ClientInfo client in clients)
        {
            AddIdentifier(byIdentifier, client.IpAddress, client);
            AddIdentifier(byIdentifier, client.MacAddress, client);
            AddIdentifier(byIdentifier, client.Name, client);
        }
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("top_clients", out JsonElement topClients) || topClients.ValueKind != JsonValueKind.Array)
        {
            Debug.WriteLine("AdGuard statistics did not contain top_clients.");
            return 0;
        }
        int matched = 0;
        foreach (JsonElement item in topClients.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            foreach (JsonProperty property in item.EnumerateObject())
            {
                if (!TryGetInteger(property.Value, out int count)) continue;
                if (!byIdentifier.TryGetValue(NormalizeIdentifier(property.Name), out ClientInfo? client)) continue;
                client.TotalQueries = Math.Max(client.TotalQueries, count);
                matched++;
                break;
            }
        }
        Debug.WriteLine($"Applied statistics totals to {matched} clients.");
        return matched;
    }

    private static void AddIdentifier(Dictionary<string, ClientInfo> lookup, string? identifier, ClientInfo client)
    {
        if (string.IsNullOrWhiteSpace(identifier) || identifier == "-") return;
        string key = NormalizeIdentifier(identifier);
        if (key.Length > 0) lookup[key] = client;
    }

    private static string NormalizeIdentifier(string value) => IPAddress.TryParse(value.Trim(), out _) || value.Contains(':', StringComparison.Ordinal) ? ClientIdentity.NormalizeEndpoint(value) : value.Trim();

    private static bool TryGetInteger(JsonElement value, out int result)
    {
        if (value.TryGetInt32(out result)) return true;
        result = 0;
        return false;
    }
}
