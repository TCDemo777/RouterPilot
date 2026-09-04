using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;
using System.Reflection;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using RouterPilot.Models;
using RouterPilot.Services;

namespace RouterPilot.ViewModels;

public sealed partial class MaintenanceViewModel : ObservableObject
{
    private readonly MaintenanceOperationService _operations;
    private readonly IBackupRestoreService _backupRestoreService;
    private readonly MaintenanceHistoryService _historyService;
    private readonly FirmwareUpdateService _firmwareUpdateService;
    private readonly RouterCapabilityDiscoveryService _capabilityDiscovery;
    private readonly RouterStateSnapshotService _snapshotService;
    private readonly IActiveRouterContext _activeRouter;
    private readonly IRouterSwitchCoordinator _routerSwitch;
    private DashboardViewModel _dashboard;
    private CancellationTokenSource? _diagnosticsCancellation;
    private bool _snapshotBusy;
    private bool _lifecycleBusy;
    private bool _diagnosticBusy;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string activeOperation = string.Empty;

    [ObservableProperty]
    private string lastResult = string.Empty;

    public MaintenanceViewModel(
        MaintenanceOperationService operations,
        MaintenanceHistoryService historyService,
        IBackupRestoreService backupRestoreService,
        FirmwareUpdateService firmwareUpdateService,
        RouterCapabilityDiscoveryService capabilityDiscovery,
        RouterStateSnapshotService snapshotService,
        IActiveRouterContext activeRouter,
        IRouterSwitchCoordinator routerSwitch)
    {
        _operations = operations;
        _backupRestoreService = backupRestoreService;
        _historyService = historyService;
        _firmwareUpdateService = firmwareUpdateService;
        _capabilityDiscovery = capabilityDiscovery;
        _snapshotService = snapshotService;
        _activeRouter = activeRouter;
        _routerSwitch = routerSwitch;
        _routerSwitch.Switched += RouterSwitch_Switched;
        _firmwareUpdateService.PropertyChanged += FirmwareUpdateService_PropertyChanged;
        History = historyService.Entries;
        _historyService.Changed += HistoryService_Changed;
        Actions = new ObservableCollection<MaintenanceActionItem>(
        [
            new(MaintenanceAction.RestartWifi, "Restart Wi-Fi", "Restarts the router wireless interfaces."),
            new(MaintenanceAction.RestartAdGuard, "Restart AdGuard Home", "Briefly restarts DNS filtering."),
            new(MaintenanceAction.ReconnectWan, "Reconnect WAN", "Renews the router WAN interface."),
            new(MaintenanceAction.RebootRouter, "Reboot Router", "Restarts the router and interrupts local connectivity."),
            new(MaintenanceAction.RefreshAll, "Refresh All", "Runs RouterPilot's current dashboard refresh."),
            new(MaintenanceAction.RunDiagnostics, "Run Diagnostics", "Collects the existing safe router support checks."),
            new(MaintenanceAction.BackupDiagnostics, "Backup Diagnostics", "Exports the same safe diagnostics report as a ZIP archive.")
        ]);

        _dashboard = new DashboardViewModel();
        UpdateActionHistory();
    }

    public DashboardViewModel Dashboard => _dashboard;

    public ReadOnlyObservableCollection<MaintenanceHistoryEntry> History { get; }

    public ObservableCollection<MaintenanceActionItem> Actions { get; }
    public ObservableCollection<DiagnosticCheck> DiagnosticChecks { get; } = new();
    public string DiagnosticsStatus { get; private set; } = "No diagnostic run in this session.";
    public bool IsDiagnosticsRunning { get; private set; }
    public bool IsCapabilityReportRunning { get; private set; }
    public string CapabilityReportStatus { get; private set; } = "No capability report collected in this session.";
    public string CapabilityReportText { get; private set; } = string.Empty;

