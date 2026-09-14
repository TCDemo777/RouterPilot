namespace RouterPilot.Models;

/// <summary>
/// Defines the explicit acknowledgement required before dedicated AdGuard Home
/// credentials are sent over an unencrypted local HTTP connection.
/// </summary>
public static class AdGuardCredentialTransportPolicy
{
    public static bool RequiresHttpAcknowledgement(
        bool useRouterCredentials,
        bool useHttps) =>
        !useRouterCredentials && !useHttps;

    public static bool IsAcknowledgementValid(
        bool acknowledged,
        bool useRouterCredentials,
        bool useHttps) =>
        !RequiresHttpAcknowledgement(useRouterCredentials, useHttps) || acknowledged;
}
