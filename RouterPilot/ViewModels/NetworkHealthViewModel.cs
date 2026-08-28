using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RouterPilot.Models;
using RouterPilot.Presentation;
using RouterPilot.Services;

namespace RouterPilot.ViewModels;

/// <summary>Read-only adapter over state which is already refreshed elsewhere.</summary>
public sealed partial class NetworkHealthViewModel : ObservableObject, IDisposable
{
    private const string RouterSource = "Router status";
    private const string InternetSource = "Internet / WAN";
    private const string AdGuardSource = "AdGuard";
    private const string VpnSource = "VPN";
    private const string WifiSource = "Wi-Fi";
    private const string DhcpSource = "DHCP";
    private readonly DashboardViewModel _dashboard;
    private readonly IDataFreshnessService _freshness;
    private readonly IVpnSummaryService _vpn;
    private readonly DataStatisticsViewModel _dataStatistics;
    private readonly IUiDispatcher _uiDispatcher;
    private bool _disposed;

    [ObservableProperty] private NetworkHealthViewSnapshot snapshot = new("Initializing", RouterPilotStatus.Pending, "Waiting for the existing router refresh.", []);
    public IReadOnlyList<NetworkHealthViewCheck> Checks => Snapshot.Checks;
    public string OverallStatus => Snapshot.OverallStatus;
    public string OverallDetail => Snapshot.OverallDetail;
    public string OverallColour => RouterPilotStatusPresentation.Colour(Snapshot.OverallSeverity);

    public NetworkHealthViewModel(DashboardViewModel dashboard, IDataFreshnessService freshness, IVpnSummaryService vpn, DataStatisticsViewModel dataStatistics, IUiDispatcher uiDispatcher)
    {
        _dashboard = dashboard;
        _freshness = freshness;
        _vpn = vpn;
        _dataStatistics = dataStatistics;
        _uiDispatcher = uiDispatcher;
        _dashboard.PropertyChanged += SourceChanged;
        _dataStatistics.PropertyChanged += SourceChanged;
        _freshness.Changed += FreshnessChanged;
        _vpn.SummaryChanged += VpnChanged;
        Rebuild();
    }

    private void SourceChanged(object? sender, PropertyChangedEventArgs e) => RebuildOnUiThread();
    private void FreshnessChanged() => RebuildOnUiThread();
    private void VpnChanged(VpnSummaryState _) => RebuildOnUiThread();

    private void RebuildOnUiThread()
    {
        if (_disposed) return;
        if (_uiDispatcher.CheckAccess())
        {
            Rebuild();
            return;
        }

        _ = _uiDispatcher.InvokeAsync(Rebuild);
    }

    private void Rebuild()
    {
        if (_disposed) return;
        VpnSummaryState vpn = _vpn.Current;
        Snapshot = NetworkHealthViewProjection.Create(new NetworkHealthViewInput(
            _freshness.Get(RouterSource).State, _freshness.Get(InternetSource).State, _freshness.Get(AdGuardSource).State,
            _freshness.Get(VpnSource).State, _freshness.Get(WifiSource).State, _freshness.Get(DhcpSource).State,
            _dashboard.RouterConnected, _dashboard.InternetConnected, FormatTime(_freshness.Get(RouterSource).LastSuccessUtc),
            _dashboard.WanIp, _dashboard.Gateway, _dashboard.ExternalDns, _dashboard.AdGuardAvailability,
            _dashboard.AdGuardProtectionStatusKnown, _dashboard.AdGuardProtectionEnabled, _dashboard.AdGuardProtectionPaused,
            vpn.IsAvailable, vpn.IsConfigured, vpn.State, VpnDetail(vpn), _dashboard.WifiNetworks.Count,
            _dashboard.WifiNetworks.Count(radio => radio.StatusDisplay == RouterPilotStatusPresentation.Active),
            _dashboard.WifiNetworks.Count(radio => radio.StatusDisplay == RouterPilotStatusPresentation.Disabled),
            _dashboard.WifiNetworks.Count(radio => radio.StatusDisplay == RouterPilotStatusPresentation.NotAvailable), _dashboard.WifiUniqueClientTotal,
            _dashboard.DhcpLoaded, _dashboard.DhcpLeases.Count, _dashboard.DhcpReservations.Count, _dashboard.CpuUsageDisplay,
            _dashboard.Temperature, _dashboard.MemoryUsage, _dashboard.StorageUsage, _dashboard.Uptime, _dashboard.LoadAverage,
            _dashboard.FirmwareVersion, _dashboard.FirmwareUpdateStatus, _dataStatistics.HasLoaded, _dataStatistics.Status, _dataStatistics.StatusDetail));
        OnPropertyChanged(nameof(Checks)); OnPropertyChanged(nameof(OverallStatus)); OnPropertyChanged(nameof(OverallDetail)); OnPropertyChanged(nameof(OverallColour));
    }

    private static string VpnDetail(VpnSummaryState vpn) => string.Join(" · ", new[] { vpn.Protocol, vpn.TunnelName, vpn.ProfileName, vpn.Location }.Where(value => !string.IsNullOrWhiteSpace(value)));
    private static string FormatTime(DateTimeOffset? timestamp) => timestamp is null ? "unknown" : timestamp.Value.LocalDateTime.ToString("g");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _dashboard.PropertyChanged -= SourceChanged;
        _dataStatistics.PropertyChanged -= SourceChanged;
        _freshness.Changed -= FreshnessChanged;
        _vpn.SummaryChanged -= VpnChanged;
    }
}
