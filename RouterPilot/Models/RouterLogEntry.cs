namespace RouterPilot.Models;

public sealed record RouterLogEntry(string Timestamp, string Severity, string Category, string Source, string Message)
{
    public string SearchText => $"{Timestamp} {Severity} {Category} {Source} {Message}";
}
