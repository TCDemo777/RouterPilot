using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using RouterPilot.Models;
using RouterPilot.Services;

namespace RouterPilot.Views;

public partial class RouterView : UserControl
{
    private readonly IRouterManagerProvider _routerManagerProvider;
    private readonly ObservableCollection<RouterPortSnapshot> _ports = new();
    private readonly ObservableCollection<RouterWanPathSnapshot> _multiWanPaths = new();
    private readonly ObservableCollection<RouterWifiRadioGroup> _wifiRadios = new();
    private readonly ObservableCollection<string> _wifiHistory = new();
    private readonly Dictionary<string, string> _wifiBaselines = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _refreshCancellation;
    private bool _refreshing;
    private bool _multiWanRefreshing;
    private bool _dnsRefreshing;
    private bool _performanceRefreshing;
    private bool _wifiRefreshing;
    private RouterManager? _wifiManager;

    public RouterView()
    {
        InitializeComponent();
        _routerManagerProvider = ((App)Application.Current).Services.GetRequiredService<IRouterManagerProvider>();
        PortsList.ItemsSource = _ports;
        MultiWanList.ItemsSource = _multiWanPaths;
        WifiRadiosList.ItemsSource = _wifiRadios;
        WifiHistoryList.ItemsSource = _wifiHistory;
        DnsResolversList.ItemsSource = Array.Empty<string>();
        Loaded += RouterView_Loaded;
        Unloaded += RouterView_Unloaded;
        RouterTabs.SelectionChanged += RouterTabs_SelectionChanged;
    }

    private async void RouterView_Loaded(object sender, RoutedEventArgs e)
    {
        if (RouterTabs.SelectedIndex == 1) await RefreshPortsAsync();
        else if (RouterTabs.SelectedIndex == 2) await RefreshWifiAsync();
        else if (RouterTabs.SelectedIndex == 3) await RefreshMultiWanAsync();
        else if (RouterTabs.SelectedIndex == 4) await RefreshDnsAsync();
        else if (RouterTabs.SelectedIndex == 5) await RefreshPerformanceAsync();
    }

    private void RouterView_Unloaded(object sender, RoutedEventArgs e)
    {
        _refreshCancellation?.Cancel();
    }

