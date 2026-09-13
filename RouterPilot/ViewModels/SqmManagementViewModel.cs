using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RouterPilot.Models;
using RouterPilot.Services;

namespace RouterPilot.ViewModels;

public partial class SqmManagementViewModel : ObservableObject
{
    private readonly ISqmManagementService _service;
    private SqmConfiguration? _authoritative;
    private CancellationTokenSource? _operation;
    [ObservableProperty] private bool isNative;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool enable;
    [ObservableProperty] private string upload = string.Empty;
    [ObservableProperty] private string download = string.Empty;
    [ObservableProperty] private string qdisc = "cake";
    [ObservableProperty] private string statusMessage = "Select Refresh to load SQM configuration.";

    public IReadOnlyList<string> QueueRules { get; } = ["cake", "fq_codel"];
    public bool IsReadOnly => !IsNative;
    public bool IsDirty => _authoritative is not null && Draft() != _authoritative;
    public bool CanApply => IsNative && !IsBusy && IsDirty && Draft().IsValid;
    public string ValidationMessage => Draft().IsValid
        ? string.Empty
        : "Enter whole-number upload and download values from 1 to 10000 Mbps, and select cake or fq_codel.";
    public string UploadEquivalent => TryMbps(Upload, out int value) ? $"{value / 8d:0.###} MB/s equivalent" : "—";
    public string DownloadEquivalent => TryMbps(Download, out int value) ? $"{value / 8d:0.###} MB/s equivalent" : "—";

    public SqmManagementViewModel(ISqmManagementService service) => _service = service;
    partial void OnEnableChanged(bool value) => NotifyDraftChanged();
    partial void OnUploadChanged(string value) => NotifyDraftChanged();
    partial void OnDownloadChanged(string value) => NotifyDraftChanged();
    partial void OnQdiscChanged(string value) => NotifyDraftChanged();
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(CanApply)); ApplyCommand.NotifyCanExecuteChanged(); }
    partial void OnIsNativeChanged(bool value) { OnPropertyChanged(nameof(IsReadOnly)); OnPropertyChanged(nameof(CanApply)); }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true; StatusMessage = "Loading SQM configuration…";
        try
        {
            SqmReadResult result = await _service.LoadAsync();
            bool retainDraft = IsDirty;
            IsNative = result.Capability == SqmCapabilityState.Native;
            _authoritative = result.Configuration;
            if (_authoritative is not null && !retainDraft)
            {
                Enable = _authoritative.Enable;
                Download = _authoritative.DownloadMbps.ToString();
                Upload = _authoritative.UploadMbps.ToString();
                Qdisc = _authoritative.Qdisc;
                StatusMessage = result.Message;
            }
            else if (retainDraft && _authoritative is not null)
            {
                StatusMessage = "Router SQM state refreshed; staged changes were retained.";
            }
            else
            {
                StatusMessage = result.Message;
            }
        }
        finally { IsBusy = false; NotifyDraftChanged(); }
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        SqmConfiguration requested = Draft();
        IsBusy = true; StatusMessage = "Applying SQM settings…";
        _operation?.Cancel(); _operation?.Dispose(); _operation = new CancellationTokenSource();
        SqmApplyResult result = await _service.ApplyAsync(requested, _operation.Token);
        StatusMessage = result.Message;
        if (result.Success) { _authoritative = requested; }
        IsBusy = false; NotifyDraftChanged();
    }

    [RelayCommand]
    private void Cancel()
    {
        if (_authoritative is null) return;
        Enable = _authoritative.Enable; Download = _authoritative.DownloadMbps.ToString(); Upload = _authoritative.UploadMbps.ToString(); Qdisc = _authoritative.Qdisc;
        StatusMessage = "Staged changes discarded."; NotifyDraftChanged();
    }

    private SqmConfiguration Draft() => new(Enable, Parse(Download), Parse(Upload), Qdisc);
    private static int Parse(string value) => int.TryParse(value, out int parsed) ? parsed : 0;
    private static bool TryMbps(string text, out int value) => int.TryParse(text, out value) && value is >= 1 and <= 10000;
    private void NotifyDraftChanged() { OnPropertyChanged(nameof(IsDirty)); OnPropertyChanged(nameof(CanApply)); OnPropertyChanged(nameof(ValidationMessage)); OnPropertyChanged(nameof(UploadEquivalent)); OnPropertyChanged(nameof(DownloadEquivalent)); ApplyCommand.NotifyCanExecuteChanged(); }
}
