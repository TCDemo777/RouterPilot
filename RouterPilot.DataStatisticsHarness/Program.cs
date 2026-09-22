using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;
using RouterPilot.Services;
using RouterPilot.ViewModels;
using RouterPilot.Presentation;

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
Require(ApplicationProtectionVerification.Matches(detailSnapshot, false), "Block verification should accept the requested unblocked state.");
Require(!ApplicationProtectionVerification.Matches(detailSnapshot, true), "Verification mismatch should not report success.");

var blockedDetail = new ApplicationTrafficDetail { ApplicationId = "0", ApplicationName = "example_protocol", IsBlocked = true };
Require(ApplicationProtectionVerification.Matches(blockedDetail, true), "Block verification should accept the requested blocked state.");
Require(!ApplicationProtectionVerification.Matches(null, true), "Missing read-back detail should not report success.");

using JsonDocument emptyDetail = JsonDocument.Parse("""{ "application_id": "app", "application_name": "example", "mac_addresses": {} }""");
Require(DataStatisticsParser.ParseApplicationDetail(emptyDetail.RootElement).Devices.Count == 0, "Empty detail devices were not tolerated.");

Console.WriteLine("Data Statistics parser fixtures passed.");

var traffic = new TrafficSessionAccumulator();
DateTime t0 = DateTime.UnixEpoch;
Require(traffic.Add(new NetworkTrafficObservation(100, 50, t0, "wan")) is null, "Traffic baseline must not count lifetime bytes.");
var sample = traffic.Add(new NetworkTrafficObservation(300, 150, t0.AddSeconds(2), "wan"));
Require(sample is { DownloadBytesPerSecond: 100, UploadBytesPerSecond: 50 } && traffic.DownloadedBytes == 200 && traffic.UploadedBytes == 100,
    "Traffic deltas or session totals were incorrect.");
Require(traffic.Add(new NetworkTrafficObservation(250, 160, t0.AddSeconds(4), "wan")) is null && traffic.SampleCount == 1,
    "Counter reset must establish a new baseline without a negative rate.");
Require(traffic.Add(new NetworkTrafficObservation(350, 260, t0.AddSeconds(6), "wwan")) is null,
    "Interface changes must establish a new baseline.");
Require(traffic.Add(new NetworkTrafficObservation(350, 260, t0.AddSeconds(8), "wwan")) is { DownloadBytesPerSecond: 0, UploadBytesPerSecond: 0 },
    "A genuine zero-rate sample was not retained.");
Require(TrafficRateFormatter.Format(10_764) == "10.51 KiB/s" &&
        TrafficRateFormatter.Format(12_346) == "12.06 KiB/s" &&
        TrafficRateFormatter.Format(76_477) == "74.68 KiB/s" &&
        TrafficRateFormatter.Format(2_107_392) == "2.01 MiB/s",
    "Recent samples and main traffic figures share binary rate formatting and precision.");
var throttled = new TrafficSessionAccumulator();
Require(throttled.Add(new NetworkTrafficObservation(0, 0, t0, "wan")) is null,
    "Throttled traffic baseline must not count lifetime bytes.");
for (int second = 2; second <= 20; second += 2)
    Require(throttled.Add(new NetworkTrafficObservation(second * 100, second * 50, t0.AddSeconds(second), "wan"), second == 2 || second == 12) is not null,
        "Fast telemetry observation was not accepted.");
Require(throttled.DownloadedBytes == 2000 && throttled.UploadedBytes == 1000 &&
        throttled.SampleCount == 2 && throttled.History.Count == 2,
    "Throttled history must retain all byte deltas while keeping only selected samples.");

var recentWindow = new TrafficSessionAccumulator();
Require(recentWindow.GetMostRecentHistory(5).Count == 0, "An empty traffic session must have no recent samples.");
Require(recentWindow.Add(new NetworkTrafficObservation(0, 0, t0, "wan")) is null,
    "Recent-window baseline must not create a sample.");
for (int sampleIndex = 1; sampleIndex <= 7; sampleIndex++)
    Require(recentWindow.Add(new NetworkTrafficObservation(sampleIndex * 100, sampleIndex * 50, t0.AddSeconds(sampleIndex * 2), "wan")) is not null,
        "Recent-window sample was not retained.");
Require(recentWindow.GetMostRecentHistory(5).Count == 5 &&
        recentWindow.GetMostRecentHistory(5).First().TimestampUtc == t0.AddSeconds(6) &&
        recentWindow.GetMostRecentHistory(5).Last().TimestampUtc == t0.AddSeconds(14),
    "Recent traffic window must retain the five newest samples in chronological order.");
