namespace RouterPilot.Models;

public enum PluginMutationSafety { Allowed, BlockedSystem, BlockedDependencyRisk, Unknown }

public sealed class PluginPackage
{
    public string Name { get; init; } = string.Empty;
    public string InstalledVersion { get; init; } = string.Empty;
    public string AvailableVersion { get; init; } = string.Empty;
    public string Architecture { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Dependencies { get; init; } = string.Empty;
    public string InstalledTime { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public bool IsInstalled { get; init; }
    public bool IsAvailable { get; init; }
    public bool IsUpgradable { get; init; }
    public PluginMutationSafety MutationSafety { get; init; }
    public string MutationSafetyReason => MutationSafety switch { PluginMutationSafety.BlockedSystem => "RouterPilot does not allow mutating this system package.", PluginMutationSafety.BlockedDependencyRisk => "Removal is blocked because dependency safety is not established.", PluginMutationSafety.Unknown => "Package safety could not be determined.", _ => string.Empty };
    public bool CanInstall => IsAvailable && !IsInstalled && MutationSafety is not PluginMutationSafety.BlockedSystem;
    public bool CanRemove => IsInstalled && MutationSafety == PluginMutationSafety.Allowed;
    public bool CanUpdate => IsInstalled && IsUpgradable && MutationSafety == PluginMutationSafety.Allowed;
    public string DisplaySource => string.IsNullOrWhiteSpace(Source) ? "-" : Source;
    public string DisplayInstalledVersion => string.IsNullOrWhiteSpace(InstalledVersion) ? "—" : InstalledVersion;
    public string DisplayAvailableVersion => string.IsNullOrWhiteSpace(AvailableVersion) ? "—" : AvailableVersion;
    public string DisplayArchitecture => string.IsNullOrWhiteSpace(Architecture) ? "—" : Architecture;
    public string DisplayStatus => IsUpgradable ? "Update available" : IsInstalled ? "Installed" : IsAvailable ? "Available" : "—";
    public string DisplayDependencies => string.IsNullOrWhiteSpace(Dependencies) ? "—" : Dependencies;
    public string DisplayInstalledTime => string.IsNullOrWhiteSpace(InstalledTime) ? "—" : InstalledTime;
    public string DisplayDescription => string.IsNullOrWhiteSpace(Description) ? "—" : Description;
}