    public IReadOnlyList<RouterStateSnapshot> StateSnapshots => _snapshotService.Load(_activeRouter.CurrentProfileId);
    public RouterStateSnapshot? LatestStateSnapshot => StateSnapshots.FirstOrDefault();
    public IReadOnlyList<RouterStateChange> StateChanges { get; private set; } = [];
    public IReadOnlyList<RouterStateComparisonJournalEntry> ComparisonJournal => _snapshotService.LoadJournal(_activeRouter.CurrentProfileId);
    public bool IsSnapshotBusy => _snapshotBusy;
    public string SnapshotStatus { get; private set; } = "No configuration snapshots yet.";
    public string SnapshotChangeSummary => StateChanges.Count == 0 ? "No comparable changes detected." : $"{StateChanges.Count} observable changes detected.";
    public string SnapshotComparisonReport => BuildComparisonReport();
    public bool IsLifecycleBusy => _lifecycleBusy;
    public string LifecycleStatus { get; private set; } = "No firmware lifecycle check has been run.";
    public RouterStateSnapshot? PreUpgradeSnapshot => StateSnapshots.FirstOrDefault(snapshot => snapshot.FriendlyName.StartsWith("Pre-upgrade", StringComparison.OrdinalIgnoreCase));
    public DiagnosticCategory SelectedDiagnosticCategory { get; private set; } = DiagnosticCategory.NotSure;
    public GuidedDiagnosticSession? DiagnosticSession { get; private set; }
    public bool IsDiagnosticBusy => _diagnosticBusy;
    public string DiagnosticStatus => DiagnosticSession is null
        ? "Diagnostic evidence is not loaded yet. Choose an area to review the evidence RouterPilot already has."
        : $"{DiagnosticSession.Category} checked from currently loaded evidence • {DiagnosticSession.State} • {DiagnosticSession.Findings.Count} finding(s).";
    public string DiagnosticReport => GuidedDiagnosticsService.BuildReport(DiagnosticSession);
    public string HomeNetworkReportText => NetworkHealthCentreProjection.BuildHomeNetworkReport(_dashboard);

    public void CaptureStateSnapshot()
    {
        if (_snapshotBusy || !_dashboard.RouterConnected) return;
        _snapshotBusy = true;
        OnPropertyChanged(nameof(IsSnapshotBusy));
        try
        {
            RouterStateSnapshot snapshot = RouterStateSnapshotService.FromDashboard(_activeRouter.CurrentProfileId, _dashboard);
            _snapshotService.Save(snapshot);
            SnapshotStatus = $"Snapshot captured {snapshot.CapturedAt.ToLocalTime():g}.";
            StateChanges = [];
        }
        finally
        {
            _snapshotBusy = false;
            OnPropertyChanged(nameof(IsSnapshotBusy));
            OnPropertyChanged(nameof(StateSnapshots));
            OnPropertyChanged(nameof(LatestStateSnapshot));
            OnPropertyChanged(nameof(SnapshotStatus));
            OnPropertyChanged(nameof(SnapshotChangeSummary));
        }
    }

    public async Task PrepareForFirmwareUpgradeAsync(Func<Task> refreshAll)
    {
        if (_lifecycleBusy || !_dashboard.RouterConnected) return;
        _lifecycleBusy = true;
        OnPropertyChanged(nameof(IsLifecycleBusy));
        try
        {
            LifecycleStatus = "Refreshing router state...";
            OnPropertyChanged(nameof(LifecycleStatus));
            await refreshAll();
            RouterStateSnapshot snapshot = RouterStateSnapshotService.FromDashboard(
                _activeRouter.CurrentProfileId, _dashboard,
                $"Pre-upgrade — Firmware {RouterFirmwareText} — {DateTime.Now:g}");
            _snapshotService.Save(snapshot);
            LifecycleStatus = "Pre-upgrade snapshot captured. Perform the firmware upgrade using the GL.iNet administration interface, then return to RouterPilot after the router has restarted.";
            SnapshotStatus = $"Pre-upgrade baseline captured {snapshot.CapturedAt.ToLocalTime():g}.";
        }
        catch (OperationCanceledException) { LifecycleStatus = "Pre-upgrade preparation cancelled."; }
        catch (Exception exception)
        {
            LifecycleStatus = "Pre-upgrade state could not be fully captured.";
            System.Diagnostics.Debug.WriteLine($"Firmware preparation failed ({exception.GetType().Name}).");
        }
        finally
        {
            _lifecycleBusy = false;
            OnPropertyChanged(nameof(IsLifecycleBusy));
            OnPropertyChanged(nameof(LifecycleStatus));
            OnPropertyChanged(nameof(StateSnapshots));
            OnPropertyChanged(nameof(LatestStateSnapshot));
            OnPropertyChanged(nameof(PreUpgradeSnapshot));
            OnPropertyChanged(nameof(SnapshotStatus));
        }
    }

    public async Task RunPostUpgradeCheckAsync(Func<Task> refreshAll)
    {
        await CompareLatestWithCurrentAsync(refreshAll);
        LifecycleStatus = "Post-upgrade verification completed. Review the observable configuration changes below.";
        OnPropertyChanged(nameof(LifecycleStatus));
    }

