using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using RouterPilot.Models;
using RouterPilot.Presentation;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveChartsCore;
using LiveChartsCore.Kernel;
using LiveChartsCore.SkiaSharpView;

namespace RouterPilot.ViewModels
{
    public partial class DashboardViewModel : ObservableObject
    {
        private const int TrafficHistoryCapacity = 60;
        private const int HealthHistoryCapacity = 60;
        private const int TrafficSampleIntervalSeconds = 2;
        private const int QueryHistoryCapacity = 120;
        private string _queryHistoryTimeUnits = "hours";

        //
        // Router
        //

        [ObservableProperty]
        private bool routerConnected;

        [ObservableProperty]
        private NetworkHealthSnapshot networkHealth = NetworkHealthSnapshot.Loading;

        [ObservableProperty]
        private NetworkHealthViewSnapshot networkHealthView = new(
            "Initializing",
            RouterPilotStatus.Pending,
            "Waiting for the existing router refresh.",
            []);

        public string NetworkHealthViewColour =>
            RouterPilotStatusPresentation.Colour(NetworkHealthView.OverallSeverity);

        public string NetworkHealthColour => RouterPilotStatusPresentation.Colour(NetworkHealth.OverallState switch
        {
            NetworkHealthState.Healthy => RouterPilotStatus.Active,
            NetworkHealthState.Critical => RouterPilotStatus.Error,
            NetworkHealthState.Attention => RouterPilotStatus.Pending,
            _ => RouterPilotStatus.NotAvailable
        });

        public string NetworkHealthInternetSummary => InternetStatusText;
        public string NetworkHealthWanSummary => InternetConnected ? "Connected" : "Unavailable";
        public string NetworkHealthPublicIpSummary => PublicIpStatus == PublicIpStatus.Available ? "Available" : "Unavailable";
        public string NetworkHealthAdGuardSummary => AdGuardStatusText;
        public string NetworkHealthRouterSummary => $"CPU {CpuUsageDisplay} • Memory {MemoryUsage}";
        public string NetworkHealthVpnSummary => IsVpnConnected ? $"{VpnSummary.ProfileName} • {VpnSummary.Location}" : "Disconnected";

        [ObservableProperty]
        private string routerModel = "-";

        [ObservableProperty]
        private string firmwareVersion = "-";

        // OpenWrt's board release and the GL.iNet firmware release are
        // separate values. Keep both so UI surfaces cannot label the former
        // as the installed vendor firmware.
        [ObservableProperty]
        private string routerFirmwareVersion = "-";

        // Cached state supplied by FirmwareUpdateService; the dashboard never initiates
        // firmware I/O and only uses this confirmed result for presentation/scoring.
        [ObservableProperty]
        private FirmwareUpdateCheckStatus firmwareUpdateStatus = FirmwareUpdateCheckStatus.NotAvailable;

        [ObservableProperty]
        private string firmwareLatestVersion = string.Empty;

        public bool FirmwareUpdateAvailable => FirmwareUpdateStatus == FirmwareUpdateCheckStatus.UpdateAvailable;

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

        // These are presentation copies of the existing RouterInfo memory snapshot.
        // They do not initiate router reads or participate in metric sampling.
        [ObservableProperty]
        private string memoryUsed = "-";

        [ObservableProperty]
        private string memoryCache = "-";

        [ObservableProperty]
        private double memoryPercentage;

        public bool HasMemoryDetails =>
            IsKnownMemoryDetail(MemoryUsed) || IsKnownMemoryDetail(MemoryCache);

        private static bool IsKnownMemoryDetail(string? value) =>
            !string.IsNullOrWhiteSpace(value) && value != "-";

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

        private DashboardHealthProjection CurrentHealthProjection => DashboardHealthProjection.Create(new DashboardHealthInput(
            RouterConnected,
            InternetConnected,
            IsAdGuardAvailable,
            IsAdGuardExpectedForOverallHealth,
            CpuPercentage,
            CpuUtilisationPending,
            MemoryPercentage,
            StoragePercentage,
            FirmwareUpdateAvailable,
            FirmwareUpdateStatus,
            FirmwareLatestVersion,
            Latency));

        [ObservableProperty]
        private bool includeAdGuardHomeInRouterHealth;

        public bool IsAdGuardExpectedForOverallHealth => IncludeAdGuardHomeInRouterHealth;

        public int RouterHealthScore => CurrentHealthProjection.Score;
        public string RouterHealthState => CurrentHealthProjection.State;
        public string RouterHealthSummary => CurrentHealthProjection.Summary;
        public string RouterHealthColour => CurrentHealthProjection.Colour;
        public IReadOnlyList<string> RouterHealthAttentionReasons => CurrentHealthProjection.AttentionReasons;
        public IReadOnlyList<string> RouterHealthHealthyConditions => CurrentHealthProjection.HealthyConditions;

