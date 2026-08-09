using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

public sealed class PortForwardService : IPortForwardService
{
    private readonly IRouterManagerProvider _provider;
    private readonly TimelineService _timeline;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public PortForwardService(IRouterManagerProvider provider, TimelineService timeline)
    {
        _provider = provider;
        _timeline = timeline;
    }

    public async Task<IReadOnlyList<PortForwardRuleInfo>> GetRulesAsync(CancellationToken token) =>
        await (await _provider.GetRouterManagerAsync(token)).GetPortForwardRulesAsync(token);

    public async Task<PortForwardOperationResult> AddAsync(PortForwardRuleRequest request, CancellationToken token)
    {
        await _operationGate.WaitAsync(token);
        try
        {
            RouterManager manager = await _provider.GetRouterManagerAsync(token);
            IReadOnlyList<PortForwardRuleInfo> before = await manager.GetPortForwardRulesAsync(token);
            string? validation = await ValidateAsync(manager, request, before, null, token);
            if (validation is not null) return await CompleteAsync(Failure(validation), "Add", request.Name, token);

            PortForwardOperationResult applied = await manager.MutatePortForwardAsync(PortForwardRpcOperation.Add, null, request, token);
            IReadOnlyList<PortForwardRuleInfo> after = await manager.GetPortForwardRulesAsync(token);
            List<PortForwardRuleInfo> matches = after.Where(rule => Same(rule, request)).ToList();
            if (applied.Success && matches.Count == 1)
                return await CompleteAsync(new PortForwardOperationResult { Success = true, RuleId = matches[0].Id }, "Add", request.Name, token);

            // A router-side add can succeed even if its acknowledgement is interrupted.
            // Remove only an unambiguous rule that exactly matches this request.
            if (matches.Count == 1)
            {
                PortForwardOperationResult rollback = await manager.MutatePortForwardAsync(PortForwardRpcOperation.Delete, matches[0].Id, null, token);
                bool removed = rollback.Success && !(await manager.GetPortForwardRulesAsync(token)).Any(rule => rule.Id == matches[0].Id);
                return await CompleteAsync(new PortForwardOperationResult
                {
                    FailureCategory = "VerificationFailed", Message = "RouterPilot could not verify the new port forward.",
                    RollbackAttempted = true, RollbackVerified = removed, RuleId = matches[0].Id
                }, "Add", request.Name, token);
            }

            return await CompleteAsync(applied.Success
                ? Failure("VerificationFailed", "RouterPilot could not verify the new port forward.")
                : applied, "Add", request.Name, token);
        }
        finally { _operationGate.Release(); }
    }

    public async Task<PortForwardOperationResult> UpdateAsync(string id, PortForwardRuleRequest request, CancellationToken token)
    {
        await _operationGate.WaitAsync(token);
        try
        {
            RouterManager manager = await _provider.GetRouterManagerAsync(token);
            IReadOnlyList<PortForwardRuleInfo> before = await manager.GetPortForwardRulesAsync(token);
            PortForwardRuleInfo? original = before.SingleOrDefault(rule => rule.Id == id);
            if (original is null) return await CompleteAsync(Failure("RuleNotFound"), "Update", request.Name, token);
            string? validation = await ValidateAsync(manager, request, before, id, token);
            if (validation is not null) return await CompleteAsync(Failure(validation), "Update", request.Name, token);

            PortForwardOperationResult applied = await manager.MutatePortForwardAsync(PortForwardRpcOperation.Update, id, request, token);
            PortForwardRuleInfo? updated = (await manager.GetPortForwardRulesAsync(token)).SingleOrDefault(rule => rule.Id == id);
            if (applied.Success && updated is not null && Same(updated, request))
            {
                string operation = original.Enabled != request.Enabled ? (request.Enabled ? "Enable" : "Disable") : "Update";
                return await CompleteAsync(new PortForwardOperationResult { Success = true, RuleId = id }, operation, request.Name, token);
            }

            // Restore the captured full rule only when the id still identifies one rule.
            bool rollbackAttempted = updated is not null;
            bool rollbackVerified = false;
            if (rollbackAttempted)
            {
                PortForwardOperationResult restore = await manager.MutatePortForwardAsync(PortForwardRpcOperation.Update, id, ToRequest(original), token);
                PortForwardRuleInfo? restored = (await manager.GetPortForwardRulesAsync(token)).SingleOrDefault(rule => rule.Id == id);
                rollbackVerified = restore.Success && restored is not null && Same(restored, ToRequest(original));
            }
            return await CompleteAsync(new PortForwardOperationResult
            {
                FailureCategory = "VerificationFailed", Message = "RouterPilot could not verify the updated port forward.",
                RollbackAttempted = rollbackAttempted, RollbackVerified = rollbackVerified, RuleId = id
            }, "Update", request.Name, token);
        }
        finally { _operationGate.Release(); }
    }