Require(recentWindow.History.Count == 7 && recentWindow.SampleCount == 7,
    "Recent traffic display window must not truncate underlying session history.");
Require(recentWindow.Add(new NetworkTrafficObservation(800, 400, t0.AddSeconds(16), "wan")) is not null &&
        recentWindow.GetMostRecentHistory(5).First().TimestampUtc == t0.AddSeconds(8) &&
        recentWindow.GetMostRecentHistory(5).Last().TimestampUtc == t0.AddSeconds(16) &&
        recentWindow.History.Count == 8,
    "Recent traffic window must advance without truncating underlying history.");
traffic.Reset();
Require(traffic.SampleCount == 0 && traffic.History.Count == 0 && traffic.Add(new NetworkTrafficObservation(1, 1, t0, "wan")) is null,
    "Traffic reset must be local and restore the baseline.");
Console.WriteLine("Traffic session fixtures passed.");

await RunReadSeamFixturesAsync();
RunViewModelPresentationFixtures();

static async Task RunReadSeamFixturesAsync()
{
    var activeReader = new FakeDataStatisticsReader
    {
        Traffic = _ => Task.FromResult(new NetworkTrafficSnapshot
        {
            InterfaceName = "wan",
            ReceivedBytes = 100,
            TransmittedBytes = 50,
            IsValid = true
        }),
        Status = _ => Task.FromResult(new DataStatisticsStatus
        {
            FlowStatisticsEnabled = true,
            DpiStatus = "1"
        }),
        TopApps = _ => Task.FromResult(new DataStatisticsSnapshot
        {
            PeriodSeconds = 3600,
            TopApps = [new ApplicationTrafficStat { ApplicationName = "http" }]
        })
    };
    DataStatisticsReadResult active = await CreateService(activeReader).ReadAsync();
    Require(active.Availability == DataStatisticsAvailability.Available &&
        active.Status?.IsDpiActive == true && active.Snapshot?.TopApps.Count == 1 &&
        active.TrafficSnapshot?.IsValid == true,
        "fake reader drives the real service's available classification without altering compatibility data");
    Require(activeReader.Calls.SequenceEqual(["Open", "Traffic", "Status", "TopApps"]),
        "active normal refresh acquires one read session and performs the existing three reads in order");

    var disabledReader = new FakeDataStatisticsReader
    {
        Status = _ => Task.FromResult(new DataStatisticsStatus { FlowStatisticsEnabled = false })
    };
    DataStatisticsReadResult disabled = await CreateService(disabledReader).ReadAsync();
    Require(disabled.Availability == DataStatisticsAvailability.Disabled && !disabledReader.Calls.Contains("TopApps"),
        "disabled status remains distinct and skips the top-app read through the read seam");

    var dpiInactiveReader = new FakeDataStatisticsReader
    {
        Status = _ => Task.FromResult(new DataStatisticsStatus { FlowStatisticsEnabled = true, DpiStatus = "0" })
    };
    DataStatisticsReadResult dpiInactive = await CreateService(dpiInactiveReader).ReadAsync();
    Require(dpiInactive.Availability == DataStatisticsAvailability.DpiInactive && !dpiInactiveReader.Calls.Contains("TopApps"),
        "inactive DPI remains distinct and skips the top-app read through the read seam");

    var unsupportedStatusReader = new FakeDataStatisticsReader
    {
        Status = _ => Task.FromResult(new DataStatisticsStatus())
    };
    DataStatisticsReadResult unsupportedStatus = await CreateService(unsupportedStatusReader).ReadAsync();
    Require(unsupportedStatus.Availability == DataStatisticsAvailability.Unsupported &&
        unsupportedStatus.Status is { HasFlowStatisticsState: false } &&
        !unsupportedStatusReader.Calls.Contains("TopApps"),
        "missing flow-statistics status remains the existing unsupported status-path classification");

    var optionalTrafficFailureReader = new FakeDataStatisticsReader
    {
        Traffic = _ => Task.FromException<NetworkTrafficSnapshot>(new InvalidOperationException("optional traffic unavailable")),
        Status = _ => Task.FromResult(new DataStatisticsStatus { FlowStatisticsEnabled = true, DpiStatus = "1" }),
        TopApps = _ => Task.FromResult(new DataStatisticsSnapshot())
    };
    DataStatisticsReadResult optionalTrafficFailure = await CreateService(optionalTrafficFailureReader).ReadAsync();
    Require(optionalTrafficFailure.Availability == DataStatisticsAvailability.Available &&
        optionalTrafficFailure.TrafficSnapshot is null &&
        optionalTrafficFailureReader.Calls.SequenceEqual(["Open", "Traffic", "Status", "TopApps"]),
        "optional traffic failure does not prevent the existing status and top-app read path");

    var unsupportedReader = new FakeDataStatisticsReader
    {
        Status = _ => Task.FromException<DataStatisticsStatus>(new DataStatisticsRpcException(-32601))
    };
    Require((await CreateService(unsupportedReader).ReadAsync()).Availability == DataStatisticsAvailability.Unsupported,
        "the fake reader can drive the existing method-or-service-unavailable classification");

    var temporaryFailureReader = new FakeDataStatisticsReader
    {
        Status = _ => Task.FromException<DataStatisticsStatus>(new DataStatisticsRpcException(-1))
    };
    Require((await CreateService(temporaryFailureReader).ReadAsync()).Availability == DataStatisticsAvailability.TemporarilyUnavailable,
        "the fake reader can drive the existing transient Data Statistics RPC classification");

    var delayedStatus = new TaskCompletionSource<DataStatisticsStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
    var delayedReader = new FakeDataStatisticsReader
    {
        Status = _ => delayedStatus.Task,
        TopApps = _ => Task.FromResult(new DataStatisticsSnapshot())
    };
    Task<DataStatisticsReadResult> delayedRead = CreateService(delayedReader).ReadAsync();
    await Task.Yield();
    Require(delayedReader.Calls.SequenceEqual(["Open", "Traffic", "Status"]),
        "fake reader can hold a normal service read at the status operation");
    delayedStatus.SetResult(new DataStatisticsStatus { FlowStatisticsEnabled = true, DpiStatus = "1" });
    Require((await delayedRead).Availability == DataStatisticsAvailability.Available,
        "a held fake read can complete through the real service classification path");

    string[] seamMethods = typeof(IDataStatisticsReadSession).GetMethods()
        .Select(method => method.Name)
        .OrderBy(name => name, StringComparer.Ordinal)
        .ToArray();
    Require(seamMethods.SequenceEqual([
            nameof(IDataStatisticsReadSession.GetDataStatisticsStatusAsync),
            nameof(IDataStatisticsReadSession.GetNetworkTrafficSnapshotAsync),
            nameof(IDataStatisticsReadSession.GetTopAppFlowStatisticsAsync)]),
        "the Data Statistics read seam exposes exactly the normal refresh reads and no mutation operation");

    string adapterSource = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "RouterPilot", "Services", "RouterManagerDataStatisticsReader.cs"));
    Require(adapterSource.Contains("GetRouterManagerAsync", StringComparison.Ordinal) &&
        adapterSource.Contains("routerManager.GetNetworkTrafficSnapshotAsync", StringComparison.Ordinal) &&
        adapterSource.Contains("routerManager.GetDataStatisticsStatusAsync", StringComparison.Ordinal) &&
        adapterSource.Contains("routerManager.GetTopAppFlowStatisticsAsync", StringComparison.Ordinal) &&
        !adapterSource.Contains("SetApplicationContentProtectionAsync", StringComparison.Ordinal),
        "production adapter delegates only the established RouterManager normal-refresh reads without mutation translation");

    Console.WriteLine("Data Statistics read seam fixtures passed.");
}

