using RouterPilot.Configuration;
using RouterPilot.Models;

namespace RouterPilot.Services;

public interface IRouterProfileService
{
    event EventHandler? ActiveProfileChanged;
    IReadOnlyList<RouterProfile> GetProfiles();
    RouterProfile? GetActiveProfile();
    RouterProfile SaveProfile(RouterProfile profile);
    bool RemoveInactiveProfile(string profileId);
    bool SetActiveProfile(string profileId);
}

/// <summary>Owns persisted router profiles; inactive profiles are configuration only.</summary>
public sealed class RouterProfileService : IRouterProfileService
{
    private readonly SettingsService _settingsService;

    public RouterProfileService(SettingsService settingsService) => _settingsService = settingsService;
    public event EventHandler? ActiveProfileChanged;

    public IReadOnlyList<RouterProfile> GetProfiles() =>
        _settingsService.Load().RouterProfiles.Select(profile => profile.Clone()).ToList();

    public RouterProfile? GetActiveProfile()
    {
        AppSettings settings = _settingsService.Load();
        return settings.RouterProfiles.FirstOrDefault(profile => profile.Id == settings.ActiveRouterProfileId)?.Clone();
    }

    public RouterProfile SaveProfile(RouterProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        AppSettings settings = _settingsService.Load();
        profile.Id = string.IsNullOrWhiteSpace(profile.Id) ? Guid.NewGuid().ToString("N") : profile.Id;
        profile.DisplayName = string.IsNullOrWhiteSpace(profile.DisplayName) ? "My Router" : profile.DisplayName.Trim();
        profile.RouterHost = RouterConnectionOptions.NormaliseHost(profile.RouterHost);
        profile.SshPort = profile.SshPort is >= 1 and <= 65535 ? profile.SshPort : 22;
        int index = settings.RouterProfiles.FindIndex(existing => existing.Id == profile.Id);
        if (index < 0)
            settings.RouterProfiles.Add(profile.Clone());
        else
            settings.RouterProfiles[index] = profile.Clone();
        if (string.IsNullOrWhiteSpace(settings.ActiveRouterProfileId))
            settings.ActiveRouterProfileId = profile.Id;
        SettingsService.ApplyActiveProfile(settings);
        _settingsService.Save(settings);
        return profile.Clone();
    }

    public bool RemoveInactiveProfile(string profileId)
    {
        AppSettings settings = _settingsService.Load();
        if (settings.RouterProfiles.Count <= 1 || settings.ActiveRouterProfileId == profileId)
            return false;
        bool removed = settings.RouterProfiles.RemoveAll(profile => profile.Id == profileId) > 0;
        if (removed)
            _settingsService.Save(settings);
        return removed;
    }

    public bool SetActiveProfile(string profileId)
    {
        AppSettings settings = _settingsService.Load();
        if (!settings.RouterProfiles.Any(profile => profile.Id == profileId))
            return false;
        if (settings.ActiveRouterProfileId == profileId)
            return true;
        settings.ActiveRouterProfileId = profileId;
        SettingsService.ApplyActiveProfile(settings);
        _settingsService.Save(settings);
        ActiveProfileChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
