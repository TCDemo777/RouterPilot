using System;
using System.Threading.Tasks;

namespace RouterPilot.Services;

/// <summary>
/// Executes the small, read-only router tools exposed by the About page.
/// UI text and logging remain owned by the view; this service owns only router
/// acquisition and the transport failure boundary.
/// </summary>
public sealed class RouterDiagnosticsToolService
{
    private readonly IRouterManagerProvider _routerManagerProvider;

    public RouterDiagnosticsToolService(IRouterManagerProvider routerManagerProvider)
    {
        _routerManagerProvider = routerManagerProvider;
    }

    public async Task<RouterDiagnosticsToolResult> ExecuteAsync(
        string target,
        Func<RouterManager, string, Task<string>> operation)
    {
        try
        {
            RouterManager router = await _routerManagerProvider.GetRouterManagerAsync();
            string output = await operation(router, target);
            return RouterDiagnosticsToolResult.Success(output);
        }
        catch (Exception ex)
        {
            return RouterDiagnosticsToolResult.Failure(
                DiagnosticRedactor.FailureCategory(ex));
        }
    }
}

public sealed record RouterDiagnosticsToolResult(
    bool Succeeded,
    string Output,
    string? FailureCategory)
{
    public static RouterDiagnosticsToolResult Success(string? output) =>
        new(true, output ?? string.Empty, null);

    public static RouterDiagnosticsToolResult Failure(string category) =>
        new(false, string.Empty, category);
}
