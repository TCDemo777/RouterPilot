using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
using RouterPilot.Models;
using RouterPilot.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RouterPilot.ViewModels;

public partial class NotificationCentreViewModel : ObservableObject
{
    private readonly NotificationService _notificationService;
    private readonly CollectionViewSource _notificationViewSource;

    public NotificationCentreViewModel(NotificationService notificationService)
    {
        _notificationService = notificationService;
        Notifications = notificationService.Notifications;
        _notificationViewSource = new CollectionViewSource
        {
            Source = Notifications
        };
        NotificationsView = _notificationViewSource.View;
        NotificationsView.Filter = MatchesFilter;
        ((INotifyCollectionChanged)Notifications).CollectionChanged += Notifications_CollectionChanged;
        _notificationService.PropertyChanged += NotificationService_PropertyChanged;
    }

    public ReadOnlyObservableCollection<AppNotification> Notifications { get; }

    public ICollectionView NotificationsView { get; }

    public string[] Filters { get; } =
        { "All", "Attention", "Unread", "Information", "Warning", "Error" };

    public int UnreadCount => _notificationService.UnreadCount;

    public bool HasNotifications => Notifications.Count > 0;

    public int AttentionCount => Notifications.Count(notification =>
        !notification.IsRead && notification.Severity is NotificationSeverity.Warning or NotificationSeverity.Error);

    public string AttentionSummary => AttentionCount switch
    {
        0 when HasNotifications => "No unread items need attention",
        0 => "No activity has been recorded yet",
        1 => "1 unread item needs attention",
        _ => $"{AttentionCount} unread items need attention"
    };

    public string RecentActivitySummary => Notifications.Count == 0
        ? "RouterPilot will show meaningful router, device and maintenance events here."
        : $"Latest event: {Notifications.OrderByDescending(notification => notification.Timestamp).First().TimestampDisplay}";

    public string FilterEmptyMessage => HasNotifications
        ? "No notifications match this filter. Try All to see the complete history."
        : "RouterPilot has not recorded any meaningful notifications yet.";

    [ObservableProperty]
    private string selectedFilter = "All";

    partial void OnSelectedFilterChanged(string value) => RefreshPresentation();

    [RelayCommand]
    private Task MarkAllReadAsync() => _notificationService.MarkAllReadAsync();

    [RelayCommand]
    private Task ClearAllAsync() => _notificationService.ClearAllAsync();

    [RelayCommand]
    private Task MarkReadAsync(AppNotification? notification) =>
        _notificationService.MarkReadAsync(notification);

    [RelayCommand]
    private Task RemoveAsync(AppNotification? notification) =>
        _notificationService.RemoveAsync(notification);

    private bool MatchesFilter(object item)
    {
        if (item is not AppNotification notification)
            return false;

        return SelectedFilter switch
        {
            "Unread" => !notification.IsRead,
            "Attention" => notification.Severity is NotificationSeverity.Warning or NotificationSeverity.Error,
            "Information" => notification.Severity == NotificationSeverity.Information,
            "Warning" => notification.Severity == NotificationSeverity.Warning,
            "Error" => notification.Severity == NotificationSeverity.Error,
            _ => true
        };
    }

    private void NotificationService_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NotificationService.UnreadCount))
        {
            RefreshPresentation();
        }
    }

    private void Notifications_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        RefreshPresentation();

    private void RefreshPresentation()
    {
        OnPropertyChanged(nameof(UnreadCount));
        OnPropertyChanged(nameof(HasNotifications));
        OnPropertyChanged(nameof(AttentionCount));
        OnPropertyChanged(nameof(AttentionSummary));
        OnPropertyChanged(nameof(RecentActivitySummary));
        OnPropertyChanged(nameof(FilterEmptyMessage));
        NotificationsView.Refresh();
    }
}
