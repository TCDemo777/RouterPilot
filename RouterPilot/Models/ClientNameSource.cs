namespace RouterPilot.Models;

public enum ClientNameSource
{
    Automatic,
    Router,
    AdGuard
}

public sealed record ClientNameSourceOption(ClientNameSource Source, string DisplayName);
