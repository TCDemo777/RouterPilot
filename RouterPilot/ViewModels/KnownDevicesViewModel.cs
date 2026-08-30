using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using RouterPilot.Models;
using RouterPilot.Services;

namespace RouterPilot.ViewModels;

public partial class KnownDevicesViewModel : ObservableObject, IDisposable
{
    private readonly ClientProfileService _profiles = new();
    private readonly ClientInventoryState _inventory;
    private readonly ClientsViewModel _clients;
    private readonly DispatcherTimer _relativeTimeTimer;
    private Dictionary<string, ClientProfile> _profileMap = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public ObservableCollection<KnownDeviceInfo> Devices { get; } = new();
    public IReadOnlyList<string> Filters { get; } = ["All", "Online", "Offline", "Needs Review", "Favourites", "Monitored"];
    public IReadOnlyList<string> SortOptions { get; } = ["Last observed", "Name", "Status", "First observed", "Category"];

    [ObservableProperty] private string searchText = string.Empty;
    [ObservableProperty] private string selectedFilter = "All";
    [ObservableProperty] private string selectedSort = "Last observed";
    [ObservableProperty] private KnownDeviceInfo? selectedDevice;

    public int TotalCount => _profileMap.Count;
    public int OnlineCount => _profileMap.Keys.Count(key => _inventory.Snapshot.ContainsKey(key));
    public int OfflineCount => TotalCount - OnlineCount;
    public int NeedsReviewCount => _profileMap.Values.Count(profile => profile.NeedsReview);
    public int MonitoredCount => _profileMap.Values.Count(profile => profile.MonitorAvailability);
    public bool HasDevices => TotalCount > 0;

    public ClientInfo? SelectedClient => _clients.SelectedClient;
    public ObservableCollection<ClientActivityItem> SelectedClientActivity => _clients.SelectedClientActivity;
    public bool HasSelectedClientActivity => _clients.HasSelectedClientActivity;
    public bool ShowNoSelectedClientActivity => !HasSelectedClientActivity;

    public KnownDevicesViewModel(ClientInventoryState inventory, ClientsViewModel clients)
    {
        _inventory = inventory;
        _clients = clients;
        _inventory.Changed += Inventory_Changed;
        _clients.PropertyChanged += Clients_PropertyChanged;
        _clients.SelectedClientActivity.CollectionChanged += SelectedClientActivity_CollectionChanged;
        ClientRefreshNotifier.ProfileStateChanged += ProfileStateChanged;
        _relativeTimeTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _relativeTimeTimer.Tick += RelativeTimeTimer_Tick;
        ReloadProfiles();
    }

    public async Task StartAsync()
    {
        if (_disposed) return;

        // Known Devices is a projection of the shared authoritative inventory.
        // Ensure that inventory is initialized when this page is opened directly;
        // this reuses the existing session-scoped discovery path and never adds
        // a second polling loop.
        await _clients.LoadClientsAsync();
        if (_disposed) return;
        RefreshRelativeTimePresentation();
        if (!_relativeTimeTimer.IsEnabled)
            _relativeTimeTimer.Start();
    }

    public void Stop() => _relativeTimeTimer.Stop();

    public void ReloadProfiles()
    {
        if (_disposed) return;
        Dictionary<string, ClientProfile> loaded = _profiles.Load();
        if (!_profiles.LastLoadSucceeded) return;
        _profileMap = loaded.Where(pair => ClientIdentity.NormalizeHexMac(pair.Key).Length == 12)
            .ToDictionary(pair => ClientIdentity.NormalizeHexMac(pair.Key), pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        Rebuild();
    }

    partial void OnSearchTextChanged(string value) => Rebuild();
    partial void OnSelectedFilterChanged(string value) => Rebuild();
    partial void OnSelectedSortChanged(string value) => Rebuild();
    partial void OnSelectedDeviceChanged(KnownDeviceInfo? value) => _clients.SelectedClient = value?.ToClientInfo();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _relativeTimeTimer.Stop();
        _relativeTimeTimer.Tick -= RelativeTimeTimer_Tick;
        _inventory.Changed -= Inventory_Changed;
        _clients.PropertyChanged -= Clients_PropertyChanged;
        _clients.SelectedClientActivity.CollectionChanged -= SelectedClientActivity_CollectionChanged;
        ClientRefreshNotifier.ProfileStateChanged -= ProfileStateChanged;
    }

