using System.ComponentModel;
using System.Net;
using RouterPilot.Presentation;

namespace RouterPilot.Models;

public sealed class KnownDeviceInfo : INotifyPropertyChanged
{
    public required ClientProfile Profile { get; init; }
    public ClientInfo? CurrentClient { get; init; }
    public string MacKey => ClientIdentity.NormalizeHexMac(Profile.Key);
    public bool IsOnline => CurrentClient is not null;
    public string Name => FirstFriendlyName();
    public string Secondary => IsOnline && Useful(CurrentClient!.IpAddress) ? CurrentClient.IpAddress :
        Useful(Profile.LastKnownIpAddress) ? $"Last known IP: {Profile.LastKnownIpAddress}" : string.Empty;
    public string IpAddress => IsOnline && Useful(CurrentClient!.IpAddress) ? CurrentClient.IpAddress :
        Useful(Profile.LastKnownIpAddress) ? Profile.LastKnownIpAddress : RouterPilotStatusPresentation.NotAvailable;
    public string MacAddress => IsOnline && Useful(CurrentClient!.MacAddress) ? CurrentClient.MacAddress : FormatMac(MacKey);
    public string Status => IsOnline ? "Online" : "Not currently observed";
    public string Category => !string.IsNullOrWhiteSpace(Profile.Category) ? Profile.Category :
        !string.IsNullOrWhiteSpace(CurrentClient?.DeviceType) ? CurrentClient.DeviceType : "Unknown";
    public string DeviceType => CurrentClient?.DeviceType ?? (Useful(Profile.Category) ? Profile.Category : "Unknown device");
    public string Manufacturer => CurrentClient?.Manufacturer ?? "Unknown manufacturer";
    public string ConnectionSummary => CurrentClient?.ConnectionSummary ?? Profile.LastKnownConnectionSummary;
    public bool HasConnectionSummary => !string.IsNullOrWhiteSpace(ConnectionSummary);
    public string SignalSummary => CurrentClient?.SignalSummary ?? string.Empty;
    public bool HasSignalSummary => !string.IsNullOrWhiteSpace(SignalSummary);
    public string TotalQueriesDisplay => CurrentClient?.TotalQueriesDisplay ?? RouterPilotStatusPresentation.NotAvailable;
    public string BlockedQueriesDisplay => CurrentClient?.BlockedQueriesDisplay ?? RouterPilotStatusPresentation.NotAvailable;
    public string BlockRateDisplay => CurrentClient?.BlockRateDisplay ?? RouterPilotStatusPresentation.NotAvailable;
    public AdGuardAvailabilityState AdGuardDataAvailability => CurrentClient?.AdGuardDataAvailability ?? AdGuardAvailabilityState.Unavailable;
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
        Name = Name, RouterName = SafePersistedName(), MacAddress = FormatMac(MacKey),
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
    private string FirstFriendlyName()
    {
        if (Useful(Profile.Nickname)) return Profile.Nickname;
        ClientInfo? current = CurrentClient;
        if (current is not null && Useful(current.Name) && !IsGeneratedIpLabel(current.Name, current.IpAddress)) return current.Name;
        if (current is not null && Useful(current.RouterName) && !IsGeneratedIpLabel(current.RouterName, current.IpAddress)) return current.RouterName;
        if (Useful(Profile.LastKnownName) && !IsGeneratedIpLabel(Profile.LastKnownName, Profile.LastKnownIpAddress)) return Profile.LastKnownName;
        return "Unknown device";
    }

    private string SafePersistedName() =>
        Useful(Profile.LastKnownName) && !IsGeneratedIpLabel(Profile.LastKnownName, Profile.LastKnownIpAddress)
            ? Profile.LastKnownName
            : string.Empty;

    // Older router inventories occasionally supplied the canonical IPv4 identity
    // (dots removed) as a hostname.  Only reject it when it exactly matches the
    // known address, preserving legitimate numeric user nicknames.
    private static bool IsGeneratedIpLabel(string? name, string? ip)
    {
        if (name is null || !Useful(name))
            return false;
        string normalizedIp = ClientIdentity.NormalizeEndpoint(ip);
        if (!IPAddress.TryParse(normalizedIp, out IPAddress? address) || address is null || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;
        string digits = string.Concat(address.GetAddressBytes().Select(b => b.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return string.Equals(name.Trim(), digits, StringComparison.Ordinal);
    }
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
