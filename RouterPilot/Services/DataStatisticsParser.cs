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

    public static FullApplicationStatisticsSnapshot ParseFullSnapshot(JsonElement result)
    {
        ApplicationTrafficRow? aggregate = null;
        ApplicationTrafficRow[] applications = result.TryGetProperty("applications", out JsonElement apps) &&
            apps.ValueKind == JsonValueKind.Array
            ? apps.EnumerateArray()
                .Where(app => app.ValueKind == JsonValueKind.Object)
                .Select(ParseFullApplication)
                .Where(app => !string.IsNullOrWhiteSpace(app.ApplicationId) ||
                              !string.IsNullOrWhiteSpace(app.ApplicationName))
                .Where(app =>
                {
                    if (IsAllTrafficAggregate(app))
                    {
                        aggregate ??= app;
                        return false;
                    }

                    return true;
                })
                .ToArray()
            : [];

        return new FullApplicationStatisticsSnapshot
        {
            Period = ReadString(result, "time"),
            Aggregate = aggregate,
            Applications = applications
        };
    }

    public static ApplicationTrafficDetail ParseApplicationDetail(JsonElement result)
    {
        JsonElement metadata = result.TryGetProperty("metadata", out JsonElement metadataElement) &&
            metadataElement.ValueKind == JsonValueKind.Object ? metadataElement : default;
        ApplicationDeviceTraffic[] devices = result.TryGetProperty("mac_addresses", out JsonElement macAddresses) &&
            macAddresses.ValueKind == JsonValueKind.Object
            ? macAddresses.EnumerateObject()
                .Where(property => property.Value.ValueKind == JsonValueKind.Object)
                .Select(property => ParseDevice(property.Name, property.Value))
                .ToArray()
            : [];

        return new ApplicationTrafficDetail
        {
            ApplicationId = ReadString(result, "application_id"),
            ApplicationName = ReadString(result, "application_name"),
            Identifier = ReadString(result, "identifier"),
            Label = ReadString(result, "label"),
            Url = ReadString(result, "url"),
            Description = ReadString(result, "desc"),
            LogoUrl = ReadString(result, "logo"),
            IsBlocked = result.TryGetProperty("application_block", out JsonElement blocked) ? ReadNullableBool(blocked) : null,
            PeriodSeconds = ReadNullableInt64(result, "period_seconds"),
            TotalUploadBytes = ReadNullableInt64(result, "total_upload") ?? 0,
            TotalDownloadBytes = ReadNullableInt64(result, "total_download") ?? 0,
            MetadataStartUtc = ReadUnixTime(metadata, "start_time"),
            MetadataEndUtc = ReadUnixTime(metadata, "end_time"),
            Devices = devices,
            TimeSeries = result.TryGetProperty("time_series", out JsonElement series) && series.ValueKind == JsonValueKind.Array
                ? series.EnumerateArray().Where(point => point.ValueKind == JsonValueKind.Object).Select(ParsePoint).ToArray()
                : []
        };
    }

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

    private static ApplicationTrafficRow ParseFullApplication(JsonElement app) => new()
    {
        ApplicationId = ReadString(app, "application_id"),
        ApplicationName = ReadString(app, "application_name"),
        Label = ReadString(app, "label"),
        IconUrl = ReadString(app, "icon"),
        UploadBytes = ReadNullableInt64(app, "upload") ?? 0,
        DownloadBytes = ReadNullableInt64(app, "download") ?? 0,
        TotalBytes = ReadNullableInt64(app, "total") ?? 0,
        PacketCount = ReadNullableInt64(app, "packets")
    };

    private static ApplicationDeviceTraffic ParseDevice(string macAddress, JsonElement device) => new()
    {
        MacAddress = macAddress,
        NormalizedMac = ClientIdentity.NormalizeMac(macAddress),
        Hostname = ReadString(device, "hostname"),
        UploadBytes = ReadNullableInt64(device, "upload") ?? 0,
        DownloadBytes = ReadNullableInt64(device, "download") ?? 0,
        TotalBytes = ReadNullableInt64(device, "total") ?? 0,
        PacketCount = ReadNullableInt64(device, "packets"),
        RecordCount = ReadNullableInt64(device, "record_count"),
        LastActiveUtc = ReadUnixTime(device, "last_active_time"),
        LastActiveRelative = ReadString(device, "last_active_relative")
    };

    private static bool IsAllTrafficAggregate(ApplicationTrafficRow app) =>
        string.Equals(app.ApplicationId, "-1", StringComparison.Ordinal) &&
        string.Equals(app.ApplicationName, "all_traffic", StringComparison.Ordinal);

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
