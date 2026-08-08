using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using RouterPilot.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveChartsCore;
using LiveChartsCore.Kernel;
using LiveChartsCore.SkiaSharpView;

namespace RouterPilot.ViewModels
{
    public partial class DashboardViewModel : ObservableObject
    {
        private const int TrafficHistoryCapacity = 60;
        private const int TrafficSampleIntervalSeconds = 2;
        private const int QueryHistoryCapacity = 120;
        private string _queryHistoryTimeUnits = "hours";

        //
        // Router
        //

        [ObservableProperty]
        private bool routerConnected;

        [ObservableProperty]
        private string routerModel = "-";

        [ObservableProperty]
        private string firmwareVersion = "-";

        // OpenWrt's board release and the GL.iNet firmware release are
        // separate values. Keep both so UI surfaces cannot label the former
        // as the installed vendor firmware.
        [ObservableProperty]
        private string routerFirmwareVersion = "-";

        [ObservableProperty]
        private string hostname = "-";

        [ObservableProperty]
        private string uptime = "-";

        [ObservableProperty]
        private string cpuUsage = "-";

        [ObservableProperty]
        private string temperature = "-";

        [ObservableProperty]
        private string loadAverage = "-";

        [ObservableProperty]
        private double cpuPercentage;

        [ObservableProperty]
        private bool cpuUtilisationPending;

        [ObservableProperty]
        private string memoryUsage = "-";

        [ObservableProperty]
        private double memoryPercentage;

        [ObservableProperty]
        private string storageUsage = "-";

        [ObservableProperty]
        private double storagePercentage;

        [ObservableProperty]
        private string storageUsed = "-";

        [ObservableProperty]
        private string storageAvailable = "-";

        [ObservableProperty]
        private string storageTotal = "-";

        public string CpuHealthText =>
            CpuPercentage >= 90
                ? "High usage"
                : CpuPercentage >= 70
                    ? "Elevated"
                    : CpuPercentage > 0
                        ? "Healthy"
                        : CpuUtilisationPending
                            ? RouterPilotStatusPresentation.Pending
                            : RouterPilotStatusPresentation.NotAvailable;

        public string CpuHealthColour =>
            CpuPercentage >= 90
                ? "#C62828"
                : CpuPercentage >= 70
                    ? "#B26A00"
                    : CpuPercentage > 0
                        ? "#16803C"
                        : RouterPilotStatusPresentation.Colour(
                            CpuUtilisationPending
                                ? RouterPilotStatus.Pending
                                : RouterPilotStatus.NotAvailable);

        public string CpuUsageDisplay => CpuUtilisationPending
            ? RouterPilotStatusPresentation.Pending
            : CpuPercentage >= 0 && CpuUsage != "-"
                ? CpuUsage
                : RouterPilotStatusPresentation.NotAvailable;

        public string MemoryHealthText =>
            MemoryPercentage >= 90
                ? "High usage"
                : MemoryPercentage >= 75
                    ? "Elevated"
                    : MemoryPercentage > 0
                        ? "Healthy"
                        : RouterPilotStatusPresentation.NotAvailable;

        public string MemoryHealthColour =>
            MemoryPercentage >= 90
                ? "#C62828"
                : MemoryPercentage >= 75
                    ? "#B26A00"
                    : MemoryPercentage > 0
                        ? "#16803C"
                        : "#687386";

        public string StorageHealthText =>
            StoragePercentage >= 90
                ? "Nearly full"
                : StoragePercentage >= 75
                    ? "Elevated"
                    : StoragePercentage > 0
                        ? "Healthy"
                        : RouterPilotStatusPresentation.NotAvailable;

        public string StorageHealthColour =>
            StoragePercentage >= 90
                ? "#C62828"
                : StoragePercentage >= 75
                    ? "#B26A00"
                    : StoragePercentage > 0
                        ? "#16803C"
                        : "#687386";


        //
        // AdGuard summary
        //

        [ObservableProperty]
        private bool adGuardRunning;

        [ObservableProperty]
        private AdGuardAvailabilityState adGuardAvailability =
            AdGuardAvailabilityState.Unavailable;

        [ObservableProperty]
        private AdGuardMaintenanceState adGuardMaintenanceState;

        [ObservableProperty]
        private bool adGuardProtectionEnabled;

        [ObservableProperty]
        private bool adGuardProtectionPaused;

        [ObservableProperty]
        private bool adGuardProtectionStatusKnown;

        [ObservableProperty]
        private string adGuardProtectionRemaining = "";

        [ObservableProperty]
        private string adGuardVersion = "-";

        [ObservableProperty]
        private string adGuardProcess = "-";

        [ObservableProperty]
        private string adGuardService = "-";

        [ObservableProperty]
        private string adGuardQueries = "-";

        [ObservableProperty]
        private string adGuardBlocked = "-";

        [ObservableProperty]
        private string adGuardBlockRate = "-";


        //
        // AdGuard graph and rankings
        //

