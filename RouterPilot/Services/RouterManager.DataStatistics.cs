using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

public partial class RouterManager
{
    public async Task<DataStatisticsStatus> GetDataStatisticsStatusAsync(
        CancellationToken cancellationToken = default)
    {
        string sessionId = await _sessionService.GetAdminTokenAsync(cancellationToken);
        using JsonDocument document = await _sessionService.CallAsync(
            sessionId,
            "system",
            "get_status",
            cancellationToken);

        return DataStatisticsParser.ParseStatus(GetDataStatisticsResult(document));
    }

    public async Task<DataStatisticsSnapshot> GetTopAppFlowStatisticsAsync(
        CancellationToken cancellationToken = default)
    {
        string sessionId = await _sessionService.GetAdminTokenAsync(cancellationToken);
        using JsonDocument document = await _sessionService.CallAsync(
            sessionId,
            "flow_statistics",
            "get_top_app_flow_statistics",
            new { top = "10" },
            cancellationToken);

        return DataStatisticsParser.ParseSnapshot(GetDataStatisticsResult(document));
    }

    private static JsonElement GetDataStatisticsResult(JsonDocument document)
    {
        JsonElement root = document.RootElement;
        if (root.TryGetProperty("error", out JsonElement error))
        {
            int? code = error.TryGetProperty("code", out JsonElement codeElement) &&
                codeElement.TryGetInt32(out int numericCode)
                    ? (int)numericCode
                    : null;
            throw new DataStatisticsRpcException(code);
        }

        if (!root.TryGetProperty("result", out JsonElement result) ||
            result.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "The router returned no Data Statistics result.");
        }

        return result;
    }

}

internal sealed class DataStatisticsRpcException(int? errorCode) : Exception
{
    public int? ErrorCode { get; } = errorCode;
    public bool IsMethodOrServiceUnavailable => ErrorCode == -32601;
}
