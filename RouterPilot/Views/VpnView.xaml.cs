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
    public VpnView(bool embedded = false)
    {
        InitializeComponent();
        _service = ((App)Application.Current).Services.GetRequiredService<IVpnService>();
        _viewModel = ((App)Application.Current).Services.GetRequiredService<VpnViewModel>();
        _liveStatus = ((App)Application.Current).Services.GetRequiredService<IVpnLiveStatusService>();
        _settingsService = ((App)Application.Current).Services.GetRequiredService<SettingsService>();
        DataContext = _viewModel;
        _liveStatus.StatusChanged += LiveStatusChanged;
        DiagnosticsExpander.IsExpanded = _settingsService.Load().VpnDiagnosticsExpanded;
        if (embedded) PageHeader.Visibility = Visibility.Collapsed;
        Loaded += async (_, _) => await RefreshAsync();
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
        try
        {
            IReadOnlyList<VpnTunnelInfo> tunnels = await _service.GetTunnelsAsync(CancellationToken.None);
            IReadOnlyList<VpnClientProfileInfo> profiles = await _service.GetClientProfilesAsync(CancellationToken.None);
            var profilesByGroup = profiles.ToDictionary(profile => profile.GroupId);
            var linkedTunnels = tunnels.Select(tunnel =>
            {
                List<VpnClientProfileInfo> linkedProfiles = tunnel.ProfileGroupIds.Where(profilesByGroup.ContainsKey).Select(id => profilesByGroup[id]).ToList();
                int serverConfigCount = linkedProfiles.Count == 1 ? linkedProfiles[0].ServerConfigCount : -1;
                return new VpnTunnelInfo { Id=tunnel.Id, TunnelId=tunnel.TunnelId, Name=tunnel.Name, Enabled=tunnel.Enabled, KillSwitch=tunnel.KillSwitch, Protocol=tunnel.Protocol, InterfaceName=tunnel.InterfaceName, ProfileGroupIds=tunnel.ProfileGroupIds, ActiveProfileName=linkedProfiles.FirstOrDefault()?.Name ?? string.Empty, LinkedProfilesDisplay=linkedProfiles.Count == 0 ? "No linked profile" : "Profile: " + string.Join(", ", linkedProfiles.Select(profile => profile.Name)), FromType=tunnel.FromType, ToType=tunnel.ToType, Masquerade=tunnel.Masquerade, LocalAccess=tunnel.LocalAccess, ServicePolicy=tunnel.ServicePolicy, ServerConfigCount=serverConfigCount };
            }).ToList();
            _viewModel.Replace(linkedTunnels, VpnService.Correlate(linkedTunnels, profiles));
            try { await _liveStatus.EnsureSubscribedAsync(CancellationToken.None); }
            catch (Exception exception)
            {
                // Tunnel reads are authoritative for configured/disabled state.
                // Live-status delivery is an optional enrichment of that state.
                VpnLiveStatusDiagnostics.SetSocketStartupException(exception, "Awaiting VPN socket startup");
            }
            _viewModel.ApplyLiveStatuses(_liveStatus.Current);
            _viewModel.VpnSupported = true;
            SetVpnCapability(true);
            _viewModel.VpnStatus = $"{linkedTunnels.Count} tunnel(s), {linkedTunnels.Count(tunnel => tunnel.Enabled)} enabled";
#if DEBUG
            _viewModel.VpnStatus = VpnLiveStatusDiagnostics.Last;
#endif
        }
        catch
        {
            _viewModel.VpnSupported = false;
            SetVpnCapability(false);
            _viewModel.VpnStatus = "VPN client backend is unavailable for this router session.";
#if DEBUG
            _viewModel.VpnStatus = VpnLiveStatusDiagnostics.Last;
#endif
        }
        finally { _viewModel.VpnIsLoading = false; }
    }

    internal Task RefreshForHostAsync() => RefreshAsync();

    private void LiveStatusChanged(IReadOnlyList<VpnLiveStatusInfo> statuses)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            _viewModel.ApplyLiveStatuses(statuses);
            VpnLiveStatusDiagnostics.Record("VPN UI dispatch completed: YES");
#if DEBUG
            _viewModel.VpnStatus = VpnLiveStatusDiagnostics.Last;
#endif
        });
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
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
        Button? button = sender as Button;
        if (button is not null) button.IsEnabled = false;
        _viewModel.VpnIsLoading = true; _viewModel.VpnOperationTunnelId = tunnel.TunnelId;
        try
        {
            VpnOperationResult result = await _service.SetTunnelEnabledAsync(tunnel.TunnelId, target, CancellationToken.None);
            if (!result.Success) { MessageBox.Show(result.Message, "VPN", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
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

    private static void SetVpnCapability(bool available)
    {
        if (Application.Current.MainWindow?.DataContext is DashboardViewModel dashboard)
        {
            dashboard.RouterCapabilities.VpnClient.Read = available;
            dashboard.RouterCapabilities.VpnClient.TunnelControl = available;
        }
    }
}
