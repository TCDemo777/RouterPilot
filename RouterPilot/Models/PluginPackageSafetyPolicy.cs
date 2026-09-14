namespace RouterPilot.Models;

/// <summary>Canonical hard block for router packages whose mutation can break core services.</summary>
public static class PluginPackageSafetyPolicy
{
    public static bool IsCriticalPackage(string? name)
    {
        string value = name?.Trim().ToLowerInvariant() ?? string.Empty;
        return value is "base-files" or "busybox" or "libc" or "opkg" or "procd" ||
               value.StartsWith("kernel", StringComparison.Ordinal) ||
               value.StartsWith("kmod-", StringComparison.Ordinal) ||
               value.Contains("dnsmasq", StringComparison.Ordinal) ||
               value.Contains("firewall", StringComparison.Ordinal) ||
               value.Contains("dropbear", StringComparison.Ordinal) ||
               value.StartsWith("gl-", StringComparison.Ordinal);
    }

    public static void EnsureMutationAllowed(string operation, string packageName)
    {
        if (operation != "update-indexes" && IsCriticalPackage(packageName))
            throw new InvalidOperationException("RouterPilot blocks package operations for this critical system package.");
    }
}