    public void SelectDiagnosticCategory(DiagnosticCategory category)
    {
        SelectedDiagnosticCategory = category;
        OnPropertyChanged(nameof(SelectedDiagnosticCategory));
        RunDiagnosticSession();
    }

    public void RunDiagnosticSession()
    {
        if (_diagnosticBusy) return;
        _diagnosticBusy = true;
        OnPropertyChanged(nameof(IsDiagnosticBusy));
        try { DiagnosticSession = GuidedDiagnosticsService.Build(SelectedDiagnosticCategory, _dashboard); }
        finally
        {
            _diagnosticBusy = false;
            OnPropertyChanged(nameof(IsDiagnosticBusy));
            OnPropertyChanged(nameof(DiagnosticSession));
            OnPropertyChanged(nameof(DiagnosticStatus));
            OnPropertyChanged(nameof(DiagnosticReport));
        }
    }

    public async Task CompareLatestWithCurrentAsync(Func<Task> refreshAll)
    {
        if (_snapshotBusy || LatestStateSnapshot is not { } baseline) return;
        _snapshotBusy = true;
        OnPropertyChanged(nameof(IsSnapshotBusy));
        try
        {
            await refreshAll();
            if (baseline.ProfileId != _activeRouter.CurrentProfileId)
            {
                SnapshotStatus = "Snapshot belongs to a different router/profile.";
                StateChanges = [];
                return;
            }
            RouterStateSnapshot current = RouterStateSnapshotService.FromDashboard(_activeRouter.CurrentProfileId, _dashboard);
            StateChanges = RouterStateSnapshotComparer.Compare(baseline, current);
            SnapshotStatus = $"Compared with current router at {current.CapturedAt.ToLocalTime():g}.";
            int notable = StateChanges.Count(change => change.Importance == "Notable");
            _snapshotService.AppendJournal(new RouterStateComparisonJournalEntry(1, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, baseline.ProfileId, baseline.SnapshotId, baseline.FriendlyName, StateChanges.Count, notable, StateChanges.Count - notable, 0));
        }
        catch (OperationCanceledException) { SnapshotStatus = "Snapshot comparison cancelled."; }
        catch (Exception exception)
        {
            SnapshotStatus = "Current router state could not be fully retrieved.";
            System.Diagnostics.Debug.WriteLine($"Snapshot comparison failed ({exception.GetType().Name}).");
        }
        finally
        {
            _snapshotBusy = false;
            OnPropertyChanged(nameof(IsSnapshotBusy));
            OnPropertyChanged(nameof(StateChanges));
            OnPropertyChanged(nameof(SnapshotStatus));
            OnPropertyChanged(nameof(SnapshotChangeSummary));
            OnPropertyChanged(nameof(ComparisonJournal));
            OnPropertyChanged(nameof(SnapshotComparisonReport));
        }
    }

    public string BuildComparisonReport()
    {
        RouterStateSnapshot? snapshot = LatestStateSnapshot;
        StringBuilder report = new();
        report.AppendLine("RouterPilot Configuration Comparison");
        report.AppendLine();
        report.AppendLine($"Snapshot: {snapshot?.FriendlyName ?? "Unavailable"}");
        if (snapshot is not null) report.AppendLine($"Captured: {snapshot.CapturedAt.ToLocalTime():g}");
        report.AppendLine($"Observable changes: {StateChanges.Count}");
        report.AppendLine();
        foreach (RouterStateChange change in StateChanges)
            report.AppendLine($"{change.Category}: {change.Field}: {change.OldValue} -> {change.NewValue}");
        return report.ToString();
    }

    public void DeleteLatestStateSnapshot()
    {
        if (LatestStateSnapshot is not { } snapshot) return;
        _snapshotService.Delete(_activeRouter.CurrentProfileId, snapshot.SnapshotId);
        StateChanges = [];
        SnapshotStatus = "Snapshot deleted locally.";
        OnPropertyChanged(nameof(StateSnapshots)); OnPropertyChanged(nameof(LatestStateSnapshot));
        OnPropertyChanged(nameof(PreUpgradeSnapshot)); OnPropertyChanged(nameof(StateChanges)); OnPropertyChanged(nameof(SnapshotStatus)); OnPropertyChanged(nameof(SnapshotChangeSummary));
    }

