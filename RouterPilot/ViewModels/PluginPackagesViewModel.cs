using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RouterPilot.Models;
using RouterPilot.Services;

namespace RouterPilot.ViewModels;

public partial class PluginPackagesViewModel : ObservableObject
{
    private readonly IPluginPackageService _service; private readonly IPluginPackageMutationService _mutation; private readonly IRouterProfileService _profiles; private CancellationTokenSource? _loadCancellation; private long _generation;
    public ObservableCollection<PluginPackage> Packages { get; } = new(); public ICollectionView PackagesView { get; }
    public IReadOnlyList<string> FilterOptions { get; } = ["All", "Installed", "Available", "Updates"];
    [ObservableProperty] private string searchText = string.Empty; [ObservableProperty] private string selectedFilter = "All"; [ObservableProperty] private PluginPackage? selectedPackage; [ObservableProperty] private bool isLoading; [ObservableProperty] private bool isOperating; [ObservableProperty] private string statusMessage = "Plug-ins have not been loaded."; [ObservableProperty] private string indexStatus = "Unknown"; [ObservableProperty] private string indexFreshness = "Freshness unknown"; [ObservableProperty] private int installedCount; [ObservableProperty] private int availableCount; [ObservableProperty] private string updatesCount = "—";
    public string EmptyMessage => IsLoading ? "Loading plug-ins…" : Packages.Count == 0 ? "Package information is unavailable." : "No packages match your search.";
    public bool CanInstall => !IsOperating && SelectedPackage?.CanInstall == true;
    public bool CanRemove => !IsOperating && SelectedPackage?.CanRemove == true;
    public bool CanUpdate => !IsOperating && SelectedPackage?.CanUpdate == true;
    public bool CanRefreshIndexes => !IsOperating;
    public PluginPackagesViewModel(IPluginPackageService service, IPluginPackageMutationService mutation, IRouterProfileService profiles) { _service = service; _mutation = mutation; _profiles = profiles; _profiles.ActiveProfileChanged += (_, _) => _ = RefreshAsync(); PackagesView = CollectionViewSource.GetDefaultView(Packages); PackagesView.Filter = FilterPackage; }
    [RelayCommand] public async Task RefreshAsync()
    { long generation = Interlocked.Increment(ref _generation); string? selectedName = SelectedPackage?.Name; _loadCancellation?.Cancel(); _loadCancellation?.Dispose(); _loadCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60)); IsLoading = true; StatusMessage = "Loading read-only plug-in inventory…"; try { PluginInventorySnapshot snapshot = await _service.LoadAsync(_loadCancellation.Token).ConfigureAwait(true); if (generation != Interlocked.Read(ref _generation)) return; Packages.Clear(); foreach (PluginPackage package in snapshot.Packages) Packages.Add(package); SelectedPackage = selectedName is null ? null : Packages.FirstOrDefault(package => string.Equals(package.Name, selectedName, StringComparison.OrdinalIgnoreCase)); InstalledCount = snapshot.InstalledCount; AvailableCount = snapshot.AvailableCount; UpdatesCount = snapshot.UpgradableCount?.ToString("N0") ?? "—"; IndexStatus = snapshot.InventoryStatus switch { PluginInventoryAvailability.Available => "Available", PluginInventoryAvailability.Stale => "Stale", PluginInventoryAvailability.Unavailable => "Unavailable", _ => "Unknown" }; IndexFreshness = snapshot.IndexFreshness == PluginInventoryFreshness.Known ? "Freshness known" : "Freshness unknown"; StatusMessage = snapshot.InstalledError ?? snapshot.AvailableError ?? snapshot.UpgradableError ?? $"Read {Packages.Count:N0} packages from the router."; PackagesView.Refresh(); } catch (OperationCanceledException) when (_loadCancellation?.IsCancellationRequested == true) { StatusMessage = "Plug-in inventory refresh cancelled."; } catch (Exception ex) { StatusMessage = OperationFailurePolicy.UserMessage(ex, "Plug-in inventory", "Plug-in information is currently unavailable."); IndexStatus = "Unavailable"; } finally { IsLoading = false; OnPropertyChanged(nameof(EmptyMessage)); } }
    public async Task InstallSelectedAsync() => await MutateAsync("install", "Installing…", "Installed successfully.");
    public async Task RemoveSelectedAsync() => await MutateAsync("remove", "Uninstalling…", "Uninstalled successfully.");
    public async Task UpdateSelectedAsync() => await MutateAsync("update-package", "Updating…", "Updated successfully.");
    public async Task RefreshIndexesAsync() => await MutateAsync("update-indexes", "Refreshing package indexes…", "Package indexes refreshed.");
    private async Task MutateAsync(string operation, string working, string success)
    {
        PluginPackage? selected = SelectedPackage;
        if (operation != "update-indexes" && selected is null) return;
        if (operation == "install" && !CanInstall || operation == "remove" && !CanRemove || operation == "update-package" && !CanUpdate || operation == "update-indexes" && !CanRefreshIndexes) return;
        IsOperating = true; StatusMessage = working; NotifyActionState();
        try { await _mutation.ExecuteAsync(operation, selected?.Name ?? string.Empty); StatusMessage = success; await RefreshAsync(); }
        catch (Exception ex) { StatusMessage = "Verifying package state…"; await RefreshAsync(); StatusMessage = OperationFailurePolicy.UserMessage(ex, "Plug-in operation", "The package operation could not be completed; the refreshed package state is authoritative."); }
        finally { IsOperating = false; NotifyActionState(); }
    }
    partial void OnSelectedPackageChanged(PluginPackage? value) => NotifyActionState();
    partial void OnIsOperatingChanged(bool value) => NotifyActionState();
    partial void OnSearchTextChanged(string value) => PackagesView.Refresh(); partial void OnSelectedFilterChanged(string value) => PackagesView.Refresh();
    private void NotifyActionState() { OnPropertyChanged(nameof(CanInstall)); OnPropertyChanged(nameof(CanRemove)); OnPropertyChanged(nameof(CanUpdate)); OnPropertyChanged(nameof(CanRefreshIndexes)); }
    private bool FilterPackage(object item) => item is PluginPackage p && (string.IsNullOrWhiteSpace(SearchText) || p.Name.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase) || p.Description.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase)) && (SelectedFilter switch { "Installed" => p.IsInstalled, "Available" => p.IsAvailable, "Updates" => p.IsUpgradable, _ => true });
}