        public string RouterHealthAttentionText => string.Join(Environment.NewLine, RouterHealthAttentionReasons.Select(reason => $"• {reason}"));
        public string RouterHealthHealthyText => string.Join(Environment.NewLine, RouterHealthHealthyConditions.Select(condition => $"• {condition}"));

        public string InternetQualityState => CurrentHealthProjection.InternetQualityState;
        public string InternetQualityDetail => CurrentHealthProjection.InternetQualityDetail;
        public string InternetQualityColour => CurrentHealthProjection.InternetQualityColour;

        public string RouterLastRebootEstimate
        {
            get
            {
                if (!TryParseUptime(Uptime, out TimeSpan uptime)) return RouterPilotStatusPresentation.NotAvailable;
                return $"Approximately {DateTimeOffset.Now - uptime:dd MMM yyyy HH:mm}";
            }
        }

        private static bool TryParseUptime(string? value, out TimeSpan uptime)
        {
            uptime = default;
            if (string.IsNullOrWhiteSpace(value) || value == "-") return false;
            Match day = Regex.Match(value, @"(?<value>\d+)\s*day", RegexOptions.IgnoreCase);
            Match hour = Regex.Match(value, @"(?<value>\d+)\s*hour", RegexOptions.IgnoreCase);
            Match minute = Regex.Match(value, @"(?<value>\d+)\s*min", RegexOptions.IgnoreCase);
            if (day.Success || hour.Success || minute.Success)
            {
                uptime = TimeSpan.FromDays(day.Success ? int.Parse(day.Groups["value"].Value) : 0)
                    + TimeSpan.FromHours(hour.Success ? int.Parse(hour.Groups["value"].Value) : 0)
                    + TimeSpan.FromMinutes(minute.Success ? int.Parse(minute.Groups["value"].Value) : 0);
                return uptime > TimeSpan.Zero;
            }
            return TimeSpan.TryParse(value, out uptime);
        }

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

        public string TemperatureHealthText =>
            RouterTemperatureHealth.Text(RouterModel, Temperature);

        public string TemperatureHealthColour =>
            RouterTemperatureHealth.Colour(RouterModel, Temperature);

        public string TemperatureHealthToolTip =>
            RouterTemperatureHealth.ToolTip(RouterModel, Temperature);


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
        private string publicIp = "Unavailable";

        [ObservableProperty]
        private PublicIpStatus publicIpStatus = PublicIpStatus.Unknown;

        public string PublicIpDisplay => PublicIpStatus switch
        {
            PublicIpStatus.Loading => "Refreshing…",
            PublicIpStatus.Available when !string.IsNullOrWhiteSpace(PublicIp) => PublicIp,
            _ => "Unavailable"
        };

        [ObservableProperty]
        private string gateway = "-";

        [ObservableProperty]
        private string externalDns = "-";

        [ObservableProperty]
        private string routerLanAddress = "-";

        public DnsResolverPathPresentation DnsResolverPath =>
            DnsResolverPathPresentation.Create(
                ExternalDns,
                RouterLanAddress,
                InternetConnected);

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

        public ObservableCollection<double> CpuHistory { get; } = new();

        public ObservableCollection<double> MemoryHistory { get; } = new();

        [ObservableProperty] private string internetReliabilityAvailability = "Insufficient history";
        [ObservableProperty] private string internetReliabilityStatus = "Checking";
        [ObservableProperty] private string internetReliabilityUptime = "-";
        [ObservableProperty] private string internetReliabilityOutages = "-";
        [ObservableProperty] private string internetReliabilityDowntime = "-";
        [ObservableProperty] private string internetReliabilityObserved = "-";
        [ObservableProperty] private string internetReliabilityLongestOutage = "-";
        [ObservableProperty] private string internetReliabilityLastOutage = "No outages observed";

        public ISeries[] CpuSparklineSeries { get; }

        public ISeries[] MemorySparklineSeries { get; }

        public Axis[] SparklineXAxes { get; }

        public Axis[] CpuSparklineYAxes { get; }

        public Axis[] MemorySparklineYAxes { get; }

        public bool HasCpuTrend => CpuHistory.Count >= 2;

        public bool HasMemoryTrend => MemoryHistory.Count >= 2;

        public bool IsCpuTrendCollecting => !HasCpuTrend;

        public bool IsMemoryTrendCollecting => !HasMemoryTrend;

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

            SparklineXAxes = new Axis[]
            {
                new Axis { IsVisible = false, ShowSeparatorLines = false }
            };

            CpuSparklineYAxes = [CreateSparklineYAxis()];
            MemorySparklineYAxes = [CreateSparklineYAxis()];

            CpuSparklineSeries = new ISeries[]
            {
                new LineSeries<double>
                {
                    Values = CpuHistory,
                    GeometrySize = 0,
                    LineSmoothness = 0.35,
                    Fill = null
                }
            };

