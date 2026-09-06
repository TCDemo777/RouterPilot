using RouterPilot.Models;

namespace RouterPilot.Services;

public interface IPluginPackageService { Task<PluginInventorySnapshot> LoadAsync(CancellationToken cancellationToken = default); }

public sealed class PluginPackageService : IPluginPackageService
{
    private readonly IRouterManagerProvider _provider;
    public PluginPackageService(IRouterManagerProvider provider) => _provider = provider;
    public async Task<PluginInventorySnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        RouterManager manager = await _provider.GetRouterManagerAsync(cancellationToken).ConfigureAwait(false);
        string installed = await manager.RunReadOnlySshCommandAsync("/usr/libexec/opkg-call list-installed 2>/dev/null", cancellationToken).ConfigureAwait(false);
        string available = await manager.RunReadOnlySshCommandAsync("/usr/libexec/opkg-call list-available 2>/dev/null", cancellationToken).ConfigureAwait(false);
        string upgradable = await manager.RunReadOnlySshCommandAsync("opkg list-upgradable 2>/dev/null", cancellationToken).ConfigureAwait(false);
        return PluginPackageParser.Merge(installed, available, upgradable);
    }
}

internal static class PluginPackageParser
{
    public static PluginInventorySnapshot Merge(string installedText, string availableText, string upgradableText)
    {
        Dictionary<string, PackageFields> installed = ParseRecords(installedText, true), available = ParseRecords(availableText, false);
        HashSet<string> updates = ParseUpgradable(upgradableText);
        var merged = new Dictionary<string, PackageFields>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, PackageFields fields) in installed) merged[name] = fields;
        foreach ((string name, PackageFields fields) in available) merged[name] = merged.TryGetValue(name, out PackageFields? old) ? MergeFields(old, fields) : fields;
        List<PluginPackage> packages = merged.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => ToModel(pair.Key, pair.Value, updates.Contains(pair.Key))).ToList();
        bool installedOk = IsUsable(installedText), availableOk = IsUsable(availableText), updatesOk = IsUsable(upgradableText);
        return new PluginInventorySnapshot(packages, packages.Count(p => p.IsInstalled), packages.Count(p => p.IsAvailable), updatesOk ? updates.Count : null,
            availableOk ? PluginInventoryAvailability.Available : PluginInventoryAvailability.Unavailable,
            PluginInventoryFreshness.Unknown,
            installedOk ? null : "Installed package information is unavailable.", availableOk ? null : "Available package information is unavailable.", updatesOk ? null : "Update information is unavailable.", DateTimeOffset.UtcNow);
    }
    private static bool IsUsable(string text) => !string.IsNullOrWhiteSpace(text) && !text.Contains("SSH_", StringComparison.OrdinalIgnoreCase) && !text.Contains("Collected errors", StringComparison.OrdinalIgnoreCase);
    private static Dictionary<string, PackageFields> ParseRecords(string text, bool installed)
    {
        var result = new Dictionary<string, PackageFields>(StringComparer.OrdinalIgnoreCase); PackageFields current = new() { Installed = installed, Available = !installed };
        void Commit() { if (!string.IsNullOrWhiteSpace(current.Name)) result[current.Name] = current; current = new PackageFields { Installed = installed, Available = !installed }; }
        foreach (string raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.None))
        { string line = raw.Trim(); if (line.Length == 0) { Commit(); continue; } int colon = line.IndexOf(':'); if (colon <= 0) continue; string key = line[..colon].Trim().ToLowerInvariant(), value = line[(colon + 1)..].Trim(); if (key is "package" or "name") { if (!string.IsNullOrWhiteSpace(current.Name) && !string.Equals(current.Name, value, StringComparison.OrdinalIgnoreCase)) Commit(); current = current with { Name = value }; } else if (key is "version" or "installed-version") current = current with { Version = value }; else if (key is "architecture" or "arch") current = current with { Architecture = value }; else if (key is "status") current = current with { Status = value }; else if (key is "depends" or "dependencies") current = current with { Dependencies = value }; else if (key is "installed-time" or "installed time") current = current with { InstalledTime = value }; else if (key is "description" or "desc") current = current with { Description = value }; }
        Commit(); return result;
    }
    private static HashSet<string> ParseUpgradable(string text) => new(text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Where(line => line.Contains(" - ", StringComparison.Ordinal) && !line.StartsWith("Collected errors", StringComparison.OrdinalIgnoreCase)).Select(line => line.Split(" - ", 2)[0].Trim()), StringComparer.OrdinalIgnoreCase);
    private static PackageFields MergeFields(PackageFields a, PackageFields b) => a with { Available = true, AvailableVersion = b.Version, Architecture = string.IsNullOrWhiteSpace(a.Architecture) ? b.Architecture : a.Architecture, Status = string.IsNullOrWhiteSpace(a.Status) ? b.Status : a.Status, Dependencies = string.IsNullOrWhiteSpace(a.Dependencies) ? b.Dependencies : a.Dependencies, Description = string.IsNullOrWhiteSpace(a.Description) ? b.Description : a.Description };
    private static PluginPackage ToModel(string name, PackageFields f, bool update) => new() { Name = name, InstalledVersion = f.Installed ? f.Version : string.Empty, AvailableVersion = f.Available ? (f.AvailableVersion.Length == 0 ? f.Version : f.AvailableVersion) : string.Empty, Architecture = f.Architecture, Status = f.Status, Dependencies = f.Dependencies, InstalledTime = f.InstalledTime, Description = f.Description, IsInstalled = f.Installed, IsAvailable = f.Available, IsUpgradable = update, MutationSafety = Classify(name, f) };
    private static PluginMutationSafety Classify(string name, PackageFields fields)
    {
        string value = name.ToLowerInvariant();
        if (value is "base-files" or "busybox" or "libc" or "opkg" || value.StartsWith("kernel", StringComparison.Ordinal) || value.Contains("firewall", StringComparison.Ordinal) || value.Contains("dns", StringComparison.Ordinal) || value.Contains("dropbear", StringComparison.Ordinal) || value.Contains("tailscale", StringComparison.Ordinal) || value.Contains("adguard", StringComparison.Ordinal) || value.Contains("gl-") || value.Contains("luci", StringComparison.Ordinal)) return PluginMutationSafety.BlockedSystem;
        if (value is "tree" or "file" or "less" or "bc" or "htop" or "nano" or "jq") return PluginMutationSafety.Allowed;
        return PluginMutationSafety.Unknown;
    }
    private sealed record PackageFields(string Name = "", string Version = "", string AvailableVersion = "", string Architecture = "", string Status = "", string Dependencies = "", string InstalledTime = "", string Description = "", bool Installed = false, bool Available = false);
}