    public async Task CollectCapabilityReportAsync()
    {
        if (IsCapabilityReportRunning) return;
        IsCapabilityReportRunning = true;
        CapabilityReportStatus = "Collecting read-only capability evidence…";
        OnPropertyChanged(nameof(IsCapabilityReportRunning)); OnPropertyChanged(nameof(CapabilityReportStatus));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(45));
        try
        {
            string raw = await _capabilityDiscovery.CollectAsync(timeout.Token);
            CapabilityReportText = RouterCapabilityDiscoveryReportBuilder.Build(raw);
            CapabilityReportStatus = "Capability report complete. The report is sanitized and read-only.";
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        { CapabilityReportStatus = "Capability report cancelled or timed out."; }
        catch (Exception exception)
        { CapabilityReportStatus = "Capability report unavailable."; System.Diagnostics.Debug.WriteLine($"Capability discovery failed ({exception.GetType().Name})."); }
        finally { IsCapabilityReportRunning = false; OnPropertyChanged(nameof(IsCapabilityReportRunning)); OnPropertyChanged(nameof(CapabilityReportStatus)); OnPropertyChanged(nameof(CapabilityReportText)); }
    }

    public string BackupFolder => _backupRestoreService.BackupFolder;

    public bool CanManageBackups => !IsBusy;

    public FirmwareUpdateCheck FirmwareUpdate => _firmwareUpdateService.Current;
    public string FirmwareCurrentVersion => string.IsNullOrWhiteSpace(FirmwareUpdate.CurrentVersion)
        ? RouterPilotStatusPresentation.NotAvailable
        : FirmwareUpdate.CurrentVersion;
    public string FirmwareLatestVersion => string.IsNullOrWhiteSpace(FirmwareUpdate.LatestVersion)
        ? FirmwareUpdate.Status == FirmwareUpdateCheckStatus.UpToDate
            ? "No newer version available"
            : RouterPilotStatusPresentation.NotAvailable
        : FirmwareUpdate.LatestVersion;
    public bool IsFirmwareChecking => _firmwareUpdateService.IsChecking;
    public bool CanCheckFirmware => !IsFirmwareChecking && _dashboard.RouterConnected;
    public string FirmwareStatusText => IsFirmwareChecking
        ? "Checking…"
        : FirmwareUpdate.Status switch
        {
            FirmwareUpdateCheckStatus.UpToDate => "No update available",
            FirmwareUpdateCheckStatus.UpdateAvailable => "Update available",
            FirmwareUpdateCheckStatus.Error => "Unable to check",
            FirmwareUpdateCheckStatus.Pending or FirmwareUpdateCheckStatus.NotAvailable => "Not checked",
            _ => "Unavailable"
        };

    public string RouterIdentityText => string.IsNullOrWhiteSpace(Dashboard.RouterModel) || Dashboard.RouterModel == "-"
        ? RouterPilotStatusPresentation.NotAvailable
        : Dashboard.RouterModel;
    public string RouterFirmwareText => string.IsNullOrWhiteSpace(Dashboard.RouterFirmwareVersion) || Dashboard.RouterFirmwareVersion == "-"
        ? RouterPilotStatusPresentation.NotAvailable
        : Dashboard.RouterFirmwareVersion;
    public string RouterUptimeText => string.IsNullOrWhiteSpace(Dashboard.Uptime) || Dashboard.Uptime == "-"
        ? RouterPilotStatusPresentation.NotAvailable
        : Dashboard.Uptime;
    public string DhcpServiceText => Dashboard.DhcpLoaded ? Dashboard.DhcpStatusDisplay : RouterPilotStatusPresentation.NotAvailable;
    public string DhcpLeaseCountText => Dashboard.DhcpLoaded ? Dashboard.DhcpLeases.Count.ToString() : RouterPilotStatusPresentation.NotAvailable;
    public string DhcpReservationCountText => Dashboard.DhcpLoaded ? Dashboard.DhcpReservations.Count.ToString() : RouterPilotStatusPresentation.NotAvailable;
    public string UpdateReadinessText => FirmwareUpdate.Status == FirmwareUpdateCheckStatus.UpdateAvailable
        ? "Review the release and create a secure backup before updating."
        : "Firmware identity is read-only; no update is downloaded or installed by RouterPilot.";