    public async Task<PortForwardOperationResult> DeleteAsync(string id, CancellationToken token)
    {
        await _operationGate.WaitAsync(token);
        try
        {
            RouterManager manager = await _provider.GetRouterManagerAsync(token);
            PortForwardRuleInfo? original = (await manager.GetPortForwardRulesAsync(token)).SingleOrDefault(rule => rule.Id == id);
            if (original is null) return await CompleteAsync(Failure("RuleNotFound"), "Delete", null, token);
            PortForwardOperationResult applied = await manager.MutatePortForwardAsync(PortForwardRpcOperation.Delete, id, null, token);
            bool absent = !(await manager.GetPortForwardRulesAsync(token)).Any(rule => rule.Id == id);
            if (applied.Success && absent)
                return await CompleteAsync(new PortForwardOperationResult { Success = true, RuleId = id }, "Delete", original.Name, token);

            // Never recreate after an ambiguous delete: a duplicate forward is worse
            // than requiring the user to refresh and review the current router state.
            return await CompleteAsync(applied.Success
                ? Failure("VerificationFailed", "RouterPilot could not verify deletion of the port forward.")
                : applied, "Delete", original.Name, token);
        }
        finally { _operationGate.Release(); }
    }

    private static async Task<string?> ValidateAsync(RouterManager manager, PortForwardRuleRequest request, IReadOnlyList<PortForwardRuleInfo> rules, string? selfId, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 64 || request.Name.Any(char.IsControl)) return "InvalidName";
        if (request.Protocol is not ("tcp" or "udp" or "tcp udp")) return "InvalidProtocol";
        if (!ValidPort(request.ExternalPort) || !ValidPort(request.InternalPort)) return "InvalidPort";
        if (!IPAddress.TryParse(request.DestinationIp, out IPAddress? address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return "InvalidDestinationIp";
        if (rules.Any(rule => rule.Id != selfId && ProtocolsOverlap(rule.Protocol, request.Protocol) && rule.ExternalPort == request.ExternalPort)) return "PortConflict";

        try
        {
            DhcpSnapshot snapshot = await manager.GetDhcpSnapshotAsync();
            if (snapshot.Scopes.Count > 0 && !snapshot.Scopes.Any(scope => scope.ContainsUsableHost(request.DestinationIp))) return "OutsideKnownLanScope";
        }
        catch
        {
            // Scope data improves validation when available; it must not turn a
            // temporary DHCP read failure into an unrelated firewall write failure.
        }
        return null;
    }

    private async Task<PortForwardOperationResult> CompleteAsync(PortForwardOperationResult result, string operation, string? name, CancellationToken token)
    {
        string verb = operation switch { "Add" => "added", "Update" => "edited", "Delete" => "deleted", "Enable" => "enabled", "Disable" => "disabled", _ => "changed" };
        string title = result.Success ? $"Port forward {verb}" : "Port forward operation failed";
        try
        {
            await _timeline.AddAsync(new TimelineEvent
            {
                Category = TimelineCategory.Router,
                EventType = result.Success ? TimelineEventType.MaintenanceCompleted : TimelineEventType.MaintenanceFailed,
                Title = title,
                Message = string.IsNullOrWhiteSpace(name) ? "Port forwarding" : name,
                Severity = result.Success ? TimelineSeverity.Success : TimelineSeverity.Warning,
                Source = "Port Forwarding"
            }, token);
        }
        catch { }
        return result;
    }

    private static PortForwardOperationResult Failure(string category, string? message = null) => new() { FailureCategory = category, Message = message ?? "RouterPilot could not apply the port forward." };
    private static bool ValidPort(string value) => int.TryParse(value, out int port) && port is > 0 and <= 65535;
    private static bool ProtocolsOverlap(string left, string right) => ProtocolParts(left).Overlaps(ProtocolParts(right));
    private static HashSet<string> ProtocolParts(string value) => value.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
    private static PortForwardRuleRequest ToRequest(PortForwardRuleInfo rule) => new() { Name = rule.Name, Protocol = rule.Protocol, SourceZone = rule.SourceZone, ExternalPort = rule.ExternalPort, DestinationZone = rule.DestinationZone, DestinationIp = rule.DestinationIp, InternalPort = rule.InternalPort, Enabled = rule.Enabled };
    private static bool Same(PortForwardRuleInfo rule, PortForwardRuleRequest request) => rule.Name == request.Name && rule.Protocol == request.Protocol && rule.SourceZone == request.SourceZone && rule.ExternalPort == request.ExternalPort && rule.DestinationZone == request.DestinationZone && rule.DestinationIp == request.DestinationIp && rule.InternalPort == request.InternalPort && rule.Enabled == request.Enabled;
}
