using System;
using System.Linq;
using System.Text.Json;
using RouterPilot.Models;
using RouterPilot.Services;
using RouterPilot.ViewModels;

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

using JsonDocument fullHour = JsonDocument.Parse("""
{
  "time": "hour",
  "applications": [
    { "application_id": "-1", "application_name": "all_traffic", "label": "All traffic", "upload": 200000000000, "download": 300000000000, "total": 500000000000, "icon": "" },
    { "application_id": "0", "application_name": "http", "label": "HTTP/S", "upload": 10, "download": 90, "total": 100, "packets": 15, "icon": "" },
    { "application_id": "0", "application_name": "quic", "label": "QUIC", "upload": 20, "download": 180, "total": 200, "icon": "" },
    { "application_id": "malformed", "application_name": "partial", "total": "not-a-number" },
    "not-an-application"
  ]
}
""");
var fullHourSnapshot = DataStatisticsParser.ParseFullSnapshot(fullHour.RootElement);
Require(fullHourSnapshot.Period == "hour", "Full table hour period was not parsed.");
Require(fullHourSnapshot.Aggregate?.TotalBytes == 500000000000, "All traffic aggregate was not extracted.");
Require(fullHourSnapshot.Applications.Count == 3, "Aggregate or malformed rows were handled incorrectly.");
Require(fullHourSnapshot.Applications.Count(row => row.ApplicationId == "0") == 2, "Duplicate application ID rows collided in full table.");
Require(fullHourSnapshot.Applications.Single(row => row.ApplicationName == "quic").PacketCount is null, "Missing packets were not tolerated.");
Require(fullHourSnapshot.Applications.Single(row => row.ApplicationName == "partial").TotalBytes == 0, "Malformed numeric field was not tolerated.");
Require(DataStatisticsViewModel.ArePeriodsAligned(3600, fullHourSnapshot.Period), "Hour periods should align.");
Require(DataStatisticsViewModel.ArePeriodsAligned(86400, "day"), "Day periods should align.");
Require(!DataStatisticsViewModel.ArePeriodsAligned(3600, "day"), "Period mismatch was not detected.");

using JsonDocument emptyFullTable = JsonDocument.Parse("""{ "time": "week", "applications": [] }""");
var emptyFullSnapshot = DataStatisticsParser.ParseFullSnapshot(emptyFullTable.RootElement);
Require(emptyFullSnapshot.Period == "week" && emptyFullSnapshot.Aggregate is null && emptyFullSnapshot.Applications.Count == 0,
    "Empty full table was not tolerated.");

using JsonDocument detail = JsonDocument.Parse("""
{
  "application_id": "0", "application_name": "example_protocol", "identifier": "example", "label": "Example",
  "url": "https://example.invalid", "desc": "Sanitised application description.", "logo": "", "application_block": false,
  "period_seconds": 3600, "total_upload": 200000000000, "total_download": 300000000000,
  "metadata": { "start_time": 1700000000, "end_time": 1700000300 },
  "mac_addresses": {
    "AA:BB:CC:DD:EE:FF": { "hostname": "Example device", "upload": 10, "download": 90, "total": 100, "packets": 5, "record_count": 2, "last_active_time": 1700000300, "last_active_relative": "recently" },
    "aabbccddeeff": { "hostname": "", "upload": 20, "download": 180, "total": 200, "record_count": 1 },
    "invalid-key": { "upload": 1, "download": 2, "total": 3 },
    "malformed": "not-an-object"
  },
  "time_series": [{ "start_time": 1700000000, "end_time": 1700000300, "upload": 30, "download": 270, "total": 300 }]
}
""");
var detailSnapshot = DataStatisticsParser.ParseApplicationDetail(detail.RootElement);
Require(detailSnapshot.ApplicationId == "0" && detailSnapshot.ApplicationName == "example_protocol", "Detail request key was not retained.");
Require(detailSnapshot.TotalBytes == 500000000000, "Large application detail counters were not added safely.");
Require(detailSnapshot.MetadataEndUtc?.ToUnixTimeSeconds() == 1700000300 && detailSnapshot.TimeSeries.Count == 1, "Detail metadata or time series was not parsed.");
Require(detailSnapshot.Devices.Count == 3, "MAC dictionary or malformed device handling failed.");
Require(detailSnapshot.Devices.Count(device => device.CanViewClient) == 2, "MAC normalization did not retain valid MAC keys.");
Require(detailSnapshot.Devices.First(device => device.Hostname == string.Empty && device.CanViewClient).PacketCount is null, "Missing device packets were not tolerated.");
Require(ClientIdentity.NormalizeMac("aa-bb-cc-dd-ee-ff") == "AABBCCDDEEFF", "ClientIdentity MAC normalization failed.");

using JsonDocument emptyDetail = JsonDocument.Parse("""{ "application_id": "app", "application_name": "example", "mac_addresses": {} }""");
Require(DataStatisticsParser.ParseApplicationDetail(emptyDetail.RootElement).Devices.Count == 0, "Empty detail devices were not tolerated.");

Console.WriteLine("Data Statistics parser fixtures passed.");
