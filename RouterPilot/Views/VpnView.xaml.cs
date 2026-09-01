using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using RouterPilot.Models;
using RouterPilot.Services;
using RouterPilot.ViewModels;

namespace RouterPilot.Views;

public partial class VpnView : UserControl
{
    private readonly IVpnService _service;
    private readonly VpnViewModel _viewModel;
    private readonly IVpnLiveStatusService _liveStatus;
    private readonly SettingsService _settingsService;
    private readonly VpnScheduleService _vpnScheduleService;
    private readonly IDataFreshnessService _dataFreshnessService;
    private readonly ITailscaleStatusService _tailscale;
    private readonly IActiveRouterContext _activeRouter;
    private readonly SemaphoreSlim _tailscaleRefreshGate = new(1, 1);
    private readonly object _refreshSync = new();
    private CancellationTokenSource? _refreshCts;
    private bool _eventsAttached;
    private const string VpnFreshnessSource = "VPN";
#if DEBUG
    private int _vpnStateCaptureNumber;
    private VpnStateCaptureSnapshot? _previousVpnStateCapture;
#endif
    public VpnView(bool embedded = false)
    {
        InitializeComponent();
        _service = ((App)Application.Current).Services.GetRequiredService<IVpnService>();
        _viewModel = ((App)Application.Current).Services.GetRequiredService<VpnViewModel>();
        _liveStatus = ((App)Application.Current).Services.GetRequiredService<IVpnLiveStatusService>();
        _settingsService = ((App)Application.Current).Services.GetRequiredService<SettingsService>();
        _vpnScheduleService = ((App)Application.Current).Services.GetRequiredService<VpnScheduleService>();
        _dataFreshnessService = ((App)Application.Current).Services.GetRequiredService<IDataFreshnessService>();
        _tailscale = ((App)Application.Current).Services.GetRequiredService<ITailscaleStatusService>();
        _activeRouter = ((App)Application.Current).Services.GetRequiredService<IActiveRouterContext>();
        DataContext = _viewModel;
        VpnSchedulePanel.DataContext = _vpnScheduleService;
        AttachEvents();
        UpdateVpnScheduleEmptyState();
        DiagnosticsExpander.IsExpanded = _settingsService.Load().VpnDiagnosticsExpanded;
#if DEBUG
        CaptureVpnStateButton.Visibility = Visibility.Visible;
#endif
        if (embedded) PageHeader.Visibility = Visibility.Collapsed;
        Loaded += VpnView_Loaded;
        Unloaded += (_, _) => StopRefresh();
    }

    private async void VpnView_Loaded(object sender, RoutedEventArgs e)
    {
        AttachEvents();
        await RefreshAsync();
    }

    private void AttachEvents()
    {
        if (_eventsAttached) return;
        _eventsAttached = true;
        _vpnScheduleService.SchedulesChanged += VpnSchedules_Changed;
        _liveStatus.StatusChanged += LiveStatusChanged;
    }