            MemorySparklineSeries = new ISeries[]
            {
                new LineSeries<double>
                {
                    Values = MemoryHistory,
                    GeometrySize = 0,
                    LineSmoothness = 0.35,
                    Fill = null
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

        [ObservableProperty]
        private string dataFreshnessFooter = "Loading";

        [ObservableProperty]
        private string dataFreshnessColour = RouterPilotStatusPresentation.Colour(RouterPilotStatus.Pending);

        // Presentation-only: connectivity has not yet been determined by the
        // first established dashboard refresh.
        [ObservableProperty]
        private bool isInitialising = true;

        [ObservableProperty]
        private VpnSummaryState vpnSummary = new();


        //
        // Status text
        //

        public string RouterStatusText =>
            IsInitialising
                ? "Initializing"
                : RouterPilotStatusPresentation.Text(
                RouterConnected
                    ? RouterPilotStatus.Connected
                    : RouterPilotStatus.Error);

        public string RouterStatusColour =>
            RouterPilotStatusPresentation.Colour(
                IsInitialising
                    ? RouterPilotStatus.Pending
                    :
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
            IsInitialising
                ? "Initializing"
                : !InternetConnected
                ? RouterPilotStatusPresentation.Text(RouterPilotStatus.Error)
                : IsVpnConnected
                    ? "Connected via VPN"
                    : RouterPilotStatusPresentation.Text(RouterPilotStatus.Connected);

        public string InternetStatusColour =>
            RouterPilotStatusPresentation.Colour(
                IsInitialising
                    ? RouterPilotStatus.Pending
                    : InternetConnected
                    ? RouterPilotStatus.Connected
                    : RouterPilotStatus.Error);

        public string VpnStatusText => VpnSummary.State;

        public string VpnCompactFooterStatusText => VpnSummary.State switch
        {
            "Connected" when !string.IsNullOrWhiteSpace(VpnSummary.Location) => $"VPN: {VpnSummary.Location}",
            "Connected" => "VPN: On",
            "Connecting" => "VPN: Connecting",
            _ => "VPN: Off"
        };

        public string VpnFooterStatusText => VpnSummary.State == "Connected" && !string.IsNullOrWhiteSpace(VpnSummary.Location)
            ? $"Connected • {VpnSummary.Location}"
            : VpnSummary.State;

        public bool IsVpnConnected => string.Equals(VpnSummary.State, "Connected", StringComparison.Ordinal);

        public string VpnContextLine => !IsVpnConnected
            ? string.Empty
            : !string.IsNullOrWhiteSpace(VpnSummary.Protocol) && !string.IsNullOrWhiteSpace(VpnSummary.Location)
                ? $"Connected via VPN \u2022 {VpnSummary.Protocol} \u2022 {VpnSummary.Location}"
                : !string.IsNullOrWhiteSpace(VpnSummary.Location)
                    ? $"Connected via VPN \u2022 {VpnSummary.Location}"
                    : !string.IsNullOrWhiteSpace(VpnSummary.Protocol)
                        ? $"Connected via VPN \u2022 {VpnSummary.Protocol}"
                        : "Connected via VPN";

        public string VpnDnsContext => IsVpnConnected
            ? "VPN active \u2014 router DNS shown"
            : string.Empty;

        public string VpnStatusColour => RouterPilotStatusPresentation.Colour(VpnSummary.State switch
        {
            "Connected" => RouterPilotStatus.Connected,
            "Connecting" => RouterPilotStatus.Pending,
            "Disconnected" => RouterPilotStatus.Disabled,
            _ => RouterPilotStatus.NotAvailable
        });

        public string VpnStatusDetail => VpnSummary.State == "Connected"
            ? !string.IsNullOrWhiteSpace(VpnSummary.Location) ? VpnSummary.Location
                : !string.IsNullOrWhiteSpace(VpnSummary.ProfileName) ? VpnSummary.ProfileName
                : VpnSummary.TunnelName
            : VpnSummary.IsConfigured ? VpnSummary.TunnelName : string.Empty;

        public string VpnProtocolDisplay => VpnSummary.Protocol;

        public string VpnNetworkSummary => VpnSummary.State == "Connected"
            ? !string.IsNullOrWhiteSpace(VpnSummary.Location) ? $"Connected \u2022 {VpnSummary.Location}"
                : string.IsNullOrWhiteSpace(VpnSummary.Protocol) ? "Connected" : $"Connected via {VpnSummary.Protocol}"
            : VpnSummary.State;

        public string OverallStatusColour =>
            RouterPilotStatusPresentation.Colour(
                RouterConnected && InternetConnected && AdGuardRunning
                    ? RouterPilotStatus.Active
                    : RouterPilotStatus.Error);



        public ObservableCollection<WifiRadioInfo> WifiNetworks { get; } = new();
        public ObservableCollection<WifiSignalQualitySummary> WifiSignalQuality { get; } = new();
        public ObservableCollection<WifiWeakClientInfo> WeakWifiClients { get; } = new();
        public int WifiClientsWithSignal { get; private set; }
        public int WifiClientTotal { get; private set; }
        public bool HasWifiNetworks => WifiNetworks.Count > 0;
        public bool HasWeakWifiClients => WeakWifiClients.Count > 0;
        public bool HasWifiClients => WifiClientTotal > 0;
        public bool HasWifiSignalData => WifiClientsWithSignal > 0;
        public int WifiUniqueClientTotal { get; private set; }
        public int Wifi24ClientTotal { get; private set; }
        public int Wifi5ClientTotal { get; private set; }
        public int Wifi6ClientTotal { get; private set; }
        public int WifiGuestClientTotal { get; private set; }
        public string WifiSignalAvailabilityDisplay =>
            $"Signal available for {WifiClientsWithSignal} of {WifiClientTotal} Wi-Fi client{(WifiClientTotal == 1 ? string.Empty : "s")}";
        public string WifiSignalEmptyMessage => !HasWifiClients
            ? "No Wi-Fi clients connected"
            : !HasWifiSignalData
                ? "Signal data unavailable for current Wi-Fi clients"
                : "No weak Wi-Fi clients detected";

        public RouterCapabilities RouterCapabilities { get; } = new();

        public IEnumerable<WifiRadioInfo> GuestWifiNetworks => WifiNetworks
            .Where(network => network.IsVerifiedGuestNetwork);

        public bool HasGuestWifiNetworks => WifiNetworks.Any(network => network.IsVerifiedGuestNetwork);

        public ObservableCollection<DhcpConfigurationInfo> DhcpConfigurations { get; } = new();
        public ObservableCollection<DhcpLeaseInfo> DhcpLeases { get; } = new();
        public ObservableCollection<DhcpReservationInfo> DhcpReservations { get; } = new();
        public ObservableCollection<DhcpNetworkScopeInfo> DhcpNetworkScopes { get; } = new();
        public ObservableCollection<DhcpScopeCapacityInfo> DhcpScopeCapacities { get; } = new();
        public ObservableCollection<DhcpLeaseInfo> UnreservedDhcpLeases { get; } = new();
        public ObservableCollection<PortForwardRuleInfo> PortForwardRules { get; } = new();
        public ObservableCollection<LanClientInfo> LanClients { get; } = new();
        [ObservableProperty] private int lanConnectedCount;
        [ObservableProperty] private bool lanIsLoading;
        [ObservableProperty] private string lanStatus = string.Empty;
        [ObservableProperty] private bool portForwardIsLoading;
        [ObservableProperty] private string portForwardStatus = string.Empty;
        public bool PortForwardingSupported => RouterCapabilities.PortForwarding.Read;
        public bool PortForwardingWriteSupported => RouterCapabilities.PortForwarding.Write && !PortForwardIsLoading;

        partial void OnPortForwardIsLoadingChanged(bool value) => OnPropertyChanged(nameof(PortForwardingWriteSupported));

        public void SetPortForwardingCapabilities(bool read, bool write)
        {
            RouterCapabilities.PortForwarding.Read = read;
            RouterCapabilities.PortForwarding.Write = write;
            OnPropertyChanged(nameof(PortForwardingSupported));
            OnPropertyChanged(nameof(PortForwardingWriteSupported));
        }
        public ObservableCollection<string> DhcpWarnings { get; } = new();

        [ObservableProperty]
        private bool dhcpLoaded;

        public string DhcpStatusDisplay => !DhcpLoaded
            ? RouterPilotStatusPresentation.Pending
            : DhcpConfigurations.Count == 0
            ? RouterPilotStatusPresentation.NotAvailable
            : DhcpConfigurations.Any(configuration => configuration.Enabled)
                ? RouterPilotStatusPresentation.Active
                : RouterPilotStatusPresentation.Disabled;

        public string DhcpStatusColour => RouterPilotStatusPresentation.Colour(
            !DhcpLoaded ? RouterPilotStatus.Pending :
            DhcpConfigurations.Count == 0 ? RouterPilotStatus.NotAvailable :
            DhcpConfigurations.Any(configuration => configuration.Enabled) ? RouterPilotStatus.Active : RouterPilotStatus.Disabled);

        public bool HasDhcpLeases => DhcpLeases.Count > 0;
        public bool HasDhcpReservations => DhcpReservations.Count > 0;
        public bool HasUnreservedDhcpLeases => UnreservedDhcpLeases.Count > 0;
        public int UnreservedDhcpLeaseCount => UnreservedDhcpLeases.Count;
        public bool DhcpReservationValidationReady { get; private set; }

        [ObservableProperty]
        private bool dhcpReservationMutationInProgress;

        public bool CanManageDhcpReservations =>
            RouterCapabilities.Dhcp.ReservationsWrite && !DhcpReservationMutationInProgress;

        [ObservableProperty]
        private string wifiRefreshError = string.Empty;

        public void UpdateWifiRadios(IEnumerable<WifiRadioInfo> radios)
        {
            List<WifiRadioInfo> networkList = radios?.ToList() ?? new List<WifiRadioInfo>();
            HashSet<string> expandedNetworks = WifiNetworks
                .Where(network => network.IsExpanded)
                .Select(WifiNetworkIdentity)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (WifiRadioInfo network in networkList)
            {
                network.IsExpanded = expandedNetworks.Contains(WifiNetworkIdentity(network));
            }

            WifiNetworks.Clear();
            foreach (WifiRadioInfo network in networkList
                         .OrderByDescending(r => r.ClientCount)
                         .ThenBy(r => r.Band)
                         .ThenBy(r => r.Ssid, StringComparer.OrdinalIgnoreCase))
            {
                WifiNetworks.Add(network);
            }

            RouterCapabilities.WiFi.Read = networkList.Count > 0;
            // The same successful AP configuration read carries the network
            // association used for guest discovery; a guest AP need not exist
            // for the read capability itself to be available.
            RouterCapabilities.WiFi.GuestRead = networkList.Count > 0;
            RouterCapabilities.WiFi.ClientRead = networkList.Any(network => network.ClientCount > 0);
            RouterCapabilities.WiFi.SignalRead = networkList
                .SelectMany(network => network.Clients)
                .Any(client => !string.Equals(client.Signal, "-", StringComparison.Ordinal));
            RouterCapabilities.WiFi.ChannelWidthRead = networkList
                .Any(network => !string.Equals(network.ChannelWidth, "N/A", StringComparison.Ordinal));
            UpdateWifiIntelligence(networkList);
            ReevaluatePortForwardIntelligence();
            OnPropertyChanged(nameof(GuestWifiNetworks));
            OnPropertyChanged(nameof(HasGuestWifiNetworks));
            OnPropertyChanged(nameof(HasWifiNetworks));

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

        private void UpdateWifiIntelligence(IEnumerable<WifiRadioInfo> networks)
        {
            List<(WifiRadioInfo Network, WifiClientInfo Client, int Signal)> clients = networks
                .SelectMany(network => network.Clients.Select(client => (Network: network, Client: client, Signal: ParseWifiSignal(client.Signal))))
                .ToList();

            WifiClientTotal = clients.Count;
            List<(WifiRadioInfo Network, WifiClientInfo Client, int Signal)> uniqueClients = clients
                .GroupBy(item => WifiClientIdentity(item.Network, item.Client), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            WifiUniqueClientTotal = uniqueClients.Count;
            Wifi24ClientTotal = uniqueClients.Count(item => item.Network.Band.StartsWith("2.4", StringComparison.OrdinalIgnoreCase));
            Wifi5ClientTotal = uniqueClients.Count(item => item.Network.Band.StartsWith("5", StringComparison.OrdinalIgnoreCase));
            Wifi6ClientTotal = uniqueClients.Count(item => item.Network.Band.StartsWith("6", StringComparison.OrdinalIgnoreCase));
            WifiGuestClientTotal = uniqueClients.Count(item => item.Network.IsVerifiedGuestNetwork);
            List<(WifiRadioInfo Network, WifiClientInfo Client, int Signal)> samples = clients
                .Where(item => item.Signal != int.MinValue)
                .ToList();
            WifiClientsWithSignal = samples.Count;

            string[] qualities = ["Excellent", "Good", "Fair", "Poor"];
            WifiSignalQuality.Clear();
            foreach (string quality in qualities)
            {
                WifiSignalQuality.Add(new WifiSignalQualitySummary
                {
                    Quality = quality,
                    Count = samples.Count(item => item.Client.SignalQuality == quality)
                });
            }

            WeakWifiClients.Clear();
            foreach ((WifiRadioInfo network, WifiClientInfo client, int signal) in samples
                         .Where(item => item.Client.SignalQuality is "Fair" or "Poor")
                         .OrderBy(item => item.Signal))
            {
                WeakWifiClients.Add(new WifiWeakClientInfo
                {
                    Name = client.Name,
                    Band = network.Band,
                    Ssid = network.Ssid,
                    Signal = client.Signal,
                    SignalQuality = client.SignalQuality,
                    SignalDbm = signal
                });
            }

            OnPropertyChanged(nameof(WifiClientsWithSignal));
            OnPropertyChanged(nameof(WifiClientTotal));
            OnPropertyChanged(nameof(WifiSignalAvailabilityDisplay));
            OnPropertyChanged(nameof(HasWeakWifiClients));
            OnPropertyChanged(nameof(HasWifiClients));
            OnPropertyChanged(nameof(HasWifiSignalData));
            OnPropertyChanged(nameof(WifiSignalEmptyMessage));
            OnPropertyChanged(nameof(WifiUniqueClientTotal));
            OnPropertyChanged(nameof(Wifi24ClientTotal));
            OnPropertyChanged(nameof(Wifi5ClientTotal));
            OnPropertyChanged(nameof(Wifi6ClientTotal));
            OnPropertyChanged(nameof(WifiGuestClientTotal));
        }

        private static string WifiClientIdentity(WifiRadioInfo network, WifiClientInfo client)
        {
            string mac = new string((client.MacAddress ?? string.Empty)
                .Where(Uri.IsHexDigit)
                .Select(char.ToUpperInvariant)
                .ToArray());
            return mac.Length == 12
                ? "mac:" + mac
                : $"network:{network.Interface}|{client.IpAddress}|{client.Name}";
        }

        private static string WifiNetworkIdentity(WifiRadioInfo network) =>
            $"{network.Radio}|{network.Interface}|{network.Ssid}|{network.NetworkAssociation}";

        private static int ParseWifiSignal(string? value)
        {
            string numeric = (value ?? string.Empty)
                .Replace("dBm", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim();
            return int.TryParse(numeric, out int signal) ? signal : int.MinValue;
        }

        public void UpdateDhcpSnapshot(
            DhcpSnapshot snapshot,
            IReadOnlyDictionary<string, ClientProfile> profiles)
        {
            ApplyDhcpProfileCorrelation(snapshot.Leases, profiles);
            ApplyDhcpProfileCorrelation(snapshot.Reservations, profiles);

            ReplaceDhcpCollection(DhcpConfigurations, snapshot.Configurations);
            ReplaceDhcpCollection(DhcpLeases, snapshot.Leases);
            ReplaceDhcpCollection(DhcpReservations, snapshot.Reservations);
            ReplaceDhcpCollection(DhcpNetworkScopes, snapshot.Scopes);
            ReplaceDhcpCollection(DhcpWarnings, snapshot.Warnings);
            ReplaceDhcpCollection(DhcpScopeCapacities, BuildDhcpScopeCapacities(snapshot.Scopes, snapshot.Leases, snapshot.Reservations));
            ReplaceDhcpCollection(UnreservedDhcpLeases, BuildUnreservedDhcpLeases(snapshot.Leases, snapshot.Reservations));

            RouterCapabilities.Dhcp.Read = snapshot.Configurations.Count > 0;
            RouterCapabilities.Dhcp.ActiveLeases = true;
            RouterCapabilities.Dhcp.ReservationsRead = snapshot.Reservations.Count > 0 || snapshot.Configurations.Count > 0;
            DhcpReservationValidationReady = snapshot.Configurations.Count > 0 &&
                snapshot.Scopes.Any(scope => scope.DhcpEnabled && scope.Status == "Active" &&
                    scope.NetworkAddress is not null && scope.BroadcastAddress is not null && scope.RouterAddress is not null) &&
                snapshot.Reservations is not null;
            UpdateDhcpReservationWriteCapability();
            OnPropertyChanged(nameof(DhcpReservationValidationReady));
            OnPropertyChanged(nameof(CanManageDhcpReservations));
            DhcpLoaded = true;
            OnPropertyChanged(nameof(DhcpStatusDisplay));
            OnPropertyChanged(nameof(DhcpStatusColour));
            OnPropertyChanged(nameof(HasDhcpLeases));
            OnPropertyChanged(nameof(HasDhcpReservations));
            OnPropertyChanged(nameof(HasUnreservedDhcpLeases));
            OnPropertyChanged(nameof(UnreservedDhcpLeaseCount));
            ReevaluatePortForwardIntelligence();
        }

        private static IEnumerable<DhcpScopeCapacityInfo> BuildDhcpScopeCapacities(
            IEnumerable<DhcpNetworkScopeInfo> scopes,
            IEnumerable<DhcpLeaseInfo> leases,
            IEnumerable<DhcpReservationInfo> reservations)
        {
            List<DhcpLeaseInfo> currentLeases = leases.Where(IsCurrentDhcpLease).ToList();
            List<DhcpReservationInfo> enabledReservations = reservations.Where(reservation => reservation.Enabled).ToList();

            return scopes.Where(scope => scope.DhcpEnabled).Select(scope =>
            {
                int activeLeaseCount = currentLeases
                    .Where(lease => IsInDynamicRange(scope, lease.IpAddress))
                    .Select(lease => lease.IpAddress.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();

                int reservationCount = enabledReservations.Count(reservation =>
                    ParseIpv4(reservation.IpAddress) is IPAddress address && scope.ContainsAddress(address));
                int reservationsInDynamicRange = enabledReservations.Count(reservation => IsInDynamicRange(scope, reservation.IpAddress));

                return new DhcpScopeCapacityInfo
                {
                    Scope = scope,
                    ActiveLeaseCount = activeLeaseCount,
                    EnabledReservationCount = reservationCount,
                    EnabledReservationsInDynamicRangeCount = reservationsInDynamicRange
                };
            });
        }

        private static IEnumerable<DhcpLeaseInfo> BuildUnreservedDhcpLeases(
            IEnumerable<DhcpLeaseInfo> leases,
            IEnumerable<DhcpReservationInfo> reservations)
        {
            List<DhcpReservationInfo> enabledReservations = reservations.Where(reservation => reservation.Enabled).ToList();
            return leases.Where(IsCurrentDhcpLease)
                .Where(lease => !enabledReservations.Any(reservation => ClientIdentity.MacEquals(reservation.MacAddress, lease.MacAddress)))
                .ToList();
        }

        private static bool IsCurrentDhcpLease(DhcpLeaseInfo lease) =>
            lease.IsStatic || lease.Expiry is null || lease.Expiry > DateTimeOffset.UtcNow;

        private static bool IsInDynamicRange(DhcpNetworkScopeInfo scope, string? address)
        {
            IPAddress? parsedAddress = ParseIpv4(address);
            IPAddress? start = ParseIpv4(scope.DynamicRangeStart);
            IPAddress? end = ParseIpv4(scope.DynamicRangeEnd);
            if (parsedAddress is null || start is null || end is null) return false;

            uint value = ToIpv4UInt(parsedAddress);
            return value >= ToIpv4UInt(start) && value <= ToIpv4UInt(end);
        }

        private static IPAddress? ParseIpv4(string? value) =>
            IPAddress.TryParse(value, out IPAddress? address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                ? address
                : null;

        private static uint ToIpv4UInt(IPAddress address)
        {
            byte[] bytes = address.GetAddressBytes();
            return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        }

        public void ReevaluatePortForwardIntelligence()
        {
            PortForwardRuleIntelligence.Evaluate(
                PortForwardRules,
                DhcpLeases,
                DhcpReservations,
                WifiNetworks,
                DhcpLoaded);
            OnPropertyChanged(nameof(PortForwardRules));
        }

        private static void ApplyDhcpProfileCorrelation(
            IEnumerable<DhcpLeaseInfo> leases,
            IReadOnlyDictionary<string, ClientProfile> profiles)
        {
            foreach (DhcpLeaseInfo lease in leases)
            {
                if (!profiles.TryGetValue(ClientIdentity.NormalizeMac(lease.MacAddress), out ClientProfile? profile)) continue;
                lease.ProfileId = profile.Key;
                lease.IsFavourite = profile.IsFavorite;
                if (!string.IsNullOrWhiteSpace(profile.Nickname)) lease.ClientName = profile.Nickname;
                if (!string.IsNullOrWhiteSpace(profile.Category)) lease.DeviceType = profile.Category;
            }
        }

        private static void ApplyDhcpProfileCorrelation(
            IEnumerable<DhcpReservationInfo> reservations,
            IReadOnlyDictionary<string, ClientProfile> profiles)
        {
            foreach (DhcpReservationInfo reservation in reservations)
            {
                if (!profiles.TryGetValue(ClientIdentity.NormalizeMac(reservation.MacAddress), out ClientProfile? profile)) continue;
                reservation.ProfileId = profile.Key;
                reservation.IsFavourite = profile.IsFavorite;
                if (!string.IsNullOrWhiteSpace(profile.Nickname)) reservation.Hostname = profile.Nickname;
                if (!string.IsNullOrWhiteSpace(profile.Category)) reservation.DeviceType = profile.Category;
            }
        }

        private static void ReplaceDhcpCollection<T>(ObservableCollection<T> target, IEnumerable<T> values)
        {
            target.Clear();
            foreach (T value in values) target.Add(value);
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
            OnPropertyChanged(nameof(VpnStatusText));
            OnPropertyChanged(nameof(VpnCompactFooterStatusText));
            OnPropertyChanged(nameof(VpnFooterStatusText));
            OnPropertyChanged(nameof(VpnStatusColour));
            OnPropertyChanged(nameof(VpnStatusDetail));
            OnPropertyChanged(nameof(VpnProtocolDisplay));
            OnPropertyChanged(nameof(VpnNetworkSummary));
            OnPropertyChanged(nameof(IsVpnConnected));
            OnPropertyChanged(nameof(VpnContextLine));
            OnPropertyChanged(nameof(VpnDnsContext));
            OnPropertyChanged(nameof(OverallStatusColour));
            NotifyRouterHealthChanged();
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

        partial void OnMemoryUsedChanged(string value) =>
            OnPropertyChanged(nameof(HasMemoryDetails));

        partial void OnMemoryCacheChanged(string value) =>
            OnPropertyChanged(nameof(HasMemoryDetails));


        //
        // Status property updates
        //

        partial void OnRouterConnectedChanged(
            bool value)
        {
            RefreshStatusIndicators();
            UpdateDhcpReservationWriteCapability();
            OnPropertyChanged(nameof(CanManageDhcpReservations));
        }

        partial void OnIsInitialisingChanged(bool value) => RefreshStatusIndicators();

        partial void OnNetworkHealthChanged(NetworkHealthSnapshot value) => OnPropertyChanged(nameof(NetworkHealthColour));

        partial void OnNetworkHealthViewChanged(NetworkHealthViewSnapshot value) =>
            OnPropertyChanged(nameof(NetworkHealthViewColour));

        partial void OnVpnSummaryChanged(VpnSummaryState value)
        {
            RefreshStatusIndicators();
        }

        partial void OnPublicIpChanged(string value) => OnPropertyChanged(nameof(PublicIpDisplay));

        partial void OnPublicIpStatusChanged(PublicIpStatus value) => OnPropertyChanged(nameof(PublicIpDisplay));

        partial void OnDhcpReservationMutationInProgressChanged(bool value) =>
            OnPropertyChanged(nameof(CanManageDhcpReservations));

        private void UpdateDhcpReservationWriteCapability() =>
            RouterCapabilities.Dhcp.ReservationsWrite =
                DhcpReservationValidationReady && RouterConnected;

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
            NotifyRouterHealthChanged();
        }

        partial void OnIncludeAdGuardHomeInRouterHealthChanged(bool value) =>
            NotifyRouterHealthChanged();

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
            OnPropertyChanged(nameof(DnsResolverPath));
            RefreshStatusIndicators();
        }

        partial void OnExternalDnsChanged(string value) =>
            OnPropertyChanged(nameof(DnsResolverPath));

        partial void OnRouterLanAddressChanged(string value) =>
            OnPropertyChanged(nameof(DnsResolverPath));

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
            OnPropertyChanged(nameof(TemperatureHealthText));
            OnPropertyChanged(nameof(TemperatureHealthColour));
            OnPropertyChanged(nameof(TemperatureHealthToolTip));
            NotifyRouterHealthChanged();
        }

        private void NotifyRouterHealthChanged()
        {
            OnPropertyChanged(nameof(RouterHealthScore));
            OnPropertyChanged(nameof(RouterHealthState));
            OnPropertyChanged(nameof(RouterHealthSummary));
            OnPropertyChanged(nameof(RouterHealthColour));
            OnPropertyChanged(nameof(RouterHealthAttentionReasons));
            OnPropertyChanged(nameof(RouterHealthHealthyConditions));
            OnPropertyChanged(nameof(RouterHealthAttentionText));
            OnPropertyChanged(nameof(RouterHealthHealthyText));
            OnPropertyChanged(nameof(InternetQualityState));
            OnPropertyChanged(nameof(InternetQualityDetail));
            OnPropertyChanged(nameof(InternetQualityColour));
            OnPropertyChanged(nameof(RouterLastRebootEstimate));
        }

        partial void OnCpuPercentageChanged(double value)
        {
            if (!CpuUtilisationPending && CpuUsage != "-" && double.IsFinite(value))
            {
                AddHistoryPoint(CpuHistory, value);
                UpdateSparklineAxis(CpuHistory, CpuSparklineYAxes[0]);
                OnPropertyChanged(nameof(HasCpuTrend));
                OnPropertyChanged(nameof(IsCpuTrendCollecting));
            }

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
            if (MemoryUsage != "-" && double.IsFinite(value))
            {
                AddHistoryPoint(MemoryHistory, value);
                UpdateSparklineAxis(MemoryHistory, MemorySparklineYAxes[0]);
                OnPropertyChanged(nameof(HasMemoryTrend));
                OnPropertyChanged(nameof(IsMemoryTrendCollecting));
            }

            NotifyResourceHealthChanged();
        }

        partial void OnStoragePercentageChanged(double value)
        {
            NotifyResourceHealthChanged();
        }

        partial void OnLatencyChanged(string value)
        {
            OnPropertyChanged(nameof(InternetQualityState));
            OnPropertyChanged(nameof(InternetQualityDetail));
            OnPropertyChanged(nameof(InternetQualityColour));
        }

        partial void OnUptimeChanged(string value) => OnPropertyChanged(nameof(RouterLastRebootEstimate));

        partial void OnFirmwareUpdateStatusChanged(FirmwareUpdateCheckStatus value)
        {
            OnPropertyChanged(nameof(FirmwareUpdateAvailable));
            NotifyRouterHealthChanged();
        }

        partial void OnFirmwareLatestVersionChanged(string value) => NotifyRouterHealthChanged();

        private static void AddHistoryPoint(ObservableCollection<double> collection, double value)
        {
            collection.Add(Math.Clamp(value, 0, 100));
            while (collection.Count > HealthHistoryCapacity)
            {
                collection.RemoveAt(0);
            }
        }

        private static Axis CreateSparklineYAxis() => new()
        {
            IsVisible = false,
            ShowSeparatorLines = false,
            MinLimit = 0,
            MaxLimit = 100
        };

        // Overview sparklines intentionally use the visible samples' local range.
        // This affects only their shape, never the sampled values or Analytics axes.
        private static void UpdateSparklineAxis(
            IEnumerable<double> samples,
            Axis axis)
        {
            double[] values = samples.Where(double.IsFinite).ToArray();
            if (values.Length < 2) return;

            double minimum = values.Min();
            double maximum = values.Max();
            double range = maximum - minimum;
            double padding = Math.Max(1d, range * 0.2d);

            axis.MinLimit = Math.Max(0d, minimum - padding);
            axis.MaxLimit = Math.Min(100d, maximum + padding);

            // A flat, truthful series still needs a drawable local range.
            if (axis.MaxLimit - axis.MinLimit < 1d)
            {
                axis.MinLimit = Math.Max(0d, minimum - 0.5d);
                axis.MaxLimit = Math.Min(100d, maximum + 0.5d);
            }
        }

    }
}
