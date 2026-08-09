using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

public interface IPortForwardService
{
    Task<IReadOnlyList<PortForwardRuleInfo>> GetRulesAsync(CancellationToken cancellationToken);
    Task<PortForwardOperationResult> AddAsync(PortForwardRuleRequest request, CancellationToken cancellationToken);
    Task<PortForwardOperationResult> UpdateAsync(string id, PortForwardRuleRequest request, CancellationToken cancellationToken);
    Task<PortForwardOperationResult> DeleteAsync(string id, CancellationToken cancellationToken);
}

internal enum PortForwardRpcOperation { Add, Update, Delete }
