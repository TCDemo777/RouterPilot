using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using RouterPilot.Models;

namespace RouterPilot.Services;

// Restored shared update checker; About invokes this service rather than issuing HTTP itself.
public sealed class UpdateService : IDisposable
{
    public const string ReleasesPageUrl = "https://github.com/TCDemo777/RouterPilot/releases";
    private const string ReleasesApiUrl = "https://api.github.com/repos/TCDemo777/RouterPilot/releases?per_page=20";
    private readonly SettingsService _settingsService;
    private readonly NotificationService _notificationService;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public UpdateService(SettingsService settingsService, NotificationService notificationService)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RouterPilot", CurrentVersion));
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public string CurrentVersion => GetCurrentVersion();
    public ReleaseInfo? LatestRelease { get; private set; }
    public DateTimeOffset? LastSuccessfulCheck => _settingsService.Load().LastSuccessfulUpdateCheckUtc;

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(bool manual, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AppSettings settings = _settingsService.Load();
            try
            {
                using HttpResponseMessage response = await _httpClient.GetAsync(
                    ReleasesApiUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                    return Result(UpdateCheckStatus.Unavailable, "GitHub rate limiting prevented the update check.");
                response.EnsureSuccessStatusCode();
                await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

                LatestRelease = document.RootElement.EnumerateArray()
                    .Where(item => !GetBoolean(item, "draft") && !GetBoolean(item, "prerelease"))
                    .Select(ParseRelease)
                    .Where(item => SemanticVersion.TryParse(item.Version, out _))
                    .OrderByDescending(item => SemanticVersion.Parse(item.Version))
                    .FirstOrDefault();

                DateTimeOffset now = DateTimeOffset.UtcNow;
                settings.LastSuccessfulUpdateCheckUtc = now;
                settings.LatestVersionSeen = LatestRelease?.Version ?? string.Empty;
                _settingsService.Save(settings);

                if (LatestRelease is null || SemanticVersion.Parse(LatestRelease.Version) <= SemanticVersion.Parse(CurrentVersion))
                    return Result(UpdateCheckStatus.UpToDate, "RouterPilot is up to date.", now);

                if (!string.Equals(settings.LastNotifiedUpdateVersion, LatestRelease.Version, StringComparison.OrdinalIgnoreCase))
                {
                    bool added = await _notificationService.AddAsync(new AppNotification
                    {
                        Title = $"RouterPilot {LatestRelease.Version} is available.",
                        Message = "Open GitHub Releases to view the release notes and downloads.",
                        Severity = NotificationSeverity.Information,
                        Category = NotificationCategory.System,
                        ActionTarget = LatestRelease.ReleaseNotesUrl?.AbsoluteUri ?? ReleasesPageUrl,
                        DeduplicationKey = "RouterPilotUpdate-" + LatestRelease.Version
                    });
                    if (added)
                    {
                        settings.LastNotifiedUpdateVersion = LatestRelease.Version;
                        _settingsService.Save(settings);
                    }
                }
                return Result(UpdateCheckStatus.UpdateAvailable, $"RouterPilot {LatestRelease.Version} is available.", now);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { return Result(UpdateCheckStatus.Unavailable, "The GitHub update check timed out."); }
            catch (HttpRequestException)
            { return Result(UpdateCheckStatus.Unavailable, "GitHub Releases is currently unavailable."); }
            catch (JsonException)
            { return Result(UpdateCheckStatus.Unavailable, "GitHub returned an unreadable release response."); }
        }
        finally { _gate.Release(); }
    }

    private UpdateCheckResult Result(UpdateCheckStatus status, string message, DateTimeOffset? checkedAt = null) => new()
    { Status = status, CurrentVersion = CurrentVersion, LatestRelease = LatestRelease, CheckedAt = checkedAt ?? LastSuccessfulCheck, Message = message };

    private static ReleaseInfo ParseRelease(JsonElement item)
    {
        string tag = GetString(item, "tag_name");
        return new ReleaseInfo
        {
            Tag = tag,
            Version = SemanticVersion.Normalize(tag),
            PublishedAt = item.TryGetProperty("published_at", out JsonElement date) && date.TryGetDateTimeOffset(out DateTimeOffset published) ? published : null,
            ReleaseNotesUrl = TryGetTrustedGitHubUrl(GetString(item, "html_url")),
            IsPrerelease = GetBoolean(item, "prerelease")
        };
    }

    private static string GetCurrentVersion()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        string value = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "0.0.0";
        int metadata = value.IndexOf('+');
        return SemanticVersion.Normalize(metadata >= 0 ? value[..metadata] : value);
    }

    private static string GetString(JsonElement item, string name) => item.TryGetProperty(name, out JsonElement value) ? value.GetString() ?? string.Empty : string.Empty;
    private static bool GetBoolean(JsonElement item, string name) => item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private static Uri? TryGetTrustedGitHubUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(uri.Host, "www.github.com", StringComparison.OrdinalIgnoreCase)
            ? uri
            : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _httpClient.Dispose(); _gate.Dispose();
    }

    private readonly record struct SemanticVersion(int Major, int Minor, int Patch, string Prerelease) : IComparable<SemanticVersion>
    {
        public static string Normalize(string value) => value.Trim().TrimStart('v', 'V');
        public static bool TryParse(string value, out SemanticVersion version)
        {
            version = default; string[] split = Normalize(value).Split('-', 2); string[] numbers = split[0].Split('.');
            if (numbers.Length < 2 || !int.TryParse(numbers[0], out int major) || !int.TryParse(numbers[1], out int minor) ||
                (numbers.Length > 2 && !int.TryParse(numbers[2], out _))) return false;
            version = new(major, minor, numbers.Length > 2 ? int.Parse(numbers[2]) : 0, split.Length > 1 ? split[1] : string.Empty); return true;
        }
        public static SemanticVersion Parse(string value) => TryParse(value, out SemanticVersion result) ? result : default;
        public int CompareTo(SemanticVersion other)
        {
            int result = Major.CompareTo(other.Major); if (result == 0) result = Minor.CompareTo(other.Minor); if (result == 0) result = Patch.CompareTo(other.Patch);
            if (result != 0) return result; if (Prerelease.Length == 0) return other.Prerelease.Length == 0 ? 0 : 1; if (other.Prerelease.Length == 0) return -1;
            return string.Compare(Prerelease, other.Prerelease, StringComparison.OrdinalIgnoreCase);
        }
        public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;
        public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;
    }
}
