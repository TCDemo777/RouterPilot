using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using RouterPilot.Models;
using RouterPilot.ViewModels;

namespace RouterPilot.Services;

public sealed class RouterStateSnapshotService
{
    private const int MaxSnapshotsPerProfile = 10;
    private const int MaxJournalEntriesPerProfile = 25;
    private readonly string _path;
    private readonly string _journalPath;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public RouterStateSnapshotService(ApplicationDataPathProvider paths)
    {
        _path = Path.Combine(paths.CurrentPath, "router-state-snapshots.json");
        _journalPath = Path.Combine(paths.CurrentPath, "router-state-comparison-journal.json");
    }

    public IReadOnlyList<RouterStateSnapshot> Load(string profileId)
    {
        try
        {
            if (!File.Exists(_path)) return [];
            return (JsonSerializer.Deserialize<List<RouterStateSnapshot>>(File.ReadAllText(_path), _json) ?? [])
                .Where(snapshot => snapshot.ProfileId == profileId)
                .OrderByDescending(snapshot => snapshot.CapturedAt)
                .Take(MaxSnapshotsPerProfile)
                .ToList();
        }
        catch (JsonException) { return []; }
        catch (IOException) { return []; }
    }

    public void Save(RouterStateSnapshot snapshot)
    {
        List<RouterStateSnapshot> all = LoadAll();
        all.RemoveAll(item => item.ProfileId == snapshot.ProfileId && item.SnapshotId == snapshot.SnapshotId);
        all.Add(snapshot);
        all = all.GroupBy(item => item.ProfileId)
            .SelectMany(group => group.OrderByDescending(item => item.CapturedAt).Take(MaxSnapshotsPerProfile))
            .ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        AtomicWrite(_path, JsonSerializer.Serialize(all, _json));
    }

    public void Delete(string profileId, string snapshotId)
    {
        List<RouterStateSnapshot> all = LoadAll();
        all.RemoveAll(item => item.ProfileId == profileId && item.SnapshotId == snapshotId);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        AtomicWrite(_path, JsonSerializer.Serialize(all, _json));
    }

    public IReadOnlyList<RouterStateComparisonJournalEntry> LoadJournal(string profileId)
    {
        try
        {
            if (!File.Exists(_journalPath)) return [];
            return (JsonSerializer.Deserialize<List<RouterStateComparisonJournalEntry>>(File.ReadAllText(_journalPath), _json) ?? [])
                .Where(entry => entry.ProfileId == profileId)
                .OrderByDescending(entry => entry.ComparedAt)
                .Take(MaxJournalEntriesPerProfile)
                .ToList();
        }
        catch (JsonException) { return []; }
        catch (IOException) { return []; }
    }

    public void AppendJournal(RouterStateComparisonJournalEntry entry)
    {
        List<RouterStateComparisonJournalEntry> all;
        try
        {
            all = !File.Exists(_journalPath)
                ? []
                : JsonSerializer.Deserialize<List<RouterStateComparisonJournalEntry>>(File.ReadAllText(_journalPath), _json) ?? [];
        }
        catch (JsonException) { all = []; }
        catch (IOException) { all = []; }
        all.RemoveAll(item => item.JournalId == entry.JournalId);
        all.Add(entry);
        all = all.GroupBy(item => item.ProfileId)
            .SelectMany(group => group.OrderByDescending(item => item.ComparedAt).Take(MaxJournalEntriesPerProfile))
            .ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
        AtomicWrite(_journalPath, JsonSerializer.Serialize(all, _json));
    }

