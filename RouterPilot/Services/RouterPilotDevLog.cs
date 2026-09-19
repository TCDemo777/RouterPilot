using System.Diagnostics;
using System.Text.RegularExpressions;

namespace RouterPilot.Services;

public enum RouterPilotDevLogCategory
{
    App,
    UI,
    Router,
    Auth,
    RPC,
    Dashboard,
    VPN,
    PIA,
    WireGuard,
    AdGuard,
    WiFi,
    Background,
    System,
    Error
}

public enum RouterPilotDevLogLevel { Trace, Debug, Info, Warn, Error }

public sealed record RouterPilotDevLogEntry(long SequenceNumber, DateTimeOffset Timestamp, RouterPilotDevLogCategory Category, RouterPilotDevLogLevel Level, string Operation, string Message, long? DurationMs = null, string? Outcome = null)
{
    public string DisplayText => $"{Timestamp:HH:mm:ss.fff} #{SequenceNumber:D6} {Level,-5} {Category,-10} {(string.IsNullOrWhiteSpace(Operation) ? "-" : Operation),-18} {Message}{(DurationMs is long ms ? $" duration={ms}ms" : string.Empty)}{(string.IsNullOrWhiteSpace(Outcome) ? string.Empty : $" outcome={Outcome}")}";
}

public interface IRouterPilotDevLog
{
    IReadOnlyList<RouterPilotDevLogEntry> Entries { get; }
    event EventHandler<RouterPilotDevLogEntry>? EntryAdded;
    void Write(RouterPilotDevLogCategory category, string message, RouterPilotDevLogLevel level = RouterPilotDevLogLevel.Info);
    void Write(RouterPilotDevLogCategory category, string operation, string message, RouterPilotDevLogLevel level, long? durationMs = null, string? outcome = null);
    string CreateOperationId(string prefix);
    void Clear();
}

public sealed class RouterPilotDevLog : IRouterPilotDevLog
{
    public const int DefaultCapacity = 5000;
    private readonly object _gate = new();
    private readonly Queue<RouterPilotDevLogEntry> _entries = new();
    private readonly int _capacity;
    private long _sequence;
    private long _operationSequence;

    public RouterPilotDevLog(int capacity = DefaultCapacity)
    {
        _capacity = Math.Clamp(capacity, 100, 5000);
    }

    public IReadOnlyList<RouterPilotDevLogEntry> Entries
    {
        get { lock (_gate) return _entries.ToArray(); }
    }

    public event EventHandler<RouterPilotDevLogEntry>? EntryAdded;

    public void Write(RouterPilotDevLogCategory category, string message, RouterPilotDevLogLevel level = RouterPilotDevLogLevel.Info)
        => Write(category, string.Empty, message, level);

    public string CreateOperationId(string prefix) => $"{Sanitize(prefix).ToUpperInvariant().Replace(' ', '-')}-{Interlocked.Increment(ref _operationSequence):D4}";

    public void Write(RouterPilotDevLogCategory category, string operation, string message, RouterPilotDevLogLevel level, long? durationMs = null, string? outcome = null)
    {
        string safe = Sanitize(message);
        if (safe.Length == 0) return;
        RouterPilotDevLogEntry entry = new(Interlocked.Increment(ref _sequence), DateTimeOffset.Now, category, level, Sanitize(operation), safe, durationMs, Sanitize(outcome));
        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > _capacity) _entries.Dequeue();
        }
        Debug.WriteLine($"[{level}] [{category}] {safe}");
        Trace.WriteLine($"[{level}] [{category}] {safe}");
        try { EntryAdded?.Invoke(this, entry); }
        catch { /* diagnostics must never disrupt the operation being observed */ }
    }

    public void Clear()
    {
        lock (_gate) _entries.Clear();
    }

    internal static string Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return string.Empty;
        string value = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        value = Regex.Replace(value,
            @"(?ix)\b(password|passwd|username|user_name|session|sid|token|authorization|private_key|preshared_key|public_key|psk|secret|mac_address|wifi_password|wifi_psk)\b\s*[:=]\s*[^\s,;]+",
            "$1=[redacted]", RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"(?i)\bAuthorization\s+[^\s]+", "Authorization [redacted]", RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"(?i)(https?://)[^\s/@]+:[^\s/@]+@", "$1[redacted]@", RegexOptions.CultureInvariant);
        // Backstop for common MAC-address-like values, without attempting to
        // inspect or retain arbitrary router payloads.
        value = System.Text.RegularExpressions.Regex.Replace(value, "(?<![A-Fa-f0-9])(?:[A-Fa-f0-9]{2}[:-]){5}[A-Fa-f0-9]{2}(?![A-Fa-f0-9])", "[redacted]", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return value.Length > 512 ? value[..512] : value;
    }
}
