using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RouterPilot.Models;

public sealed class PortForwardRuleInfo : INotifyPropertyChanged
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

    private string targetClientName = string.Empty;
    private string targetStatusTitle = string.Empty;
    private string targetStatusDetail = string.Empty;
    private string targetStatusSeverity = string.Empty;

    public string TargetClientName { get => targetClientName; private set => SetField(ref targetClientName, value); }
    public string TargetStatusTitle { get => targetStatusTitle; private set => SetField(ref targetStatusTitle, value); }
    public string TargetStatusDetail { get => targetStatusDetail; private set => SetField(ref targetStatusDetail, value); }
    public string TargetStatusSeverity { get => targetStatusSeverity; private set => SetField(ref targetStatusSeverity, value); }
    public bool HasTargetClientName => !string.IsNullOrWhiteSpace(TargetClientName);
    public bool HasTargetStatus => !string.IsNullOrWhiteSpace(TargetStatusTitle);
    public bool HasTargetStatusDetail => !string.IsNullOrWhiteSpace(TargetStatusDetail);

    public void SetTargetIntelligence(string clientName, string title, string detail, string severity)
    {
        TargetClientName = clientName;
        TargetStatusTitle = title;
        TargetStatusDetail = detail;
        TargetStatusSeverity = severity;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void SetField(ref string field, string value, [CallerMemberName] string? propertyName = null)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName is nameof(TargetClientName)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasTargetClientName)));
        if (propertyName is nameof(TargetStatusTitle)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasTargetStatus)));
        if (propertyName is nameof(TargetStatusDetail)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasTargetStatusDetail)));
    }
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