    private static void AtomicWrite(string path, string content)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, true);
    }

    private List<RouterStateSnapshot> LoadAll()
    {
        try
        {
            return !File.Exists(_path)
                ? []
                : JsonSerializer.Deserialize<List<RouterStateSnapshot>>(File.ReadAllText(_path), _json) ?? [];
        }
        catch (JsonException) { return []; }
        catch (IOException) { return []; }
    }

    public static RouterStateSnapshot FromDashboard(string profileId, DashboardViewModel dashboard)
    {
        RouterAdvancedSnapshot advanced = dashboard.AdvancedRouterSnapshot;
        return new RouterStateSnapshot(
            1, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, $"Snapshot — {DateTime.Now:g}", profileId,
            Safe(dashboard.RouterModel), Safe(dashboard.FirmwareVersion),
            new RouterStateSystem(Safe(dashboard.RouterKernelVersion), Safe(dashboard.RouterArchitecture), Safe(advanced.NetworkMode)),
            new RouterStateNetwork(advanced.GuestEnabled, advanced.IoTEnabled, advanced.GuestIgmpSnooping, advanced.IoTIgmpSnooping, advanced.NatMasquerade, advanced.NatMasqueradeIpv6),
            new RouterStateTraffic(advanced.SqmEnabled, Safe(advanced.SqmQueueDiscipline), Safe(advanced.SqmDownload), Safe(advanced.SqmUpload), advanced.DpiConfigured),
            new RouterStateServices(advanced.WebDavEnabled, advanced.DlnaRunning, advanced.ZeroTierInstalled, advanced.ZeroTierEnabled));
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) || value == "-" ? "Unknown" : value;
}

public static class RouterStateSnapshotComparer
{
    public static IReadOnlyList<RouterStateChange> Compare(RouterStateSnapshot older, RouterStateSnapshot newer)
    {
        if (older.ProfileId != newer.ProfileId) return [];
        List<RouterStateChange> changes = [];
        Add(changes, "System", "Firmware", older.FirmwareVersion, newer.FirmwareVersion, "Notable", "Maintenance");
        Add(changes, "System", "Network mode", older.System.NetworkMode, newer.System.NetworkMode, "Notable", "Network");
        Add(changes, "Network", "Guest Network", older.Network.GuestEnabled, newer.Network.GuestEnabled, "Notable", "Network");
        Add(changes, "Network", "IoT Network", older.Network.IoTEnabled, newer.Network.IoTEnabled, "Notable", "Network");
        Add(changes, "Network", "NAT masquerade", older.Network.NatMasquerade, newer.Network.NatMasquerade, "Information", "Network");
        Add(changes, "Traffic Processing", "SQM", older.Traffic.SqmEnabled, newer.Traffic.SqmEnabled, "Information", "Performance");
        Add(changes, "Traffic Processing", "SQM queue discipline", older.Traffic.SqmQueueDiscipline, newer.Traffic.SqmQueueDiscipline, "Information", "Performance");
        Add(changes, "Traffic Processing", "SQM download", older.Traffic.SqmDownload, newer.Traffic.SqmDownload, "Information", "Performance");
        Add(changes, "Traffic Processing", "SQM upload", older.Traffic.SqmUpload, newer.Traffic.SqmUpload, "Information", "Performance");
        Add(changes, "Traffic Processing", "DPI configured", older.Traffic.DpiConfigured, newer.Traffic.DpiConfigured, "Information", "Performance");
        Add(changes, "Services", "WebDAV", older.Services.WebDavEnabled, newer.Services.WebDavEnabled, "Information", "Storage");
        Add(changes, "Services", "DLNA runtime", older.Services.DlnaRunning, newer.Services.DlnaRunning, "Information", "Storage");
        Add(changes, "Services", "ZeroTier", older.Services.ZeroTierEnabled, newer.Services.ZeroTierEnabled, "Information", "VPN");
        return changes.OrderByDescending(change => change.Importance == "Notable").ThenBy(change => change.Category).ThenBy(change => change.Field).ToList();
    }

    private static void Add<T>(ICollection<RouterStateChange> changes, string category, string field, T? oldValue, T? newValue, string importance, string destination) where T : struct
    {
        if (!oldValue.HasValue || !newValue.HasValue || EqualityComparer<T>.Default.Equals(oldValue.Value, newValue.Value)) return;
        changes.Add(new(category, field, "Changed", Format(oldValue), Format(newValue), importance, destination));
    }

    private static void Add(ICollection<RouterStateChange> changes, string category, string field, string oldValue, string newValue, string importance, string destination)
    {
        if (oldValue == "Unknown" || newValue == "Unknown" || oldValue == newValue) return;
        changes.Add(new(category, field, "Changed", oldValue, newValue, importance, destination));
    }

    private static string Format<T>(T value) => value is bool boolean ? (boolean ? "Enabled" : "Disabled") : value?.ToString() ?? "Unknown";
}