        public ObservableCollection<AdGuardTimePoint>
            AdGuardQueryHistory
        {
            get;
        } = new();

        public ISeries[] QueryHistorySeries { get; }

        public Axis[] QueryHistoryXAxes { get; }

        public Axis[] QueryHistoryYAxes { get; }

        public ObservableCollection<AdGuardRankedItem>
            TopClients
        {
            get;
        } = new();

        public ObservableCollection<AdGuardRankedItem>
            TopQueriedDomains
        {
            get;
        } = new();

        public ObservableCollection<AdGuardRankedItem>
            TopBlockedDomains
        {
            get;
        } = new();


        //
        // Internet
        //

        [ObservableProperty]
        private bool internetConnected;

        [ObservableProperty]
        private string wanIp = "-";

        [ObservableProperty]
        private string gateway = "-";

        [ObservableProperty]
        private string externalDns = "-";

        [ObservableProperty]
        private string advertisedDns = "-";

        [ObservableProperty]
        private string latency = "-";

        [ObservableProperty]
        private string wifi24Ssid = "-";

        [ObservableProperty]
        private string wifi24Channel = "-";

        [ObservableProperty]
        private string wifi24Clients = "0 clients";

        [ObservableProperty]
        private string wifi24Status = RouterPilotStatusPresentation.NotAvailable;

        [ObservableProperty]
        private string wifi5Ssid = "-";

        [ObservableProperty]
        private string wifi5Channel = "-";

        [ObservableProperty]
        private string wifi5Clients = "0 clients";

        [ObservableProperty]
        private string wifi5Status = RouterPilotStatusPresentation.NotAvailable;

        [ObservableProperty]
        private string wanInterface = "-";

        [ObservableProperty]
        private string currentDownload = "0 Mbps";

        [ObservableProperty]
        private string currentUpload = "0 Mbps";

        [ObservableProperty]
        private string peakDownload = "0 Mbps";

        [ObservableProperty]
        private string peakUpload = "0 Mbps";

        [ObservableProperty]
        private string averageDownload = "0 Mbps";

        [ObservableProperty]
        private string averageUpload = "0 Mbps";

        public ObservableCollection<double> DownloadHistory { get; } = new();

        public ObservableCollection<double> UploadHistory { get; } = new();

        public ISeries[] NetworkTrafficSeries { get; }

        public Axis[] NetworkTrafficXAxes { get; }

        public Axis[] NetworkTrafficYAxes { get; }

        public DashboardViewModel()
        {
            QueryHistorySeries = new ISeries[]
            {
                new LineSeries<AdGuardTimePoint>
                {
                    Name = "Queries",
                    Values = AdGuardQueryHistory,
                    Mapping = (point, index) =>
                        new Coordinate(index, point.Queries),
                    GeometrySize = 0,
                    LineSmoothness = 0.35,
                    XToolTipLabelFormatter = point =>
                        point.Model is { } model
                            ? $"Time: {model.FormatTimeLabel(_queryHistoryTimeUnits)}"
                            : "Time: -",
                    YToolTipLabelFormatter = point =>
                        point.Model is { } model
                            ? $"Queries: {model.Queries:N0}"
                            : "Queries: 0"
                }
            };

            QueryHistoryXAxes = new Axis[]
            {
                new Axis
                {
                    MinStep = 1,
                    Labeler = FormatQueryHistoryAxisLabel
                }
            };

            QueryHistoryYAxes = new Axis[]
            {
                new Axis
                {
                    Name = "Queries",
                    MinLimit = 0
                }
            };

            NetworkTrafficSeries = new ISeries[]
            {
                new LineSeries<double>
                {
                    Name = "Download",
                    Values = DownloadHistory,
                    GeometrySize = 0,
                    LineSmoothness = 0.35,
                    YToolTipLabelFormatter = point =>
                        $"Download: {point.Model:0.00} Mbps"
                },
                new LineSeries<double>
                {
                    Name = "Upload",
                    Values = UploadHistory,
                    GeometrySize = 0,
                    LineSmoothness = 0.35,
                    YToolTipLabelFormatter = point =>
                        $"Upload: {point.Model:0.00} Mbps"
                }
            };

            NetworkTrafficXAxes = new Axis[]
            {
                new Axis
                {
                    MinLimit = 0,
                    MaxLimit = TrafficHistoryCapacity,
                    MinStep = 15,
                    ForceStepToMin = true,
                    Labeler = FormatTrafficTimeLabel
                }
            };

            NetworkTrafficYAxes = new Axis[]
            {
                new Axis
                {
                    Name = "Mbps",
                    MinLimit = 0
                }
            };
        }

        public string DnsServer
        {
            get => ExternalDns;
            set => ExternalDns = value;
        }


        //
        // Dashboard
        //

        [ObservableProperty]
        private string statusMessage = "Ready";

        [ObservableProperty]
        private string lastRefresh = "-";


        //
        // Status text
        //

