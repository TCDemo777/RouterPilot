using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>Deletes RouterPilot-owned data for one offline known device. It performs no router I/O.</summary>
public sealed class KnownDeviceForgetService
{
    private readonly ClientProfileService _profiles;
    private readonly IClientPresenceHistoryService _presenceHistory;
    private readonly ClientInventoryState _inventory;
    private readonly NotificationService _notifications;

    public KnownDeviceForgetService(ClientProfileService profiles, IClientPresenceHistoryService presenceHistory, ClientInventoryState inventory, NotificationService notifications)
    {
        _profiles = profiles;
        _presenceHistory = presenceHistory;
        _inventory = inventory;
        _notifications = notifications;
    }

    public bool IsCurrentlyObserved(string? identity) => _inventory.Snapshot.ContainsKey(ClientIdentity.NormalizeMac(identity));

    public async Task<KnownDeviceForgetResult> ForgetAsync(string? identity)
    {
        string key = ClientIdentity.NormalizeMac(identity);
        if (key.Length != 12)
            return new(false, "This device does not have a persistent MAC identity.");
        if (IsCurrentlyObserved(key))
            return new(false, "This device is currently on the network. Disconnect it before forgetting its saved history.");

        Dictionary<string, ClientProfile> profiles = _profiles.Load();
        if (!_profiles.LastLoadSucceeded)
            return new(false, "RouterPilot could not read the saved device inventory.");
        if (!profiles.Remove(key, out ClientProfile? removed))
            return new(false, "This device is no longer present in RouterPilot's saved inventory.");

        try { _profiles.Save(profiles.Values); }
        catch { return new(false, "RouterPilot could not remove the saved device record."); }

        if (!_presenceHistory.Clear(key))
        {
            try
            {
                profiles[key] = removed;
                _profiles.Save(profiles.Values);
            }
            catch { }
            return new(false, "RouterPilot could not remove this device's availability history.");
        }

        try { await _notifications.RemoveDeviceNotificationsAsync(key); }
        catch
        {
            // Notification history is a non-critical local sink. Profile and
            // monitoring state are already gone, so it cannot cause new alerts.
        }

        ClientRefreshNotifier.NotifyProfileStateChanged();
        return new(true, "Device forgotten. Router configuration was not changed.");
    }

}

public sealed record KnownDeviceForgetResult(bool Success, string Message);