static void RunViewModelPresentationFixtures()
{
    RunOnSta(() =>
    {
        AssertViewModelPresentation(
            "available",
            new FakeDataStatisticsReader
            {
                Traffic = _ => Task.FromResult(ValidTraffic()),
                Status = _ => Task.FromResult(ActiveStatus()),
                TopApps = _ => Task.FromResult(new DataStatisticsSnapshot
                {
                    PeriodSeconds = 3600,
                    TopApps = [new ApplicationTrafficStat { ApplicationName = "http", Label = "HTTP/S", TotalBytes = 10 }]
                })
            },
            RouterPilotStatus.Active,
            "Data Statistics active",
            "Application traffic classified by the router's DPI engine.",
            "Past Hour",
            expectedTopApps: 1);

        AssertViewModelPresentation(
            "disabled",
            new FakeDataStatisticsReader { Status = _ => Task.FromResult(new DataStatisticsStatus { FlowStatisticsEnabled = false }) },
            RouterPilotStatus.Disabled,
            "Data Statistics is disabled",
            "Data Statistics is disabled on the router.",
            "Current period unavailable",
            expectedTopApps: 0);

        AssertViewModelPresentation(
            "DPI inactive",
            new FakeDataStatisticsReader { Status = _ => Task.FromResult(new DataStatisticsStatus { FlowStatisticsEnabled = true, DpiStatus = "0" }) },
            RouterPilotStatus.Pending,
            "Data Statistics is unavailable",
            "The router's DPI engine is not currently active.",
            "Current period unavailable",
            expectedTopApps: 0);

        AssertViewModelPresentation(
            "unsupported",
            new FakeDataStatisticsReader { Status = _ => Task.FromResult(new DataStatisticsStatus()) },
            RouterPilotStatus.Disabled,
            "Data Statistics is not available",
            "This router does not expose the required Data Statistics read interface.",
            "Current period unavailable",
            expectedTopApps: 0);

        AssertViewModelPresentation(
            "temporarily unavailable",
            new FakeDataStatisticsReader
            {
                Status = _ => Task.FromException<DataStatisticsStatus>(new DataStatisticsRpcException(-1))
            },
            RouterPilotStatus.Error,
            "Data Statistics temporarily unavailable",
            "RouterPilot could not read Data Statistics. Try Refresh again.",
            "Current period unavailable",
            expectedTopApps: 0);

        var context = new HarnessActiveRouterContext("router-a", version: 1);
        var delayedStatus = new TaskCompletionSource<DataStatisticsStatus>();
        var delayedReader = new FakeDataStatisticsReader
        {
            Traffic = _ => Task.FromResult(ValidTraffic()),
            Status = _ => delayedStatus.Task,
            TopApps = _ => Task.FromResult(new DataStatisticsSnapshot
            {
                PeriodSeconds = 3600,
                TopApps = [new ApplicationTrafficStat { ApplicationName = "router-a-app" }]
            })
        };
        var staleFollowUpProvider = new ThrowingRouterManagerProvider();
        using var viewModel = CreateViewModelWithProvider(delayedReader, context, staleFollowUpProvider);
        Task staleRefresh = viewModel.RefreshCommand.ExecuteAsync(null);
        Require(delayedReader.Calls.SequenceEqual(["Open", "Traffic", "Status"]),
            "router-A refresh is held before any accepted-result follow-up work");

        context.Switch("router-b");
        viewModel.ResetForRouterSession();
        Require(!viewModel.HasLoaded && viewModel.Status == RouterPilotStatus.Pending &&
            viewModel.TopApps.Count == 0 && viewModel.AllApplications.Count == 0 &&
            viewModel.TrafficHistory.Count == 0,
            "router-session reset clears the existing Data Statistics presentation before a stale completion");

        delayedStatus.SetResult(ActiveStatus());
        staleRefresh.GetAwaiter().GetResult();
        Require(!viewModel.HasLoaded && viewModel.Status == RouterPilotStatus.Pending &&
            viewModel.StatusTitle == "Data Statistics" &&
            viewModel.StatusDetail == "Loading statistics for the selected router." &&
            viewModel.TopApps.Count == 0 && viewModel.AllApplications.Count == 0 &&
            viewModel.TrafficHistory.Count == 0 && staleFollowUpProvider.GetManagerCalls == 0,
            "delayed router-A completion may finish its in-flight read but cannot overwrite reset router-B presentation, loaded state, traffic, collections, or trigger ViewModel follow-up reads");

        delayedReader.Calls.Clear();
        delayedReader.Status = _ => Task.FromResult(ActiveStatus());
        delayedReader.TopApps = _ => Task.FromResult(new DataStatisticsSnapshot
        {
            PeriodSeconds = 3600,
            TopApps = [new ApplicationTrafficStat { ApplicationName = "router-b-app", Label = "Router B", TotalBytes = 20 }]
        });
        viewModel.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Require(viewModel.HasLoaded && viewModel.Status == RouterPilotStatus.Active &&
            viewModel.TopApps.Count == 1 && viewModel.TopApps[0].ApplicationName == "router-b-app" &&
            delayedReader.Calls.SequenceEqual(["Open", "Traffic", "Status", "TopApps"]),
            "successful current router-B refresh is accepted and published normally after stale router-A rejection");
    });

    Console.WriteLine("Data Statistics ViewModel presentation and router-context fixtures passed.");
}