    public async Task RunDiagnosticsAsync(Func<Task> refreshAll)
    {
        if (IsDiagnosticsRunning) return;
        IsDiagnosticsRunning = true;
        _diagnosticsCancellation?.Cancel();
        _diagnosticsCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken token = _diagnosticsCancellation.Token;
        DiagnosticChecks.Clear();
        DiagnosticsStatus = "Running diagnostics…";
        OnPropertyChanged(nameof(IsDiagnosticsRunning)); OnPropertyChanged(nameof(DiagnosticsStatus));
        try
        {
            await refreshAll();
            token.ThrowIfCancellationRequested();
            AddCheck("Router connection", Dashboard.RouterConnected ? "Pass" : "Unavailable", Dashboard.RouterStatusText, "Router");
            AddCheck("Internet reachability", Dashboard.InternetConnected ? "Pass" : "Attention", Dashboard.InternetStatusText, "Network");
            AddCheck("Gateway context", IsKnown(Dashboard.Gateway) ? "Pass" : "Unavailable", IsKnown(Dashboard.Gateway) ? "Gateway information available" : "Gateway information unavailable", "Network");
            AddCheck("Router DNS", Dashboard.RouterConnected ? "Available" : "Unknown", "See Router → DNS for native DNS telemetry.", "Router DNS");
            AddCheck("AdGuard", Dashboard.IsAdGuardAvailable ? "Pass" : "Unavailable", Dashboard.AdGuardStatusText, "Protection");
            AddCheck("VPN context", Dashboard.VpnSummary.State == "Connected" ? "Pass" : "Unknown", Dashboard.VpnNetworkSummary, "VPN");
            AddCheck("Router resources", Dashboard.RouterConnected ? "Available" : "Unavailable", Dashboard.RouterConnected ? $"CPU {Dashboard.CpuUsage}; temperature {Dashboard.Temperature}" : "Resource telemetry unavailable", "Performance");
            DiagnosticsStatus = $"Diagnostics completed • {DiagnosticChecks.Count} checks • {DateTime.Now:g}";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            DiagnosticsStatus = "Diagnostics cancelled.";
            AddCheck("Diagnostic run", "Cancelled", "No further checks were started.", "Diagnostics");
        }
        catch (Exception exception)
        {
            DiagnosticsStatus = "Diagnostics completed with unavailable checks.";
            AddCheck("Diagnostic run", "Unavailable", "One or more shared observations could not be loaded.", "Diagnostics");
            System.Diagnostics.Debug.WriteLine($"Diagnostics composition failed ({exception.GetType().Name}).");
        }
        finally { IsDiagnosticsRunning = false; OnPropertyChanged(nameof(IsDiagnosticsRunning)); OnPropertyChanged(nameof(DiagnosticsStatus)); }
    }

    public void CancelDiagnostics() => _diagnosticsCancellation?.Cancel();

    private void AddCheck(string title, string state, string summary, string source) => DiagnosticChecks.Add(new DiagnosticCheck(title, state, summary, source));
    private static bool IsKnown(string? value) => !string.IsNullOrWhiteSpace(value) && value != "-";

    public string BuildDiagnosticReport()
    {
        StringBuilder report = new("RouterPilot Diagnostic Report\n\n");
        report.AppendLine($"Generated: {DateTime.Now:g}");
        report.AppendLine($"Router: {StateForReport(Dashboard.RouterConnected, Dashboard.RouterStatusText)}");
        report.AppendLine($"Internet: {StateForReport(Dashboard.InternetConnected, Dashboard.InternetStatusText)}");
        report.AppendLine($"AdGuard: {StateForReport(Dashboard.IsAdGuardAvailable, Dashboard.AdGuardStatusText)}");
        report.AppendLine($"VPN: {Dashboard.VpnSummary.State}");
        report.AppendLine($"Firmware: {RouterFirmwareText}");
        report.AppendLine("\nChecks:");
        foreach (DiagnosticCheck check in DiagnosticChecks) report.AppendLine($"- {check.Title}: {check.State} — {check.Summary}");
        report.AppendLine("\nThis report omits addresses, client identities, SSIDs, domains, endpoints, credentials and raw command output.");
        return report.ToString();
    }

    private static string StateForReport(bool available, string value) => available ? "Available" : "Unavailable";
    public string FirmwareStatusColour => RouterPilotStatusPresentation.Colour(
        IsFirmwareChecking ? RouterPilotStatus.Pending : FirmwareUpdate.Status switch
        {
            FirmwareUpdateCheckStatus.UpToDate => RouterPilotStatus.Active,
            FirmwareUpdateCheckStatus.UpdateAvailable => RouterPilotStatus.Pending,
            FirmwareUpdateCheckStatus.Error => RouterPilotStatus.Error,
            _ => RouterPilotStatus.NotAvailable
        });
    public string FirmwareLastChecked => FirmwareUpdate.LastChecked is { } value
        ? value.ToLocalTime().ToString("dd MMM yyyy HH:mm")
        : RouterPilotStatusPresentation.NotAvailable;
    public bool HasFirmwareReleaseNotes => !string.IsNullOrWhiteSpace(FirmwareUpdate.ReleaseNotes) ||
                                            !string.IsNullOrWhiteSpace(FirmwareUpdate.ReleaseNotesUrl) ||
                                            !string.IsNullOrWhiteSpace(FirmwareUpdate.DownloadUrl);
    public string? FirmwareLink => FirmwareUpdate.ReleaseNotesUrl ?? FirmwareUpdate.DownloadUrl;

