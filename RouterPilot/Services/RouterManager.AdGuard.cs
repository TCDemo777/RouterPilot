using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;
using RouterPilot.Configuration;

namespace RouterPilot.Services
{
    internal sealed record AdGuardQueryLogReadResult(
        bool IsAvailable,
        List<QueryLogEntry> Entries);

    public partial class RouterManager
    {
        // AdGuard Status
        //

        public async Task<AdGuardStatus>
            GetAdGuardStatusAsync()
        {
            string service =
                await _ssh.RunCommandAsync(
                    "/etc/init.d/adguardhome status");

            string process =
                await _ssh.RunCommandAsync(
                    "pgrep -a AdGuardHome");

            string version =
                await _ssh.RunCommandAsync(
                    "/usr/bin/AdGuardHome --version");

            return new AdGuardStatus
            {
                IsRunning =
                    service.Contains(
                        "running",
                        StringComparison.OrdinalIgnoreCase),

                ServiceStatus =
                    service.Trim(),

                Process =
                    string.IsNullOrWhiteSpace(process)
                        ? "Not Running"
                        : process.Trim(),

                Version =
                    version.Trim()
            };
        }


        //
        // AdGuard Protection
        //

        public async Task<AdGuardProtectionStatus>
            GetAdGuardProtectionStatusAsync()
        {
            string token =
                await GetAdminTokenAsync();

            AdGuardControlResponse response =
                await RequestAdGuardControlAsync(
                    HttpMethod.Get,
                    "status",
                    token);

            if (response.RequiresNewToken)
            {
                InvalidateAdminToken();

                token =
                    await GetAdminTokenAsync();

                response =
                    await RequestAdGuardControlAsync(
                        HttpMethod.Get,
                        "status",
                        token);
            }

            if (!response.IsSuccess)
            {
                throw CreateAdGuardControlException(
                    "read protection status",
                    response);
            }

            return ParseAdGuardProtectionStatus(
                response.Content);
        }

        public Task<AdGuardProtectionStatus>
            EnableProtectionAsync()
        {
            return SetAdGuardProtectionAsync(
                true,
                TimeSpan.Zero);
        }

        public Task<AdGuardProtectionStatus>
            ResumeProtectionAsync()
        {
            return SetAdGuardProtectionAsync(
                true,
                TimeSpan.Zero);
        }

        public Task<AdGuardProtectionStatus>
            DisableProtectionAsync()
        {
            return SetAdGuardProtectionAsync(
                false,
                TimeSpan.Zero);
        }

        public Task<AdGuardProtectionStatus>
            PauseProtectionAsync(
                TimeSpan duration)
        {
            if (duration <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(duration),
                    "Pause duration must be greater than zero.");
            }

            return SetAdGuardProtectionAsync(
                false,
                duration);
        }

        private async Task<AdGuardProtectionStatus>
            SetAdGuardProtectionAsync(
                bool enabled,
                TimeSpan duration)
        {
            long durationMilliseconds =
                enabled
                    ? 0
                    : Math.Max(
                        0,
                        (long)duration.TotalMilliseconds);

            string requestJson =
                JsonSerializer.Serialize(
                    new
                    {
                        enabled,
                        duration =
                            durationMilliseconds
                    });

            string token =
                await GetAdminTokenAsync();

            AdGuardControlResponse response =
                await RequestAdGuardControlAsync(
                    HttpMethod.Post,
                    "protection",
                    token,
                    requestJson);

            if (response.RequiresNewToken)
            {
                InvalidateAdminToken();

                token =
                    await GetAdminTokenAsync();

                response =
                    await RequestAdGuardControlAsync(
                        HttpMethod.Post,
                        "protection",
                        token,
                        requestJson);
            }

            if (!response.IsSuccess)
            {
                throw CreateAdGuardControlException(
                    enabled
                        ? "enable protection"
                        : "disable protection",
                    response);
            }

            return await GetAdGuardProtectionStatusAsync();
        }

        private async Task<AdGuardControlResponse>
            RequestAdGuardControlAsync(
                HttpMethod method,
                string endpoint,
                string token,
                string? json = null,
                CancellationToken cancellationToken = default)
        {
            AdGuardHttpResponse response =
                await SendAdGuardRequestAsync(
                    method,
                    "control/" + endpoint.TrimStart('/'),
                    token,
                    json,
                    timeout: TimeSpan.FromSeconds(10),
                    noCache: false,
                    cancellationToken: cancellationToken);

            return new AdGuardControlResponse(
                response.StatusCode,
                response.Content);
        }

