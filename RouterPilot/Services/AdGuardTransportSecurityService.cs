using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using RouterPilot.Models;

namespace RouterPilot.Services;

public sealed partial class AdGuardTransportSecurityService : ObservableObject
{
    [ObservableProperty]
    private AdGuardTransportSecurityStatus status =
        AdGuardTransportSecurityStatus.Unavailable;

    [ObservableProperty]
    private string detail = "AdGuard Home transport is unavailable.";

    public void MarkAvailable(Uri endpoint)
    {
        bool isHttps = string.Equals(
            endpoint.Scheme,
            Uri.UriSchemeHttps,
            StringComparison.OrdinalIgnoreCase);

        SetStatus(
            isHttps
                ? AdGuardTransportSecurityStatus.Secure
                : AdGuardTransportSecurityStatus.Unencrypted,
            isHttps
                ? "AdGuard Home control traffic is protected by HTTPS."
                : "AdGuard Home control traffic is unencrypted on the local network.");
    }

    public void MarkUnavailable(string message)
    {
        SetStatus(AdGuardTransportSecurityStatus.Unavailable, message);
    }

    private void SetStatus(
        AdGuardTransportSecurityStatus value,
        string message)
    {
        void Apply()
        {
            Status = value;
            Detail = message;
        }

        if (Application.Current?.Dispatcher is { } dispatcher &&
            !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(Apply);
            return;
        }

        Apply();
    }
}