    private async void RouterTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != RouterTabs) return;
        if (RouterTabs.SelectedIndex == 1) await RefreshPortsAsync();
        else if (RouterTabs.SelectedIndex == 2) await RefreshWifiAsync();
        else if (RouterTabs.SelectedIndex == 3) await RefreshMultiWanAsync();
        else if (RouterTabs.SelectedIndex == 4) await RefreshDnsAsync();
        else if (RouterTabs.SelectedIndex == 5) await RefreshPerformanceAsync();
    }

    private async Task RefreshPortsAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _refreshCancellation.Token;
        try
        {
            RouterManager manager = await _routerManagerProvider.GetRouterManagerAsync(cancellationToken);
            RouterPortTelemetryResult result = await manager.GetRouterPortTelemetryAsync(cancellationToken);
            RouterManager current = await _routerManagerProvider.GetRouterManagerAsync(cancellationToken);
            if (!ReferenceEquals(manager, current) || cancellationToken.IsCancellationRequested) return;

            _ports.Clear();
            foreach (RouterPortSnapshot port in result.Ports)
                if (port.IsPhysical || port.InterfaceType == RouterInterfaceType.Unknown)
                    _ports.Add(port);
            PortsStatus.Text = result.Capability == RouterCapabilityState.Supported
                ? $"{_ports.Count} authoritative Ethernet interface(s) found."
                : "Ethernet port telemetry is currently unavailable.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            PortsStatus.Text = "Ethernet port telemetry is currently unavailable.";
            System.Diagnostics.Debug.WriteLine($"Router port refresh failed ({exception.GetType().Name}).");
        }
        finally { _refreshing = false; }
    }

    private async Task RefreshMultiWanAsync()
    {
        if (_multiWanRefreshing) return;
        _multiWanRefreshing = true;
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _refreshCancellation.Token;
        try
        {
            RouterManager manager = await _routerManagerProvider.GetRouterManagerAsync(cancellationToken);
            RouterMultiWanSnapshot snapshot = await manager.GetRouterMultiWanTelemetryAsync(cancellationToken);
            RouterManager current = await _routerManagerProvider.GetRouterManagerAsync(cancellationToken);
            if (!ReferenceEquals(manager, current) || cancellationToken.IsCancellationRequested) return;

            _multiWanPaths.Clear();
            foreach (RouterWanPathSnapshot path in snapshot.WanPaths) _multiWanPaths.Add(path);
            MultiWanSummary.Text = snapshot.Mode switch
            {
                RouterMultiWanMode.Failover => "Multi-WAN \u2022 Failover",
                RouterMultiWanMode.LoadBalancing => "Multi-WAN \u2022 Load balancing",
                RouterMultiWanMode.SingleWan => "Multi-WAN \u2022 Single WAN",
                _ => "Multi-WAN \u2022 Mode unknown"
            };
            MultiWanStatus.Text = snapshot.CapabilityState == RouterCapabilityState.Supported
                ? $"{_multiWanPaths.Count} uplink path(s) reported by the router."
                : "Multi-WAN telemetry is currently unavailable.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            MultiWanStatus.Text = "Multi-WAN telemetry is currently unavailable.";
            System.Diagnostics.Debug.WriteLine($"Multi-WAN refresh failed ({exception.GetType().Name}).");
        }
        finally { _multiWanRefreshing = false; }
    }

    private async Task RefreshWifiAsync()
    {
        if (_wifiRefreshing) return;
        _wifiRefreshing = true;
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _refreshCancellation.Token;
        try
        {
            RouterManager manager = await _routerManagerProvider.GetRouterManagerAsync(cancellationToken);
            if (!ReferenceEquals(_wifiManager, manager))
            {
                _wifiBaselines.Clear();
                _wifiHistory.Clear();
                _wifiManager = manager;
            }
            List<WifiRadioInfo> networks = await manager.GetWifiRadiosAsync();
            RouterManager current = await _routerManagerProvider.GetRouterManagerAsync(cancellationToken);
            if (!ReferenceEquals(manager, current) || cancellationToken.IsCancellationRequested) return;

            _wifiRadios.Clear();
            List<RouterWifiRadioGroup> groups = GroupWifiNetworks(networks).ToList();
            foreach (RouterWifiRadioGroup radio in groups)
                _wifiRadios.Add(radio);
            WifiStatus.Text = _wifiRadios.Count > 0
                ? $"{_wifiRadios.Count} physical wireless radio(s) reported by the router."
                : "Wi-Fi telemetry is currently unavailable.";
            WifiSummary.Text = groups.Count == 0 ? "Telemetry unavailable" : $"{groups.Count} radios • {groups.Sum(g => g.Networks.Sum(n => n.ClientCount))} associated clients";
            WifiAttention.Text = groups.Count == 0 ? "Wi-Fi telemetry is currently unavailable." : string.Empty;
            RecordWifiTransitions(groups);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _wifiRadios.Clear();
            WifiStatus.Text = "Wi-Fi telemetry is currently unavailable.";
            System.Diagnostics.Debug.WriteLine($"Router Wi-Fi refresh failed ({exception.GetType().Name}).");
        }
        finally { _wifiRefreshing = false; }
    }

    private void RecordWifiTransitions(IReadOnlyList<RouterWifiRadioGroup> groups)
    {
        foreach (RouterWifiRadioGroup group in groups)
        {
            string state = string.Join("|", group.Networks.Select(n => $"{n.StatusDisplay}:{n.Channel}:{n.ChannelWidth}:{n.HardwareMode}"));
            string key = $"{group.Radio}\u001f{group.Band}";
            if (_wifiBaselines.TryGetValue(key, out string? previous) && !string.Equals(previous, state, StringComparison.Ordinal))
            {
                _wifiHistory.Insert(0, $"{DateTime.Now:g}  {group.DisplayName} radio state changed");
                while (_wifiHistory.Count > 100) _wifiHistory.RemoveAt(_wifiHistory.Count - 1);
            }
            _wifiBaselines[key] = state;
        }
    }

    private void CopyWifiSummary_Click(object sender, RoutedEventArgs e)
    {
        StringBuilder text = new();
        text.AppendLine("RouterPilot Wi-Fi Summary");
        foreach (RouterWifiRadioGroup group in _wifiRadios)
        {
            text.AppendLine($"{group.DisplayName} ({group.RadioDisplay})");
            foreach (WifiRadioInfo network in group.Networks)
                text.AppendLine($"  State: {network.StatusDisplay}; Channel: {network.Channel}; Width: {network.ChannelWidth}; Mode: {network.HardwareMode}; Clients: {network.ClientCountDisplay}");
        }
        try { Clipboard.SetText(text.ToString()); WifiStatus.Text = "Wi-Fi summary copied."; }
        catch { WifiStatus.Text = "Wi-Fi summary could not be copied."; }
    }

    private static IEnumerable<RouterWifiRadioGroup> GroupWifiNetworks(IEnumerable<WifiRadioInfo> networks)
    {
        return networks
            .Where(network => network is not null)
            .GroupBy(network => $"{network.Radio}\u001f{network.Band}", StringComparer.OrdinalIgnoreCase)
            .Select(group => new RouterWifiRadioGroup(
                group.First().Radio,
                group.First().Band,
                group.OrderBy(network => network.Ssid, StringComparer.OrdinalIgnoreCase).ToArray()))
            .OrderBy(group => BandSortOrder(group.Band))
            .ThenBy(group => group.Radio, StringComparer.OrdinalIgnoreCase);
    }

    private static int BandSortOrder(string band) => band switch
    {
        "2.4 GHz" => 0,
        "5 GHz" => 1,
        "6 GHz" => 2,
        _ => 3
    };

    private sealed record RouterWifiRadioGroup(
        string Radio,
        string Band,
        IReadOnlyList<WifiRadioInfo> Networks)
    {
        public string DisplayName => Band is "-" or "Unknown" ? $"Radio {Radio}" : Band;
        public string RadioDisplay => string.IsNullOrWhiteSpace(Radio) || Radio == "-" ? "—" : Radio;
    }

    private async Task RefreshDnsAsync()
    {
        if (_dnsRefreshing) return;
        _dnsRefreshing = true;
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _refreshCancellation.Token;
        try
        {
            RouterManager manager = await _routerManagerProvider.GetRouterManagerAsync(cancellationToken);
            RouterDnsSnapshot snapshot = await manager.GetRouterDnsTelemetryAsync(cancellationToken);
            RouterManager current = await _routerManagerProvider.GetRouterManagerAsync(cancellationToken);
            if (!ReferenceEquals(manager, current) || cancellationToken.IsCancellationRequested) return;

            string adGuardText = "Unknown";
            try
            {
                AdGuardStatus adGuard = await manager.GetAdGuardStatusAsync();
                adGuardText = adGuard.IsRunning ? "Running" : "Stopped";
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Router DNS AdGuard status unavailable ({exception.GetType().Name}).");
            }
            current = await _routerManagerProvider.GetRouterManagerAsync(cancellationToken);
            if (!ReferenceEquals(manager, current) || cancellationToken.IsCancellationRequested) return;

            DnsStatus.Text = snapshot.CapabilityState == RouterCapabilityState.Supported
                ? "Read-only DNS configuration and runtime information."
                : "DNS telemetry is currently unavailable.";
            DnsModeText.Text = snapshot.Mode == RouterDnsMode.Unknown ? "—" : snapshot.Mode.ToString();
            DnsEncryptionText.Text = snapshot.EncryptionMode == RouterDnsEncryptionMode.Unknown ? "—" : snapshot.EncryptionMode.ToString();
            DnsRuntimeText.Text = snapshot.RuntimeState == RouterDnsRuntimeState.Unknown
                ? "—"
                : $"{snapshot.ServiceName ?? "DNS service"} · {snapshot.RuntimeState}";
            DnsHandlesText.Text = snapshot.AdGuardHandlesClientRequests switch { true => "Yes", false => "No", _ => "Unknown" };
            DnsVpnText.Text = snapshot.VpnDnsState ?? "—";
            DnsResolversList.ItemsSource = snapshot.UpstreamResolvers.Count == 0 ? new[] { "—" } : snapshot.UpstreamResolvers;
            DnsAdGuardText.Text = adGuardText;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            DnsStatus.Text = "DNS telemetry is currently unavailable.";
            DnsModeText.Text = DnsEncryptionText.Text = DnsRuntimeText.Text = DnsVpnText.Text = "—";
            DnsAdGuardText.Text = DnsHandlesText.Text = "Unknown";
            DnsResolversList.ItemsSource = new[] { "—" };
            System.Diagnostics.Debug.WriteLine($"Router DNS refresh failed ({exception.GetType().Name}).");
        }
        finally { _dnsRefreshing = false; }
    }

    private async Task RefreshPerformanceAsync()
    {
        if (_performanceRefreshing) return;
        _performanceRefreshing = true;
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _refreshCancellation.Token;
        try
        {
            RouterManager manager = await _routerManagerProvider.GetRouterManagerAsync(cancellationToken);
            RouterInfo info = await manager.GetRouterInfoAsync();
            RouterManager current = await _routerManagerProvider.GetRouterManagerAsync(cancellationToken);
            if (!ReferenceEquals(manager, current) || cancellationToken.IsCancellationRequested) return;

            PerformanceStatus.Text = "Read-only router resource telemetry.";
            PerformanceCpuText.Text = string.IsNullOrWhiteSpace(info.CpuUsage) || info.CpuUsage == "-" ? "—" : info.CpuUsage;
            PerformanceLoadText.Text = string.IsNullOrWhiteSpace(info.LoadAverage) || info.LoadAverage == "-" ? "—" : info.LoadAverage;
            PerformanceTemperatureText.Text = string.IsNullOrWhiteSpace(info.Temperature) || info.Temperature == "-" ? "—" : info.Temperature;
            PerformanceMemoryText.Text = info.MemoryUsed == "-" || info.MemoryUsage == "-" ? "—" : $"{info.MemoryUsed} · {info.MemoryUsage}";
            PerformanceStorageText.Text = string.IsNullOrWhiteSpace(info.StorageUsage) || info.StorageUsage == "-" ? "—" : info.StorageUsage;
            PerformanceUptimeText.Text = string.IsNullOrWhiteSpace(info.Uptime) || info.Uptime == "-" ? "—" : info.Uptime;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            PerformanceStatus.Text = "Router resource telemetry is currently unavailable.";
            PerformanceCpuText.Text = PerformanceLoadText.Text = PerformanceTemperatureText.Text = "—";
            PerformanceMemoryText.Text = PerformanceStorageText.Text = PerformanceUptimeText.Text = "—";
            System.Diagnostics.Debug.WriteLine($"Router performance refresh failed ({exception.GetType().Name}).");
        }
        finally { _performanceRefreshing = false; }
    }
}
