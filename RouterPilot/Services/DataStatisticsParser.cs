using System;
using System.Linq;
using System.Text.Json;
using RouterPilot.Models;

namespace RouterPilot.Services;

public static class DataStatisticsParser
{
    public static DataStatisticsStatus ParseStatus(JsonElement result)
    {
        JsonElement system = result.TryGetProperty("system", out JsonElement systemElement) &&
            systemElement.ValueKind == JsonValueKind.Object ? systemElement : result;
        bool? enabled = system.TryGetProperty("flow_statistics_enabled", out JsonElement enabledElement)
            ? ReadNullableBool(enabledElement) : null;
        JsonElement dpiInfo = system.TryGetProperty("dpi_info", out JsonElement dpiElement) &&
            dpiElement.ValueKind == JsonValueKind.Object ? dpiElement : default;
        return new DataStatisticsStatus
        {
            FlowStatisticsEnabled = enabled,
            DpiStatus = ReadString(dpiInfo, "status"),
            DpiLibraryVersion = ReadString(dpiInfo, "lib_version"),
            DpiLibraryUpdateTime = ReadString(dpiInfo, "lib_update_time")
        };
    }

    public static DataStatisticsSnapshot ParseSnapshot(JsonElement result) => new()
    {
        MaxBytes = ReadNullableInt64(result, "max_bytes"),
        PeriodSeconds = ReadNullableInt64(result, "period_seconds"),
        TopApps = result.TryGetProperty("top_apps", out JsonElement apps) && apps.ValueKind == JsonValueKind.Array
            ? apps.EnumerateArray().Select(ParseApplication).ToArray()
            : []
    };

    private static ApplicationTrafficStat ParseApplication(JsonElement app) => new()
    {
        ApplicationId = ReadString(app, "application_id"), ApplicationName = ReadString(app, "application_name"),
        Label = ReadString(app, "label"), IconUrl = ReadString(app, "icon"),
        UploadBytes = ReadNullableInt64(app, "upload") ?? 0, DownloadBytes = ReadNullableInt64(app, "download") ?? 0,
        TotalBytes = ReadNullableInt64(app, "total") ?? 0,
        TimeSeries = app.TryGetProperty("time_series", out JsonElement series) && series.ValueKind == JsonValueKind.Array
            ? series.EnumerateArray().Select(ParsePoint).ToArray() : []
    };

    private static ApplicationTrafficPoint ParsePoint(JsonElement point) => new()
    {
        StartTimeUtc = ReadUnixTime(point, "start_time"), EndTimeUtc = ReadUnixTime(point, "end_time"),
        UploadBytes = ReadNullableInt64(point, "upload") ?? 0, DownloadBytes = ReadNullableInt64(point, "download") ?? 0,
        TotalBytes = ReadNullableInt64(point, "total") ?? 0
    };

    private static DateTimeOffset? ReadUnixTime(JsonElement source, string property)
    {
        long? value = ReadNullableInt64(source, property);
        if (value is null) return null;
        try { return DateTimeOffset.FromUnixTimeSeconds(value.Value); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static long? ReadNullableInt64(JsonElement source, string property)
    {
        if (source.ValueKind != JsonValueKind.Object || !source.TryGetProperty(property, out JsonElement value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out long number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), out long number) => number,
            _ => null
        };
    }

    private static bool? ReadNullableBool(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true, JsonValueKind.False => false,
        JsonValueKind.String when bool.TryParse(value.GetString(), out bool parsed) => parsed,
        _ => null
    };

    private static string ReadString(JsonElement source, string property) =>
        source.ValueKind == JsonValueKind.Object && source.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : string.Empty;
}
