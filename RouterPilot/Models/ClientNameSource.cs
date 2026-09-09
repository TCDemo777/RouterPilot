namespace RouterPilot.Models;

public enum ClientNameSource
{
    Automatic,
    Router,
    AdGuard,
    ConfiguredNames
}

public sealed record ClientNameSourceOption(ClientNameSource Source, string DisplayName);
