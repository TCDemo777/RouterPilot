using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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
    private readonly ITailscaleConfigurationService _tailscaleConfiguration;
    private readonly VpnOperationIntentService _operationIntent;
    private readonly IActiveRouterContext _activeRouter;
    private readonly IRouterPilotDevLog _devLog;
    private readonly ClientInventoryState _clientInventory;
    private readonly ClientInventoryCoordinator _clientInventoryCoordinator;
    private readonly IClientDisplayNameService _clientNames;
    private readonly SemaphoreSlim _tailscaleRefreshGate = new(1, 1);
    private readonly object _refreshSync = new();
    private readonly object _operationSync = new();
    private CancellationTokenSource? _refreshCts;
    private CancellationTokenSource? _operationCts;
    // Kept separately because _operationCts is also used by Tailscale UI
    // actions. A VPN Disconnect may supersede only an active VPN Connect.
    private CancellationTokenSource? _vpnTunnelOperationCts;
    private long _refreshGeneration;
    private bool _eventsAttached;
    private bool _openingVpnDeviceEditor;
    private bool _updatingTailscaleControls;
    private string? _tailscaleApplyingField;
    private bool _tailscaleApplyingValue;
    private TailscaleStatus? _lastTailscaleReadStatus;
    private const string VpnFreshnessSource = "VPN";
    private const string EmptyVpnDeviceAssignmentTitle = "At least one device is required";
#if DEBUG
    private static long _piaManualSnapshotSequence;
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
        _tailscaleConfiguration = ((App)Application.Current).Services.GetRequiredService<ITailscaleConfigurationService>();
        _operationIntent = ((App)Application.Current).Services.GetRequiredService<VpnOperationIntentService>();
        _activeRouter = ((App)Application.Current).Services.GetRequiredService<IActiveRouterContext>();
        _devLog = ((App)Application.Current).Services.GetRequiredService<IRouterPilotDevLog>();
        _clientInventory = ((App)Application.Current).Services.GetRequiredService<ClientInventoryState>();
        _clientInventoryCoordinator = ((App)Application.Current).Services.GetRequiredService<ClientInventoryCoordinator>();
        _clientNames = ((App)Application.Current).Services.GetRequiredService<IClientDisplayNameService>();
        DataContext = _viewModel;
        _viewModel.SetMakePrimaryAction(MakePrimaryAsync);
        _viewModel.SetRefreshExistingServersAction(RefreshExistingRouterServersAsync);
        VpnLiveStatusDiagnostics.Record("VpnView DataContext assigned to shared VpnViewModel: YES");
        VpnLiveStatusDiagnostics.Record($"VPN_VM_INSTANCE={RuntimeHelpers.GetHashCode(_viewModel)}");
        VpnSchedulePanel.DataContext = _vpnScheduleService;
        AttachEvents();
        UpdateVpnScheduleEmptyState();
        DiagnosticsExpander.IsExpanded = _settingsService.Load().VpnDiagnosticsExpanded;
#if DEBUG
        CapturePiaStateButton.Visibility = Visibility.Visible;