static void AssertViewModelPresentation(
    string scenario,
    FakeDataStatisticsReader reader,
    RouterPilotStatus expectedStatus,
    string expectedTitle,
    string expectedDetail,
    string expectedPeriod,
    int expectedTopApps)
{
    using var viewModel = CreateViewModel(reader, new HarnessActiveRouterContext("router-a", version: 1));
    viewModel.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
    Require(viewModel.HasLoaded && !viewModel.IsLoading &&
        viewModel.Status == expectedStatus &&
        viewModel.StatusTitle == expectedTitle &&
        viewModel.StatusDetail == expectedDetail &&
        viewModel.CurrentPeriod == expectedPeriod &&
        viewModel.TopApps.Count == expectedTopApps &&
        viewModel.AllApplications.Count == 0 &&
        viewModel.TrafficHistory.Count == 0,
        $"{scenario} ViewModel presentation retains the current observable status, text, period, collection, and initial traffic state");
}

static DataStatisticsViewModel CreateViewModel(FakeDataStatisticsReader reader, IActiveRouterContext context) =>
    CreateViewModelWithProvider(reader, context, new ThrowingRouterManagerProvider());

static DataStatisticsViewModel CreateViewModelWithProvider(FakeDataStatisticsReader reader, IActiveRouterContext context,
    ThrowingRouterManagerProvider provider) =>
    new(CreateService(reader, provider), new ClientInventoryState(), new ClientProfileService(), context);