        public string RouterStatusText =>
            RouterPilotStatusPresentation.Text(
                RouterConnected
                    ? RouterPilotStatus.Connected
                    : RouterPilotStatus.Error);

        public string RouterStatusColour =>
            RouterPilotStatusPresentation.Colour(
                RouterConnected
                    ? RouterPilotStatus.Connected
                    : RouterPilotStatus.Error);

        private RouterPilotStatus AdGuardStatus => AdGuardMaintenanceState switch
        {
            AdGuardMaintenanceState.Restarting => RouterPilotStatus.Pending,
            AdGuardMaintenanceState.Failed => RouterPilotStatus.Error,
            _ => AdGuardAvailability switch
            {
                AdGuardAvailabilityState.Available when AdGuardRunning => RouterPilotStatus.Active,
                AdGuardAvailabilityState.Available or AdGuardAvailabilityState.AuthenticationFailed => RouterPilotStatus.Error,
                _ => RouterPilotStatus.NotAvailable
            }
        };

        public string AdGuardStatusText =>
            RouterPilotStatusPresentation.Text(AdGuardStatus);

        public string AdGuardStatusSubtitle =>
            AdGuardMaintenanceState == AdGuardMaintenanceState.Restarting
                ? "Restarting AdGuard Home"
                : "DNS filtering";

        public bool IsAdGuardAvailable =>
            AdGuardAvailability == AdGuardAvailabilityState.Available;

        public string AdGuardVersionDisplay => IsAdGuardAvailable ? AdGuardVersion : RouterPilotStatusPresentation.NotAvailable;
        public string AdGuardServiceDisplay => IsAdGuardAvailable ? AdGuardStatusText : RouterPilotStatusPresentation.NotAvailable;
        public string AdGuardQueriesDisplay => IsAdGuardAvailable ? AdGuardQueries : RouterPilotStatusPresentation.NotAvailable;
        public string AdGuardBlockedDisplay => IsAdGuardAvailable ? AdGuardBlocked : RouterPilotStatusPresentation.NotAvailable;
        public string AdGuardBlockRateDisplay => IsAdGuardAvailable ? AdGuardBlockRate : RouterPilotStatusPresentation.NotAvailable;
        public string AdGuardLiveStatusText => RouterPilotStatusPresentation.Text(
            IsAdGuardAvailable
                ? RouterPilotStatus.Active
                : RouterPilotStatus.NotAvailable);
        public string TopClientsEmptyText => IsAdGuardAvailable ? "No client ranking data yet." : RouterPilotStatusPresentation.NotAvailable;
        public string TopBlockedDomainsEmptyText => IsAdGuardAvailable ? "No blocked-domain data yet." : RouterPilotStatusPresentation.NotAvailable;
        public string TopQueriedDomainsEmptyText => IsAdGuardAvailable ? "No requested-domain data yet." : RouterPilotStatusPresentation.NotAvailable;

        public string AdGuardAvailabilityMessage => AdGuardAvailability switch
        {
            AdGuardAvailabilityState.Available => string.Empty,
            AdGuardAvailabilityState.NotConfigured =>
                "AdGuard Home is not configured. Router monitoring remains active.",
            AdGuardAvailabilityState.AuthenticationFailed =>
                "AdGuard Home authentication failed. Router monitoring remains active.",
            _ => "AdGuard Home is unavailable. Router monitoring remains active."
        };

        public string AdGuardStatusColour =>
            RouterPilotStatusPresentation.Colour(AdGuardStatus);

        public string AdGuardProtectionStatusText =>
            !IsAdGuardAvailable || !AdGuardProtectionStatusKnown
                ? RouterPilotStatusPresentation.Text(RouterPilotStatus.NotAvailable)
                : AdGuardProtectionEnabled
                    ? RouterPilotStatusPresentation.Text(RouterPilotStatus.Active)
                    : AdGuardProtectionPaused
                        ? RouterPilotStatusPresentation.Text(RouterPilotStatus.Pending)
                        : RouterPilotStatusPresentation.Text(RouterPilotStatus.Disabled);

        public string AdGuardProtectionStatusColour =>
            !IsAdGuardAvailable || !AdGuardProtectionStatusKnown
                ? RouterPilotStatusPresentation.Colour(RouterPilotStatus.NotAvailable)
                : AdGuardProtectionEnabled
                    ? RouterPilotStatusPresentation.Colour(RouterPilotStatus.Active)
                    : AdGuardProtectionPaused
                        ? RouterPilotStatusPresentation.Colour(RouterPilotStatus.Pending)
                        : RouterPilotStatusPresentation.Colour(RouterPilotStatus.Disabled);

        public string InternetStatusText =>
            RouterPilotStatusPresentation.Text(
                InternetConnected
                    ? RouterPilotStatus.Connected
                    : RouterPilotStatus.Error);

