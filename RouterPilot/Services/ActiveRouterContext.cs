using RouterPilot.Models;

namespace RouterPilot.Services;

public interface IActiveRouterContext
{
    RouterProfile CurrentProfile { get; }
    string CurrentProfileId { get; }
    long Version { get; }
}

/// <summary>
/// The single authoritative router configuration boundary. v2.1 exposes one
/// active profile only; it deliberately performs no connection or switching.
/// </summary>
public sealed class ActiveRouterContext : IActiveRouterContext
{
    private readonly IRouterProfileService _profiles;

    public ActiveRouterContext(IRouterProfileService profiles)
    {
        _profiles = profiles;
        _profiles.ActiveProfileChanged += (_, _) => Interlocked.Increment(ref _version);
    }

    private long _version;
    public RouterProfile CurrentProfile => _profiles.GetActiveProfile()
        ?? throw new InvalidOperationException("No active router profile is configured.");
    public string CurrentProfileId => CurrentProfile.Id;
    public long Version => Interlocked.Read(ref _version);
}