    private async Task RefreshAsync()
    {
        VpnLiveStatusDiagnostics.Record("VpnView.RefreshAsync entered: YES");
        if (_viewModel.VpnIsLoading)
        {
            VpnLiveStatusDiagnostics.Record("VpnView.RefreshAsync returned early: already loading");
            return;
        }
        _viewModel.VpnIsLoading = true;
        CancellationTokenSource refreshCts;
        lock (_refreshSync)
        {
            _refreshCts?.Cancel();
            _refreshCts?.Dispose();
            _refreshCts = new CancellationTokenSource();
            refreshCts = _refreshCts;
        }
        CancellationToken token = refreshCts.Token;
        string profileId = _activeRouter.CurrentProfileId;
        long contextVersion = _activeRouter.Version;
        bool IsCurrent() => profileId == _activeRouter.CurrentProfileId && contextVersion == _activeRouter.Version;
        Task tailscaleTask = LoadTailscaleAsync(token, IsCurrent);
        try
        {
            (IReadOnlyList<VpnTunnelInfo> tunnels, IReadOnlyList<VpnClientProfileInfo> profiles) = await _service.GetInventoryAsync(token);
            await tailscaleTask;
            token.ThrowIfCancellationRequested();
            if (!IsCurrent()) return;
            var profilesByGroup = profiles.ToDictionary(profile => profile.GroupId);
            var linkedTunnels = tunnels.Select(tunnel =>
            {
                List<VpnClientProfileInfo> linkedProfiles = tunnel.ProfileGroupIds.Where(profilesByGroup.ContainsKey).Select(id => profilesByGroup[id]).ToList();
                int serverConfigCount = linkedProfiles.Count == 1 ? linkedProfiles[0].ServerConfigCount : -1;
                return new VpnTunnelInfo { Id=tunnel.Id, TunnelId=tunnel.TunnelId, Name=tunnel.Name, Enabled=tunnel.Enabled, KillSwitch=tunnel.KillSwitch, Protocol=tunnel.Protocol, InterfaceName=tunnel.InterfaceName, ProfileGroupIds=tunnel.ProfileGroupIds, ActiveProfileName=linkedProfiles.FirstOrDefault()?.Name ?? string.Empty, LinkedProfilesDisplay=linkedProfiles.Count == 0 ? "No linked profile" : "Profile: " + string.Join(", ", linkedProfiles.Select(profile => profile.Name)), FromType=tunnel.FromType, ToType=tunnel.ToType, Masquerade=tunnel.Masquerade, LocalAccess=tunnel.LocalAccess, ServicePolicy=tunnel.ServicePolicy, ServerConfigCount=serverConfigCount };
            }).ToList();
            _viewModel.Replace(linkedTunnels, VpnService.Correlate(linkedTunnels, profiles));
            try { await _liveStatus.EnsureSubscribedAsync(token); }
            catch (Exception exception)
            {
                // Tunnel reads are authoritative for configured/disabled state.
                // Live-status delivery is an optional enrichment of that state.
                VpnLiveStatusDiagnostics.SetSocketStartupException(exception, "Awaiting VPN socket startup");
            }
            _dataFreshnessService.MarkSuccess(VpnFreshnessSource);
            _viewModel.ApplyLiveStatuses(_liveStatus.Current, vpnInventoryAuthoritative: true);
            _viewModel.VpnSupported = true;
            SetVpnCapability(RouterCapabilityState.Supported);
            _viewModel.VpnStatus = $"{linkedTunnels.Count} tunnel(s), {linkedTunnels.Count(tunnel => tunnel.Enabled)} enabled";
#if DEBUG
            _viewModel.VpnStatus = VpnLiveStatusDiagnostics.Last;
#endif
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            if (!IsCurrent()) return;
            _dataFreshnessService.MarkUnavailable(VpnFreshnessSource);
            _viewModel.ApplyLiveStatuses(_liveStatus.Current, vpnInventoryAuthoritative: false);
            _viewModel.VpnSupported = false;
            SetVpnCapability(RouterCapabilityState.Unknown);
            _viewModel.VpnStatus = "VPN client backend is unavailable for this router session.";
#if DEBUG
            _viewModel.VpnStatus = VpnLiveStatusDiagnostics.Last;
#endif
        }
        finally
        {
            _viewModel.VpnInventoryLoadCompleted = true;
            _viewModel.VpnIsLoading = false;
        }
    }

