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
    private readonly ObservableCollection<string> _multiWanHistory = new();
    private readonly Dictionary<string, string> _multiWanBaselines = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PortSessionState> _portStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<string> _portHistory = new();
    private readonly ObservableCollection<RouterWifiRadioGroup> _wifiRadios = new();
    private readonly ObservableCollection<string> _wifiHistory = new();
    private readonly Dictionary<string, string> _wifiBaselines = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _refreshCancellation;
    private bool _refreshing;
    private bool _multiWanRefreshing;
    private bool _dnsRefreshing;
    private bool _performanceRefreshing;
    private RouterManager? _performanceManager;
    private readonly List<PerformanceSample> _performanceSamples = new();
    private readonly ObservableCollection<string> _performanceHistory = new();
    private double? _performancePeakCpu;
    private double? _performancePeakMemory;
    private double? _performancePeakTemperature;
    private DateTime? _performanceSessionStarted;
    private string? _performanceLastThermalBand;
    private bool _wifiRefreshing;
    private RouterManager? _multiWanManager;
    private RouterManager? _portsManager;
    private RouterManager? _wifiManager;

    public RouterView()
    {
        InitializeComponent();
        _routerManagerProvider = ((App)Application.Current).Services.GetRequiredService<IRouterManagerProvider>();
        PortsList.ItemsSource = _ports;
        PortsHistoryList.ItemsSource = _portHistory;
        MultiWanList.ItemsSource = _multiWanPaths;
        MultiWanHistoryList.ItemsSource = _multiWanHistory;
        WifiRadiosList.ItemsSource = _wifiRadios;
        WifiHistoryList.ItemsSource = _wifiHistory;
        PerformanceHistoryList.ItemsSource = _performanceHistory;
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
            if (!ReferenceEquals(_portsManager, manager))
            {
                _portStates.Clear();
                _portHistory.Clear();
                _portsManager = manager;
            }
            RouterPortTelemetryResult result = await manager.GetRouterPortTelemetryAsync(cancellationToken);
            RouterManager current = await _routerManagerProvider.GetRouterManagerAsync(cancellationToken);
            if (!ReferenceEquals(manager, current) || cancellationToken.IsCancellationRequested) return;

            bool authoritative = result.Capability == RouterCapabilityState.Supported && result.Ports.Count > 0;
            if (authoritative) RecordPortTransitions(result.Ports);
            _ports.Clear();
            foreach (RouterPortSnapshot port in result.Ports)
                if (port.IsPhysical || port.InterfaceType == RouterInterfaceType.Unknown)
                    _ports.Add(port);
            PortsStatus.Text = result.Capability == RouterCapabilityState.Supported
                ? $"{_ports.Count} authoritative Ethernet interface(s) found."
                : "Ethernet port telemetry is currently unavailable.";
            PortsSummary.Text = authoritative ? $"Connected: {_ports.Count(port => port.Carrier == true)}  •  Disconnected: {_ports.Count(port => port.Carrier == false)}  •  Session link changes: {_portStates.Values.Sum(state => state.LinkChanges)}" : "Telemetry unavailable —";
            PortsAttention.Text = authoritative && _portStates.Values.Any(state => state.NewErrorsThisSession > 0 || state.NewDropsThisSession > 0)
                ? $"New Ethernet errors/drops observed on {_portStates.Values.Count(state => state.NewErrorsThisSession > 0 || state.NewDropsThisSession > 0)} port(s)."
                : string.Empty;
            PortsSessionText.Text = $"Session history (RouterPilot observations) • {_portHistory.Count} event(s)";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            PortsStatus.Text = "Ethernet port telemetry is currently unavailable.";
            System.Diagnostics.Debug.WriteLine($"Router port refresh failed ({exception.GetType().Name}).");
        }
        finally { _refreshing = false; }
    }

    private void RecordPortTransitions(IReadOnlyList<RouterPortSnapshot> ports)
    {
        foreach (RouterPortSnapshot port in ports.Where(port => port.IsPhysical || port.InterfaceType == RouterInterfaceType.Unknown))
        {
            if (!_portStates.TryGetValue(port.Id, out PortSessionState? state))
            {
                _portStates[port.Id] = new PortSessionState(port);
                continue;
            }
            if (state.Carrier.HasValue && port.Carrier.HasValue && state.Carrier != port.Carrier)
            {
                state.LinkChanges++;
                AddPortHistory($"{port.FriendlyName} link {(port.Carrier == true ? "connected" : "disconnected")}");
            }
            if (state.NegotiatedSpeedMbps.HasValue && port.NegotiatedSpeedMbps.HasValue && state.NegotiatedSpeedMbps != port.NegotiatedSpeedMbps)
                AddPortHistory($"{port.FriendlyName} speed changed {state.NegotiatedSpeedMbps} -> {port.NegotiatedSpeedMbps} Mbps");
            if (!string.Equals(state.Duplex, "Unknown", StringComparison.OrdinalIgnoreCase) && !string.Equals(port.Duplex, "Unknown", StringComparison.OrdinalIgnoreCase) && !string.Equals(state.Duplex, port.Duplex, StringComparison.OrdinalIgnoreCase))
                AddPortHistory($"{port.FriendlyName} duplex changed {state.Duplex} -> {port.Duplex}");
            long newErrors = CounterDelta(state.RxErrors, port.RxErrors) + CounterDelta(state.TxErrors, port.TxErrors);
            long newDrops = CounterDelta(state.RxDropped, port.RxDropped) + CounterDelta(state.TxDropped, port.TxDropped);
            state.NewErrorsThisSession += newErrors;
            state.NewDropsThisSession += newDrops;
            if (newErrors > 0) AddPortHistory($"New Ethernet errors observed on {port.FriendlyName}: {newErrors}");
            if (newDrops > 0) AddPortHistory($"New Ethernet drops observed on {port.FriendlyName}: {newDrops}");
            state.Update(port);
        }
    }

    private void AddPortHistory(string message)
    {
        _portHistory.Insert(0, $"{DateTime.Now:g}  {message}");
        while (_portHistory.Count > 100) _portHistory.RemoveAt(_portHistory.Count - 1);
    }

    private static long CounterDelta(long? previous, long? current) => previous.HasValue && current.HasValue && current.Value >= previous.Value ? current.Value - previous.Value : 0;

    private sealed class PortSessionState
    {
        public bool? Carrier { get; private set; }
        public int? NegotiatedSpeedMbps { get; private set; }
        public string Duplex { get; private set; }
        public long? RxErrors { get; private set; }
        public long? TxErrors { get; private set; }
        public long? RxDropped { get; private set; }
        public long? TxDropped { get; private set; }
        public int LinkChanges { get; set; }
        public long NewErrorsThisSession { get; set; }
        public long NewDropsThisSession { get; set; }
        public PortSessionState(RouterPortSnapshot port) { Duplex = port.Duplex; Update(port); }
        public void Update(RouterPortSnapshot port)
        {
            Carrier = port.Carrier; NegotiatedSpeedMbps = port.NegotiatedSpeedMbps; Duplex = port.Duplex;
            RxErrors = port.RxErrors; TxErrors = port.TxErrors; RxDropped = port.RxDropped; TxDropped = port.TxDropped;
        }
    }

    private void CopyPortsSummary_Click(object sender, RoutedEventArgs e)
    {
        StringBuilder text = new("RouterPilot Ethernet Ports Summary\n");
        text.AppendLine($"Physical ports observed: {_ports.Count}");
        text.AppendLine($"Connected: {_ports.Count(port => port.Carrier == true)}");
        text.AppendLine($"Disconnected: {_ports.Count(port => port.Carrier == false)}");
        foreach (RouterPortSnapshot port in _ports)
        {
            PortSessionState? state = _portStates.GetValueOrDefault(port.Id);
            text.AppendLine($"{port.FriendlyName}: {port.LinkState}; Speed: {port.SpeedDisplay}; Duplex: {port.Duplex}; New errors: {state?.NewErrorsThisSession ?? 0}; New drops: {state?.NewDropsThisSession ?? 0}");
        }
        text.AppendLine($"Generated: {DateTime.Now:g}");
        try { Clipboard.SetText(text.ToString()); PortsStatus.Text = "Ports summary copied."; }
        catch { PortsStatus.Text = "Ports summary could not be copied."; }
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
            if (!ReferenceEquals(_multiWanManager, manager))
            {
                _multiWanBaselines.Clear();
                _multiWanHistory.Clear();
                _multiWanManager = manager;
            }
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
            RecordMultiWanTransitions(snapshot);
            MultiWanSessionText.Text = $"Session path changes: {_multiWanHistory.Count}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            MultiWanStatus.Text = "Multi-WAN telemetry is currently unavailable.";
            System.Diagnostics.Debug.WriteLine($"Multi-WAN refresh failed ({exception.GetType().Name}).");
        }
        finally { _multiWanRefreshing = false; }
    }

    private void RecordMultiWanTransitions(RouterMultiWanSnapshot snapshot)
    {
        string state = $"{snapshot.Mode}|{snapshot.ActivePathId}|{snapshot.DefaultPathId}|" + string.Join(",", snapshot.WanPaths.Select(path => $"{path.Id}:{path.RuntimeState}"));
        if (_multiWanBaselines.TryGetValue("snapshot", out string? previous) && !string.Equals(previous, state, StringComparison.Ordinal))
        {
            _multiWanHistory.Insert(0, $"{DateTime.Now:g}  Multi-WAN observed state changed");
            while (_multiWanHistory.Count > 100) _multiWanHistory.RemoveAt(_multiWanHistory.Count - 1);
        }
        _multiWanBaselines["snapshot"] = state;
    }

    private void CopyMultiWanSummary_Click(object sender, RoutedEventArgs e)
    {
        StringBuilder text = new();
        text.AppendLine("RouterPilot Multi-WAN Summary");
        text.AppendLine($"Summary: {MultiWanSummary.Text}");
        text.AppendLine($"Paths: {_multiWanPaths.Count}");
        foreach (RouterWanPathSnapshot path in _multiWanPaths)
            text.AppendLine($"{path.Name}: {path.RuntimeState}; Interface: {path.InterfaceName}; Default route: {(path.IsDefault ? "Yes" : "No")}");
        try { Clipboard.SetText(text.ToString()); MultiWanStatus.Text = "Multi-WAN summary copied."; }
        catch { MultiWanStatus.Text = "Multi-WAN summary could not be copied."; }
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
            if (!ReferenceEquals(_performanceManager, manager))
            {
                ResetPerformanceSessionState();
                _performanceManager = manager;
            }
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
            RecordPerformanceSample(info);
            PerformanceSessionText.Text = $"Session: {_performanceSamples.Count} successful observation(s)";
            PerformancePeakText.Text = $"Peaks — CPU: {FormatPercent(_performancePeakCpu)}  Memory: {FormatPercent(_performancePeakMemory)}  Temperature: {FormatTemperature(_performancePeakTemperature)}";
            PerformanceAttentionText.Text = BuildPerformanceAttention();
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

    private void RecordPerformanceSample(RouterInfo info)
    {
        DateTime now = DateTime.Now;
        _performanceSessionStarted ??= now;
        double? cpu = info.CpuUsagePercent ?? ParsePercent(info.CpuUsage);
        double? memory = ParsePercent(info.MemoryUsage);
        double? temperature = ParseNumber(info.Temperature);
        double? load = info.LoadAverage1Minute;
        _performanceSamples.Add(new PerformanceSample(now, cpu, memory, temperature, load));
        while (_performanceSamples.Count > 120) _performanceSamples.RemoveAt(0);
        if (cpu.HasValue) _performancePeakCpu = Math.Max(_performancePeakCpu ?? cpu.Value, cpu.Value);
        if (memory.HasValue) _performancePeakMemory = Math.Max(_performancePeakMemory ?? memory.Value, memory.Value);
        if (temperature.HasValue)
        {
            _performancePeakTemperature = Math.Max(_performancePeakTemperature ?? temperature.Value, temperature.Value);
            string band = temperature.Value >= 80 ? "High" : temperature.Value >= 65 ? "Elevated" : "Normal";
            if (_performanceLastThermalBand is not null && band != _performanceLastThermalBand)
                AddPerformanceHistory($"Temperature {(_performanceLastThermalBand == "Normal" ? "entered elevated" : band == "High" ? "entered high" : "returned below elevated")} range.");
            _performanceLastThermalBand = band;
        }
        PerformanceHistoryList.Visibility = _performanceHistory.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private string BuildPerformanceAttention()
    {
        var items = new List<string>();
        if (_performanceSamples.Count == 0) return "Performance telemetry is currently unavailable.";
        if (_performanceLastThermalBand == "High") items.Add("Temperature is in the high RouterPilot guidance range.");
        else if (_performanceLastThermalBand == "Elevated") items.Add("Temperature is in the elevated RouterPilot guidance range.");
        if (_performanceSamples.TakeLast(3).Count(sample => sample.Cpu is >= 85) >= 2) items.Add("High CPU utilisation has been observed across several recent samples.");
        return string.Join(" ", items);
    }

    private void AddPerformanceHistory(string message)
    {
        _performanceHistory.Insert(0, $"{DateTime.Now:g}  {message}");
        while (_performanceHistory.Count > 100) _performanceHistory.RemoveAt(_performanceHistory.Count - 1);
    }

    private void ResetPerformanceSessionState()
    {
        _performanceSamples.Clear(); _performanceHistory.Clear();
        _performancePeakCpu = _performancePeakMemory = _performancePeakTemperature = null;
        _performanceSessionStarted = null; _performanceLastThermalBand = null;
    }

    private static double? ParsePercent(string? value)
    {
        double? parsed = ParseNumber(value);
        return parsed is >= 0 and <= 100 ? parsed : null;
    }

    private static double? ParseNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "-") return null;
        string clean = value.Replace("%", "", StringComparison.Ordinal).Replace("°", "", StringComparison.Ordinal).Replace("C", "", StringComparison.OrdinalIgnoreCase).Trim();
        return double.TryParse(clean, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double result) && !double.IsNaN(result) && !double.IsInfinity(result) && result >= 0 ? result : null;
    }

    private static string FormatPercent(double? value) => value.HasValue ? $"{value.Value:0.#}%" : "—";
    private static string FormatTemperature(double? value) => value.HasValue ? $"{value.Value:0.#} °C" : "—";

    private void ResetPerformanceSession_Click(object sender, RoutedEventArgs e)
    {
        ResetPerformanceSessionState();
        PerformanceSessionText.Text = "Session reset (RouterPilot local only).";
        PerformancePeakText.Text = "Peaks — CPU: —  Memory: —  Temperature: —";
        PerformanceAttentionText.Text = string.Empty;
        PerformanceHistoryList.Visibility = Visibility.Collapsed;
    }

    private void CopyPerformanceSummary_Click(object sender, RoutedEventArgs e)
    {
        StringBuilder text = new("RouterPilot Performance Summary\n");
        text.AppendLine($"CPU: {PerformanceCpuText.Text}");
        text.AppendLine($"Load: {PerformanceLoadText.Text}");
        text.AppendLine($"Memory: {PerformanceMemoryText.Text}");
        text.AppendLine($"Root storage: {PerformanceStorageText.Text}");
        text.AppendLine($"Temperature: {PerformanceTemperatureText.Text}");
        text.AppendLine($"Uptime: {PerformanceUptimeText.Text}");
        text.AppendLine($"{PerformancePeakText.Text}");
        text.AppendLine($"Session samples: {_performanceSamples.Count}");
        text.AppendLine("Generated: " + DateTime.Now.ToString("g"));
        try { Clipboard.SetText(text.ToString()); PerformanceStatus.Text = "Performance summary copied."; }
        catch { PerformanceStatus.Text = "Performance summary could not be copied."; }
    }

    private sealed record PerformanceSample(DateTime Timestamp, double? Cpu, double? Memory, double? Temperature, double? Load);
}
