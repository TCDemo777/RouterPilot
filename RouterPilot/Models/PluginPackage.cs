namespace RouterPilot.Models;

public enum PluginMutationSafety { Allowed, BlockedSystem, BlockedDependencyRisk, Unknown }
public enum PluginUpdateProtection { Safe, ProtectedOverrideable, HardBlockedCritical }

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
    public bool ShouldShowUninstall => IsInstalled;
    public bool ShouldShowUpdate => IsInstalled && IsUpgradable;
    public PluginUpdateProtection UpdateProtection => MutationSafety == PluginMutationSafety.Allowed ? PluginUpdateProtection.Safe : IsHardCritical(Name) ? PluginUpdateProtection.HardBlockedCritical : PluginUpdateProtection.ProtectedOverrideable;
    public bool CanForceUpdate => ShouldShowUpdate && UpdateProtection == PluginUpdateProtection.ProtectedOverrideable;
    public string MutationSafetyReason => MutationSafety switch { PluginMutationSafety.BlockedSystem => "System package protected by RouterPilot. Updating or uninstalling this package could affect core router services.", PluginMutationSafety.BlockedDependencyRisk => "Removal is blocked because dependency safety is not established.", PluginMutationSafety.Unknown => "Package safety could not be determined.", _ => string.Empty };
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
    private static bool IsHardCritical(string name)
    {
        string value = name.ToLowerInvariant();
        return value is "base-files" or "busybox" or "libc" or "opkg" or "procd" || value.StartsWith("kernel", StringComparison.Ordinal) || value.StartsWith("kmod-", StringComparison.Ordinal) || value.Contains("dnsmasq", StringComparison.Ordinal) || value.Contains("firewall", StringComparison.Ordinal) || value.Contains("dropbear", StringComparison.Ordinal) || value.StartsWith("gl-", StringComparison.Ordinal);
    }
}