    private async Task LoadTailscaleAsync(CancellationToken token, Func<bool> isCurrent)
    {
        if (!await _tailscaleRefreshGate.WaitAsync(0, token).ConfigureAwait(true)) return;
        try
        {
            TailscaleStatus status = await _tailscale.GetStatusAsync(token);
            if (isCurrent()) _viewModel.ApplyTailscaleStatus(status);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch when (isCurrent()) { _viewModel.ApplyTailscaleStatus(TailscaleStatus.Unavailable("Tailscale status is currently unavailable.")); }
        finally { _tailscaleRefreshGate.Release(); }
    }

    internal Task RefreshForHostAsync() => RefreshAsync();

    private void StopRefresh()
    {
        lock (_refreshSync)
        {
            _refreshCts?.Cancel();
            _refreshCts?.Dispose();
            _refreshCts = null;
        }
        _liveStatus.StatusChanged -= LiveStatusChanged;
        _vpnScheduleService.SchedulesChanged -= VpnSchedules_Changed;
        _eventsAttached = false;
    }

    private void LiveStatusChanged(IReadOnlyList<VpnLiveStatusInfo> statuses)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            bool authoritative = _dataFreshnessService.Get(VpnFreshnessSource).State == DataFreshnessState.Fresh;
            _viewModel.ApplyLiveStatuses(statuses, authoritative, fromLiveStatusEvent: true);
            VpnLiveStatusDiagnostics.Record("VPN UI dispatch completed: YES");
#if DEBUG
            _viewModel.VpnStatus = VpnLiveStatusDiagnostics.Last;
#endif
        });
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void VpnSchedules_Changed(object? sender, EventArgs e) =>
        _ = Dispatcher.InvokeAsync(UpdateVpnScheduleEmptyState);

    private void UpdateVpnScheduleEmptyState() =>
        VpnScheduleEmptyText.Visibility = _vpnScheduleService.Schedules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void AddVpnSchedule_Click(object sender, RoutedEventArgs e) =>
        VpnScheduleEditorDialog.Show(Window.GetWindow(this), null, SaveVpnScheduleAsync);

    private void EditVpnSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: VpnSchedule schedule })
            VpnScheduleEditorDialog.Show(Window.GetWindow(this), schedule, SaveVpnScheduleAsync);
    }

    private async Task<string?> SaveVpnScheduleAsync(VpnSchedule schedule) => await _vpnScheduleService.SaveAsync(schedule);

    private async void DeleteVpnSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: VpnSchedule schedule }) return;
        if (MessageBox.Show($"Delete VPN schedule '{schedule.Name}'?", "VPN Schedule", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _vpnScheduleService.DeleteAsync(schedule.Id);
    }
    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        string report = BuildDiagnosticReport();
        Clipboard.SetText(report);
        DiagnosticsTextBox.Text = report;
        DiagnosticsNotice.Text = "✓ Diagnostics copied";
    }

    private void ExportDebugReport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export RouterPilot Debug Report",
            Filter = "Text files (*.txt)|*.txt",
            FileName = $"RouterPilot_Debug_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
        };
        if (dialog.ShowDialog() != true) return;
        File.WriteAllText(dialog.FileName, BuildDiagnosticReport(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        DiagnosticsNotice.Text = "✓ Report exported";
    }

    private void CopyVpnDetails_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(BuildVpnDetailsReport());
        _viewModel.VpnStatus = "✓ VPN details copied";
    }

    private async void CaptureVpnState_Click(object sender, RoutedEventArgs e)
    {
#if DEBUG
        CaptureVpnStateButton.IsEnabled = false;
        try
        {
            VpnStateCaptureSnapshot capture = await _service.GetDebugStateCaptureAsync(CancellationToken.None);
            int number = ++_vpnStateCaptureNumber;
            DiagnosticsTextBox.Text = DiagnosticRedactor.RedactForExport(BuildVpnStateCaptureReport(number, capture, _liveStatus.Current, _previousVpnStateCapture));
            _previousVpnStateCapture = capture;
            DiagnosticsNotice.Text = $"Capture {number} recorded";
        }
        catch
        {
            DiagnosticsNotice.Text = "VPN state capture could not be completed.";
        }
        finally
        {
            CaptureVpnStateButton.IsEnabled = true;
        }
#endif
    }

    private void DiagnosticsExpander_Expanded(object sender, RoutedEventArgs e)
    {
        DiagnosticsTextBox.Text = BuildDiagnosticReport();
        AppSettings settings = _settingsService.Load();
        settings.VpnDiagnosticsExpanded = true;
        _settingsService.Save(settings);
    }

    private void DiagnosticsExpander_Collapsed(object sender, RoutedEventArgs e)
    {
        AppSettings settings = _settingsService.Load();
        settings.VpnDiagnosticsExpanded = false;
        _settingsService.Save(settings);
    }

    private string BuildDiagnosticReport()
    {
        DashboardViewModel? dashboard = Application.Current.MainWindow?.DataContext as DashboardViewModel;
        var report = new StringBuilder();
        report.AppendLine("----------------------------------------");
        report.AppendLine("RouterPilot Diagnostics");
        report.AppendLine("----------------------------------------");
        report.AppendLine($"Timestamp: {DateTimeOffset.Now:O}");
        report.AppendLine($"RouterPilot version: {Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "Unknown"}");
        report.AppendLine("Build: " + GetBuildKind());
#if DEBUG
        report.AppendLine("Executing assembly path: " + (Environment.ProcessPath ?? "Unknown"));
#endif
        report.AppendLine();
        report.AppendLine("VPN Dashboard");
        report.AppendLine("-------------");
        report.Append(BuildVpnDetailsReport());
        report.AppendLine();
        report.AppendLine("VPN Live Diagnostics");
        report.AppendLine("--------------------");
        report.AppendLine(VpnLiveStatusDiagnostics.Report());
        if (dashboard is not null)
        {
            report.AppendLine();
            report.AppendLine("Router");
            report.AppendLine("------");
            if (!string.IsNullOrWhiteSpace(dashboard.RouterModel) && dashboard.RouterModel != "-") report.AppendLine("Model: " + dashboard.RouterModel);
            if (!string.IsNullOrWhiteSpace(dashboard.FirmwareVersion) && dashboard.FirmwareVersion != "-") report.AppendLine("Firmware: " + dashboard.FirmwareVersion);
        }
        report.AppendLine("----------------------------------------");
        report.AppendLine("End of report");
        return report.ToString();
    }

    private string BuildVpnDetailsReport()
    {
        var report = new StringBuilder();
        report.AppendLine("----------------------------------------");
        report.AppendLine("VPN Status");
        report.AppendLine("----------------------------------------");
        foreach (VpnTunnelInfo tunnel in _viewModel.VpnTunnels)
        {
            report.AppendLine();
            Append(report, "Tunnel", tunnel.Name);
            Append(report, "State", tunnel.ConnectionState);
            Append(report, "Protocol", tunnel.Protocol);
            Append(report, "Profile", tunnel.ActiveProfileName);
            Append(report, "Location", tunnel.LiveLocation);
            Append(report, "Active server", tunnel.LiveServerName);
            Append(report, "Server", tunnel.LiveEndpoint);
            Append(report, "Virtual IP", tunnel.LiveVirtualIp);
            if (tunnel.HasLiveConnection)
            {
                Append(report, "Download", tunnel.LiveDownload);
                Append(report, "Upload", tunnel.LiveUpload);
            }
            Append(report, "Kill Switch", tunnel.KillSwitch ? "Enabled" : "Disabled");
        }
        if (_viewModel.VpnTunnels.Count == 0) report.AppendLine("No VPN tunnels are configured.");
        report.AppendLine("----------------------------------------");
        return report.ToString();
    }

 #if DEBUG
    private static string BuildVpnStateCaptureReport(int number, VpnStateCaptureSnapshot capture, IReadOnlyList<VpnLiveStatusInfo> liveStatuses, VpnStateCaptureSnapshot? previous)
    {
        var report = new StringBuilder();
        report.AppendLine($"VPN STATE CAPTURE {number}");
        report.AppendLine($"Timestamp: {DateTimeOffset.Now:O}");
        report.AppendLine("Source reads: vpn-client/get_all_config_list and existing tunnel/live-status state");
        report.AppendLine();
        report.AppendLine("Profiles:");
        if (capture.ProfileGroups.Count == 0) report.AppendLine("  None returned.");
        foreach (VpnProfileGroupCapture group in capture.ProfileGroups.OrderBy(group => group.Protocol).ThenBy(group => group.GroupId))
        {
            report.AppendLine($"  Protocol: {SafeCaptureValue(group.Protocol)} | GroupId: {group.GroupId} | Provider: {group.IsProvider} | Name: {SafeCaptureValue(group.GroupName)} | PeerCount: {group.Peers.Count}");
            foreach (VpnPeerCapture peer in group.Peers.OrderBy(peer => peer.PeerId))
                report.AppendLine($"    PeerId/ClientId: {peer.PeerId} | Provider: {peer.IsProvider} | Name: {SafeCaptureValue(peer.Name)} | Location: {SafeCaptureValue(peer.Location)}");
        }
        report.AppendLine();
        report.AppendLine("Tunnels:");
        if (capture.Tunnels.Count == 0) report.AppendLine("  None returned.");
        foreach (VpnTunnelInfo tunnel in capture.Tunnels.OrderBy(tunnel => tunnel.TunnelId))
        {
            VpnLiveStatusInfo? live = liveStatuses.SingleOrDefault(status => status.TunnelId == tunnel.TunnelId);
            report.AppendLine($"  TunnelId: {tunnel.TunnelId} | Enabled: {tunnel.Enabled} | Protocol: {SafeCaptureValue(tunnel.Protocol)} | GroupIds: [{string.Join(", ", tunnel.ProfileGroupIds)}]");
            report.AppendLine($"    State: {SafeCaptureValue(live?.ConnectionState)} | LiveGroupId: {live?.GroupId?.ToString() ?? "Unavailable"} | LivePeerId/ClientId: {live?.PeerId?.ToString() ?? "Unavailable"}");
            report.AppendLine($"    Endpoint: {SafeCaptureValue(live?.EndpointDisplay)}");
        }
        if (previous is not null) AppendCaptureChanges(report, previous, capture);
        return report.ToString();
    }

    private static void AppendCaptureChanges(StringBuilder report, VpnStateCaptureSnapshot previous, VpnStateCaptureSnapshot current)
    {
        var changes = new List<string>();
        var previousGroups = previous.ProfileGroups.ToDictionary(group => $"{group.Protocol}:{group.GroupId}");
        var currentGroups = current.ProfileGroups.ToDictionary(group => $"{group.Protocol}:{group.GroupId}");
        foreach (string key in previousGroups.Keys.Union(currentGroups.Keys).OrderBy(key => key))
        {
            bool hadPrevious = previousGroups.TryGetValue(key, out VpnProfileGroupCapture? before);
            bool hasCurrent = currentGroups.TryGetValue(key, out VpnProfileGroupCapture? after);
            if (!hadPrevious || !hasCurrent) { changes.Add($"  Profile {key}: {(hasCurrent ? "added" : "removed")}"); continue; }
            VpnProfileGroupCapture beforeGroup = before!;
            VpnProfileGroupCapture afterGroup = after!;
            if (beforeGroup.IsProvider != afterGroup.IsProvider) changes.Add($"  Profile {key} provider flag: {beforeGroup.IsProvider} -> {afterGroup.IsProvider}");
            if (beforeGroup.Peers.Count != afterGroup.Peers.Count) changes.Add($"  Profile {key} peer count: {beforeGroup.Peers.Count} -> {afterGroup.Peers.Count}");
            var beforePeers = beforeGroup.Peers.ToDictionary(peer => peer.PeerId);
            var afterPeers = afterGroup.Peers.ToDictionary(peer => peer.PeerId);
            foreach (int peerId in beforePeers.Keys.Union(afterPeers.Keys).OrderBy(id => id))
            {
                bool hadOldPeer = beforePeers.TryGetValue(peerId, out VpnPeerCapture? oldPeer);
                bool hasNewPeer = afterPeers.TryGetValue(peerId, out VpnPeerCapture? newPeer);
                if (!hadOldPeer || !hasNewPeer) { changes.Add($"  Profile {key} peer {peerId}: {(hasNewPeer ? "added" : "removed")}"); continue; }
                VpnPeerCapture oldValue = oldPeer!;
                VpnPeerCapture newValue = newPeer!;
                if (!string.Equals(oldValue.Location, newValue.Location, StringComparison.Ordinal)) changes.Add($"  Profile {key} peer {peerId} location: {SafeCaptureValue(oldValue.Location)} -> {SafeCaptureValue(newValue.Location)}");
                if (oldValue.IsProvider != newValue.IsProvider) changes.Add($"  Profile {key} peer {peerId} provider flag: {oldValue.IsProvider} -> {newValue.IsProvider}");
            }
        }
        var beforeTunnels = previous.Tunnels.ToDictionary(tunnel => tunnel.TunnelId);
        var afterTunnels = current.Tunnels.ToDictionary(tunnel => tunnel.TunnelId);
        foreach (int tunnelId in beforeTunnels.Keys.Union(afterTunnels.Keys).OrderBy(id => id))
        {
            bool hadBeforeTunnel = beforeTunnels.TryGetValue(tunnelId, out VpnTunnelInfo? before);
            bool hasAfterTunnel = afterTunnels.TryGetValue(tunnelId, out VpnTunnelInfo? after);
            if (!hadBeforeTunnel || !hasAfterTunnel) { changes.Add($"  Tunnel {tunnelId}: {(hasAfterTunnel ? "added" : "removed")}"); continue; }
            VpnTunnelInfo beforeTunnel = before!;
            VpnTunnelInfo afterTunnel = after!;
            if (beforeTunnel.Enabled != afterTunnel.Enabled) changes.Add($"  Tunnel {tunnelId} enabled: {beforeTunnel.Enabled} -> {afterTunnel.Enabled}");
            string oldGroups = string.Join(",", beforeTunnel.ProfileGroupIds.OrderBy(id => id));
            string newGroups = string.Join(",", afterTunnel.ProfileGroupIds.OrderBy(id => id));
            if (!string.Equals(oldGroups, newGroups, StringComparison.Ordinal)) changes.Add($"  Tunnel {tunnelId} group IDs: [{oldGroups}] -> [{newGroups}]");
        }
        report.AppendLine();
        report.AppendLine("Changes from previous capture:");
        if (changes.Count == 0) report.AppendLine("  No safe profile/group/tunnel mapping changes observed.");
        else foreach (string change in changes) report.AppendLine(change);
    }

    private static string SafeCaptureValue(string? value) => string.IsNullOrWhiteSpace(value) ? "Unavailable" : value;
