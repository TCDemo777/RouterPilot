namespace RouterPilot.Models;

public enum PluginInventoryAvailability { Unknown, Available, Unavailable, Stale }
public enum PluginInventoryFreshness { Unknown, Known }

public sealed record PluginInventorySnapshot(
    IReadOnlyList<PluginPackage> Packages,
    int InstalledCount,
    int AvailableCount,
    int? UpgradableCount,
    PluginInventoryAvailability InventoryStatus,
    PluginInventoryFreshness IndexFreshness,
    string? InstalledError,
    string? AvailableError,
    string? UpgradableError,
    DateTimeOffset RetrievedAt);
