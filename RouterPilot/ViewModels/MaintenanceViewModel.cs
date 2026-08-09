using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
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
    private DashboardViewModel _dashboard;

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
        FirmwareUpdateService firmwareUpdateService)
    {
        _operations = operations;
        _backupRestoreService = backupRestoreService;
        _historyService = historyService;
        _firmwareUpdateService = firmwareUpdateService;
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

    public string BackupFolder => _backupRestoreService.BackupFolder;

    public bool CanManageBackups => !IsBusy;

    public FirmwareUpdateCheck FirmwareUpdate => _firmwareUpdateService.Current;
    public string FirmwareCurrentVersion => string.IsNullOrWhiteSpace(FirmwareUpdate.CurrentVersion)
        ? RouterPilotStatusPresentation.NotAvailable
        : FirmwareUpdate.CurrentVersion;
    public string FirmwareLatestVersion => string.IsNullOrWhiteSpace(FirmwareUpdate.LatestVersion)
        ? RouterPilotStatusPresentation.NotAvailable
        : FirmwareUpdate.LatestVersion;
    public bool IsFirmwareChecking => _firmwareUpdateService.IsChecking;
    public bool CanCheckFirmware => !IsFirmwareChecking && _dashboard.RouterConnected;
    public string FirmwareStatusText => IsFirmwareChecking
        ? "Pending"
        : FirmwareUpdate.Status switch
        {
            FirmwareUpdateCheckStatus.UpToDate => "Up to date",
            FirmwareUpdateCheckStatus.UpdateAvailable => "Update available",
            FirmwareUpdateCheckStatus.Error => "Error",
            _ => RouterPilotStatusPresentation.NotAvailable
        };
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
            nameof(DashboardViewModel.AdGuardAvailability))
        {
            OnPropertyChanged(nameof(WifiStatusText));
            OnPropertyChanged(nameof(WifiStatusColour));
            OnPropertyChanged(nameof(HealthSummary));
            OnPropertyChanged(nameof(HealthSummaryDetail));
            OnPropertyChanged(nameof(HealthSummaryColour));
            OnPropertyChanged(nameof(CanCheckFirmware));
            UpdateAvailability();
        }
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