        public string InternetStatusColour =>
            RouterPilotStatusPresentation.Colour(
                InternetConnected
                    ? RouterPilotStatus.Connected
                    : RouterPilotStatus.Error);

        public string OverallStatusColour =>
            RouterPilotStatusPresentation.Colour(
                RouterConnected && InternetConnected && AdGuardRunning
                    ? RouterPilotStatus.Active
                    : RouterPilotStatus.Error);



        public ObservableCollection<WifiRadioInfo> WifiNetworks { get; } = new();

        [ObservableProperty]
        private string wifiRefreshError = string.Empty;

        public void UpdateWifiRadios(IEnumerable<WifiRadioInfo> radios)
        {
            List<WifiRadioInfo> networkList = radios?.ToList() ?? new List<WifiRadioInfo>();

            WifiNetworks.Clear();
            foreach (WifiRadioInfo network in networkList
                         .OrderByDescending(r => r.ClientCount)
                         .ThenBy(r => r.Band)
                         .ThenBy(r => r.Ssid, StringComparer.OrdinalIgnoreCase))
            {
                WifiNetworks.Add(network);
            }

            WifiRadioInfo? radio24 = networkList.FirstOrDefault(r => r.Band.StartsWith("2.4", StringComparison.OrdinalIgnoreCase));
            WifiRadioInfo? radio5 = networkList.FirstOrDefault(r => r.Band.StartsWith("5", StringComparison.OrdinalIgnoreCase));

            Wifi24Ssid = radio24?.Ssid ?? "Not detected";
            Wifi24Channel = radio24 == null ? "-" : $"Channel {radio24.Channel}";
            Wifi24Clients = $"{networkList.Where(r => r.Band.StartsWith("2.4", StringComparison.OrdinalIgnoreCase)).Sum(r => r.ClientCount)} clients";
            Wifi24Status = radio24?.StatusDisplay ?? RouterPilotStatusPresentation.NotAvailable;

            Wifi5Ssid = radio5?.Ssid ?? "Not detected";
            Wifi5Channel = radio5 == null ? "-" : $"Channel {radio5.Channel}";
            Wifi5Clients = $"{networkList.Where(r => r.Band.StartsWith("5", StringComparison.OrdinalIgnoreCase)).Sum(r => r.ClientCount)} clients";
            Wifi5Status = radio5?.StatusDisplay ?? RouterPilotStatusPresentation.NotAvailable;
        }

        //
        // Collection updates
        //

        public void UpdateNetworkTraffic(
            double downloadMbps,
            double uploadMbps,
            double peakDownloadMbps,
            double peakUploadMbps,
            double averageDownloadMbps,
            double averageUploadMbps,
            string interfaceName)
        {
            if (Application.Current?.Dispatcher is { } dispatcher &&
                !dispatcher.CheckAccess())
            {
                dispatcher.Invoke(
                    () => UpdateNetworkTraffic(
                        downloadMbps,
                        uploadMbps,
                        peakDownloadMbps,
                        peakUploadMbps,
                        averageDownloadMbps,
                        averageUploadMbps,
                        interfaceName));
                return;
            }

            WanInterface = string.IsNullOrWhiteSpace(interfaceName)
                ? "-"
                : interfaceName;

            CurrentDownload = FormatTrafficRate(downloadMbps);
            CurrentUpload = FormatTrafficRate(uploadMbps);
            PeakDownload = FormatTrafficRate(peakDownloadMbps);
            PeakUpload = FormatTrafficRate(peakUploadMbps);
            AverageDownload = FormatTrafficRate(averageDownloadMbps);
            AverageUpload = FormatTrafficRate(averageUploadMbps);

            AddTrafficPoint(DownloadHistory, downloadMbps);
            AddTrafficPoint(UploadHistory, uploadMbps);
        }

        public void ClearNetworkTraffic()
        {
            if (Application.Current?.Dispatcher is { } dispatcher &&
                !dispatcher.CheckAccess())
            {
                dispatcher.Invoke(ClearNetworkTraffic);
                return;
            }

            WanInterface = "-";
            CurrentDownload = "0 Mbps";
            CurrentUpload = "0 Mbps";
            PeakDownload = "0 Mbps";
            PeakUpload = "0 Mbps";
            AverageDownload = "0 Mbps";
            AverageUpload = "0 Mbps";
            DownloadHistory.Clear();
            UploadHistory.Clear();
        }

        private static void AddTrafficPoint(
            ObservableCollection<double> collection,
            double value)
        {
            collection.Add(Math.Max(0, value));

            while (collection.Count > TrafficHistoryCapacity)
            {
                collection.RemoveAt(0);
            }
        }

        private static string FormatTrafficTimeLabel(double sampleIndex)
        {
            int secondsAgo = Math.Max(
                0,
                (int)Math.Round(
                    (TrafficHistoryCapacity - sampleIndex) *
                    TrafficSampleIntervalSeconds));

            return secondsAgo switch
            {
                120 => "2m ago",
                60 => "1m ago",
                0 => "Now",
                _ => $"{secondsAgo}s ago"
            };
        }

