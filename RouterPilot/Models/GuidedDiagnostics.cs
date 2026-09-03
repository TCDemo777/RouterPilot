using System;
using System.Collections.Generic;

namespace RouterPilot.Models;

public enum DiagnosticCategory { Internet, WiFi, DNS, VPN, Client, RouterPerformance, Ethernet, Storage, NotSure }

public sealed record DiagnosticFinding(string Title, string Summary, string Importance, string Destination, string RecommendedAction);

public sealed record GuidedDiagnosticSession(
    DiagnosticCategory Category,
    DateTimeOffset StartedAt,
    string State,
    IReadOnlyList<DiagnosticFinding> Findings,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> UnavailableEvidence);
