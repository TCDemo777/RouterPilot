namespace RouterPilot.Models;

/// <summary>
/// The single authentication choice used by every AdGuard Home control API
/// operation for one router profile.  The dedicated password only exists in
/// memory after the profile has been decrypted by the settings service.
/// </summary>
public sealed record AdGuardAuthenticationConfiguration(
    bool UseRouterCredentials,
    string Username,
    string Password)
{
    public static AdGuardAuthenticationConfiguration RouterCredentials { get; } =
        new(true, string.Empty, string.Empty);
}

public enum AdGuardConnectionTestResult
{
    Connected,
    AuthenticationFailed,
    Unavailable
}