    public string LastBackupDate => History
        .FirstOrDefault(item => item.Action == MaintenanceAction.CreateBackup)?.TimestampDisplay ?? RouterPilotStatusPresentation.NotAvailable;

    public string LastBackupResult => History
        .FirstOrDefault(item => item.Action == MaintenanceAction.CreateBackup)?.OutcomeDisplay ?? RouterPilotStatusPresentation.NotAvailable;

    public string LastBackupDestination => History
        .FirstOrDefault(item => item.Action == MaintenanceAction.CreateBackup)?.OutputPath ?? BackupFolder;

    public string LastBackupSize => History
        .FirstOrDefault(item => item.Action == MaintenanceAction.CreateBackup)?.OutputSizeBytes is long size
            ? FormatFileSize(size)
            : RouterPilotStatusPresentation.NotAvailable;

    public string BuildSupportSnapshot()
    {
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown";
        StringBuilder report = new();
        report.AppendLine("RouterPilot Support Snapshot");
        report.AppendLine();
        report.AppendLine($"RouterPilot version: {version}");
        report.AppendLine($"Router model: {Dashboard.RouterModel ?? RouterPilotStatusPresentation.NotAvailable}");
        report.AppendLine($"Router connection: {Dashboard.RouterStatusText}");
        report.AppendLine($"Internet: {Dashboard.InternetStatusText}");
        report.AppendLine($"AdGuard: {Dashboard.AdGuardStatusText}");
        report.AppendLine($"Wi-Fi telemetry: {WifiStatusText}");
        report.AppendLine($"Firmware: {FirmwareCurrentVersion}");
        report.AppendLine($"Last diagnostics: {History.FirstOrDefault(item => item.Action == MaintenanceAction.RunDiagnostics)?.TimestampDisplay ?? RouterPilotStatusPresentation.NotAvailable}");
        report.AppendLine($"Generated: {DateTime.Now:g}");
        report.AppendLine();
        report.AppendLine("This snapshot intentionally omits passwords, tokens, IP/MAC addresses, SSIDs, client identities, DNS history, VPN endpoints and private keys.");
        return report.ToString();
    }

    public string HealthSummary => IsBusy
        ? RouterPilotStatusPresentation.Pending
        : _dashboard.RouterConnected && _dashboard.InternetConnected && _dashboard.IsAdGuardAvailable
            ? "Healthy"
            : "Attention Required";

    public string HealthSummaryDetail => IsBusy
        ? "A maintenance operation is running."
        : HealthSummary == "Healthy"
            ? "All currently monitored services are operational."
            : "One or more monitored services need attention.";

    public string HealthSummaryColour => RouterPilotStatusPresentation.Colour(
        IsBusy
            ? RouterPilotStatus.Pending
            : HealthSummary == "Healthy"
                ? RouterPilotStatus.Active
                : RouterPilotStatus.Error);

    public string WifiStatusText => _dashboard.RouterConnected
        ? RouterPilotStatusPresentation.Active
        : RouterPilotStatusPresentation.NotAvailable;

    public string WifiStatusColour => RouterPilotStatusPresentation.Colour(
        _dashboard.RouterConnected ? RouterPilotStatus.Active : RouterPilotStatus.NotAvailable);

    public async Task ExecuteAsync(MaintenanceActionItem action, Func<Task> refreshAll)
    {
        if (IsBusy || !action.IsAvailable)
            return;

        IsBusy = true;
        ActiveOperation = action.Title;
        UpdateAvailability();

        try
        {
            MaintenanceOperationResult result = await _operations.ExecuteAsync(action.Action, refreshAll);
            LastResult = result.Message;
            UpdateActionHistory();
        }
        finally
        {
            ActiveOperation = string.Empty;
            IsBusy = false;
            OnPropertyChanged(nameof(LastBackupDate));
            OnPropertyChanged(nameof(LastBackupResult));
            OnPropertyChanged(nameof(LastBackupDestination));
            OnPropertyChanged(nameof(LastBackupSize));
            UpdateAvailability();
        }
    }