    private void Inventory_Changed(object? sender, EventArgs e) => Rebuild();
    private void Clients_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ClientsViewModel.SelectedClient) or nameof(ClientsViewModel.HasSelectedClientActivity))
        {
            OnPropertyChanged(nameof(SelectedClient));
            OnPropertyChanged(nameof(HasSelectedClientActivity));
            OnPropertyChanged(nameof(ShowNoSelectedClientActivity));
        }
    }

    private void SelectedClientActivity_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasSelectedClientActivity));
        OnPropertyChanged(nameof(ShowNoSelectedClientActivity));
    }
    private void ProfileStateChanged(object? sender, EventArgs e) => ReloadProfiles();

    private void RelativeTimeTimer_Tick(object? sender, EventArgs e)
    {
        if (_disposed) return;

        RefreshRelativeTimePresentation();
    }

    private void RefreshRelativeTimePresentation()
    {
        foreach (KnownDeviceInfo device in Devices)
        {
            device.RefreshLastObservedPresentation();
        }
    }

    private void Rebuild()
    {
        if (_disposed) return;
        string? selectedMac = SelectedDevice?.MacKey;
        IEnumerable<KnownDeviceInfo> query = _profileMap.Select(pair => new KnownDeviceInfo
        {
            Profile = pair.Value,
            CurrentClient = _inventory.Snapshot.TryGetValue(pair.Key, out ClientInfo? client) ? client : null
        });
        string text = SearchText.Trim();
        if (text.Length > 0) query = query.Where(device => Matches(device, text));
        query = SelectedFilter switch
        {
            "Online" => query.Where(device => device.IsOnline),
            "Offline" => query.Where(device => !device.IsOnline),
            "Needs Review" => query.Where(device => device.NeedsReview),
            "Favourites" => query.Where(device => device.IsFavourite),
            "Monitored" => query.Where(device => device.IsMonitored),
            _ => query
        };
        query = SelectedSort switch
        {
            "Name" => query.OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase),
            "Status" => query.OrderByDescending(device => device.IsOnline).ThenBy(device => device.Name, StringComparer.OrdinalIgnoreCase),
            "First observed" => query.OrderByDescending(device => device.Profile.FirstSeenUtc),
            "Category" => query.OrderBy(device => device.Category, StringComparer.OrdinalIgnoreCase).ThenBy(device => device.Name, StringComparer.OrdinalIgnoreCase),
            _ => query.OrderByDescending(device => device.IsOnline).ThenByDescending(device => device.Profile.LastSeenUtc)
        };
        Devices.Clear();
        foreach (KnownDeviceInfo device in query) Devices.Add(device);
        SelectedDevice = selectedMac is null ? null : Devices.FirstOrDefault(device => device.MacKey == selectedMac);
        if (SelectedDevice is not null)
            _clients.SelectedClient = SelectedDevice.ToClientInfo();
        OnPropertyChanged(nameof(TotalCount)); OnPropertyChanged(nameof(OnlineCount)); OnPropertyChanged(nameof(OfflineCount));
        OnPropertyChanged(nameof(NeedsReviewCount)); OnPropertyChanged(nameof(MonitoredCount)); OnPropertyChanged(nameof(HasDevices));
    }

    private static bool Matches(KnownDeviceInfo device, string text)
    {
        string mac = ClientIdentity.NormalizeHexMac(text);
        string terms = $"{device.Name} {device.Profile.LastKnownName} {device.Profile.LastKnownIpAddress} {device.MacKey} {device.Category}";
        return terms.Contains(text, StringComparison.OrdinalIgnoreCase) || (mac.Length >= 2 && device.MacKey.Contains(mac, StringComparison.OrdinalIgnoreCase));
    }
}