#endif

    private static void Append(StringBuilder report, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) report.AppendLine($"{label}: {value}");
    }

    private static string GetBuildKind()
    {
#if DEBUG
        return "Debug";
#else
        return "Release";
#endif
    }
    private async void ToggleTunnel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: VpnTunnelInfo tunnel } || _viewModel.VpnIsLoading) return;
        bool target = !tunnel.Enabled;
        if (target && !tunnel.CanConnect)
        {
            _viewModel.VpnStatus = tunnel.ServerSelectionLimitationText;
            return;
        }
        if (!target && MessageBox.Show($"Disconnect {tunnel.Name}? Network traffic using this tunnel may be interrupted.", "Disconnect VPN", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (!target) _viewModel.MarkExplicitDisconnect(tunnel.TunnelId);
        Button? button = sender as Button;
        if (button is not null) button.IsEnabled = false;
        _viewModel.VpnIsLoading = true; _viewModel.VpnOperationTunnelId = tunnel.TunnelId;
        try
        {
            VpnOperationResult result = await _service.SetTunnelEnabledAsync(tunnel.TunnelId, target, CancellationToken.None);
            if (!result.Success) { MessageBox.Show(result.Message, "VPN", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            if (target) _viewModel.BeginConnectionAttempt(tunnel);
            _viewModel.VpnIsLoading = false;
            await RefreshAsync();
        }
        finally
        {
            _viewModel.VpnOperationTunnelId = 0;
            _viewModel.VpnIsLoading = false;
            if (button is not null) button.IsEnabled = true;
        }
    }
    private async void VpnTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != VpnTabs) return;
        bool profiles = VpnTabs.SelectedIndex == 1; DashboardContent.Visibility = profiles ? Visibility.Collapsed : Visibility.Visible; ProfilesContent.Visibility = profiles ? Visibility.Visible : Visibility.Collapsed;
        await RefreshAsync();
    }

    private static void SetVpnCapability(RouterCapabilityState telemetryState)
    {
        if (Application.Current.MainWindow?.DataContext is DashboardViewModel dashboard)
        {
            dashboard.RouterCapabilities.VpnClient.Telemetry = telemetryState;
            bool available = telemetryState == RouterCapabilityState.Supported;
            dashboard.RouterCapabilities.VpnClient.Read = available;
            dashboard.RouterCapabilities.VpnClient.TunnelControl = available;
        }
    }
}
