using System.ComponentModel;

namespace RouterPilot.Models;

public sealed class KnownDeviceInfo : INotifyPropertyChanged
{
    public required ClientProfile Profile { get; init; }
    public ClientInfo? CurrentClient { get; init; }
    public string MacKey => ClientIdentity.NormalizeHexMac(Profile.Key);
    public bool IsOnline => CurrentClient is not null;
    public string Name => !string.IsNullOrWhiteSpace(Profile.Nickname) ? Profile.Nickname :
        !string.IsNullOrWhiteSpace(CurrentClient?.Name) ? CurrentClient.Name :
        !string.IsNullOrWhiteSpace(Profile.LastKnownName) ? Profile.LastKnownName : FormatMac(MacKey);
    public string Secondary => IsOnline && Useful(CurrentClient!.IpAddress) ? CurrentClient.IpAddress :
        Useful(Profile.LastKnownIpAddress) ? $"Last known IP: {Profile.LastKnownIpAddress}" : string.Empty;
    public string Status => IsOnline ? "Online" : "Not currently observed";
    public string Category => !string.IsNullOrWhiteSpace(Profile.Category) ? Profile.Category :
        !string.IsNullOrWhiteSpace(CurrentClient?.DeviceType) ? CurrentClient.DeviceType : "Unknown";
    public string LastObserved => FormatObserved(Profile.LastSeenUtc);
    public bool IsFavourite => Profile.IsFavorite;
    public bool IsMonitored => Profile.MonitorAvailability;
    public bool NeedsReview => Profile.NeedsReview;
    public string DeviceIcon => CurrentClient?.DeviceIcon ?? "\u25CF";
    public string FavoriteGlyph => IsFavourite ? "\u2605" : "\u2606";
    public string HealthText => NeedsReview ? "Needs Review" : IsOnline ? "Online" : "Offline";
    public string HealthColour => NeedsReview ? "#D97706" : IsOnline ? "#16A34A" : "#687386";
    public ClientInfo ToClientInfo() => CurrentClient ?? new ClientInfo
    {
        Name = Name, RouterName = Profile.LastKnownName, MacAddress = FormatMac(MacKey),
        IpAddress = Useful(Profile.LastKnownIpAddress) ? Profile.LastKnownIpAddress : "-",
        Notes = Profile.Notes, CustomCategory = Profile.Category, DeviceType = Category,
        IsFavorite = Profile.IsFavorite, MonitorAvailability = Profile.MonitorAvailability,
        NeedsReview = Profile.NeedsReview, FirstSeenUtc = Profile.FirstSeenUtc,
        LastObservedUtc = Profile.LastSeenUtc, HealthText = "Offline", HealthColour = "#687386"
    };

    /// <summary>Refreshes only the wall-clock-derived relative observation text.</summary>
    public void RefreshLastObservedPresentation() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastObserved)));

    public event PropertyChangedEventHandler? PropertyChanged;

    private static bool Useful(string? value) => !string.IsNullOrWhiteSpace(value) && value != "-";
    private static string FormatMac(string key) => key.Length == 12 ? string.Join(":", Enumerable.Range(0, 6).Select(i => key.Substring(i * 2, 2))) : key;
    private static string FormatObserved(DateTime value)
    {
        if (value == default) return "—";
        TimeSpan age = DateTime.UtcNow - value;
        if (age < TimeSpan.FromMinutes(1)) return "Now";
        if (age < TimeSpan.FromHours(1)) return $"{Math.Max(1, (int)age.TotalMinutes)} min ago";
        if (age < TimeSpan.FromDays(1)) return $"{Math.Max(1, (int)age.TotalHours)}h ago";
        return value.ToLocalTime().Date == DateTime.Today.AddDays(-1) ? $"Yesterday {value.ToLocalTime():t}" : value.ToLocalTime().ToString("d");
    }
}