static DataStatisticsStatus ActiveStatus() => new() { FlowStatisticsEnabled = true, DpiStatus = "1" };

static NetworkTrafficSnapshot ValidTraffic() => new()
{
    InterfaceName = "wan",
    ReceivedBytes = 100,
    TransmittedBytes = 50,
    IsValid = true,
    CapturedAtUtc = DateTime.UnixEpoch
};

static void RunOnSta(Action action)
{
    Exception? failure = null;
    using var completed = new ManualResetEventSlim();
    var thread = new Thread(() =>
    {
        try { action(); }
        catch (Exception exception) { failure = exception; }
        finally { completed.Set(); }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    completed.Wait();
    if (failure is not null)
        throw new InvalidOperationException("Data Statistics ViewModel fixture failed.", failure);
}

static DataStatisticsService CreateService(FakeDataStatisticsReader reader,
    ThrowingRouterManagerProvider? provider = null) =>
    new(provider ?? new ThrowingRouterManagerProvider(), reader);

sealed class FakeDataStatisticsReader : IDataStatisticsReader, IDataStatisticsReadSession
{
    public List<string> Calls { get; } = [];
    public Func<CancellationToken, Task<NetworkTrafficSnapshot>> Traffic { get; set; } =
        _ => Task.FromResult(new NetworkTrafficSnapshot());
    public Func<CancellationToken, Task<DataStatisticsStatus>> Status { get; set; } =
        _ => Task.FromResult(new DataStatisticsStatus());
    public Func<CancellationToken, Task<DataStatisticsSnapshot>> TopApps { get; set; } =
        _ => Task.FromResult(new DataStatisticsSnapshot());

    public Task<IDataStatisticsReadSession> OpenReadSessionAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add("Open");
        return Task.FromResult<IDataStatisticsReadSession>(this);
    }

    public Task<NetworkTrafficSnapshot> GetNetworkTrafficSnapshotAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add("Traffic");
        return Traffic(cancellationToken);
    }

    public Task<DataStatisticsStatus> GetDataStatisticsStatusAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add("Status");
        return Status(cancellationToken);
    }

    public Task<DataStatisticsSnapshot> GetTopAppFlowStatisticsAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add("TopApps");
        return TopApps(cancellationToken);
    }
}

sealed class ThrowingRouterManagerProvider : IRouterManagerProvider
{
    public int GetManagerCalls { get; private set; }

    public Task<RouterManager> GetRouterManagerAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<RouterManager>(CreateReadFailure());

    public void Invalidate() { }
    public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private Exception CreateReadFailure()
    {
        GetManagerCalls++;
        return new InvalidOperationException("Normal Data Statistics reads must use IDataStatisticsReader.");
    }
}

sealed class HarnessActiveRouterContext : IActiveRouterContext
{
    private RouterProfile _profile;

    public HarnessActiveRouterContext(string profileId, long version)
    {
        _profile = new RouterProfile { Id = profileId };
        Version = version;
    }

    public RouterProfile CurrentProfile => _profile;
    public string CurrentProfileId => _profile.Id;
    public long Version { get; private set; }
    public void InvalidateSession() => Version++;

    public void Switch(string profileId)
    {
        _profile = new RouterProfile { Id = profileId };
        Version++;
    }
}
