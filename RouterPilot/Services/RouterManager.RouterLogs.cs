namespace RouterPilot.Services;

public partial class RouterManager
{
    public Task<string> GetRouterLogsAsync(CancellationToken cancellationToken = default) =>
        _ssh.RunCommandAsync("logread -l 250", cancellationToken);
}
