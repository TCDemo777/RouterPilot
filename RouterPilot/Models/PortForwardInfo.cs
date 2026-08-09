using System;
using System.Collections.Generic;

namespace RouterPilot.Models;

public sealed class PortForwardRuleInfo
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Protocol { get; init; } = string.Empty;
    public string SourceZone { get; init; } = string.Empty;
    public string ExternalPort { get; init; } = string.Empty;
    public string DestinationZone { get; init; } = string.Empty;
    public string DestinationIp { get; init; } = string.Empty;
    public string InternalPort { get; init; } = string.Empty;
    public bool Enabled { get; init; }
}

public sealed class PortForwardRuleRequest
{
    public string Name { get; init; } = string.Empty;
    public string Protocol { get; init; } = "tcp";
    public string SourceZone { get; init; } = "wan";
    public string ExternalPort { get; init; } = string.Empty;
    public string DestinationZone { get; init; } = "lan";
    public string DestinationIp { get; init; } = string.Empty;
    public string InternalPort { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
}

public sealed class PortForwardOperationResult
{
    public bool Success { get; init; }
    public string FailureCategory { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public bool RollbackAttempted { get; init; }
    public bool RollbackVerified { get; init; }
    public string? RuleId { get; init; }
}
