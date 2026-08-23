using System;
using System.Text.Json;
using RouterPilot.Services;

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

using JsonDocument hour = JsonDocument.Parse("""
{
  "period_seconds": 3600,
  "max_bytes": 600000000,
  "top_apps": [
    { "application_id": "0", "application_name": "http", "label": "HTTP/S", "icon": "", "upload": 10, "download": 90, "total": 100,
      "time_series": [{ "start_time": 1700000000, "end_time": 1700000300, "upload": 3, "download": 27, "total": 30 }] },
    { "application_id": "0", "application_name": "wireguard", "label": "WireGuard", "icon": null, "upload": 20, "download": 180, "total": 200,
      "time_series": [{ "start_time": 1700000000, "end_time": 1700000300, "upload": 6, "download": 54, "total": 60 }] }
  ]
}
""");
var hourSnapshot = DataStatisticsParser.ParseSnapshot(hour.RootElement);
Require(hourSnapshot.PeriodSeconds == 3600, "Hour period was not parsed.");
Require(hourSnapshot.TopApps.Count == 2, "Duplicate application ID rows collided.");
Require(hourSnapshot.TopApps[0].TimeSeries[0].EndTimeUtc!.Value.ToUnixTimeSeconds() - hourSnapshot.TopApps[0].TimeSeries[0].StartTimeUtc!.Value.ToUnixTimeSeconds() == 300, "Hour bucket was not parsed.");

using JsonDocument day = JsonDocument.Parse("""
{ "period_seconds": 86400, "max_bytes": 300000000000, "top_apps": [
  { "application_id": "app", "application_name": "example", "label": "Example", "upload": 100000000000, "download": 200000000000, "total": 300000000000,
    "time_series": [{ "start_time": 1700000000, "end_time": 1700007200, "upload": 1, "download": 2, "total": 3 }] }
] }
""");
var daySnapshot = DataStatisticsParser.ParseSnapshot(day.RootElement);
Require(daySnapshot.PeriodSeconds == 86400, "Day period was not parsed.");
Require(daySnapshot.TopApps[0].TotalBytes == 300000000000, "64-bit total was not parsed.");
Require(daySnapshot.TopApps[0].TimeSeries[0].EndTimeUtc!.Value.ToUnixTimeSeconds() - daySnapshot.TopApps[0].TimeSeries[0].StartTimeUtc!.Value.ToUnixTimeSeconds() == 7200, "Day bucket was not parsed.");

using JsonDocument malformed = JsonDocument.Parse("""{ "period_seconds": 999, "top_apps": null }""");
var malformedSnapshot = DataStatisticsParser.ParseSnapshot(malformed.RootElement);
Require(malformedSnapshot.TopApps.Count == 0 && malformedSnapshot.PeriodSeconds == 999, "Malformed response was not tolerated.");

using JsonDocument activeStatus = JsonDocument.Parse("""{ "system": { "flow_statistics_enabled": true, "dpi_info": { "status": "1" } } }""");
using JsonDocument disabledStatus = JsonDocument.Parse("""{ "system": { "flow_statistics_enabled": false } }""");
Require(DataStatisticsParser.ParseStatus(activeStatus.RootElement).IsDpiActive, "Active status was not parsed.");
Require(DataStatisticsParser.ParseStatus(disabledStatus.RootElement).FlowStatisticsEnabled is false, "Disabled status was not parsed.");

Console.WriteLine("Data Statistics parser fixtures passed.");
