using System;
using System.Diagnostics;

namespace RouterPilot.Services;

/// <summary>
/// Keeps user-operation feedback safe while retaining a categorised diagnostic
/// record. Callers supply a concise, action-specific message rather than
/// surfacing exception text from router, filesystem, or UI boundaries.
/// </summary>
public static class OperationFailurePolicy
{
    public static string UserMessage(Exception exception, string operation, string safeMessage)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(safeMessage);

        Debug.WriteLine(
            $"{operation} failed ({DiagnosticRedactor.FailureCategory(exception)}).");
        return safeMessage;
    }
}