#endif
        if (embedded) PageHeader.Visibility = Visibility.Collapsed;
        Loaded += VpnView_Loaded;
        Unloaded += (_, _) => StopRefresh();
    }

    private async void VpnView_Loaded(object sender, RoutedEventArgs e)
    {
        AttachEvents();
        VpnLiveStatusDiagnostics.Record("VpnView activation refresh: YES");
        await RefreshAsync();
    }

    private void AttachEvents()
    {
        if (_eventsAttached) return;
        _eventsAttached = true;
        _vpnScheduleService.SchedulesChanged += VpnSchedules_Changed;
        _liveStatus.StatusChanged += LiveStatusChanged;
    }

    private async Task RefreshAsync(bool force = false, bool refreshTailscale = true)
    {
        VpnLiveStatusDiagnostics.Record("VpnView.RefreshAsync entered: YES");
        if (_viewModel.VpnIsLoading && !force)
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
        long refreshGeneration = Interlocked.Increment(ref _refreshGeneration);
        VpnLiveStatusDiagnostics.Record($"REFRESH_GENERATION_STARTED={refreshGeneration}; VM={RuntimeHelpers.GetHashCode(_viewModel)}");
        CancellationToken token = refreshCts.Token;
        string profileId = _activeRouter.CurrentProfileId;
        long contextVersion = _activeRouter.Version;
        bool IsCurrent() => refreshGeneration == Interlocked.Read(ref _refreshGeneration)
            && profileId == _activeRouter.CurrentProfileId
            && contextVersion == _activeRouter.Version;
        // Unified VPN mutations reconcile only their own subsystem.  Starting
        // a second Tailscale read here would let a transient optional read
        // failure replace a valid top-of-page snapshot with Unavailable.
        Task tailscaleTask = refreshTailscale ? LoadTailscaleAsync(token, IsCurrent) : Task.CompletedTask;
        try
        {
            VpnInventorySnapshot inventory = await _service.GetInventoryAsync(token);
            IReadOnlyList<VpnTunnelInfo> tunnels = inventory.Tunnels;
            IReadOnlyList<VpnClientProfileInfo> profiles = inventory.Profiles;
            token.ThrowIfCancellationRequested();
            if (!IsCurrent()) return;
            var profilesByGroup = profiles.ToDictionary(profile => profile.GroupId);
            var linkedTunnels = tunnels.Select(tunnel =>
            {
                List<VpnClientProfileInfo> linkedProfiles = tunnel.ProfileGroupIds.Where(profilesByGroup.ContainsKey).Select(id => profilesByGroup[id]).ToList();
                int serverConfigCount = linkedProfiles.Count == 1 ? linkedProfiles[0].ServerConfigCount : -1;
                return new VpnTunnelInfo { Id=tunnel.Id, TunnelId=tunnel.TunnelId, Name=tunnel.Name, Enabled=tunnel.Enabled, KillSwitch=tunnel.KillSwitch, Protocol=tunnel.Protocol, InterfaceName=tunnel.InterfaceName, ProfileGroupIds=tunnel.ProfileGroupIds, ActiveProfileName=linkedProfiles.FirstOrDefault()?.Name ?? string.Empty, LinkedProfilesDisplay=linkedProfiles.Count == 0 ? "No linked profile" : "Profile: " + string.Join(", ", linkedProfiles.Select(profile => profile.Name)), FromType=tunnel.FromType, ToType=tunnel.ToType, Masquerade=tunnel.Masquerade, LocalAccess=tunnel.LocalAccess, ServicePolicy=tunnel.ServicePolicy, ServerConfigCount=serverConfigCount, ServerConfigResolutionState=tunnel.ServerConfigResolutionState, ServerCandidateCount=tunnel.ServerCandidateCount, ConfiguredLocation=tunnel.ConfiguredLocation, RoutingPolicyState=tunnel.RoutingPolicyState, InternetRoutingScope=tunnel.InternetRoutingScope, RoutingDeviceIdentities=tunnel.RoutingDeviceIdentities, RoutingDevices=tunnel.RoutingDevices };
            }).ToList();
            _viewModel.Replace(linkedTunnels, VpnService.Correlate(linkedTunnels, profiles), inventory.ProfileInventoryState);
            VpnPiaTunnelConfigSelection? piaSelection = inventory.PiaConfigSelections.SingleOrDefault(selection =>
                selection.GroupId == inventory.PiaProviderGroup?.GroupId && linkedTunnels.Any(tunnel => tunnel.TunnelId == selection.TunnelId));
            _viewModel.SetPiaProviderManagement(inventory.PiaProviderGroup, linkedTunnels, piaSelection);
            _viewModel.SetPiaConfigResolution(piaSelection);
            _devLog.Write(RouterPilotDevLogCategory.PIA,
                $"ExistingServers.Projected count={_viewModel.ExistingRouterServers.Count}", RouterPilotDevLogLevel.Debug);
#if DEBUG
            Debug.WriteLine($"PIA_LINK.ViewLinkedProfilePresent={(linkedTunnels.Any(tunnel => !string.Equals(tunnel.LinkedProfilesDisplay, "No linked profile", StringComparison.Ordinal)) ? "YES" : "NO")}");
            Debug.WriteLine($"PIA_LINK.CanManagePiaProviderServers={(_viewModel.CanManagePiaProviderServers ? "YES" : "NO")}");
#endif
            try { await _liveStatus.EnsureSubscribedAsync(token); }
            catch (Exception exception)
            {
                // Tunnel reads are authoritative for configured/disabled state.
                // Live-status delivery is an optional enrichment of that state.
                VpnLiveStatusDiagnostics.SetSocketStartupException(exception, "Awaiting VPN socket startup");
            }
            _dataFreshnessService.MarkSuccess(VpnFreshnessSource);
            _viewModel.ApplyLiveStatuses(_liveStatus.Current, vpnInventoryAuthoritative: true);
            _ = EnrichRoutingPolicyAsync(linkedTunnels, IsCurrent, token);
            _viewModel.VpnSupported = true;
            SetVpnCapability(RouterCapabilityState.Supported);
            _viewModel.VpnStatus = $"{linkedTunnels.Count} tunnel(s), {linkedTunnels.Count(tunnel => tunnel.Enabled)} enabled";
#if DEBUG
            _viewModel.VpnStatus = VpnLiveStatusDiagnostics.Last;
#endif
            await tailscaleTask;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            if (!IsCurrent()) return;
            _dataFreshnessService.MarkUnavailable(VpnFreshnessSource);
            _viewModel.MarkVpnProfileInventoryUnavailable();
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
            // A cancelled refresh may complete after the active profile has
            // already started a replacement refresh. Do not clear the new
            // profile's loading indicator from the stale operation.
            if (IsCurrent())
            {
                _viewModel.VpnInventoryLoadCompleted = true;
                _viewModel.VpnIsLoading = false;
            }
        }
    }

    private async Task LoadTailscaleAsync(CancellationToken token, Func<bool> isCurrent, bool preserveConnectedRuntime = false)
    {
        await _tailscaleRefreshGate.WaitAsync(token).ConfigureAwait(true);
        try
        {
            TailscaleStatus status = await _tailscale.GetStatusAsync(token);
            TailscaleConfigurationSnapshot configuration = await _tailscaleConfiguration.GetConfigurationAsync(token);
            await PublishTailscaleStateAsync(status, configuration, isCurrent, token, preserveConnectedRuntime);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch when (isCurrent())
        {
            await PublishTailscaleStateAsync(
                TailscaleStatus.Unavailable("Tailscale status is currently unavailable."),
                TailscaleConfigurationSnapshot.Unknown,
                isCurrent,
                token,
                preserveConnectedRuntime);
        }
        finally { _tailscaleRefreshGate.Release(); }
    }

    private async Task EnrichRoutingPolicyAsync(IReadOnlyList<VpnTunnelInfo> tunnels, Func<bool> isCurrent, CancellationToken token)
    {
        try
        {
            IReadOnlyList<VpnTunnelInfo> enriched = await _service.EnrichRoutingPolicyAsync(tunnels, token);
            if (!isCurrent() || token.IsCancellationRequested) return;
            // The primary inventory and live status have already been
            // published. This optional update cannot delay or replace them.
            _viewModel.ApplyRoutingPolicy(enriched);
            _viewModel.ApplyLiveStatuses(_liveStatus.Current, vpnInventoryAuthoritative: true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            VpnLiveStatusDiagnostics.Record($"VPN routing policy enrichment unavailable: {DiagnosticRedactor.FailureCategory(exception)}");
        }
    }

    private async Task PublishTailscaleStateAsync(
        TailscaleStatus status,
        TailscaleConfigurationSnapshot configuration,
        Func<bool> isCurrent,
        CancellationToken token,
        bool preserveConnectedRuntime = false)
    {
        await Dispatcher.InvokeAsync(() =>
        {
            if (!isCurrent() || token.IsCancellationRequested) return;
            _lastTailscaleReadStatus = status;
            bool preserve = preserveConnectedRuntime
                && _viewModel.TailscaleStatus?.State == TailscaleState.Connected
                && status.State != TailscaleState.Connected;
            if (!preserve) _viewModel.ApplyTailscaleStatus(status);
            _viewModel.ApplyTailscaleConfiguration(configuration);
            ApplyTailscaleControls();
            VpnLiveStatusDiagnostics.Record($"Tailscale runtime result published: {status.State}");
            VpnLiveStatusDiagnostics.Record($"TAILSCALE_PUBLISH_KIND={(preserve ? "PRESERVED_RUNTIME" : "FULL")}; visible={_viewModel.TailscaleStatus?.State}");
            VpnLiveStatusDiagnostics.Record("Tailscale snapshot assigned to active VPN ViewModel: YES");
            VpnLiveStatusDiagnostics.Record("Tailscale UI property publication: YES");
            VpnLiveStatusDiagnostics.Record($"SNAPSHOT_PUBLISHED_VM={RuntimeHelpers.GetHashCode(_viewModel)}");
        });
    }

    private void ApplyTailscaleControls()
    {
        _updatingTailscaleControls = true;
        TailscaleLanState.Text = _viewModel.TailscaleLanDisplay;
        TailscaleWanState.Text = _viewModel.TailscaleWanDisplay;
        TailscaleEnabledState.Text = _viewModel.TailscaleEnabledDisplay;
        SetTailscaleAction(TailscaleEnabledButton, "enabled", _viewModel.TailscaleConfiguration.Enabled, _viewModel.TailscaleEnabledCanEdit);
        SetTailscaleAction(TailscaleLanButton, "lan", _viewModel.TailscaleConfiguration.LanEnabled, _viewModel.TailscaleLanCanEdit);
        SetTailscaleAction(TailscaleWanButton, "wan", _viewModel.TailscaleConfiguration.WanEnabled, _viewModel.TailscaleWanCanEdit);
        _updatingTailscaleControls = false;
    }

    private void SetTailscaleAction(Button button, string field, bool? value, bool canEdit)
    {
        bool active = string.Equals(_tailscaleApplyingField, field, StringComparison.Ordinal);
        button.Content = active ? (_tailscaleApplyingValue ? "Enabling…" : "Disabling…") : value switch { true => "Disable", false => "Enable", _ => "Unavailable" };
        button.IsEnabled = !active && canEdit;
    }

    private async void TailscaleAccess_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingTailscaleControls || sender is not Button button || !button.IsEnabled || button.Tag is not string tag) return;
        TailscaleAccessField field = tag switch { "enabled" => TailscaleAccessField.Enabled, "wan" => TailscaleAccessField.Wan, _ => TailscaleAccessField.Lan };
        bool? current = tag switch { "enabled" => _viewModel.TailscaleConfiguration.Enabled, "wan" => _viewModel.TailscaleConfiguration.WanEnabled, _ => _viewModel.TailscaleConfiguration.LanEnabled };
        if (current is not bool currentValue) return;
        bool value = !currentValue;
        _tailscaleApplyingField = tag;
        _tailscaleApplyingValue = value;
        _viewModel.TailscaleSettingsApplying = true;
        ApplyTailscaleControls();
        try
        {
            VpnLiveStatusDiagnostics.Record($"ENABLE_DISABLE_CLICK field={field}; value={value}; VM={RuntimeHelpers.GetHashCode(_viewModel)}");
            using CancellationTokenSource mutationCts = new(TimeSpan.FromSeconds(30));
            lock (_operationSync) _operationCts = mutationCts;
            TailscaleMutationResult result = await _tailscaleConfiguration.SetFieldAsync(field, value, mutationCts.Token);
            _viewModel.ApplyTailscaleConfiguration(result.Snapshot);
            if (!result.Succeeded && !string.IsNullOrWhiteSpace(result.Message)) _viewModel.VpnStatus = result.Message;
            ApplyTailscaleControls();
            VpnLiveStatusDiagnostics.Record($"Tailscale action read-back: {(result.Succeeded ? "PASS" : "FAIL")}");
            VpnLiveStatusDiagnostics.Record($"MUTATION_RESULT={(result.Succeeded ? "SUCCESS" : "FAILURE")}; VM={RuntimeHelpers.GetHashCode(_viewModel)}");
            await ReconcileTailscaleAfterActionAsync(field, value, mutationCts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (_operationSync)
            {
                _operationCts = null;
            }
            _tailscaleApplyingField = null;
            _viewModel.TailscaleSettingsApplying = false; ApplyTailscaleControls();
        }
    }

    private async Task ReconcileTailscaleAfterActionAsync(TailscaleAccessField field, bool value, CancellationToken token)
    {
        const int maxAttempts = 5;
        const int followUpDelayMilliseconds = 750;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            VpnLiveStatusDiagnostics.Record($"Tailscale action reconciliation attempt {attempt + 1}/{maxAttempts}");
            bool accessMutation = field is TailscaleAccessField.Lan or TailscaleAccessField.Wan;
            await RefreshTailscaleStateAsync(token, preserveConnectedRuntime: accessMutation && attempt < maxAttempts - 1);
            token.ThrowIfCancellationRequested();
            bool configurationStable = field switch
            {
                TailscaleAccessField.Enabled => _viewModel.TailscaleConfiguration.Enabled == value,
                TailscaleAccessField.Lan => _viewModel.TailscaleConfiguration.LanEnabled == value,
                _ => _viewModel.TailscaleConfiguration.WanEnabled == value
            };
            bool runtimeStable = field switch
            {
                // After Enable, the daemon can briefly report an empty status
                // as NeedsLogin while it is joining the Tailnet. Keep the
                // active-tab reconciliation alive until an authoritative
                // Connected result arrives; the bounded attempt limit still
                // allows a genuine login-required state to settle.
                TailscaleAccessField.Enabled when value => _viewModel.TailscaleStatus?.State is TailscaleState.Connected,
                TailscaleAccessField.Enabled => _viewModel.TailscaleStatus?.State is not TailscaleState.Connected,
                _ => _lastTailscaleReadStatus is null
                    || _lastTailscaleReadStatus.State == _viewModel.TailscaleStatus?.State
            };
            if (configurationStable && runtimeStable) return;
            if (attempt < maxAttempts - 1) await Task.Delay(TimeSpan.FromMilliseconds(followUpDelayMilliseconds), token);
        }
    }

    private async Task RefreshTailscaleStateAsync(CancellationToken token, bool preserveConnectedRuntime = false)
    {
        long refreshGeneration = Interlocked.Increment(ref _refreshGeneration);
        VpnLiveStatusDiagnostics.Record($"REFRESH_GENERATION_STARTED={refreshGeneration}; VM={RuntimeHelpers.GetHashCode(_viewModel)}");
        string profileId = _activeRouter.CurrentProfileId;
        long contextVersion = _activeRouter.Version;
        bool IsCurrent() => refreshGeneration == Interlocked.Read(ref _refreshGeneration)
            && profileId == _activeRouter.CurrentProfileId
            && contextVersion == _activeRouter.Version;

        VpnLiveStatusDiagnostics.Record("Tailscale canonical state refresh started: YES");
        await LoadTailscaleAsync(token, IsCurrent, preserveConnectedRuntime);
        VpnLiveStatusDiagnostics.Record("Tailscale canonical state refresh completed: YES");
    }

    internal Task RefreshForHostAsync() => RefreshAsync();

    private void StopRefresh()
    {
        Interlocked.Increment(ref _refreshGeneration);
        _operationIntent.ClearAll();
        _viewModel.ApplyTransitionIntent();
        lock (_operationSync)
        {
            _vpnTunnelOperationCts?.Cancel();
            _vpnTunnelOperationCts = null;
            _operationCts?.Cancel();
            _operationCts = null;
        }
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
        string report = BuildShareableDiagnosticReport();
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
        File.WriteAllText(dialog.FileName, BuildShareableDiagnosticReport(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        DiagnosticsNotice.Text = "✓ Report exported";
    }

    private void CopyVpnDetails_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(DiagnosticRedactor.RedactForExport(BuildVpnDetailsReport()));
        _viewModel.VpnStatus = "✓ VPN details copied";
    }

    private void CopyTailscaleSummary_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_viewModel.BuildTailscaleSummary());
            _viewModel.VpnStatus = "Tailscale summary copied.";
        }
        catch
        {
            _viewModel.VpnStatus = "Tailscale summary could not be copied.";
        }
    }

    private async void CapturePiaState_Click(object sender, RoutedEventArgs e)
    {
#if DEBUG
        CapturePiaStateButton.IsEnabled = false;
        PiaManualStateSnapshot snapshot;
        try
        {
            snapshot = await _service.CapturePiaManualStateAsync(_viewModel.PiaProviderGroupId, _viewModel.PiaProviderTunnelId, CancellationToken.None);
        }
        catch
        {
            // Presentation must still provide one safe, complete block when a
            // read cannot be started; do not retry or run a refresh pipeline.
            snapshot = new PiaManualStateSnapshot();
        }

        try
        {
            string report = BuildPiaManualStateSnapshot(Interlocked.Increment(ref _piaManualSnapshotSequence), snapshot);
            Debug.WriteLine(report);
            try
            {
                // This click handler resumes on the WPF UI thread, which is
                // required for System.Windows.Clipboard access.
                Clipboard.SetText(report);
                DiagnosticsNotice.Text = "PIA state snapshot copied to clipboard.";
                MessageBox.Show("PIA state snapshot copied to clipboard.", "RouterPilot", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch
            {
                DiagnosticsNotice.Text = "Clipboard unavailable; the PIA state snapshot is ready to copy manually.";
                ShowPiaSnapshotFallback(report);
            }
        }
        finally
        {
            CapturePiaStateButton.IsEnabled = true;
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

    private string BuildShareableDiagnosticReport() =>
        DiagnosticRedactor.RedactForExport(BuildDiagnosticReport());

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
            // Endpoint and virtual-address information remains available in the
            // local UI, but is intentionally excluded from shareable reports.
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
    private static string BuildPiaManualStateSnapshot(long sequence, PiaManualStateSnapshot snapshot)
    {
        RouterManager.VpnTunnelStructuralSnapshot? tunnel = snapshot.Tunnel;
        var report = new StringBuilder();
        report.AppendLine("PIA_MANUAL_STATE_SNAPSHOT");
        report.AppendLine($"SnapshotSequence={sequence}");
        report.AppendLine($"Timestamp={DateTimeOffset.UtcNow:O}");
        report.AppendLine();
        report.AppendLine($"Provider.ConfigReadSucceeded={snapshot.ConfigReadSucceeded}");
        report.AppendLine($"Provider.ConfigCount={snapshot.Configs.Count}");
        // Always include Config[0] so an unavailable/empty provider read has
        // the same paste-friendly shape as a populated one.
        for (int index = 0; index < Math.Max(1, snapshot.Configs.Count); index++)
        {
            PiaManualConfigSnapshot? config = index < snapshot.Configs.Count ? snapshot.Configs[index] : null;
            report.AppendLine();
            report.AppendLine($"Provider.Config[{index}].ConfigId={SafePiaValue(config?.ConfigId)}");
            report.AppendLine($"Provider.Config[{index}].Name={SafePiaValue(config?.Name)}");
            report.AppendLine($"Provider.Config[{index}].Location={SafePiaValue(config?.Location)}");
        }
        report.AppendLine();
        report.AppendLine($"Primary.TunnelReadSucceeded={snapshot.TunnelReadSucceeded}");
        report.AppendLine($"Primary.TunnelId={SafePiaValue(tunnel?.TunnelId)}");
        report.AppendLine($"Primary.Enabled={SafePiaValue(tunnel?.Enabled)}");
        report.AppendLine($"Primary.ViaPresent={SafePiaValue(tunnel?.ViaPresent)}");
        report.AppendLine($"Primary.ViaType={SafePiaValue(tunnel?.ViaType)}");
        report.AppendLine($"Primary.ViaConfigCount={SafePiaValue(tunnel?.ViaConfigCount)}");
        report.AppendLine($"Primary.ViaGroupId={SafePiaValue(tunnel?.ViaGroupId)}");
        report.AppendLine($"Primary.ViaConfigId={SafePiaValue(tunnel?.ViaConfigId)}");
        report.AppendLine($"Primary.FromType={SafePiaValue(tunnel?.FromType)}");
        report.AppendLine($"Primary.MacListCount={SafePiaValue(tunnel?.MacListCount)}");
        report.AppendLine($"Primary.ToType={SafePiaValue(tunnel?.ToType)}");
        report.AppendLine("PIA_MANUAL_STATE_SNAPSHOT_END");
        return report.ToString();
    }

    private static string SafePiaValue(object? value)
    {
        if (value is null) return "<unavailable>";
        string text = value.ToString() ?? string.Empty;
        string lower = text.ToLowerInvariant();
        if (text.IndexOfAny(['\r', '\n', '{', '}', '[', ']']) >= 0 ||
            new[] { "password", "username", "token", "session", "private", "public", "preshared", "key", "peer", "endpoint", "dns", "address", "://" }.Any(lower.Contains))
            return "<unavailable>";
        text = DiagnosticRedactor.RedactForExport(text);
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return string.IsNullOrWhiteSpace(text) ? "<unavailable>" : text;
    }

    private void ShowPiaSnapshotFallback(string report)
    {
        var textBox = new TextBox { Text = report, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        var window = new Window { Title = "PIA state snapshot — copy manually", Owner = Window.GetWindow(this), Width = 780, Height = 560, MinWidth = 520, MinHeight = 360, Content = textBox, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.ShowDialog();
    }

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
        _devLog.Write(RouterPilotDevLogCategory.VPN, target ? $"Connect requested; tunnel={tunnel.TunnelId}" : $"Disconnect requested; tunnel={tunnel.TunnelId}");
        if (target && !tunnel.CanConnect)
        {
            _viewModel.VpnStatus = tunnel.ServerSelectionLimitationText;
            return;
        }
        if (!target && MessageBox.Show($"Disconnect {tunnel.Name}? Network traffic using this tunnel may be interrupted.", "Disconnect VPN", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunTunnelOperationAsync(tunnel, target, sender as Button);
    }

    // Shared by the regular tunnel button and Apply & Connect.  Keeping the
    // mutation and handshake path here ensures both use the same proven
    // Connect/Disconnect contracts.
    private async Task<bool> RunTunnelOperationAsync(VpnTunnelInfo tunnel, bool target, Button? button)
    {
        if (!target) InvalidateConnectAttemptForDisconnect(tunnel.TunnelId);
        long operationGeneration = _operationIntent.Begin(tunnel.TunnelId, target);
        _viewModel.ApplyTransitionIntent();
        if (target) _viewModel.BeginConnectionAttempt(tunnel);
        if (button is not null) button.IsEnabled = false;
        _viewModel.VpnIsLoading = true; _viewModel.VpnOperationTunnelId = tunnel.TunnelId;
        using CancellationTokenSource operationCts = new(TimeSpan.FromSeconds(30));
        using VpnConnectTrace? connectTrace = target && string.Equals(tunnel.Protocol, "WireGuard", StringComparison.OrdinalIgnoreCase) ? new VpnConnectTrace() : null;
        lock (_operationSync)
        {
            _operationCts = operationCts;
            _vpnTunnelOperationCts = operationCts;
        }
        string operationProfileId = _activeRouter.CurrentProfileId;
        long operationContextVersion = _activeRouter.Version;
        bool IsCurrentConnectOperation() => !operationCts.IsCancellationRequested &&
            operationProfileId == _activeRouter.CurrentProfileId && operationContextVersion == _activeRouter.Version &&
            _operationIntent.IsCurrent(tunnel.TunnelId, operationGeneration, VpnTransitionIntent.Connecting);
        try
        {
            // Capture a timestamp-only baseline before the enable mutation. An
            // unavailable diagnostic never blocks the user-requested connect.
            VpnWireGuardHandshakeSnapshot baseline = new();
            if (target && string.Equals(tunnel.Protocol, "WireGuard", StringComparison.OrdinalIgnoreCase))
            {
                connectTrace?.Applicability(true);
                connectTrace?.InterfaceAvailable(!string.IsNullOrWhiteSpace(tunnel.InterfaceName));
                baseline = await _service.GetWireGuardHandshakeSnapshotAsync(tunnel, operationCts.Token);
                connectTrace?.Baseline(baseline);
                operationCts.Token.ThrowIfCancellationRequested();
                if (!IsCurrentConnectOperation())
                {
                    _viewModel.CancelConnectionAttempt(tunnel.TunnelId);
                    return false;
                }
            }
            VpnOperationResult result = await _service.SetTunnelEnabledAsync(tunnel.TunnelId, target, operationCts.Token, connectTrace);
            connectTrace?.PostRpc(_liveStatus.Current.SingleOrDefault(status => status.TunnelId == tunnel.TunnelId));
            _devLog.Write(RouterPilotDevLogCategory.VPN, target ? "Connect RPC completed" : "Disconnect RPC completed", result.Success ? RouterPilotDevLogLevel.Info : RouterPilotDevLogLevel.Warn);
            if (!result.Success)
            {
                if (target) _viewModel.CancelConnectionAttempt(tunnel.TunnelId);
                MessageBox.Show(result.Message, "VPN", MessageBoxButton.OK, MessageBoxImage.Warning);
                await RefreshAsync(force: true, refreshTailscale: false);
                return false;
            }
            bool runtimeReachedTarget = await WaitForTunnelRuntimeAsync(tunnel.TunnelId, target, operationCts.Token);
            VpnLiveStatusDiagnostics.Record($"VPN tunnel runtime reconciliation: {(runtimeReachedTarget ? "PASS" : "TIMEOUT")}; target={(target ? "Connected" : "Disconnected")}");
            if (target && !runtimeReachedTarget && baseline.IsAvailable && IsCurrentConnectOperation())
            {
                VpnLiveStatusInfo? current = _liveStatus.Current.SingleOrDefault(status => status.TunnelId == tunnel.TunnelId);
                if (current is { Enabled: true, Status: 2 } && string.Equals(tunnel.Protocol, "WireGuard", StringComparison.OrdinalIgnoreCase))
                {
                    VpnWireGuardHandshakeSnapshot postAttempt = await _service.GetWireGuardHandshakeSnapshotAsync(tunnel, operationCts.Token);
                    connectTrace?.After(postAttempt);
                    VpnWireGuardHandshakeState comparison = RouterManager.CompareWireGuardHandshakeSnapshots(baseline, postAttempt);
                    connectTrace?.Comparison(comparison);
                    operationCts.Token.ThrowIfCancellationRequested();
                    current = _liveStatus.Current.SingleOrDefault(status => status.TunnelId == tunnel.TunnelId);
                    if (IsCurrentConnectOperation() && current is { Enabled: true, Status: 2 } &&
                        comparison == VpnWireGuardHandshakeState.NoHandshake)
                        _viewModel.MarkWireGuardHandshakeFailure(tunnel.TunnelId);
                }
            }
            if (runtimeReachedTarget) connectTrace?.Outcome(target ? "CONNECTED" : "DISCONNECTED");
            else if (target && baseline.IsAvailable) connectTrace?.Outcome("TRANSITION_TIMEOUT_HANDSHAKE_UNKNOWN");
            else if (target) connectTrace?.Outcome("TRANSITION_TIMEOUT_HANDSHAKE_UNKNOWN");
            _viewModel.VpnIsLoading = false;
            if (!target)
                _devLog.Write(RouterPilotDevLogCategory.VPN, $"AuthoritativeRefresh.Start source=PostDisconnect tunnel={tunnel.TunnelId}", RouterPilotDevLogLevel.Debug);
            await RefreshAsync(force: true, refreshTailscale: false);
            if (!target)
                CompleteAuthoritativeDisconnect(tunnel.TunnelId, operationGeneration);
            if (!target)
                LogPostDisconnectReconciliation(tunnel.TunnelId);
            return runtimeReachedTarget;
        }
        catch (OperationCanceledException) when (operationCts.IsCancellationRequested)
        {
            if (target) _viewModel.CancelConnectionAttempt(tunnel.TunnelId);
            _viewModel.VpnStatus = "VPN operation cancelled.";
            return false;
        }
        finally
        {
            _operationIntent.Clear(tunnel.TunnelId, operationGeneration);
            _viewModel.ApplyTransitionIntent();
            _viewModel.VpnOperationTunnelId = 0;
            _viewModel.VpnIsLoading = false;
            lock (_operationSync)
            {
                if (ReferenceEquals(_operationCts, operationCts)) _operationCts = null;
                if (ReferenceEquals(_vpnTunnelOperationCts, operationCts)) _vpnTunnelOperationCts = null;
            }
            if (button is not null) button.IsEnabled = true;
        }
    }

    private void InvalidateConnectAttemptForDisconnect(int tunnelId)
    {
        bool supersededConnect = _operationIntent.GetIntent(tunnelId) == VpnTransitionIntent.Connecting;
        CancellationTokenSource? pendingConnect = null;
        lock (_operationSync)
        {
            if (supersededConnect) pendingConnect = _vpnTunnelOperationCts;
        }
        if (pendingConnect is not null && !pendingConnect.IsCancellationRequested) pendingConnect.Cancel();
        _viewModel.MarkExplicitDisconnect(tunnelId);
        _devLog.Write(RouterPilotDevLogCategory.VPN,
            $"OperationGeneration.Invalidated reason=Disconnect tunnel={tunnelId} pendingConnect={(supersededConnect ? "true" : "false")}",
            RouterPilotDevLogLevel.Debug);
    }

    private void CompleteAuthoritativeDisconnect(int tunnelId, long operationGeneration)
    {
        if (_viewModel.VpnTunnels.SingleOrDefault(item => item.TunnelId == tunnelId) is not { Enabled: false }) return;
        _operationIntent.Clear(tunnelId, operationGeneration);
        _viewModel.VpnOperationTunnelId = 0;
        _viewModel.ApplyTransitionIntent();
        _devLog.Write(RouterPilotDevLogCategory.VPN,
            $"OperationGeneration.Completed reason=AuthoritativeDisconnect tunnel={tunnelId}",
            RouterPilotDevLogLevel.Debug);
    }

    private void LogPostDisconnectReconciliation(int tunnelId)
    {
        VpnTunnelInfo? reconciled = _viewModel.VpnTunnels.SingleOrDefault(item => item.TunnelId == tunnelId);
        if (reconciled is { Enabled: false })
        {
            _devLog.Write(RouterPilotDevLogCategory.VPN,
                $"AuthoritativePostDisconnect enabled=false state={reconciled.ConnectionState}",
                RouterPilotDevLogLevel.Info);
            _devLog.Write(RouterPilotDevLogCategory.PIA,
                $"RefreshServers.Eligibility eligible={(_viewModel.CanManagePiaProviderServers ? "true" : "false")} reason=PostDisconnectReconciled",
                RouterPilotDevLogLevel.Debug);
            return;
        }

        _devLog.Write(RouterPilotDevLogCategory.VPN,
            "AuthoritativePostDisconnect unavailableOrEnabled=true",
            RouterPilotDevLogLevel.Warn);
    }

    private async void ManageVpnDevices_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: VpnTunnelInfo requestedTunnel } || !requestedTunnel.CanManageRoutingDevices || _viewModel.VpnIsLoading || _openingVpnDeviceEditor) return;
        _openingVpnDeviceEditor = true;
        string profileId = _activeRouter.CurrentProfileId;
        long contextVersion = _activeRouter.Version;
        int tunnelId = requestedTunnel.TunnelId;
        try
        {
            // Reuse the normal Clients reconciliation so current Wi-Fi,
            // Ethernet, and firmware-only LAN clients are available before
            // creating the editor's purely local working copy.
            if (!await _clientInventoryCoordinator.RefreshAuthoritativeInventoryAsync()) return;
            if (profileId != _activeRouter.CurrentProfileId || contextVersion != _activeRouter.Version) return;
            VpnTunnelInfo? tunnel = _viewModel.VpnTunnels.SingleOrDefault(item => item.TunnelId == tunnelId);
            if (tunnel is null || !tunnel.CanManageRoutingDevices || _viewModel.VpnIsLoading) return;
        _viewModel.VpnDeviceEditorItems.Clear();
        IReadOnlyList<VpnDeviceEditorItem> editorItems = VpnDeviceEditorProjection.Build(
            _clientInventory.Snapshot, _clientInventory.PresenceSnapshot, _clientNames, tunnel.RoutingDeviceIdentities);
        foreach (VpnDeviceEditorItem item in editorItems) _viewModel.VpnDeviceEditorItems.Add(item);
        _viewModel.VpnDeviceEditorTunnelId = tunnel.TunnelId;
        _viewModel.VpnDeviceEditorStatus = string.Empty;
        _viewModel.VpnDeviceEditorOpen = true;
        int online = _viewModel.VpnDeviceEditorItems.Count(item => item.CanEdit && item.StatusDisplay == "Online");
        int unknown = _viewModel.VpnDeviceEditorItems.Count(item => item.IsUnknownExistingAssignment);
        _devLog.Write(RouterPilotDevLogCategory.VPN, $"DeviceInventory.EditorOpen inventory={_clientInventory.Snapshot.Count} online={online} assigned={tunnel.RoutingDeviceIdentities.Count} unknownAssigned={unknown}", RouterPilotDevLogLevel.Info);
        }
        finally { _openingVpnDeviceEditor = false; }
    }

    private void CancelVpnDevices_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.VpnDeviceEditorOpen = false;
        _viewModel.VpnDeviceEditorItems.Clear();
        _devLog.Write(RouterPilotDevLogCategory.VPN, "AssignedDevices.ApplyCancelled reason=UserCancelled", RouterPilotDevLogLevel.Debug);
    }

    private async void ApplyVpnDevices_Click(object sender, RoutedEventArgs e)
    {
        int tunnelId = _viewModel.VpnDeviceEditorTunnelId;
        long generation = Interlocked.Read(ref _refreshGeneration);
        string profileId = _activeRouter.CurrentProfileId; long contextVersion = _activeRouter.Version;
        bool IsCurrent() => generation == Interlocked.Read(ref _refreshGeneration) && profileId == _activeRouter.CurrentProfileId && contextVersion == _activeRouter.Version;
        VpnTunnelInfo? tunnel = _viewModel.VpnTunnels.SingleOrDefault(item => item.TunnelId == tunnelId);
        if (tunnel is null || !tunnel.CanManageRoutingDevices || _viewModel.VpnIsLoading) { _viewModel.VpnDeviceEditorStatus = "The VPN configuration changed. Refresh and review device assignments again."; return; }
        IReadOnlyList<string> selected = _viewModel.VpnDeviceEditorItems.Where(item => item.CanEdit && item.IsSelected).Select(item => item.Identity).ToList();
        IReadOnlyList<string> knownEditable = _viewModel.VpnDeviceEditorItems.Where(item => item.CanEdit).Select(item => item.Identity).ToList();
        _viewModel.VpnIsLoading = true;
        try
        {
            VpnDeviceAssignmentResult result = await _service.UpdateSelectedVpnDevicesAsync(tunnelId, selected, knownEditable, IsCurrent, CancellationToken.None);
            if (result.IsEmptyEffectiveAssignment)
            {
                _viewModel.VpnDeviceEditorStatus = result.Message;
                MessageBox.Show(result.Message, EmptyVpnDeviceAssignmentTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            await RefreshAsync(force: true, refreshTailscale: false);
            if (result.Success) { _viewModel.VpnDeviceEditorOpen = false; _viewModel.VpnDeviceEditorItems.Clear(); }
            else _viewModel.VpnDeviceEditorStatus = result.Message;
        }
        catch { _viewModel.VpnDeviceEditorStatus = "VPN device assignment is unavailable."; }
        finally { _viewModel.VpnIsLoading = false; }
    }

    private async void RefreshPiaProviderServers_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanManagePiaProviderServers) { _viewModel.PiaProviderStatus = "Disconnect the PIA WireGuard tunnel before refreshing provider servers."; return; }
        long generation = Interlocked.Read(ref _refreshGeneration);
        string profileId = _activeRouter.CurrentProfileId; long version = _activeRouter.Version;
        bool IsCurrent() => generation == Interlocked.Read(ref _refreshGeneration) && profileId == _activeRouter.CurrentProfileId && version == _activeRouter.Version;
        _viewModel.BeginPiaProviderServerCatalogueRefresh();
        _viewModel.PiaProviderOperationRunning = true;
        _devLog.Write(RouterPilotDevLogCategory.PIA, "CatalogueRefresh.Start");
        using CancellationTokenSource operationCts = new(TimeSpan.FromSeconds(30));
        try
        {
            VpnProviderServerCatalogueResult result = await _service.RefreshPiaProviderServersAsync(_viewModel.PiaProviderTunnelId, _viewModel.PiaProviderGroupId, operationCts.Token);
            if (!IsCurrent()) return;
            if (result.Success && result.Servers.Count > 0)
            {
                _viewModel.ReplacePiaProviderServers(result.Servers);
                _devLog.Write(RouterPilotDevLogCategory.PIA, $"CatalogueRefresh.Completed locations={result.Servers.Count}", RouterPilotDevLogLevel.Info);
                _devLog.Write(RouterPilotDevLogCategory.PIA,
                    $"ExistingServers.AfterCatalogueRefresh count={_viewModel.ExistingRouterServers.Count}", RouterPilotDevLogLevel.Debug);
                _devLog.Write(RouterPilotDevLogCategory.PIA, "ServerSelection.Enabled reason=CatalogueReady", RouterPilotDevLogLevel.Debug);
            }
            else
            {
                _viewModel.FailPiaProviderServerCatalogueRefresh(result.Success ? "No usable PIA servers were returned by the router." : result.Message);
                _devLog.Write(RouterPilotDevLogCategory.PIA, "CatalogueRefresh.Failed reason=UnavailableOrEmpty", RouterPilotDevLogLevel.Warn);
                _devLog.Write(RouterPilotDevLogCategory.PIA, "ServerSelection.Disabled reason=RefreshFailed", RouterPilotDevLogLevel.Debug);
            }
        }
        catch
        {
            if (IsCurrent())
            {
                _viewModel.FailPiaProviderServerCatalogueRefresh("Provider server refresh is unavailable.");
                _devLog.Write(RouterPilotDevLogCategory.PIA, "CatalogueRefresh.Failed reason=Unavailable", RouterPilotDevLogLevel.Warn);
                _devLog.Write(RouterPilotDevLogCategory.PIA, "ServerSelection.Disabled reason=RefreshFailed", RouterPilotDevLogLevel.Debug);
            }
        }
        finally { _viewModel.PiaProviderOperationRunning = false; }
    }

    private async void GeneratePiaProviderConfig_Click(object sender, RoutedEventArgs e) => await RunPiaProviderConfigAsync(false);

    private async void ApplyAndConnectPiaProviderConfig_Click(object sender, RoutedEventArgs e) => await RunPiaProviderConfigAsync(false, applyAndConnect: true);

    private Task MakePrimaryAsync() => RunPiaProviderConfigAsync(false, useExistingCandidate: true);

    private Task RefreshExistingRouterServersAsync() => RefreshAsync(force: true, refreshTailscale: false);

    private async void RegeneratePiaProviderConfig_Click(object sender, RoutedEventArgs e) => await RunPiaProviderConfigAsync(true);

    private async Task RunPiaProviderConfigAsync(bool explicitRecovery, bool applyAndConnect = false, bool useExistingCandidate = false)
    {
#if DEBUG
        Debug.WriteLine("PIA_APPLY_UI_ENTRY=YES");
#endif
        VpnProviderServerInfo? selection = useExistingCandidate
            ? _viewModel.SelectedExistingPiaConfigCandidate
            : _viewModel.SelectedPiaProviderServer;
        bool canApplySelection = useExistingCandidate ? _viewModel.CanApplyExistingPiaConfig : _viewModel.CanApplyProviderServer;
        if (!canApplySelection || selection is null)
        {
            _viewModel.PiaProviderStatus = useExistingCandidate
                ? "Choose an existing server before making it Primary."
                : "Refresh Servers and choose a server before applying the PIA catalogue selection.";
            return;
        }
        long generation = Interlocked.Read(ref _refreshGeneration);
        string profileId = _activeRouter.CurrentProfileId; long version = _activeRouter.Version;
        bool IsCurrent() => generation == Interlocked.Read(ref _refreshGeneration) && profileId == _activeRouter.CurrentProfileId && version == _activeRouter.Version;
        _viewModel.PiaProviderOperationRunning = true;
        string? combinedOperation = applyAndConnect ? _devLog.CreateOperationId("PIA") : null;
        Stopwatch? combinedTiming = applyAndConnect ? Stopwatch.StartNew() : null;
        if (applyAndConnect)
        {
            _devLog.Write(RouterPilotDevLogCategory.UI, "Action.ApplyAndConnect page=VPN", RouterPilotDevLogLevel.Info);
            _devLog.Write(RouterPilotDevLogCategory.PIA, combinedOperation!, "ApplyAndConnect.Start", RouterPilotDevLogLevel.Info);
        }
        _devLog.Write(RouterPilotDevLogCategory.PIA,
            $"ServerSelection.Pending source={(selection.IsExistingConfigCandidate ? "ExistingCandidate" : "ProviderCatalogue")}",
            RouterPilotDevLogLevel.Info);
        using CancellationTokenSource operationCts = new(TimeSpan.FromSeconds(30));
        try
        {
            VpnProviderConfigGenerationResult result = selection.IsExistingConfigCandidate
                ? await _service.ApplyExistingPiaProviderConfigAsync(_viewModel.PiaProviderTunnelId, _viewModel.PiaProviderGroupId, selection, IsCurrent, operationCts.Token)
                : await _service.GeneratePiaProviderConfigAsync(_viewModel.PiaProviderTunnelId, _viewModel.PiaProviderGroupId, selection, IsCurrent, operationCts.Token);
            if (!IsCurrent()) return;
            _viewModel.PiaProviderStatus = result.Success && explicitRecovery
                ? "VPN server configuration was regenerated and is ready to connect."
                : result.Message;
            if (result.Success && result.AuthoritativeServer is not null)
                _viewModel.ApplyAuthoritativePiaProviderServer(result.AuthoritativeServer);
            // RefreshAsync advances the page generation. Clear our local busy
            // state first so that a completed generation cannot leave the new
            // inventory snapshot with its controls permanently disabled.
            _viewModel.PiaProviderOperationRunning = false;
            await RefreshAsync(force: true, refreshTailscale: false);
            if (!applyAndConnect || !result.Success)
            {
                if (applyAndConnect)
                    _devLog.Write(RouterPilotDevLogCategory.PIA, combinedOperation!, "ApplyAndConnect.Failed stage=Apply", RouterPilotDevLogLevel.Warn, combinedTiming!.ElapsedMilliseconds, "Failed");
                return;
            }

            // RefreshAsync deliberately advances the page generation.  The
            // freshly-read tunnel is therefore the only valid input to the
            // existing Connect path; never retain a pre-generation config ID.
            if (profileId != _activeRouter.CurrentProfileId || version != _activeRouter.Version ||
                _viewModel.SelectedPiaProviderServer is not { } currentSelection ||
                !string.Equals(currentSelection.CountryName, selection.CountryName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(currentSelection.CityName, selection.CityName, StringComparison.OrdinalIgnoreCase))
            {
                _viewModel.PiaProviderStatus = "VPN server was applied, but the router profile or selected location changed before connecting.";
                _devLog.Write(RouterPilotDevLogCategory.PIA, combinedOperation!, "ApplyAndConnect.Failed stage=Connect reason=OperationInvalidated", RouterPilotDevLogLevel.Warn, combinedTiming!.ElapsedMilliseconds, "Cancelled");
                return;
            }

            VpnTunnelInfo? freshTunnel = _viewModel.VpnTunnels.SingleOrDefault(tunnel =>
                tunnel.TunnelId == _viewModel.PiaProviderTunnelId &&
                !tunnel.Enabled &&
                string.Equals(tunnel.Protocol, "WireGuard", StringComparison.OrdinalIgnoreCase));
            if (freshTunnel is null)
            {
                _viewModel.PiaProviderStatus = "VPN server was applied, but the Primary Tunnel is no longer ready to connect.";
                _devLog.Write(RouterPilotDevLogCategory.PIA, combinedOperation!, "ApplyAndConnect.Failed stage=Connect reason=TunnelStateChanged", RouterPilotDevLogLevel.Warn, combinedTiming!.ElapsedMilliseconds, "Failed");
                return;
            }

            _devLog.Write(RouterPilotDevLogCategory.PIA, combinedOperation!, "ApplyAndConnect.ApplyCompleted", RouterPilotDevLogLevel.Info);
            _viewModel.PiaProviderOperationRunning = true;
            bool connected = await RunTunnelOperationAsync(freshTunnel, target: true, button: null);
            _devLog.Write(RouterPilotDevLogCategory.PIA, combinedOperation!,
                connected ? "ApplyAndConnect.Completed" : "ApplyAndConnect.Failed stage=Connect",
                connected ? RouterPilotDevLogLevel.Info : RouterPilotDevLogLevel.Warn,
                combinedTiming!.ElapsedMilliseconds, connected ? "Completed" : "Failed");
        }
        catch
        {
            if (IsCurrent()) _viewModel.PiaProviderStatus = "Provider configuration generation is unavailable.";
            if (applyAndConnect)
                _devLog.Write(RouterPilotDevLogCategory.PIA, combinedOperation!, "ApplyAndConnect.Failed stage=Apply reason=Unavailable", RouterPilotDevLogLevel.Warn, combinedTiming!.ElapsedMilliseconds, "Failed");
        }
        finally { if (IsCurrent()) _viewModel.PiaProviderOperationRunning = false; }
    }

    private async Task<bool> WaitForTunnelRuntimeAsync(int tunnelId, bool enabled, CancellationToken token)
    {
        VpnLiveStatusInfo? current = _liveStatus.Current.SingleOrDefault(status => status.TunnelId == tunnelId);
        if (current is not null && current.Enabled == enabled && (enabled ? current.IsConnected : !current.IsConnected))
            return true;

        TaskCompletionSource<bool> reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStatusChanged(IReadOnlyList<VpnLiveStatusInfo> statuses)
        {
            VpnLiveStatusInfo? status = statuses.SingleOrDefault(item => item.TunnelId == tunnelId);
            if (status is not null && status.Enabled == enabled && (enabled ? status.IsConnected : !status.IsConnected))
                reached.TrySetResult(true);
        }

        _liveStatus.StatusChanged += OnStatusChanged;
        try
        {
            current = _liveStatus.Current.SingleOrDefault(status => status.TunnelId == tunnelId);
            if (current is not null && current.Enabled == enabled && (enabled ? current.IsConnected : !current.IsConnected))
                return true;
            Task completed = await Task.WhenAny(reached.Task, Task.Delay(TimeSpan.FromSeconds(12), token));
            return completed == reached.Task;
        }
        finally { _liveStatus.StatusChanged -= OnStatusChanged; }
    }

    private void VpnTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != VpnTabs) return;
        bool tailscale = VpnTabs.SelectedIndex == 1;
        VpnClientContent.Visibility = tailscale ? Visibility.Collapsed : Visibility.Visible;
        TailscaleContent.Visibility = tailscale ? Visibility.Visible : Visibility.Collapsed;
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
