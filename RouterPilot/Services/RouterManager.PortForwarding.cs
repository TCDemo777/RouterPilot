using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

public partial class RouterManager
{
    private readonly SemaphoreSlim _portForwardGate = new(1, 1);

    internal async Task<IReadOnlyList<PortForwardRuleInfo>> GetPortForwardRulesAsync(CancellationToken token)
    {
        string sid = await _sessionService.GetAdminTokenAsync(token);
        using JsonDocument document = await _sessionService.CallAsync(sid, "firewall", "get_port_forward_list", token);
        if (!document.RootElement.TryGetProperty("result", out JsonElement result) || !result.TryGetProperty("res", out JsonElement rules) || rules.ValueKind != JsonValueKind.Array) return Array.Empty<PortForwardRuleInfo>();
        return rules.EnumerateArray().Select(ParsePortForward).Where(rule => !string.IsNullOrWhiteSpace(rule.Id)).ToList();
    }

    internal async Task<PortForwardOperationResult> MutatePortForwardAsync(PortForwardRpcOperation operation, string? id, PortForwardRuleRequest? request, CancellationToken token)
    {
        await _portForwardGate.WaitAsync(token);
        try
        {
            object payload = operation switch
            {
                PortForwardRpcOperation.Add when request is not null => ToPayload(request),
                PortForwardRpcOperation.Update when request is not null && !string.IsNullOrWhiteSpace(id) => ToPayload(request, id),
                PortForwardRpcOperation.Delete when !string.IsNullOrWhiteSpace(id) => new { id },
                _ => throw new ArgumentException()
            };
            string sid = await _sessionService.GetAdminTokenAsync(token);
            using JsonDocument _ = await _sessionService.CallPortForwardAsync(sid, operation, payload, token);
            return new PortForwardOperationResult { Success = true, RuleId = id };
        }
        catch { return new PortForwardOperationResult { FailureCategory = "RemoteApplyFailed", Message = "RouterPilot could not apply the port forward." }; }
        finally { _portForwardGate.Release(); }
    }

    private static object ToPayload(PortForwardRuleRequest request, string? id = null) => id is null
        ? new { name = request.Name, proto = request.Protocol, dest = request.DestinationZone, dest_ip = request.DestinationIp, dest_port = request.InternalPort, enabled = request.Enabled, src = request.SourceZone, src_dport = request.ExternalPort }
        : new { id, name = request.Name, proto = request.Protocol, dest = request.DestinationZone, dest_ip = request.DestinationIp, dest_port = request.InternalPort, enabled = request.Enabled, src = request.SourceZone, src_dport = request.ExternalPort };
    private static PortForwardRuleInfo ParsePortForward(JsonElement rule) => new() { Id = Read(rule,"id"), Name=Read(rule,"name"), Protocol=Read(rule,"proto"), SourceZone=Read(rule,"src"), ExternalPort=Read(rule,"src_dport"), DestinationZone=Read(rule,"dest"), DestinationIp=Read(rule,"dest_ip"), InternalPort=Read(rule,"dest_port"), Enabled=rule.TryGetProperty("enabled",out JsonElement enabled) && enabled.ValueKind==JsonValueKind.True };
    private static string Read(JsonElement value, string property) => value.TryGetProperty(property, out JsonElement result) ? result.ToString() : string.Empty;
}