    public async Task<MaintenanceOperationResult?> CreateBackupAsync(string destinationPath)
    {
        if (IsBusy)
            return null;

        IsBusy = true;
        ActiveOperation = "Create Backup";
        UpdateAvailability();
        try
        {
            MaintenanceOperationResult result = await _operations.CreateBackupAsync(destinationPath);
            LastResult = result.Message;
            return result;
        }
        finally
        {
            ActiveOperation = string.Empty;
            IsBusy = false;
            OnPropertyChanged(nameof(LastBackupDate));
            OnPropertyChanged(nameof(LastBackupResult));
            OnPropertyChanged(nameof(LastBackupDestination));
            OnPropertyChanged(nameof(LastBackupSize));
            UpdateAvailability();
        }
    }

    public Task<BackupInspection> InspectBackupAsync(string archivePath) =>
        _backupRestoreService.InspectAsync(archivePath);

    public async Task<MaintenanceOperationResult?> RestoreBackupAsync(
        BackupInspection inspection,
        IReadOnlyCollection<string> selectedFiles)
    {
        if (IsBusy)
            return null;

        IsBusy = true;
        ActiveOperation = "Restore Backup";
        UpdateAvailability();
        try
        {
            MaintenanceOperationResult result = await _operations.RestoreBackupAsync(inspection, selectedFiles);
            LastResult = result.Message;
            return result;
        }
        finally
        {
            ActiveOperation = string.Empty;
            IsBusy = false;
            UpdateAvailability();
        }
    }

    public async Task CheckFirmwareAsync()
    {
        if (!CanCheckFirmware)
            return;

        await _firmwareUpdateService.CheckManuallyAsync();
        OnFirmwarePropertiesChanged();
    }

    public void AttachDashboard(DashboardViewModel dashboard)
    {
        if (ReferenceEquals(_dashboard, dashboard))
            return;

        _dashboard.PropertyChanged -= Dashboard_PropertyChanged;
        _dashboard = dashboard;
        _dashboard.PropertyChanged += Dashboard_PropertyChanged;
        OnPropertyChanged(nameof(Dashboard));
        OnPropertyChanged(nameof(WifiStatusText));
        OnPropertyChanged(nameof(WifiStatusColour));
        UpdateAvailability();
    }

