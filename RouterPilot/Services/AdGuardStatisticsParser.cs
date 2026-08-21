using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>
/// Deterministically maps AdGuard statistics responses to RouterPilot models.
/// Transport, authentication, retries, and failure handling remain with RouterManager.
/// </summary>
internal static class AdGuardStatisticsParser
{
    internal static AdGuardStatistics Parse(string json, DateTime now)
    {
        AdGuardStatistics stats = CreateUnavailableStatistics();

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        if (root.TryGetProperty("num_dns_queries", out JsonElement queries) &&
            queries.TryGetInt32(out int totalQueries))
        {
            stats.TotalQueries = totalQueries;
        }

        if (root.TryGetProperty("num_blocked_filtering", out JsonElement blocked) &&
            blocked.TryGetInt32(out int blockedQueries))
        {
            stats.BlockedQueries = blockedQueries;
        }

        stats.QueryHistoryTimeUnits = GetStringProperty(root, "time_units", "hours");
        stats.QueryHistory = ParseQueryHistory(root, stats.QueryHistoryTimeUnits, now);
        stats.TopClients = ParseRankedItems(root, "top_clients");
        stats.TopQueriedDomains = ParseRankedItems(root, "top_queried_domains");
        stats.TopBlockedDomains = ParseRankedItems(root, "top_blocked_domains");

        Debug.WriteLine($"Queries: {stats.TotalQueries}");
        Debug.WriteLine($"Blocked: {stats.BlockedQueries}");
        Debug.WriteLine($"Top clients: {stats.TopClients.Count}");
        Debug.WriteLine($"Top requested: {stats.TopQueriedDomains.Count}");
        Debug.WriteLine($"Top blocked: {stats.TopBlockedDomains.Count}");

        return stats;
    }

    internal static AdGuardStatistics CreateUnavailableStatistics() => new()
    {
        TotalQueries = -1,
        BlockedQueries = -1,
        QueryHistory = new List<AdGuardTimePoint>()
    };

    private static List<AdGuardRankedItem> ParseRankedItems(JsonElement root, string propertyName)
    {
        var result = new List<AdGuardRankedItem>();

        if (!root.TryGetProperty(propertyName, out JsonElement value)) return result;

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                // AdGuard Home normally returns one-property objects,
                // for example {"example.com": 42}.
                foreach (JsonProperty property in item.EnumerateObject())
                {
                    if (TryGetInteger(property.Value, out int propertyCount))
                    {
                        result.Add(new AdGuardRankedItem { Name = property.Name, Count = propertyCount });
                    }
                }

                // Also accept named object schemas used by forks.
                string name = GetStringProperty(item, "name", string.Empty);
                if (name.Length == 0) name = GetStringProperty(item, "domain", string.Empty);
                if (name.Length == 0) name = GetStringProperty(item, "client", string.Empty);

                if (name.Length > 0 && TryGetNamedInteger(item, out int namedCount))
                {
                    result.Add(new AdGuardRankedItem { Name = name, Count = namedCount });
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (TryGetInteger(property.Value, out int mappedCount))
                {
                    result.Add(new AdGuardRankedItem { Name = property.Name, Count = mappedCount });
                }
            }
        }

        return result
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => new AdGuardRankedItem { Name = group.Key, Count = group.Sum(item => item.Count) })
            .OrderByDescending(item => item.Count)
            .Take(10)
            .ToList();
    }

    private static bool TryGetNamedInteger(JsonElement item, out int value)
    {
        foreach (string propertyName in new[] { "count", "queries", "value", "num" })
        {
            if (item.TryGetProperty(propertyName, out JsonElement property) &&
                TryGetInteger(property, out value))
            {
                return true;
            }
        }

        value = 0;
        return false;
    }

    private static bool TryGetInteger(JsonElement value, out int result)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out result)) return true;

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out result)) return true;

        result = 0;
        return false;
    }

    private static List<AdGuardTimePoint> ParseQueryHistory(JsonElement root, string timeUnits, DateTime now)
    {
        var history = new List<AdGuardTimePoint>();

        if (!root.TryGetProperty("dns_queries", out JsonElement queryArray) ||
            queryArray.ValueKind != JsonValueKind.Array)
        {
            Debug.WriteLine("AdGuard statistics did not contain a dns_queries array.");
            return history;
        }

        root.TryGetProperty("blocked_filtering", out JsonElement blockedArray);

        int pointCount = queryArray.GetArrayLength();
        int startIndex = Math.Max(0, pointCount - 120);

        for (int index = startIndex; index < pointCount; index++)
        {
            int queryCount = GetArrayInteger(queryArray, index);
            int blockedCount = 0;

            if (blockedArray.ValueKind == JsonValueKind.Array && index < blockedArray.GetArrayLength())
            {
                blockedCount = GetArrayInteger(blockedArray, index);
            }

            int intervalsAgo = pointCount - index - 1;
            DateTime timestamp = SubtractTimeInterval(now, timeUnits, intervalsAgo);

            history.Add(new AdGuardTimePoint
            {
                Timestamp = timestamp,
                Queries = queryCount,
                Blocked = blockedCount
            });
        }

        return history;
    }

    private static int GetArrayInteger(JsonElement array, int index)
    {
        JsonElement value = array[index];

        if (value.TryGetInt32(out int integerValue)) return integerValue;

        if (value.TryGetInt64(out long longValue))
        {
            if (longValue > int.MaxValue) return int.MaxValue;
            if (longValue < int.MinValue) return int.MinValue;
            return (int)longValue;
        }

        return 0;
    }

    private static string GetStringProperty(JsonElement root, string propertyName, string fallbackValue)
    {
        if (root.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.String)
        {
            string? value = property.GetString();
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return fallbackValue;
    }

    private static DateTime SubtractTimeInterval(DateTime timestamp, string timeUnits, int intervalCount) =>
        timeUnits.ToLowerInvariant() switch
        {
            "seconds" => timestamp.AddSeconds(-intervalCount),
            "minutes" => timestamp.AddMinutes(-intervalCount),
            "days" => timestamp.AddDays(-intervalCount),
            "months" => timestamp.AddMonths(-intervalCount),
            _ => timestamp.AddHours(-intervalCount)
        };
}