        private static string FormatTrafficRate(double megabitsPerSecond)
        {
            if (megabitsPerSecond >= 1000)
            {
                return $"{megabitsPerSecond / 1000d:0.00} Gbps";
            }

            if (megabitsPerSecond >= 1)
            {
                return $"{megabitsPerSecond:0.0} Mbps";
            }

            return $"{megabitsPerSecond * 1000d:0} Kbps";
        }

        public void UpdateAdGuardStatistics(
            AdGuardStatistics statistics)
        {
            if (Application.Current?.Dispatcher is { } dispatcher &&
                !dispatcher.CheckAccess())
            {
                dispatcher.Invoke(
                    () => UpdateAdGuardStatistics(statistics));
                return;
            }

            string timeUnits =
                string.IsNullOrWhiteSpace(
                    statistics.QueryHistoryTimeUnits)
                    ? "hours"
                    : statistics.QueryHistoryTimeUnits;

            List<AdGuardTimePoint> incomingHistory =
                statistics.QueryHistory
                    .TakeLast(QueryHistoryCapacity)
                    .Select(point => new AdGuardTimePoint
                    {
                        Timestamp = NormalizeQueryHistoryTimestamp(
                            point.Timestamp,
                            timeUnits),
                        Queries = point.Queries,
                        Blocked = point.Blocked
                    })
                    .ToList();

            UpdateQueryHistory(incomingHistory, timeUnits);

            ReplaceCollection(
                TopClients,
                statistics.TopClients);

            ReplaceCollection(
                TopQueriedDomains,
                statistics.TopQueriedDomains);

            ReplaceCollection(
                TopBlockedDomains,
                statistics.TopBlockedDomains);
        }

        public void UpdateRankingsFromQueryLog(
            IEnumerable<QueryLogEntry> entries,
            bool onlyWhenEmpty = true)
        {
            List<QueryLogEntry> snapshot =
                entries?.ToList() ??
                new List<QueryLogEntry>();

            if (snapshot.Count == 0)
            {
                return;
            }

            if (!onlyWhenEmpty ||
                TopClients.Count == 0)
            {
                ReplaceCollection(
                    TopClients,
                    snapshot
                        .Where(entry =>
                            !string.IsNullOrWhiteSpace(entry.Client))
                        .GroupBy(
                            entry => entry.Client,
                            StringComparer.OrdinalIgnoreCase)
                        .Select(group =>
                            new
                            {
                                Name = group.Key,
                                Count = group.Count()
                            })
                        .OrderByDescending(item => item.Count)
                        .ThenBy(item => item.Name)
                        .Take(10)
                        .Select(item =>
                            CreateRankedItem(
                                item.Name,
                                item.Count)));
            }

            if (!onlyWhenEmpty ||
                TopQueriedDomains.Count == 0)
            {
                ReplaceCollection(
                    TopQueriedDomains,
                    snapshot
                        .Where(entry =>
                            !string.IsNullOrWhiteSpace(entry.Domain))
                        .GroupBy(
                            entry => entry.Domain,
                            StringComparer.OrdinalIgnoreCase)
                        .Select(group =>
                            new
                            {
                                Name = group.Key,
                                Count = group.Count()
                            })
                        .OrderByDescending(item => item.Count)
                        .ThenBy(item => item.Name)
                        .Take(10)
                        .Select(item =>
                            CreateRankedItem(
                                item.Name,
                                item.Count)));
            }

            if (!onlyWhenEmpty ||
                TopBlockedDomains.Count == 0)
            {
                ReplaceCollection(
                    TopBlockedDomains,
                    snapshot
                        .Where(entry =>
                            entry.IsBlocked &&
                            !string.IsNullOrWhiteSpace(entry.Domain))
                        .GroupBy(
                            entry => entry.Domain,
                            StringComparer.OrdinalIgnoreCase)
                        .Select(group =>
                            new
                            {
                                Name = group.Key,
                                Count = group.Count()
                            })
                        .OrderByDescending(item => item.Count)
                        .ThenBy(item => item.Name)
                        .Take(10)
                        .Select(item =>
                            CreateRankedItem(
                                item.Name,
                                item.Count)));
            }
        }

        private static AdGuardRankedItem CreateRankedItem(
            string name,
            int count)
        {
            var item =
                new AdGuardRankedItem();

            // RouterPilot has used different display-property names for this
            // model during development. Set whichever one exists without
            // introducing another compile-time dependency.
            Type itemType =
                typeof(AdGuardRankedItem);

            string[] namePropertyCandidates =
            {
                "Name",
                "Domain",
                "Label",
                "Value",
                "Client"
            };

            foreach (string propertyName in namePropertyCandidates)
            {
                var property =
                    itemType.GetProperty(propertyName);

                if (property?.CanWrite == true &&
                    property.PropertyType == typeof(string))
                {
                    property.SetValue(item, name);
                    break;
                }
            }

            string[] countPropertyCandidates =
            {
                "Count",
                "Queries",
                "Total",
                "ValueCount"
            };

            foreach (string propertyName in countPropertyCandidates)
            {
                var property =
                    itemType.GetProperty(propertyName);

                if (property?.CanWrite != true)
                {
                    continue;
                }

                if (property.PropertyType == typeof(int))
                {
                    property.SetValue(item, count);
                    break;
                }

                if (property.PropertyType == typeof(long))
                {
                    property.SetValue(item, (long)count);
                    break;
                }
            }

            return item;
        }