    private void Dashboard_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DashboardViewModel.RouterConnected) or
            nameof(DashboardViewModel.AdGuardAvailability) or
            nameof(DashboardViewModel.RouterModel) or
            nameof(DashboardViewModel.FirmwareVersion) or
            nameof(DashboardViewModel.Uptime) or
            nameof(DashboardViewModel.DhcpLoaded) or
            nameof(DashboardViewModel.DhcpStatusDisplay))
        {
            OnPropertyChanged(nameof(WifiStatusText));
            OnPropertyChanged(nameof(WifiStatusColour));
            OnPropertyChanged(nameof(HealthSummary));
            OnPropertyChanged(nameof(HealthSummaryDetail));
            OnPropertyChanged(nameof(HealthSummaryColour));
            OnPropertyChanged(nameof(CanCheckFirmware));
            OnPropertyChanged(nameof(RouterIdentityText));
            OnPropertyChanged(nameof(RouterFirmwareText));
            OnPropertyChanged(nameof(RouterUptimeText));
            OnPropertyChanged(nameof(DhcpServiceText));
            OnPropertyChanged(nameof(DhcpLeaseCountText));
            OnPropertyChanged(nameof(DhcpReservationCountText));
            UpdateAvailability();
            if (e.PropertyName is nameof(DashboardViewModel.RouterConnected) or nameof(DashboardViewModel.RouterModel))
            {
                StateChanges = [];
                OnPropertyChanged(nameof(StateSnapshots));
                OnPropertyChanged(nameof(LatestStateSnapshot));
                OnPropertyChanged(nameof(StateChanges));
                OnPropertyChanged(nameof(SnapshotChangeSummary));
            }
        }
    }

    private void RouterSwitch_Switched(object? sender, RouterProfile profile)
    {
        StateChanges = [];
        SnapshotStatus = "Router switched. Select a snapshot for the active profile.";
        OnPropertyChanged(nameof(StateSnapshots)); OnPropertyChanged(nameof(LatestStateSnapshot));
        OnPropertyChanged(nameof(StateChanges)); OnPropertyChanged(nameof(SnapshotStatus)); OnPropertyChanged(nameof(SnapshotChangeSummary));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanManageBackups));
        OnPropertyChanged(nameof(HealthSummary));
        OnPropertyChanged(nameof(HealthSummaryDetail));
        OnPropertyChanged(nameof(HealthSummaryColour));
        UpdateAvailability();
    }

    private void HistoryService_Changed(object? sender, EventArgs e)
    {
        UpdateActionHistory();
        OnPropertyChanged(nameof(LastBackupDate));
        OnPropertyChanged(nameof(LastBackupResult));
        OnPropertyChanged(nameof(LastBackupDestination));
        OnPropertyChanged(nameof(LastBackupSize));
    }

    private void FirmwareUpdateService_PropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        OnFirmwarePropertiesChanged();

    private void OnFirmwarePropertiesChanged()
    {
        OnPropertyChanged(nameof(FirmwareUpdate));
        OnPropertyChanged(nameof(FirmwareCurrentVersion));
        OnPropertyChanged(nameof(FirmwareLatestVersion));
        OnPropertyChanged(nameof(IsFirmwareChecking));
        OnPropertyChanged(nameof(CanCheckFirmware));
        OnPropertyChanged(nameof(FirmwareStatusText));
        OnPropertyChanged(nameof(FirmwareStatusColour));
        OnPropertyChanged(nameof(FirmwareLastChecked));
        OnPropertyChanged(nameof(HasFirmwareReleaseNotes));
        OnPropertyChanged(nameof(FirmwareLink));
    }

    private void UpdateActionHistory()
    {
        foreach (MaintenanceActionItem action in Actions)
        {
            MaintenanceHistoryEntry? latest = History.FirstOrDefault(item => item.Action == action.Action);
            action.LastRun = latest?.TimestampDisplay ?? RouterPilotStatusPresentation.NotAvailable;
            action.LastResult = latest?.OutcomeDisplay ?? RouterPilotStatusPresentation.NotAvailable;
            action.LastResultColour = latest?.OutcomeColour ?? RouterPilotStatusPresentation.Colour(RouterPilotStatus.NotAvailable);
        }
    }

    private static string FormatFileSize(long bytes) => bytes switch
    {
        < 1024 => bytes + " B",
        < 1024 * 1024 => $"{bytes / 1024d:0.0} KB",
        _ => $"{bytes / (1024d * 1024d):0.0} MB"
    };

    private void UpdateAvailability()
    {
        foreach (MaintenanceActionItem action in Actions)
        {
            bool requiresRouter = action.Action is not MaintenanceAction.RefreshAll and not MaintenanceAction.RunDiagnostics and not MaintenanceAction.BackupDiagnostics;
            bool requiresAdGuard = action.Action == MaintenanceAction.RestartAdGuard;
            bool available = !IsBusy &&
                (!requiresRouter || _dashboard.RouterConnected) &&
                (!requiresAdGuard || _dashboard.IsAdGuardAvailable);

            action.IsAvailable = available;
            action.Availability = available
                ? RouterPilotStatusPresentation.Active
                : IsBusy
                    ? RouterPilotStatusPresentation.Pending
                    : requiresAdGuard && !_dashboard.IsAdGuardAvailable
                        ? RouterPilotStatusPresentation.NotAvailable
                        : RouterPilotStatusPresentation.NotAvailable;
            action.AvailabilityReason = available
                ? "Available on the connected router."
                : IsBusy
                    ? "Another maintenance action is running."
                    : requiresAdGuard && !_dashboard.IsAdGuardAvailable
                        ? "AdGuard Home is not available."
                        : "Connect to the router to use this action.";
        }
    }
}

public sealed partial class MaintenanceActionItem : ObservableObject
{
    public MaintenanceActionItem(MaintenanceAction action, string title, string description)
    {
        Action = action;
        Title = title;
        Description = description;
    }

    public MaintenanceAction Action { get; }
    public string Title { get; }
    public string Description { get; }

    [ObservableProperty]
    private bool isAvailable;

    [ObservableProperty]
    private string availability = RouterPilotStatusPresentation.Pending;

    [ObservableProperty]
    private string availabilityReason = "Loading current router status.";

    [ObservableProperty]
    private string lastResult = RouterPilotStatusPresentation.NotAvailable;

    public bool HasLastResult => LastResult != RouterPilotStatusPresentation.NotAvailable;

    partial void OnLastResultChanged(string value) =>
        OnPropertyChanged(nameof(HasLastResult));

    [ObservableProperty]
    private string lastRun = RouterPilotStatusPresentation.NotAvailable;

    [ObservableProperty]
    private string lastResultColour = RouterPilotStatusPresentation.Colour(RouterPilotStatus.NotAvailable);
}

public sealed record DiagnosticCheck(string Title, string State, string Summary, string Source);
