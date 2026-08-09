using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>Single policy for whether a meaningful workflow event is historical, actionable, or both.</summary>
public static class EventRoutingPolicy
{
    public static bool ShouldNotify(MaintenanceAction action, MaintenanceOutcome outcome) =>
        outcome != MaintenanceOutcome.Success || action is not (MaintenanceAction.RefreshAll or MaintenanceAction.CreateBackup or MaintenanceAction.RunDiagnostics or MaintenanceAction.BackupDiagnostics);

    public static bool ShouldNotifyDiagnostics(DiagnosticExecutionOutcome outcome) =>
        outcome == DiagnosticExecutionOutcome.Error;
}
