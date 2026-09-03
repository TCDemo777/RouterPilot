using System;
using System.Collections.Generic;
using RouterPilot.Models;
using RouterPilot.ViewModels;

namespace RouterPilot.Services;

public static class GuidedDiagnosticsService
{
    public static GuidedDiagnosticSession Build(DiagnosticCategory category, DashboardViewModel dashboard)
    {
        List<string> evidence = [], unavailable = [];
        List<DiagnosticFinding> findings = [];
        if (!dashboard.RouterConnected)
        {
            unavailable.Add("Router connectivity");
            findings.Add(new("Router unavailable", "RouterPilot could not observe the router during this session.", "Attention", "Router", "Reconnect the router, then refresh evidence."));
        }
        else
        {
            evidence.Add("Router connectivity is available.");
            if (category is DiagnosticCategory.Internet or DiagnosticCategory.NotSure)
            {
                evidence.Add($"Internet path: {dashboard.NetworkHealthWanSummary}.");
                if (!dashboard.InternetConnected) findings.Add(new("Internet path unavailable", "No connected Internet path is currently reported.", "Attention", "Network", "Review Network and Multi-WAN status."));
            }
            if (category is DiagnosticCategory.WiFi or DiagnosticCategory.NotSure)
            {
                evidence.Add($"Wi-Fi clients observed: {dashboard.WifiClientTotal}.");
                if (dashboard.WifiClientTotal > 0 && dashboard.WifiClientsWithSignal == 0) unavailable.Add("Wi-Fi signal quality");
                else if (dashboard.WifiClientsWithSignal > 0) evidence.Add($"Signal data available for {dashboard.WifiClientsWithSignal} clients.");
            }
            if (category is DiagnosticCategory.DNS or DiagnosticCategory.NotSure)
            {
                evidence.Add($"DNS protection: {dashboard.AdGuardStatusText}.");
                if (!dashboard.IsAdGuardAvailable) unavailable.Add("AdGuard client attribution");
            }
            if (category is DiagnosticCategory.VPN or DiagnosticCategory.NotSure) evidence.Add($"VPN: {dashboard.VpnStatusText}.");
            if (category is DiagnosticCategory.RouterPerformance or DiagnosticCategory.NotSure)
            {
                evidence.Add($"Temperature: {dashboard.TemperatureHealthText}.");
                evidence.Add($"CPU: {dashboard.CpuUsageDisplay}; memory: {dashboard.MemoryUsage}.");
                evidence.Add($"SQM: {FormatState(dashboard.AdvancedRouterSnapshot.SqmEnabled)} ({dashboard.AdvancedRouterSnapshot.SqmQueueDiscipline}).");
                evidence.Add($"DPI: configured {FormatState(dashboard.AdvancedRouterSnapshot.DpiConfigured)}, runtime {FormatState(dashboard.AdvancedRouterSnapshot.DpiRunning)}.");
            }
            if (category is DiagnosticCategory.Storage or DiagnosticCategory.NotSure) evidence.Add($"External storage: {dashboard.StorageTelemetryDisplay}.");
            if (category is DiagnosticCategory.Ethernet or DiagnosticCategory.NotSure) evidence.Add("Ethernet port telemetry is available on the Router Ports page.");
            if (findings.Count == 0 && unavailable.Count == 0) findings.Add(new("No obvious issue identified", "No deterministic issue was identified in the currently available RouterPilot telemetry.", "Information", "Overview", "Review the relevant detailed page or run the bounded Internet Quality test."));
        }
        return new(category, DateTimeOffset.UtcNow, unavailable.Count > 0 ? "Partial" : "Ready", findings, evidence, unavailable);
    }

    private static string FormatState(bool? value) => value.HasValue ? (value.Value ? "Enabled" : "Disabled") : "Unknown";

    public static string BuildReport(GuidedDiagnosticSession? session)
    {
        if (session is null) return "RouterPilot Diagnostic Report\n\nDiagnostic evidence is not loaded yet.";
        List<string> lines = [$"RouterPilot Diagnostic Report", $"Category: {session.Category}", $"State: {session.State}", $"Observed: {session.StartedAt.ToLocalTime():g}", "", "EVIDENCE"];
        lines.AddRange(session.Evidence);
        lines.Add("\nFINDINGS");
        foreach (DiagnosticFinding finding in session.Findings) lines.Add($"{finding.Title}: {finding.Summary}");
        if (session.UnavailableEvidence.Count > 0) { lines.Add("\nUNAVAILABLE EVIDENCE"); lines.AddRange(session.UnavailableEvidence); }
        lines.Add("\nRECOMMENDED NEXT STEPS");
        foreach (DiagnosticFinding finding in session.Findings) lines.Add(finding.RecommendedAction);
        return string.Join(Environment.NewLine, lines);
    }
}