        private async Task<AdGuardHttpResponse>
            SendAdGuardRequestAsync(
                HttpMethod method,
                string relativeUrl,
                string token,
                string? json,
                TimeSpan timeout,
                bool noCache,
                CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentException.ThrowIfNullOrWhiteSpace(token);

            lock (_adGuardCookieLock)
            {
                _adGuardCookies.SetCookies(
                    _adGuardBaseUri,
                    $"Admin-Token={token}; Path=/");
            }

            Uri url = new Uri(
                _adGuardBaseUri,
                relativeUrl.TrimStart('/'));

            using var request = new HttpRequestMessage(method, url);

            if (json is not null)
            {
                request.Content = new ByteArrayContent(
                    System.Text.Encoding.UTF8.GetBytes(json));
                request.Content.Headers.TryAddWithoutValidation(
                    "Content-Type",
                    "application/json");
            }

            if (noCache)
            {
                request.Headers.CacheControl =
                    new System.Net.Http.Headers.CacheControlHeaderValue
                    {
                        NoCache = true,
                        NoStore = true,
                        MustRevalidate = true
                    };
                request.Headers.Pragma.ParseAdd("no-cache");
            }

            Debug.WriteLine($"Calling AdGuard {method}: {url}");

            using var timeoutCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            timeoutCts.CancelAfter(timeout);

            try
            {
                using HttpResponseMessage response =
                    await _adGuardClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        timeoutCts.Token);

                string content = await response.Content
                    .ReadAsStringAsync(timeoutCts.Token);

                _adGuardTransportSecurity.MarkAvailable(_adGuardBaseUri);

                Debug.WriteLine(
                    $"AdGuard status: {(int)response.StatusCode} " +
                    response.StatusCode);

                return new AdGuardHttpResponse(
                    response.StatusCode,
                    content);
            }
            catch (HttpRequestException exception) when (
                IsCertificateValidationFailure(exception))
            {
                _adGuardTransportSecurity.MarkUnavailable(
                    "AdGuard Home HTTPS certificate validation failed.");
                throw new InvalidOperationException(
                    "AdGuard Home HTTPS certificate validation failed. " +
                    "Verify the configured endpoint and certificate.",
                    exception);
            }
            catch (HttpRequestException)
            {
                _adGuardTransportSecurity.MarkUnavailable(
                    "AdGuard Home transport is unavailable.");
                throw;
            }
            catch (TaskCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                _adGuardTransportSecurity.MarkUnavailable(
                    "AdGuard Home transport timed out.");
                throw;
            }
        }

        private static bool IsCertificateValidationFailure(Exception exception)
        {
            for (Exception? current = exception;
                 current is not null;
                 current = current.InnerException)
            {
                if (current is System.Security.Authentication.AuthenticationException)
                {
                    return true;
                }
            }

            return false;
        }

        private static AdGuardProtectionStatus
            ParseAdGuardProtectionStatus(
                string json)
        {
            using JsonDocument document =
                JsonDocument.Parse(
                    json);

            JsonElement root =
                document.RootElement;

            bool enabled =
                root.TryGetProperty(
                    "protection_enabled",
                    out JsonElement enabledElement) &&
                enabledElement.ValueKind ==
                    JsonValueKind.True;

            long remainingMilliseconds =
                0;

            if (root.TryGetProperty(
                    "protection_disabled_duration",
                    out JsonElement durationElement))
            {
                if (!durationElement.TryGetInt64(
                        out remainingMilliseconds) &&
                    durationElement.TryGetDouble(
                        out double durationDouble))
                {
                    remainingMilliseconds =
                        (long)durationDouble;
                }
            }

            remainingMilliseconds =
                Math.Max(
                    0,
                    remainingMilliseconds);

            return new AdGuardProtectionStatus
            {
                IsEnabled =
                    enabled,

                IsPaused =
                    !enabled &&
                    remainingMilliseconds > 0,

                RemainingPause =
                    TimeSpan.FromMilliseconds(
                        remainingMilliseconds)
            };
        }

        private static Exception
            CreateAdGuardControlException(
                string action,
                AdGuardControlResponse response)
        {
            return new InvalidOperationException(
                $"Unable to {action}. " +
                $"AdGuard Home returned HTTP " +
                $"{(int)response.StatusCode} " +
                $"{response.StatusCode}.");
        }


        //
        // AdGuard Protection Management
        //

        public async Task<AdGuardProtectionOptions> GetProtectionOptionsAsync()
        {
            var filtering = await GetControlJsonAsync("filtering/status");
            var safeBrowsing = await GetControlJsonAsync("safebrowsing/status");
            var parental = await GetControlJsonAsync("parental/status");
            var safeSearch = await GetControlJsonAsync("safesearch/status");
            var queryLog = await GetControlJsonAsync("querylog/config");

            return new AdGuardProtectionOptions
            {
                FilteringEnabled = GetBoolean(filtering, "enabled"),
                FilteringIntervalHours = GetInteger(filtering, "interval", 24),
                SafeBrowsingEnabled = GetBoolean(safeBrowsing, "enabled"),
                ParentalEnabled = GetBoolean(parental, "enabled"),
                SafeSearchEnabled = GetBoolean(safeSearch, "enabled"),
                QueryLogEnabled = GetBoolean(queryLog, "enabled"),
                QueryLogAnonymizeClientIp = GetBoolean(queryLog, "anonymize_client_ip"),
                QueryLogInterval = GetDouble(queryLog, "interval", 24),
                QueryLogIgnored = GetStringArray(queryLog, "ignored"),
                SafeSearch = new AdGuardSafeSearchSettings
                {
                    Enabled = GetBoolean(safeSearch, "enabled"),
                    Bing = GetBoolean(safeSearch, "bing", true),
                    DuckDuckGo = GetBoolean(safeSearch, "duckduckgo", true),
                    Ecosia = GetBoolean(safeSearch, "ecosia", true),
                    Google = GetBoolean(safeSearch, "google", true),
                    Pixabay = GetBoolean(safeSearch, "pixabay", true),
                    Yandex = GetBoolean(safeSearch, "yandex", true),
                    YouTube = GetBoolean(safeSearch, "youtube", true)
                }
            };
        }

        public Task SetFilteringEnabledAsync(bool enabled) => SendControlJsonAsync(HttpMethod.Post, "filtering/config", JsonSerializer.Serialize(new { enabled, interval = 24 }));
        public Task SetSafeBrowsingEnabledAsync(bool enabled) => SendControlWithoutBodyAsync(HttpMethod.Post, enabled ? "safebrowsing/enable" : "safebrowsing/disable");
        public Task SetParentalEnabledAsync(bool enabled) => SendControlWithoutBodyAsync(HttpMethod.Post, enabled ? "parental/enable" : "parental/disable");

        public Task SetSafeSearchEnabledAsync(bool enabled, AdGuardSafeSearchSettings current)
        {
            string json = JsonSerializer.Serialize(new
            {
                enabled,
                bing = current.Bing,
                duckduckgo = current.DuckDuckGo,
                ecosia = current.Ecosia,
                google = current.Google,
                pixabay = current.Pixabay,
                yandex = current.Yandex,
                youtube = current.YouTube
            });
            return SendControlJsonAsync(HttpMethod.Put, "safesearch/settings", json);
        }

        public Task SetQueryLogEnabledAsync(bool enabled, AdGuardProtectionOptions current)
        {
            string json = JsonSerializer.Serialize(new
            {
                enabled,
                anonymize_client_ip = current.QueryLogAnonymizeClientIp,
                interval = current.QueryLogInterval <= 0 ? 24 : current.QueryLogInterval,
                ignored = current.QueryLogIgnored
            });
            return SendControlJsonAsync(HttpMethod.Put, "querylog/config/update", json);
        }

        public async Task<(List<BlockedServiceItem> Services, AdGuardBlockedServicesConfig Config)> GetBlockedServicesAsync()
        {
            List<BlockedServiceItem> result = await GetBlockedServiceCatalogueAsync();
            AdGuardBlockedServicesConfig config = await GetBlockedServicesConfigAsync();
            foreach (BlockedServiceItem service in result) service.IsBlocked = config.EnabledIds.Contains(service.Id);
            return (result, config);
        }

        public async Task<AdGuardBlockedServicesConfig> GetBlockedServicesConfigAsync()
        {
            JsonElement configJson = await GetControlJsonAsync("blocked_services/get");

            var config =
                new AdGuardBlockedServicesConfig();

            if (configJson.TryGetProperty(
                    "schedule",
                    out JsonElement schedule))
            {
                config.ScheduleJson =
                    schedule.GetRawText();
            }

            foreach (string id in
                GetStringArray(
                    configJson,
                    "ids"))
            {
                config.EnabledIds.Add(id);
            }

            return config;
        }

        public async Task<List<BlockedServiceItem>> GetBlockedServiceCatalogueAsync()
        {
            JsonElement all = await GetControlJsonAsync("blocked_services/all");
            var result = new List<BlockedServiceItem>();

            JsonElement array =
                default;

            if (all.ValueKind ==
                JsonValueKind.Array)
            {
                array = all;
            }
            else if (all.ValueKind ==
                     JsonValueKind.Object)
            {
                // AdGuard Home versions expose the catalogue under either
                // "blocked_services" or "services".
                if (!all.TryGetProperty(
                        "blocked_services",
                        out array))
                {
                    all.TryGetProperty(
                        "services",
                        out array);
                }
            }

            if (array.ValueKind ==
                JsonValueKind.Array)
            {
                foreach (JsonElement item in
                    array.EnumerateArray())
                {
                    string id;
                    string name;

                    if (item.ValueKind ==
                        JsonValueKind.String)
                    {
                        id =
                            item.GetString()?.Trim() ??
                            string.Empty;

                        name =
                            FormatBlockedServiceName(id);
                    }
                    else if (item.ValueKind ==
                             JsonValueKind.Object)
                    {
                        id =
                            GetString(
                                item,
                                "id");

                        if (id.Length == 0)
                        {
                            id =
                                GetString(
                                    item,
                                    "service_id");
                        }

                        name =
                            GetString(
                                item,
                                "name");

                        if (name.Length == 0)
                        {
                            name =
                                GetString(
                                    item,
                                    "display_name");
                        }

                        if (name.Length == 0)
                        {
                            name =
                                FormatBlockedServiceName(id);
                        }
                    }
                    else
                    {
                        continue;
                    }

                    if (id.Length == 0 ||
                        result.Any(service =>
                            service.Id.Equals(
                                id,
                                StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    result.Add(
                        new BlockedServiceItem
                        {
                            Id = id,
                            Name = name,
                            Category = CategorizeBlockedService(id, name),
                            IconSvg = item.ValueKind == JsonValueKind.Object ? GetString(item, "icon_svg") : string.Empty,
                            GroupId = item.ValueKind == JsonValueKind.Object ? GetString(item, "group_id") : string.Empty,
                            IsBlocked = false
                        });
                }
            }

            return result;
        }

        public Task UpdateBlockedServicesAsync(IEnumerable<string> ids, string scheduleJson)
        {
            JsonNode schedule = JsonNode.Parse(string.IsNullOrWhiteSpace(scheduleJson) ? "{}" : scheduleJson) ?? new JsonObject();
            var idArray = new JsonArray();
            foreach (string id in ids.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                idArray.Add(id);
            }
            var root = new JsonObject { ["schedule"] = schedule, ["ids"] = idArray };
            return SendControlJsonAsync(HttpMethod.Put, "blocked_services/update", root.ToJsonString());
        }

        public async Task<List<CustomFilteringRule>> GetCustomFilteringRulesAsync()
        {
            JsonElement status = await GetControlJsonAsync("filtering/status");
            var result = new List<CustomFilteringRule>();
            if (status.TryGetProperty("user_rules", out JsonElement rules) && rules.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in rules.EnumerateArray())
                {
                    string rule = item.GetString()?.Trim() ?? "";
                    if (rule.Length == 0) continue;
                    string type = rule.StartsWith("@@", StringComparison.Ordinal) ? "Allow" : rule.StartsWith("||", StringComparison.Ordinal) ? "Block" : "Custom";
                    result.Add(new CustomFilteringRule { Rule = rule, Type = type });
                }
            }
            return result;
        }

        public Task SetCustomFilteringRulesAsync(IEnumerable<string> rules) => SendControlJsonAsync(HttpMethod.Post, "filtering/set_rules", JsonSerializer.Serialize(new { rules = rules.ToArray() }));

        public async Task<List<AdGuardBlocklist>> GetBlocklistsAsync()
        {
            JsonElement status = await GetControlJsonAsync("filtering/status");
            return ParseBlocklists(status);
        }

        private static List<AdGuardBlocklist> ParseBlocklists(JsonElement status)
        {
            var result = new List<AdGuardBlocklist>();
            if (!status.TryGetProperty("filters", out JsonElement filters) ||
                filters.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (JsonElement filter in filters.EnumerateArray())
            {
                string url = GetString(filter, "url");
                if (url.Length == 0)
                    continue;

                result.Add(new AdGuardBlocklist
                {
                    Id = GetInt64(filter, "id"),
                    Name = GetString(filter, "name"),
                    Url = url,
                    Enabled = GetBoolean(filter, "enabled"),
                    RuleCount = GetInt64(filter, "rules_count"),
                    LastUpdated = GetString(filter, "last_updated"),
                    Status = GetString(filter, "status")
                });
            }

            return result;
        }

        public Task AddBlocklistAsync(AdGuardBlocklistDraft draft) =>
            SendControlJsonAsync(HttpMethod.Post, "filtering/add_url", JsonSerializer.Serialize(new
            {
                name = draft.Name,
                url = draft.Url,
                whitelist = false
            }));

        public Task SetBlocklistAsync(string currentUrl, AdGuardBlocklistDraft draft) =>
            SendControlJsonAsync(HttpMethod.Post, "filtering/set_url", JsonSerializer.Serialize(new
            {
                url = currentUrl,
                whitelist = false,
                data = new { name = draft.Name, url = draft.Url, enabled = draft.Enabled }
            }));

        public Task RemoveBlocklistAsync(string url) =>
            SendControlJsonAsync(HttpMethod.Post, "filtering/remove_url", JsonSerializer.Serialize(new
            {
                url,
                whitelist = false
            }));

        public async Task<int> RefreshBlocklistsAsync()
        {
            AdGuardControlResponse response = await SendAuthenticatedControlAsync(
                HttpMethod.Post, "filtering/refresh", JsonSerializer.Serialize(new { whitelist = false }));
            if (!response.IsSuccess)
                throw CreateAdGuardControlException("refresh blocklists", response);

            using JsonDocument document = JsonDocument.Parse(response.Content);
            return GetInteger(document.RootElement, "updated", 0);
        }

        public async Task<List<DnsRewriteRule>> GetDnsRewritesAsync()
        {
            JsonElement root = await GetControlJsonAsync("rewrite/list");
            var result = new List<DnsRewriteRule>();
            JsonElement array = root.ValueKind == JsonValueKind.Array ? root : (root.TryGetProperty("rewrites", out JsonElement rewrites) ? rewrites : default);
            if (array.ValueKind == JsonValueKind.Array)
                foreach (JsonElement item in array.EnumerateArray()) result.Add(new DnsRewriteRule { Domain = GetString(item, "domain"), Answer = GetString(item, "answer") });
            return result;
        }

        public Task AddDnsRewriteAsync(string domain, string answer) => SendControlJsonAsync(HttpMethod.Post, "rewrite/add", JsonSerializer.Serialize(new { domain, answer }));
        public Task DeleteDnsRewriteAsync(string domain, string answer) => SendControlJsonAsync(HttpMethod.Post, "rewrite/delete", JsonSerializer.Serialize(new { domain, answer }));

        private async Task<JsonElement> GetControlJsonAsync(string endpoint)
        {
            AdGuardControlResponse response = await SendAuthenticatedControlAsync(HttpMethod.Get, endpoint, null);
            if (!response.IsSuccess) throw CreateAdGuardControlException("read " + endpoint, response);
            using JsonDocument document = JsonDocument.Parse(response.Content);
            return document.RootElement.Clone();
        }

        private async Task SendControlJsonAsync(HttpMethod method, string endpoint, string json)
        {
            AdGuardControlResponse response = await SendAuthenticatedControlAsync(method, endpoint, json);
            if (!response.IsSuccess) throw CreateAdGuardControlException("update " + endpoint, response);
        }

        private async Task SendControlWithoutBodyAsync(HttpMethod method, string endpoint)
        {
            AdGuardControlResponse response = await SendAuthenticatedControlAsync(method, endpoint, null);
            if (!response.IsSuccess) throw CreateAdGuardControlException("update " + endpoint, response);
        }

        private async Task<AdGuardControlResponse> SendAuthenticatedControlAsync(HttpMethod method, string endpoint, string? json)
        {
            string token = await GetAdminTokenAsync();
            AdGuardControlResponse response = await RequestAdGuardControlAsync(method, endpoint, token, json);
            if (response.RequiresNewToken)
            {
                InvalidateAdminToken();
                token = await GetAdminTokenAsync();
                response = await RequestAdGuardControlAsync(method, endpoint, token, json);
            }
            return response;
        }

        private static bool GetBoolean(JsonElement root, string name, bool fallback = false) => root.TryGetProperty(name, out JsonElement value) ? value.ValueKind == JsonValueKind.True : fallback;
        private static int GetInteger(JsonElement root, string name, int fallback) => root.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result) ? result : fallback;
        private static long GetInt64(JsonElement root, string name) => root.TryGetProperty(name, out JsonElement value) && value.TryGetInt64(out long result) ? result : 0;
        private static double GetDouble(JsonElement root, string name, double fallback) => root.TryGetProperty(name, out JsonElement value) && value.TryGetDouble(out double result) ? result : fallback;
        private static string CategorizeBlockedService(
            string id,
            string name)
        {
            string value =
                $"{id} {name}".ToLowerInvariant();

            if (ContainsAny(value,
                    "playstation", "xbox", "steam", "epic-games",
                    "nintendo", "roblox", "battle.net", "ea ",
                    "gaming", "twitch"))
            {
                return "Gaming";
            }

            if (ContainsAny(value,
                    "netflix", "disney", "hulu", "prime-video",
                    "amazon-prime", "youtube", "vimeo", "dailymotion",
                    "streaming", "paramount", "peacock", "hbo"))
            {
                return "Streaming & Video";
            }

            if (ContainsAny(value,
                    "spotify", "soundcloud", "deezer", "tidal",
                    "apple-music", "music"))
            {
                return "Music";
            }

            if (ContainsAny(value,
                    "facebook", "instagram", "tiktok", "twitter",
                    "x.com", "snapchat", "pinterest", "reddit",
                    "linkedin", "social"))
            {
                return "Social Media";
            }

            if (ContainsAny(value,
                    "whatsapp", "telegram", "signal", "discord",
                    "messenger", "skype", "zoom", "teams",
                    "slack", "communication", "chat"))
            {
                return "Messaging & Meetings";
            }

            if (ContainsAny(value,
                    "dropbox", "onedrive", "google-drive", "icloud",
                    "cloud", "box.com"))
            {
                return "Cloud Storage";
            }

            if (ContainsAny(value,
                    "github", "gitlab", "bitbucket", "stackoverflow",
                    "developer", "coding"))
            {
                return "Development";
            }

            if (ContainsAny(value,
                    "amazon", "ebay", "aliexpress", "etsy",
                    "shopping", "shop"))
            {
                return "Shopping";
            }

            if (ContainsAny(value,
                    "openai", "chatgpt", "claude", "gemini",
                    "copilot", "artificial-intelligence"))
            {
                return "AI Services";
            }

            if (ContainsAny(value,
                    "gmail", "outlook", "protonmail", "yahoo-mail",
                    "email", "mail"))
            {
                return "Email";
            }

            if (ContainsAny(value,
                    "adult", "porn", "xxx"))
            {
                return "Adult Content";
            }

            return "Other";
        }

        private static bool ContainsAny(
            string value,
            params string[] terms)
        {
            return terms.Any(term =>
                value.Contains(
                    term,
                    StringComparison.OrdinalIgnoreCase));
        }

        private static string FormatBlockedServiceName(
            string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return string.Empty;
            }

            string text =
                id.Replace('_', ' ')
                  .Replace('-', ' ');

            return
                System.Globalization.CultureInfo
                    .InvariantCulture
                    .TextInfo
                    .ToTitleCase(
                        text.ToLowerInvariant());
        }

        private static string GetString(JsonElement root, string name) => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? "" : "";
        private static string[] GetStringArray(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out JsonElement array) || array.ValueKind != JsonValueKind.Array) return [];
            return array.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray();
        }

        //
        // AdGuard Statistics
        //

        public async Task<AdGuardStatistics>
            GetAdGuardStatisticsAsync()
        {
            AdGuardStatistics stats =
                AdGuardStatisticsParser.CreateUnavailableStatistics();

            try
            {
                string token =
                    await GetAdminTokenAsync();

                AdGuardStatsResponse firstAttempt =
                    await RequestAdGuardStatisticsAsync(
                        token);

                if (firstAttempt.RequiresNewToken)
                {
                    Debug.WriteLine(
                        "The GL.iNet Admin-Token was rejected. " +
                        "Obtaining a new token and retrying.");

                    InvalidateAdminToken();

                    token =
                        await GetAdminTokenAsync();

                    AdGuardStatsResponse secondAttempt =
                        await RequestAdGuardStatisticsAsync(
                            token);

                    if (!secondAttempt.IsSuccess)
                    {
                        LogFailedAdGuardResponse(
                            secondAttempt);

                        return stats;
                    }

                    return AdGuardStatisticsParser.Parse(
                        secondAttempt.Content,
                        DateTime.Now);
                }

                if (!firstAttempt.IsSuccess)
                {
                    LogFailedAdGuardResponse(
                        firstAttempt);

                    return stats;
                }

                return AdGuardStatisticsParser.Parse(
                    firstAttempt.Content,
                    DateTime.Now);
            }
            catch (TaskCanceledException)
            {
                Debug.WriteLine(
                    "The AdGuard statistics request timed out.");
            }
            catch (HttpRequestException ex)
            {
                LogAdGuardFailure("statistics", ex);
            }
            catch (JsonException ex)
            {
                LogAdGuardFailure("statistics", ex);
            }
            catch (Exception ex)
            {
                LogAdGuardFailure("statistics", ex);
            }

            return stats;
        }

        //
        // AdGuard Clients
        //

        public async Task<List<ClientInfo>>
            GetAdGuardClientsAsync()
        {
            try
            {
                string token =
                    await GetAdminTokenAsync();

                AdGuardClientsResponse clientsResponse =
                    await RequestAdGuardClientsAsync(
                        token);

                if (clientsResponse.RequiresNewToken)
                {
                    InvalidateAdminToken();

                    token =
                        await GetAdminTokenAsync();

                    clientsResponse =
                        await RequestAdGuardClientsAsync(
                            token);
                }

                if (!clientsResponse.IsSuccess)
                {
                    LogFailedClientsResponse(
                        clientsResponse);

                    return new List<ClientInfo>();
                }

                List<ClientInfo> clients =
                    ParseAdGuardClients(
                        clientsResponse.Content);

                AdGuardQueryLogResponse queryLogResponse =
                    await RequestAdGuardQueryLogAsync(
                        token);

                if (queryLogResponse.RequiresNewToken)
                {
                    InvalidateAdminToken();

                    token =
                        await GetAdminTokenAsync();

                    queryLogResponse =
                        await RequestAdGuardQueryLogAsync(
                            token);
                }

                int matchedQueryLogEntries = 0;
                bool queryLogAvailable = false;

                if (queryLogResponse.IsSuccess)
                {
                    queryLogAvailable =
                        QueryLogResponseHasEntries(
                            queryLogResponse.Content);

                    matchedQueryLogEntries =
                        ApplyQueryLogStatistics(
                            clients,
                            queryLogResponse.Content);
                }
                else
                {
                    LogFailedQueryLogResponse(
                        queryLogResponse);
                }

                // An empty log can also mean logging is disabled.  Confirm
                // configuration so cards can explain unavailable fields.
                try
                {
                    JsonElement queryLogConfig =
                        await GetControlJsonAsync(
                            "querylog/config");

                    queryLogAvailable =
                        GetBoolean(
                            queryLogConfig,
                            "enabled");
                }
                catch (Exception ex)
                {
                    LogAdGuardFailure("query-log configuration", ex);
                }

                foreach (ClientInfo client in clients)
                {
                    client.QueryLogAvailable =
                        queryLogAvailable;
                }

                // The query log and statistics store are independent in
                // AdGuard Home.  A valid query-log response can be empty
                // while /control/stats still contains live per-client totals.
                // Always merge top_clients so the cards do not collapse back
                // to zero merely because query-log retrieval is unavailable.
                AdGuardStatsResponse statsResponse =
                    await RequestAdGuardStatisticsAsync(
                        token);

                if (statsResponse.RequiresNewToken)
                {
                    InvalidateAdminToken();
                    token =
                        await GetAdminTokenAsync();

                    statsResponse =
                        await RequestAdGuardStatisticsAsync(
                            token);
                }

                int matchedStatisticsClients = 0;

                if (statsResponse.IsSuccess)
                {
                    matchedStatisticsClients =
                        AdGuardClientCorrelationService.ApplyTopClientTotals(
                            clients,
                            statsResponse.Content);
                }
                else
                {
                    LogFailedAdGuardResponse(
                        statsResponse);
                }

                Debug.WriteLine(
                    "Client activity merge complete. " +
                    $"Query-log matches: {matchedQueryLogEntries}; " +
                    $"statistics matches: {matchedStatisticsClients}.");

                return clients;
            }
            catch (TaskCanceledException)
            {
                Debug.WriteLine(
                    "The AdGuard clients request timed out.");
            }
            catch (HttpRequestException ex)
            {
                LogAdGuardFailure("clients", ex);
            }
            catch (JsonException ex)
            {
                LogAdGuardFailure("clients", ex);
            }
            catch (Exception ex)
            {
                LogAdGuardFailure("clients", ex);
            }

            return new List<ClientInfo>();
        }


        public async Task<string> GetClientDiagnosticsAsync()
        {
            var report =
                new System.Text.StringBuilder();

            report.AppendLine("RouterPilot Client Diagnostics");
            report.AppendLine(
                "Generated: " +
                DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            report.AppendLine("Router endpoint: configured");
            report.AppendLine();

            try
            {
                string token =
                    await GetAdminTokenAsync();

                report.AppendLine("Authentication");
                report.AppendLine("--------------");
                report.AppendLine("Administrative authentication: succeeded");
                report.AppendLine();

                AdGuardClientsResponse clientsResponse =
                    await RequestAdGuardClientsAsync(
                        token);

                report.AppendLine("Clients endpoint");
                report.AppendLine("----------------");
                report.AppendLine(
                    $"HTTP {(int)clientsResponse.StatusCode} " +
                    clientsResponse.StatusCode);

                if (clientsResponse.IsSuccess)
                {
                    List<ClientInfo> clients =
                        ParseAdGuardClients(
                            clientsResponse.Content);

                    report.AppendLine(
                        "Configured clients parsed: " +
                        clients.Count);

                    report.AppendLine("Client identifiers: excluded from diagnostics");
                }

                report.AppendLine();

                AdGuardQueryLogResponse queryLogResponse =
                    await RequestAdGuardQueryLogAsync(
                        token,
                        500);

                report.AppendLine("Query-log endpoint");
                report.AppendLine("------------------");
                report.AppendLine(
                    $"HTTP {(int)queryLogResponse.StatusCode} " +
                    queryLogResponse.StatusCode);

                AppendQueryLogDiagnosticSummary(
                    report,
                    queryLogResponse.Content);

                report.AppendLine();

                AdGuardStatsResponse statsResponse =
                    await RequestAdGuardStatisticsAsync(
                        token);

                report.AppendLine("Statistics endpoint");
                report.AppendLine("-------------------");
                report.AppendLine(
                    $"HTTP {(int)statsResponse.StatusCode} " +
                    statsResponse.StatusCode);

                AppendStatisticsDiagnosticSummary(
                    report,
                    statsResponse.Content);

                report.AppendLine();

                AdGuardControlResponse queryLogConfig =
                    await RequestAdGuardControlAsync(
                        HttpMethod.Get,
                        "querylog/config",
                        token);

                report.AppendLine("Query-log configuration");
                report.AppendLine("-----------------------");
                report.AppendLine(
                    $"HTTP {(int)queryLogConfig.StatusCode} " +
                    queryLogConfig.StatusCode);

                AppendConfigurationDiagnosticSummary(
                    report,
                    queryLogConfig.Content);

                report.AppendLine();
                report.AppendLine("Interpretation");
                report.AppendLine("--------------");
                report.AppendLine(
                    "Queries are merged from statistics/top_clients. " +
                    "Blocked and Last seen require matching query-log entries.");
                report.AppendLine(
                    "A disabled query log is safe to repair from this page; " +
                    "the existing retention and privacy settings are preserved.");
            }
            catch (Exception ex)
            {
                report.AppendLine();
                report.AppendLine("Diagnostics failed");
                report.AppendLine("------------------");
                report.AppendLine(
                    "Failure category: " +
                    DiagnosticRedactor.FailureCategory(ex));
            }

            return report.ToString();
        }

        private static void AppendQueryLogDiagnosticSummary(
            System.Text.StringBuilder report,
            string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                report.AppendLine("Response body: empty");
                return;
            }

            try
            {
                using JsonDocument document =
                    JsonDocument.Parse(json);

                JsonElement root =
                    document.RootElement;

                int count =
                    root.TryGetProperty(
                        "data",
                        out JsonElement data) &&
                    data.ValueKind == JsonValueKind.Array
                        ? data.GetArrayLength()
                        : -1;

                report.AppendLine(
                    "Entries returned: " +
                    (count < 0
                        ? "data array missing"
                        : count));

                report.AppendLine(
                    "Oldest cursor: " +
                    GetStringProperty(
                        root,
                        "oldest",
                        "(missing)"));

                if (count > 0)
                    report.AppendLine("Query-log client identifiers: excluded from diagnostics");
            }
            catch (JsonException)
            {
                report.AppendLine(
                    "Invalid JSON response.");
            }
        }

        private static void AppendStatisticsDiagnosticSummary(
            System.Text.StringBuilder report,
            string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                report.AppendLine("Response body: empty");
                return;
            }

            try
            {
                using JsonDocument document =
                    JsonDocument.Parse(json);

                JsonElement root =
                    document.RootElement;

                report.AppendLine(
                    "Total DNS queries: " +
                    GetIntegerProperty(
                        root,
                        "num_dns_queries",
                        -1));

                report.AppendLine(
                    "Blocked queries: " +
                    GetIntegerProperty(
                        root,
                        "num_blocked_filtering",
                        -1));

                int topClientCount =
                    root.TryGetProperty(
                        "top_clients",
                        out JsonElement topClients) &&
                    topClients.ValueKind == JsonValueKind.Array
                        ? topClients.GetArrayLength()
                        : -1;

                report.AppendLine(
                    "top_clients entries: " +
                    (topClientCount < 0
                        ? "missing"
                        : topClientCount));

                if (topClientCount > 0)
                    report.AppendLine("Top-client identifiers: excluded from diagnostics");
            }
            catch (JsonException)
            {
                report.AppendLine("Response format: invalid JSON");
            }
        }

        private static void AppendConfigurationDiagnosticSummary(
            System.Text.StringBuilder report,
            string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                report.AppendLine("Response body: empty");
                return;
            }

            try
            {
                using JsonDocument document =
                    JsonDocument.Parse(json);

                JsonElement root =
                    document.RootElement;

                report.AppendLine(
                    "Enabled: " +
                    GetBoolean(
                        root,
                        "enabled"));

                report.AppendLine(
                    "Anonymise client IP: " +
                    GetBoolean(
                        root,
                        "anonymize_client_ip"));

                report.AppendLine(
                    "Retention interval: " +
                    GetDouble(
                        root,
                        "interval",
                        -1));
            }
            catch (JsonException)
            {
                report.AppendLine("Response format: invalid JSON");
            }
        }

        private static int GetIntegerProperty(
            JsonElement root,
            string name,
            int fallback)
        {
            return root.TryGetProperty(
                       name,
                       out JsonElement value) &&
                   TryGetInteger(
                       value,
                       out int result)
                ? result
                : fallback;
        }

        //
        // AdGuard Query Log
        //

        public async Task<List<QueryLogEntry>>
            GetQueryLogAsync(int limit = 500) =>
            (await GetQueryLogResultAsync(limit)).Entries;

        internal async Task<AdGuardQueryLogReadResult>
            GetQueryLogResultAsync(int limit = 500)
        {
            try
            {
                string token =
                    await GetAdminTokenAsync();

                AdGuardQueryLogResponse response =
                    await RequestAdGuardQueryLogAsync(
                        token,
                        limit);

                if (response.RequiresNewToken)
                {
                    InvalidateAdminToken();

                    token =
                        await GetAdminTokenAsync();

                    response =
                        await RequestAdGuardQueryLogAsync(
                            token,
                            limit);
                }

                if (!response.IsSuccess)
                {
                    LogFailedQueryLogResponse(
                        response);

                    return new AdGuardQueryLogReadResult(
                        IsAvailable: false,
                        Entries: []);
                }

                return new AdGuardQueryLogReadResult(
                    IsAvailable: true,
                    Entries: ParseAdGuardQueryLog(
                        response.Content));
            }
            catch (TaskCanceledException)
            {
                Debug.WriteLine(
                    "The AdGuard query-log request timed out.");
            }
            catch (HttpRequestException ex)
            {
                LogAdGuardFailure("query-log", ex);
            }
            catch (JsonException ex)
            {
                LogAdGuardFailure("query-log", ex);
            }
            catch (Exception ex)
            {
                LogAdGuardFailure("query-log", ex);
            }

            return new AdGuardQueryLogReadResult(
                IsAvailable: false,
                Entries: []);
        }

        private async Task<AdGuardClientsResponse>
            RequestAdGuardClientsAsync(
                string token,
                CancellationToken cancellationToken = default)
        {
            AdGuardHttpResponse response =
                await SendAdGuardRequestAsync(
                    HttpMethod.Get,
                    "control/clients",
                    token,
                    json: null,
                    timeout: TimeSpan.FromSeconds(10),
                    noCache: false,
                    cancellationToken: cancellationToken);

            return new AdGuardClientsResponse(
                response.StatusCode,
                response.Content);
        }

        private async Task<AdGuardQueryLogResponse>
            RequestAdGuardQueryLogAsync(
                string token,
                int limit = 5000,
                CancellationToken cancellationToken = default)
        {
            int safeLimit = Math.Clamp(limit, 1, 5000);
            long cacheBuster =
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string futureCursor = Uri.EscapeDataString(
                DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"));

            string[] relativeUrls =
            {
                "control/querylog" +
                $"?search=&response_status=&older_than=&limit={safeLimit}" +
                $"&_={cacheBuster}",
                "control/querylog" +
                $"?search=&response_status=&older_than={futureCursor}" +
                $"&limit={safeLimit}&_={cacheBuster + 1}",
                "control/querylog" +
                $"?limit={safeLimit}&_={cacheBuster + 2}"
            };

            AdGuardQueryLogResponse? lastResponse = null;

            foreach (string relativeUrl in relativeUrls)
            {
                AdGuardHttpResponse response =
                    await SendAdGuardRequestAsync(
                        HttpMethod.Get,
                        relativeUrl,
                        token,
                        json: null,
                        timeout: TimeSpan.FromSeconds(15),
                        noCache: true,
                        cancellationToken: cancellationToken);

                lastResponse = new AdGuardQueryLogResponse(
                    response.StatusCode,
                    response.Content);

                if (response.IsSuccess &&
                    QueryLogResponseHasEntries(response.Content))
                {
                    return lastResponse;
                }
            }

            return lastResponse ??
                new AdGuardQueryLogResponse(
                    HttpStatusCode.ServiceUnavailable,
                    string.Empty);
        }

        private static bool QueryLogResponseHasEntries(
            string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            try
            {
                using JsonDocument document =
                    JsonDocument.Parse(json);

                return document.RootElement.TryGetProperty(
                           "data",
                           out JsonElement data) &&
                       data.ValueKind == JsonValueKind.Array &&
                       data.GetArrayLength() > 0;
            }
            catch
            {
                return false;
            }
        }

        private static List<QueryLogEntry>
            ParseAdGuardQueryLog(
                string json)
        {
            var entries =
                new List<QueryLogEntry>();

            using JsonDocument document =
                JsonDocument.Parse(
                    json);

            JsonElement root =
                document.RootElement;

            if (!root.TryGetProperty(
                    "data",
                    out JsonElement data) ||
                data.ValueKind !=
                    JsonValueKind.Array)
            {
                Debug.WriteLine(
                    "AdGuard query log did not contain " +
                    "a data array.");

                return entries;
            }

            foreach (JsonElement item
                     in data.EnumerateArray())
            {
                string timeText =
                    GetClientStringProperty(
                        item,
                        "time");

                string displayTime =
                    timeText;

                DateTimeOffset? timestampValue = null;

                if (DateTimeOffset.TryParse(
                        timeText,
                        out DateTimeOffset timestamp))
                {
                    timestampValue = timestamp;
                    displayTime =
                        timestamp
                            .ToLocalTime()
                            .ToString(
                                "dd MMM yyyy HH:mm:ss");
                }

                string clientAddress =
                    GetClientStringProperty(
                        item,
                        "client");

                string clientName =
                    GetNestedStringProperty(
                        item,
                        "client_info",
                        "name");

                string client =
                    !string.IsNullOrWhiteSpace(clientName)
                        ? string.IsNullOrWhiteSpace(clientAddress)
                            ? clientName
                            : $"{clientName} ({clientAddress})"
                        : string.IsNullOrWhiteSpace(clientAddress)
                            ? "-"
                            : clientAddress;

                string domain =
                    GetQueryDomain(
                        item);

                string reason =
                    GetClientStringProperty(
                        item,
                        "reason");

                entries.Add(
                    new QueryLogEntry
                    {
                        Time =
                            string.IsNullOrWhiteSpace(
                                displayTime)
                                ? "-"
                                : displayTime,

                        Timestamp = timestampValue,

                        Client =
                            client,

                        ClientAddress =
                            clientAddress,

                        ClientName =
                            clientName,

                        Domain =
                            string.IsNullOrWhiteSpace(
                                domain)
                                ? "-"
                                : domain,

                        IsBlocked =
                            IsBlockedQueryReason(
                                reason)
                    });
            }

            Debug.WriteLine(
                $"AdGuard query-log entries loaded: " +
                entries.Count);

            return entries;
        }

        private static string GetQueryDomain(
            JsonElement entry)
        {
            if (!entry.TryGetProperty(
                    "question",
                    out JsonElement question) ||
                question.ValueKind !=
                    JsonValueKind.Object)
            {
                return string.Empty;
            }

            return GetClientStringProperty(
                question,
                "name");
        }

        private static int ApplyQueryLogStatistics(
            List<ClientInfo> clients,
            string json)
        {
            var clientsByAddress =
                new Dictionary<string, ClientInfo>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (ClientInfo client in clients)
            {
                if (!string.IsNullOrWhiteSpace(
                        client.IpAddress) &&
                    client.IpAddress != "-")
                {
                    clientsByAddress[
                        ClientIdentity.NormalizeEndpoint(client.IpAddress)] =
                        client;
                }
            }

            using JsonDocument document =
                JsonDocument.Parse(
                    json);

            JsonElement root =
                document.RootElement;

            if (!root.TryGetProperty(
                    "data",
                    out JsonElement entries) ||
                entries.ValueKind !=
                    JsonValueKind.Array)
            {
                Debug.WriteLine(
                    "AdGuard query log did not contain " +
                    "a data array.");

                return 0;
            }

            int matchedEntries = 0;

            var mostRecentByClient =
                new Dictionary<string, DateTimeOffset>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (JsonElement entry
                     in entries.EnumerateArray())
            {
                string clientAddress =
                    GetClientStringProperty(
                        entry,
                        "client");

                string normalizedClientAddress =
                    ClientIdentity.NormalizeEndpoint(clientAddress);

                if (normalizedClientAddress.Length == 0 ||
                    !clientsByAddress.TryGetValue(
                        normalizedClientAddress,
                        out ClientInfo? client))
                {
                    continue;
                }

                matchedEntries++;
                client.TotalQueries++;

                string reason =
                    GetClientStringProperty(
                        entry,
                        "reason");

                if (IsBlockedQueryReason(
                        reason))
                {
                    client.BlockedQueries++;
                }

                string timeText =
                    GetClientStringProperty(
                        entry,
                        "time");

                if (DateTimeOffset.TryParse(
                        timeText,
                        out DateTimeOffset timestamp))
                {
                    if (!mostRecentByClient.TryGetValue(
                            normalizedClientAddress,
                            out DateTimeOffset current) ||
                        timestamp > current)
                    {
                        mostRecentByClient[
                            normalizedClientAddress] =
                            timestamp;
                    }
                }
            }

            foreach (KeyValuePair<string, DateTimeOffset> item
                     in mostRecentByClient)
            {
                if (clientsByAddress.TryGetValue(
                        item.Key,
                        out ClientInfo? client))
                {
                    client.LastSeen =
                        item.Value
                            .ToLocalTime()
                            .ToString(
                                "dd MMM yyyy HH:mm:ss");
                }
            }

            Debug.WriteLine(
                "Applied query-log statistics to " +
                $"{mostRecentByClient.Count} clients " +
                $"from {matchedEntries} matching entries.");

            return matchedEntries;
        }

        // Compatibility shim retained for existing diagnostics/harness callers;
        // correlation implementation lives in the dedicated service.
        private static int ApplyClientTotalsFromStatistics(
            List<ClientInfo> clients,
            string json) =>
            AdGuardClientCorrelationService.ApplyTopClientTotals(clients, json);

        private static bool IsBlockedQueryReason(
            string reason)
        {
            if (string.IsNullOrWhiteSpace(
                    reason))
            {
                return false;
            }

            bool filteredBlock =
                reason.StartsWith(
                    "Filtered",
                    StringComparison.OrdinalIgnoreCase) &&
                !reason.Contains(
                    "WhiteList",
                    StringComparison.OrdinalIgnoreCase);

            return filteredBlock ||
                   reason.Equals(
                       "SafeBrowsing",
                       StringComparison.OrdinalIgnoreCase) ||
                   reason.Equals(
                       "Parental",
                       StringComparison.OrdinalIgnoreCase) ||
                   reason.Equals(
                       "SafeSearch",
                       StringComparison.OrdinalIgnoreCase) ||
                   reason.Equals(
                       "BlockedService",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static void LogAdGuardFailure(
            string operation,
            Exception exception)
        {
            Debug.WriteLine(
                "AdGuard operation failed. " +
                $"Operation: {operation}; " +
                $"Category: {DiagnosticRedactor.FailureCategory(exception)}.");
        }

        private static void LogFailedQueryLogResponse(
            AdGuardQueryLogResponse response)
        {
            Debug.WriteLine(
                "AdGuard query-log request failed with status " +
                $"{(int)response.StatusCode} " +
                response.StatusCode +
                ".");

        }

        private static List<ClientInfo>
            ParseAdGuardClients(
                string json)
        {
            var clients =
                new List<ClientInfo>();

            var knownIdentifiers =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

            using JsonDocument document =
                JsonDocument.Parse(
                    json);

            JsonElement root =
                document.RootElement;

            ParseConfiguredClients(
                root,
                clients,
                knownIdentifiers);

            ParseAutomaticClients(
                root,
                clients,
                knownIdentifiers);

            clients.Sort(
                (left, right) =>
                    string.Compare(
                        left.Name,
                        right.Name,
                        StringComparison.OrdinalIgnoreCase));

            Debug.WriteLine(
                $"AdGuard clients loaded: {clients.Count}");

            return clients;
        }

        private static void ParseConfiguredClients(
            JsonElement root,
            List<ClientInfo> clients,
            HashSet<string> knownIdentifiers)
        {
            if (!root.TryGetProperty(
                    "clients",
                    out JsonElement configuredClients) ||
                configuredClients.ValueKind !=
                    JsonValueKind.Array)
            {
                return;
            }

            foreach (JsonElement configuredClient
                     in configuredClients.EnumerateArray())
            {
                string name =
                    GetClientStringProperty(
                        configuredClient,
                        "name");

                if (!configuredClient.TryGetProperty(
                        "ids",
                        out JsonElement identifiers) ||
                    identifiers.ValueKind !=
                        JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement identifierElement
                         in identifiers.EnumerateArray())
                {
                    if (identifierElement.ValueKind !=
                        JsonValueKind.String)
                    {
                        continue;
                    }

                    string identifier =
                        identifierElement
                            .GetString()?
                            .Trim() ??
                        string.Empty;

                    AddClient(
                        clients,
                        knownIdentifiers,
                        name,
                        identifier);
                }
            }
        }

        private static void ParseAutomaticClients(
            JsonElement root,
            List<ClientInfo> clients,
            HashSet<string> knownIdentifiers)
        {
            if (!root.TryGetProperty(
                    "auto_clients",
                    out JsonElement automaticClients) ||
                automaticClients.ValueKind !=
                    JsonValueKind.Array)
            {
                return;
            }

            foreach (JsonElement automaticClient
                     in automaticClients.EnumerateArray())
            {
                string name =
                    GetClientStringProperty(
                        automaticClient,
                        "name");

                string ipAddress =
                    GetClientStringProperty(
                        automaticClient,
                        "ip");

                AddClient(
                    clients,
                    knownIdentifiers,
                    name,
                    ipAddress);
            }
        }

        private static void AddClient(
            List<ClientInfo> clients,
            HashSet<string> knownIdentifiers,
            string name,
            string identifier)
        {
            if (string.IsNullOrWhiteSpace(
                    identifier))
            {
                return;
            }

            string normalisedIdentifier =
                identifier.Trim();

            if (!knownIdentifiers.Add(
                    normalisedIdentifier))
            {
                return;
            }

            string displayName =
                string.IsNullOrWhiteSpace(name)
                    ? normalisedIdentifier
                    : name.Trim();

            string ipAddress =
                "-";

            string macAddress =
                "-";

            if (IPAddress.TryParse(
                    normalisedIdentifier,
                    out _))
            {
                ipAddress =
                    normalisedIdentifier;
            }
            else if (LooksLikeMacAddress(
                         normalisedIdentifier))
            {
                macAddress =
                    normalisedIdentifier;
            }
            else
            {
                ipAddress =
                    normalisedIdentifier;
            }

            // Configured AdGuard Home clients can expose an IP address
            // and a MAC address as separate identifiers with the same name.
            // Merge those identifiers into one card instead of creating two
            // incomplete client records.
            if (!string.IsNullOrWhiteSpace(name))
            {
                ClientInfo? existingClient =
                    clients.FirstOrDefault(
                        client =>
                            string.Equals(
                                client.Name,
                                displayName,
                                StringComparison.OrdinalIgnoreCase));

                if (existingClient is not null)
                {
                    if (ipAddress != "-" &&
                        existingClient.IpAddress == "-")
                    {
                        existingClient.IpAddress = ipAddress;
                    }

                    if (macAddress != "-" &&
                        existingClient.MacAddress == "-")
                    {
                        existingClient.MacAddress = macAddress;
                    }

                    return;
                }
            }

            clients.Add(
                new ClientInfo
                {
                    Name =
                        displayName,

                    IpAddress =
                        ipAddress,

                    MacAddress =
                        macAddress,

                    TotalQueries =
                        0,

                    BlockedQueries =
                        0,

                    LastSeen =
                        "-"
                });
        }

        private static string GetClientStringProperty(
            JsonElement element,
            string propertyName)
        {
            if (element.TryGetProperty(
                    propertyName,
                    out JsonElement property) &&
                property.ValueKind ==
                    JsonValueKind.String)
            {
                return property
                           .GetString()?
                           .Trim() ??
                       string.Empty;
            }

            return string.Empty;
        }

        private static string GetNestedStringProperty(
            JsonElement element,
            string objectPropertyName,
            string stringPropertyName)
        {
            if (!element.TryGetProperty(
                    objectPropertyName,
                    out JsonElement nestedObject) ||
                nestedObject.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            return GetClientStringProperty(
                nestedObject,
                stringPropertyName);
        }

        private static bool LooksLikeMacAddress(
            string value)
        {
            string compactValue =
                value
                    .Replace(
                        ":",
                        string.Empty,
                        StringComparison.Ordinal)
                    .Replace(
                        "-",
                        string.Empty,
                        StringComparison.Ordinal);

            if (compactValue.Length != 12)
            {
                return false;
            }

            foreach (char character
                     in compactValue)
            {
                bool isHexadecimal =
                    character >= '0' &&
                    character <= '9' ||
                    character >= 'a' &&
                    character <= 'f' ||
                    character >= 'A' &&
                    character <= 'F';

                if (!isHexadecimal)
                {
                    return false;
                }
            }

            return true;
        }

        private static void LogFailedClientsResponse(
            AdGuardClientsResponse response)
        {
            if (response.RequiresNewToken)
            {
                Debug.WriteLine(
                    "The GL.iNet Admin-Token is missing, " +
                    "invalid or expired.");
            }

            Debug.WriteLine(
                "AdGuard clients request failed with status " +
                $"{(int)response.StatusCode} " +
                response.StatusCode +
                ".");

        }

        private async Task<string>
            GetAdminTokenAsync()
        {
            if (!string.IsNullOrWhiteSpace(
                    _adminToken))
            {
                return _adminToken;
            }

            await _tokenLock.WaitAsync();

            try
            {
                if (!string.IsNullOrWhiteSpace(
                        _adminToken))
                {
                    return _adminToken;
                }

                Debug.WriteLine(
                    "No cached GL.iNet Admin-Token is available. " +
                    "Logging in automatically.");

                string token =
                    await _sessionService
                        .GetAdminTokenAsync(
                            CancellationToken.None);

                if (string.IsNullOrWhiteSpace(
                        token))
                {
                    throw new InvalidOperationException(
                        "GL.iNet login succeeded but no " +
                        "Admin-Token was returned.");
                }

                _adminToken =
                    token;

                Debug.WriteLine(
                    "GL.iNet Admin-Token obtained successfully.");

                return token;
            }
            finally
            {
                _tokenLock.Release();
            }
        }

        private void InvalidateAdminToken()
        {
            _adminToken =
                null;
        }

        private async Task<AdGuardStatsResponse>
            RequestAdGuardStatisticsAsync(
                string token,
                CancellationToken cancellationToken = default)
        {
            AdGuardHttpResponse response =
                await SendAdGuardRequestAsync(
                    HttpMethod.Get,
                    "control/stats",
                    token,
                    json: null,
                    timeout: TimeSpan.FromSeconds(10),
                    noCache: false,
                    cancellationToken: cancellationToken);

            return new AdGuardStatsResponse(
                response.StatusCode,
                response.Content);
        }

        private static string GetStringProperty(
            JsonElement root,
            string propertyName,
            string fallbackValue)
        {
            if (root.TryGetProperty(
                    propertyName,
                    out JsonElement property) &&
                property.ValueKind ==
                    JsonValueKind.String)
            {
                string? value =
                    property.GetString();

                if (!string.IsNullOrWhiteSpace(
                        value))
                {
                    return value;
                }
            }

            return fallbackValue;
        }

        private static bool TryGetInteger(
            JsonElement value,
            out int result)
        {
            if (value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt32(out result))
            {
                return true;
            }

            if (value.ValueKind == JsonValueKind.String &&
                int.TryParse(value.GetString(), out result))
            {
                return true;
            }

            result = 0;
            return false;
        }

        private static void LogFailedAdGuardResponse(
            AdGuardStatsResponse response)
        {
            if (response.RequiresNewToken)
            {
                Debug.WriteLine(
                    "The GL.iNet Admin-Token is missing, " +
                    "invalid or expired.");
            }

            Debug.WriteLine(
                "AdGuard request failed with status " +
                $"{(int)response.StatusCode} " +
                response.StatusCode +
                ".");

        }

        // Reboot
        //

        public Task RebootRouterAsync()
        {
            return _ssh.RunCommandAsync(
                "reboot");
        }

        private sealed class AdGuardControlResponse
        {
            public AdGuardControlResponse(
                HttpStatusCode statusCode,
                string content)
            {
                StatusCode =
                    statusCode;

                Content =
                    content;
            }

            public HttpStatusCode StatusCode
            {
                get;
            }

            public string Content
            {
                get;
            }

            public bool IsSuccess =>
                (int)StatusCode >= 200 &&
                (int)StatusCode <= 299;

            public bool RequiresNewToken =>
                StatusCode ==
                    HttpStatusCode.Unauthorized ||
                StatusCode ==
                    HttpStatusCode.Forbidden;
        }

        private sealed class AdGuardClientsResponse
        {
            public AdGuardClientsResponse(
                HttpStatusCode statusCode,
                string content)
            {
                StatusCode =
                    statusCode;

                Content =
                    content;
            }

            public HttpStatusCode StatusCode
            {
                get;
            }

            public string Content
            {
                get;
            }

            public bool IsSuccess =>
                (int)StatusCode >= 200 &&
                (int)StatusCode <= 299;

            public bool RequiresNewToken =>
                StatusCode ==
                    HttpStatusCode.Unauthorized ||
                StatusCode ==
                    HttpStatusCode.Forbidden;
        }

        private sealed class AdGuardQueryLogResponse
        {
            public AdGuardQueryLogResponse(
                HttpStatusCode statusCode,
                string content)
            {
                StatusCode =
                    statusCode;

                Content =
                    content;
            }

            public HttpStatusCode StatusCode
            {
                get;
            }

            public string Content
            {
                get;
            }

            public bool IsSuccess =>
                (int)StatusCode >= 200 &&
                (int)StatusCode <= 299;

            public bool RequiresNewToken =>
                StatusCode ==
                    HttpStatusCode.Unauthorized ||
                StatusCode ==
                    HttpStatusCode.Forbidden;
        }

        private sealed class AdGuardStatsResponse
        {
            public AdGuardStatsResponse(
                HttpStatusCode statusCode,
                string content)
            {
                StatusCode =
                    statusCode;

                Content =
                    content;
            }

            public HttpStatusCode StatusCode
            {
                get;
            }

            public string Content
            {
                get;
            }

            public bool IsSuccess =>
                (int)StatusCode >= 200 &&
                (int)StatusCode <= 299;

            public bool RequiresNewToken =>
                StatusCode ==
                    HttpStatusCode.Unauthorized ||
                StatusCode ==
                    HttpStatusCode.Forbidden;
        }

        private sealed record AdGuardHttpResponse(
            HttpStatusCode StatusCode,
            string Content)
        {
            public bool IsSuccess =>
                (int)StatusCode is >= 200 and <= 299;
        }
    }

}
