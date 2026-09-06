using RouterPilot.Models;

namespace RouterPilot.Services;

public interface IPluginPackageMutationService
{
    Task ExecuteAsync(string operation, string packageName, CancellationToken cancellationToken = default);
}

public sealed class PluginPackageMutationService : IPluginPackageMutationService
{
    private readonly IRouterManagerProvider _provider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public PluginPackageMutationService(IRouterManagerProvider provider) => _provider = provider;

    public async Task ExecuteAsync(string operation, string packageName, CancellationToken cancellationToken = default)
    {
        if (operation is not ("install" or "remove" or "update-indexes")) throw new ArgumentOutOfRangeException(nameof(operation));
        if (operation != "update-indexes" && !IsSafePackageName(packageName)) throw new InvalidOperationException("The selected package name is invalid.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RouterManager manager = await _provider.GetRouterManagerAsync(cancellationToken).ConfigureAwait(false);
            string command = operation == "update-indexes" ? "/usr/libexec/opkg-call update 2>/dev/null" : $"/usr/libexec/opkg-call {operation} {packageName} 2>/dev/null";
            string output = await manager.RunReadOnlySshCommandAsync(command, cancellationToken).ConfigureAwait(false);
            if (!output.Contains("\"code\":0", StringComparison.OrdinalIgnoreCase) && !output.Contains("\"code\": 0", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The router package operation did not confirm success.");
        }
        finally { _gate.Release(); }
    }

    private static bool IsSafePackageName(string value) => value.Length is > 0 and <= 80 && value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.' or '+');
}