        public void ClearAdGuardStatistics()
        {
            if (Application.Current?.Dispatcher is { } dispatcher &&
                !dispatcher.CheckAccess())
            {
                dispatcher.Invoke(ClearAdGuardStatistics);
                return;
            }

            AdGuardQueryHistory.Clear();
            TopClients.Clear();
            TopQueriedDomains.Clear();
            TopBlockedDomains.Clear();

            AdGuardProtectionEnabled = false;
            AdGuardProtectionPaused = false;
            AdGuardProtectionStatusKnown = false;
            AdGuardProtectionRemaining = "";
        }

        private string FormatQueryHistoryAxisLabel(double pointIndex)
        {
            int index = (int)Math.Round(pointIndex);

            return index >= 0 &&
                   index < AdGuardQueryHistory.Count
                ? AdGuardQueryHistory[index]
                    .FormatTimeLabel(_queryHistoryTimeUnits)
                : string.Empty;
        }

        private void UpdateQueryHistory(
            IReadOnlyList<AdGuardTimePoint> incomingHistory,
            string timeUnits)
        {
            if (!CanUpdateQueryHistoryIncrementally(
                    incomingHistory,
                    timeUnits,
                    out int existingOverlapIndex))
            {
                RebuildQueryHistory(incomingHistory, timeUnits);
                return;
            }

            for (int index = 0;
                 index < existingOverlapIndex;
                 index++)
            {
                AdGuardQueryHistory.RemoveAt(0);
            }

            int overlapCount = AdGuardQueryHistory.Count;

            for (int index = 0; index < overlapCount; index++)
            {
                AdGuardTimePoint current = AdGuardQueryHistory[index];
                AdGuardTimePoint incoming = incomingHistory[index];

                if (current.Queries != incoming.Queries)
                {
                    AdGuardQueryHistory[index] = incoming;
                }
            }

            for (int index = overlapCount;
                 index < incomingHistory.Count;
                 index++)
            {
                AdGuardQueryHistory.Add(incomingHistory[index]);
            }

            _queryHistoryTimeUnits = timeUnits;
        }

        private bool CanUpdateQueryHistoryIncrementally(
            IReadOnlyList<AdGuardTimePoint> incomingHistory,
            string timeUnits,
            out int existingOverlapIndex)
        {
            existingOverlapIndex = -1;

            if (!string.Equals(
                    _queryHistoryTimeUnits,
                    timeUnits,
                    StringComparison.OrdinalIgnoreCase) ||
                AdGuardQueryHistory.Count == 0 ||
                incomingHistory.Count == 0 ||
                !HasValidQueryHistoryChronology(AdGuardQueryHistory, timeUnits) ||
                !HasValidQueryHistoryChronology(incomingHistory, timeUnits))
            {
                return false;
            }

            DateTime incomingStart = incomingHistory[0].Timestamp;
            existingOverlapIndex = AdGuardQueryHistory
                .Select((point, index) => (point, index))
                .Where(item => item.point.Timestamp == incomingStart)
                .Select(item => item.index)
                .DefaultIfEmpty(-1)
                .First();

            if (existingOverlapIndex < 0)
                return false;

            int overlapCount =
                AdGuardQueryHistory.Count - existingOverlapIndex;

            if (overlapCount > incomingHistory.Count)
                return false;

            for (int index = 0; index < overlapCount; index++)
            {
                if (AdGuardQueryHistory[existingOverlapIndex + index].Timestamp !=
                    incomingHistory[index].Timestamp)
                {
                    return false;
                }
            }

            return true;
        }

        private void RebuildQueryHistory(
            IReadOnlyList<AdGuardTimePoint> incomingHistory,
            string timeUnits)
        {
            AdGuardQueryHistory.Clear();

            foreach (AdGuardTimePoint point in incomingHistory)
                AdGuardQueryHistory.Add(point);

            _queryHistoryTimeUnits = timeUnits;
        }

        private static bool HasValidQueryHistoryChronology(
            IReadOnlyList<AdGuardTimePoint> history,
            string timeUnits)
        {
            for (int index = 1; index < history.Count; index++)
            {
                if (history[index].Timestamp <= history[index - 1].Timestamp ||
                    history[index].Timestamp != AddQueryHistoryBucket(
                        history[index - 1].Timestamp,
                        timeUnits))
                {
                    return false;
                }
            }

            return true;
        }

