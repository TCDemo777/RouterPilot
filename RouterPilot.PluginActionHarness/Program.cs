using RouterPilot.Models;

static class Program
{
    private static int Main()
    {
        Verify("CASE A", new PluginPackage { Name = "tree", IsAvailable = true, MutationSafety = PluginMutationSafety.Allowed }, install: true, remove: false, update: false, reason: false);
        Verify("CASE B", new PluginPackage { Name = "tree", IsAvailable = true, IsInstalled = true, MutationSafety = PluginMutationSafety.Allowed }, install: false, remove: true, update: false, reason: false);
        Verify("CASE C", new PluginPackage { Name = "iperf3", IsAvailable = true, IsInstalled = true, IsUpgradable = true, MutationSafety = PluginMutationSafety.Allowed }, install: false, remove: true, update: true, reason: false);
        Verify("CASE D", new PluginPackage { Name = "busybox", IsInstalled = true, MutationSafety = PluginMutationSafety.BlockedSystem }, install: false, remove: false, update: false, reason: true);
        Verify("CASE E", new PluginPackage { Name = "ordinary-with-reverse-dependency", IsInstalled = true, IsUpgradable = true, MutationSafety = PluginMutationSafety.BlockedDependencyRisk }, install: false, remove: false, update: false, reason: true);
        Verify("available unknown", new PluginPackage { Name = "normal-authoritative-package", IsAvailable = true, MutationSafety = PluginMutationSafety.Unknown }, install: true, remove: false, update: false, reason: true);
        Console.WriteLine("Plugin action matrix: PASS");
        return 0;
    }

    private static void Verify(string name, PluginPackage package, bool install, bool remove, bool update, bool reason)
    {
        bool actualReason = !string.IsNullOrWhiteSpace(package.MutationSafetyReason);
        if (package.CanInstall != install || package.CanRemove != remove || package.CanUpdate != update || actualReason != reason)
            throw new InvalidOperationException($"{name}: expected install={install}, remove={remove}, update={update}, reason={reason}; actual install={package.CanInstall}, remove={package.CanRemove}, update={package.CanUpdate}, reason={actualReason}.");
        Console.WriteLine($"{name}: install={package.CanInstall}, uninstall={package.CanRemove}, update={package.CanUpdate}, reason={actualReason}");
    }
}
