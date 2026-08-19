using System.Text.Json.Serialization;

namespace RouterPilot.Models;

public sealed class VpnSchedule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "VPN schedule";
    public bool IsEnabled { get; set; } = true;
    public ScheduleDays Days { get; set; } = ScheduleDays.All;
    public TimeOnly? EnableTime { get; set; }
    public TimeOnly? DisableTime { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<string> ExecutedOccurrences { get; set; } = [];

    [JsonIgnore]
    public string DaysDisplay => Days == ScheduleDays.All ? "Every day" : Days.ToString().Replace(",", " ·");
    [JsonIgnore]
    public string TimeDisplay => EnableTime is not null && DisableTime is not null
        ? $"Enable {EnableTime:HH\\:mm} · Disable {DisableTime:HH\\:mm}"
        : EnableTime is not null ? $"Enable {EnableTime:HH\\:mm}" : $"Disable {DisableTime:HH\\:mm}";
    [JsonIgnore]
    public string Summary => $"{DaysDisplay} · {TimeDisplay}";
    [JsonIgnore]
    public string StateDisplay => IsEnabled ? "Enabled" : "Disabled";
}

public enum VpnScheduledAction { Enable, Disable }