        private static DateTime NormalizeQueryHistoryTimestamp(
            DateTime timestamp,
            string timeUnits)
        {
            return timeUnits.ToLowerInvariant() switch
            {
                "second" or "seconds" => new DateTime(
                    timestamp.Year, timestamp.Month, timestamp.Day,
                    timestamp.Hour, timestamp.Minute, timestamp.Second,
                    timestamp.Kind),
                "minute" or "minutes" => new DateTime(
                    timestamp.Year, timestamp.Month, timestamp.Day,
                    timestamp.Hour, timestamp.Minute, 0,
                    timestamp.Kind),
                "day" or "days" => new DateTime(
                    timestamp.Year, timestamp.Month, timestamp.Day,
                    0, 0, 0, timestamp.Kind),
                "month" or "months" => new DateTime(
                    timestamp.Year, timestamp.Month, 1,
                    0, 0, 0, timestamp.Kind),
                _ => new DateTime(
                    timestamp.Year, timestamp.Month, timestamp.Day,
                    timestamp.Hour, 0, 0, timestamp.Kind)
            };
        }

        private static DateTime AddQueryHistoryBucket(
            DateTime timestamp,
            string timeUnits)
        {
            return timeUnits.ToLowerInvariant() switch
            {
                "second" or "seconds" => timestamp.AddSeconds(1),
                "minute" or "minutes" => timestamp.AddMinutes(1),
                "day" or "days" => timestamp.AddDays(1),
                "month" or "months" => timestamp.AddMonths(1),
                _ => timestamp.AddHours(1)
            };
        }

        private static void ReplaceCollection<T>(
            ObservableCollection<T> destination,
            IEnumerable<T> source)
        {
            destination.Clear();

            List<T> items = source
                .Take(5)
                .ToList();

            int maximumCount = items
                .OfType<AdGuardRankedItem>()
                .Select(item => item.Count)
                .DefaultIfEmpty(0)
                .Max();

            int rank = 1;

            foreach (T item in items)
            {
                if (item is AdGuardRankedItem rankedItem)
                {
                    rankedItem.Rank = rank;
                    rankedItem.RelativePercent =
                        maximumCount <= 0
                            ? 0
                            : Math.Max(4, rankedItem.Count * 100d / maximumCount);
                    rank++;
                }

                destination.Add(item);
            }
        }


        //
        // Refresh indicators
        //

        public void RefreshStatusIndicators()
        {
            OnPropertyChanged(nameof(RouterStatusText));
            OnPropertyChanged(nameof(RouterStatusColour));
            OnPropertyChanged(nameof(AdGuardStatusText));
            OnPropertyChanged(nameof(AdGuardStatusColour));
            OnPropertyChanged(nameof(AdGuardProtectionStatusText));
            OnPropertyChanged(nameof(AdGuardProtectionStatusColour));
            OnPropertyChanged(nameof(InternetStatusText));
            OnPropertyChanged(nameof(InternetStatusColour));
            OnPropertyChanged(nameof(OverallStatusColour));
        }


        //
        // Convert CPU text to progress value
        //

        partial void OnCpuUsageChanged(
            string value)
        {
            if (double.TryParse(
                value.Replace("%", ""),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double result))
            {
                CpuPercentage =
                    double.IsFinite(result) && result >= 0 && result <= 100
                        ? result
                        : 0;
            }
            else
            {
                CpuPercentage = 0;
            }

            OnPropertyChanged(nameof(CpuUsageDisplay));
        }


        //
        // Convert memory text to progress value
        //

        partial void OnMemoryUsageChanged(
            string value)
        {
            if (double.TryParse(
                value.Replace("%", ""),
                out double result))
            {
                MemoryPercentage = result;
            }
            else
            {
                MemoryPercentage = 0;
            }
        }


        //
        // Status property updates
        //

        partial void OnRouterConnectedChanged(
            bool value)
        {
            RefreshStatusIndicators();
        }

        partial void OnAdGuardRunningChanged(
            bool value)
        {
            RefreshStatusIndicators();
        }

        partial void OnAdGuardAvailabilityChanged(
            AdGuardAvailabilityState value)
        {
            OnPropertyChanged(nameof(AdGuardAvailabilityMessage));
            OnPropertyChanged(nameof(IsAdGuardAvailable));
            OnPropertyChanged(nameof(AdGuardVersionDisplay));
            OnPropertyChanged(nameof(AdGuardServiceDisplay));
            OnPropertyChanged(nameof(AdGuardQueriesDisplay));
            OnPropertyChanged(nameof(AdGuardBlockedDisplay));
            OnPropertyChanged(nameof(AdGuardBlockRateDisplay));
            OnPropertyChanged(nameof(AdGuardLiveStatusText));
            OnPropertyChanged(nameof(TopClientsEmptyText));
            OnPropertyChanged(nameof(TopBlockedDomainsEmptyText));
            OnPropertyChanged(nameof(TopQueriedDomainsEmptyText));
            RefreshStatusIndicators();
        }

