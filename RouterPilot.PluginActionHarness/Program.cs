using RouterPilot.Models;

static class Program
{
    private static int Main()
    {
        Verify("CASE A", new PluginPackage { Name = "tree", IsAvailable = true, MutationSafety = PluginMutationSafety.Allowed }, install: true, remove: false, update: false, showRemove: false, showUpdate: false, reason: false);
        Verify("CASE B", new PluginPackage { Name = "tree", IsAvailable = true, IsInstalled = true, MutationSafety = PluginMutationSafety.Allowed }, install: false, remove: true, update: false, showRemove: true, showUpdate: false, reason: false);
        Verify("CASE C", new PluginPackage { Name = "iperf3", IsAvailable = true, IsInstalled = true, IsUpgradable = true, MutationSafety = PluginMutationSafety.Allowed }, install: false, remove: true, update: true, showRemove: true, showUpdate: true, reason: false);
        Verify("CASE D", new PluginPackage { Name = "busybox", IsInstalled = true, IsUpgradable = true, MutationSafety = PluginMutationSafety.BlockedSystem }, install: false, remove: false, update: false, showRemove: true, showUpdate: true, reason: true);
        Verify("CASE E", new PluginPackage { Name = "ordinary-with-reverse-dependency", IsInstalled = true, IsUpgradable = true, MutationSafety = PluginMutationSafety.BlockedDependencyRisk }, install: false, remove: false, update: false, showRemove: true, showUpdate: true, reason: true);
        Verify("available unknown", new PluginPackage { Name = "normal-authoritative-package", IsAvailable = true, MutationSafety = PluginMutationSafety.Unknown }, install: true, remove: false, update: false, showRemove: false, showUpdate: false, reason: true);
        Console.WriteLine("Plugin action matrix: PASS");
        return 0;
    }

    private static void Verify(string name, PluginPackage package, bool install, bool remove, bool update, bool showRemove, bool showUpdate, bool reason)
    {
        bool actualReason = !string.IsNullOrWhiteSpace(package.MutationSafetyReason);
        if (package.CanInstall != install || package.CanRemove != remove || package.CanUpdate != update || package.ShouldShowUninstall != showRemove || package.ShouldShowUpdate != showUpdate || actualReason != reason)
            throw new InvalidOperationException($"{name}: action presentation did not match the expected state.");
        Console.WriteLine($"{name}: install={package.CanInstall}, uninstall={package.CanRemove}/{package.ShouldShowUninstall}, update={package.CanUpdate}/{package.ShouldShowUpdate}, reason={actualReason}");
    }
}
