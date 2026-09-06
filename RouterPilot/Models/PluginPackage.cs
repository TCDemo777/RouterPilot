namespace RouterPilot.Models;

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
    public string DisplaySource => string.IsNullOrWhiteSpace(Source) ? "-" : Source;
    public string DisplayInstalledVersion => string.IsNullOrWhiteSpace(InstalledVersion) ? "—" : InstalledVersion;
    public string DisplayAvailableVersion => string.IsNullOrWhiteSpace(AvailableVersion) ? "—" : AvailableVersion;
    public string DisplayArchitecture => string.IsNullOrWhiteSpace(Architecture) ? "—" : Architecture;
    public string DisplayStatus => IsUpgradable ? "Update available" : IsInstalled ? "Installed" : IsAvailable ? "Available" : "—";
    public string DisplayDependencies => string.IsNullOrWhiteSpace(Dependencies) ? "—" : Dependencies;
    public string DisplayInstalledTime => string.IsNullOrWhiteSpace(InstalledTime) ? "—" : InstalledTime;
    public string DisplayDescription => string.IsNullOrWhiteSpace(Description) ? "—" : Description;
}