        partial void OnAdGuardMaintenanceStateChanged(AdGuardMaintenanceState value)
        {
            OnPropertyChanged(nameof(IsAdGuardAvailable));
            OnPropertyChanged(nameof(AdGuardAvailabilityMessage));
            OnPropertyChanged(nameof(AdGuardStatusSubtitle));
            RefreshStatusIndicators();
        }

        partial void OnAdGuardVersionChanged(string value) =>
            OnPropertyChanged(nameof(AdGuardVersionDisplay));

        partial void OnAdGuardServiceChanged(string value) =>
            OnPropertyChanged(nameof(AdGuardServiceDisplay));

        partial void OnAdGuardQueriesChanged(string value) =>
            OnPropertyChanged(nameof(AdGuardQueriesDisplay));

        partial void OnAdGuardBlockedChanged(string value) =>
            OnPropertyChanged(nameof(AdGuardBlockedDisplay));

        partial void OnAdGuardBlockRateChanged(string value) =>
            OnPropertyChanged(nameof(AdGuardBlockRateDisplay));

        partial void OnAdGuardProtectionEnabledChanged(
            bool value)
        {
            RefreshStatusIndicators();
        }

        partial void OnInternetConnectedChanged(
            bool value)
        {
            RefreshStatusIndicators();
        }

        partial void OnAdGuardProtectionPausedChanged(
            bool value)
        {
            RefreshStatusIndicators();
        }

        partial void OnAdGuardProtectionStatusKnownChanged(
            bool value)
        {
            RefreshStatusIndicators();
        }

        public void UpdateStorageUsage(string? rawStorage)
        {
            StorageUsage = string.IsNullOrWhiteSpace(rawStorage)
                ? "-"
                : rawStorage.Trim();

            StoragePercentage = 0;
            StorageUsed = "-";
            StorageAvailable = "-";
            StorageTotal = "-";

            if (string.IsNullOrWhiteSpace(rawStorage))
            {
                NotifyResourceHealthChanged();
                return;
            }

            string[] lines = rawStorage
                .Replace("\r", string.Empty)
                .Split(
                    '\n',
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries);

            string? candidate = lines
                .FirstOrDefault(line =>
                    line.Contains("/overlay", StringComparison.OrdinalIgnoreCase) ||
                    line.EndsWith(" /", StringComparison.OrdinalIgnoreCase))
                ?? lines.LastOrDefault();

            if (string.IsNullOrWhiteSpace(candidate))
            {
                NotifyResourceHealthChanged();
                return;
            }

            string[] parts = candidate.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries);

            int percentIndex = Array.FindIndex(
                parts,
                part => part.EndsWith("%", StringComparison.Ordinal));

            if (percentIndex >= 0 &&
                double.TryParse(
                    parts[percentIndex].TrimEnd('%'),
                    out double percent))
            {
                StoragePercentage = Math.Clamp(percent, 0, 100);

                // Typical df output:
                // Filesystem 1K-blocks Used Available Use% Mounted-on
                if (percentIndex >= 3)
                {
                    StorageTotal = FormatStorageSize(parts[percentIndex - 3]);
                    StorageUsed = FormatStorageSize(parts[percentIndex - 2]);
                    StorageAvailable = FormatStorageSize(parts[percentIndex - 1]);
                }

                StorageUsage = $"{StoragePercentage:0.#}% used";
            }

            NotifyResourceHealthChanged();
        }

        private static string FormatStorageSize(string value)
        {
            if (!double.TryParse(value, out double number))
            {
                return value;
            }

            // df commonly reports 1K blocks.
            double bytes = number * 1024d;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int unit = 0;

            while (bytes >= 1024d && unit < units.Length - 1)
            {
                bytes /= 1024d;
                unit++;
            }

            return $"{bytes:0.#} {units[unit]}";
        }

        private void NotifyResourceHealthChanged()
        {
            OnPropertyChanged(nameof(CpuHealthText));
            OnPropertyChanged(nameof(CpuHealthColour));
            OnPropertyChanged(nameof(MemoryHealthText));
            OnPropertyChanged(nameof(MemoryHealthColour));
            OnPropertyChanged(nameof(StorageHealthText));
            OnPropertyChanged(nameof(StorageHealthColour));
        }

        partial void OnCpuPercentageChanged(double value)
        {
            NotifyResourceHealthChanged();
            OnPropertyChanged(nameof(CpuUsageDisplay));
        }

        partial void OnCpuUtilisationPendingChanged(bool value)
        {
            NotifyResourceHealthChanged();
            OnPropertyChanged(nameof(CpuUsageDisplay));
        }

        partial void OnMemoryPercentageChanged(double value)
        {
            NotifyResourceHealthChanged();
        }

        partial void OnStoragePercentageChanged(double value)
        {
            NotifyResourceHealthChanged();
        }

    }
}
