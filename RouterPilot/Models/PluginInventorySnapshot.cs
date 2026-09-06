namespace RouterPilot.Models;

public enum PluginInventoryAvailability { Unknown, Available, Unavailable, Stale }

public sealed record PluginInventorySnapshot(
    IReadOnlyList<PluginPackage> Packages,
    int InstalledCount,
    int AvailableCount,
    int? UpgradableCount,
    PluginInventoryAvailability IndexStatus,
    string? InstalledError,
    string? AvailableError,
    string? UpgradableError,
    DateTimeOffset RetrievedAt);
