using System.Net.Http;
using System.Text.RegularExpressions;

namespace RouterPilot.Services;

/// <summary>
/// Removes secrets and unnecessary identifiers before diagnostic material is
/// persisted, copied, or exported outside the application.
/// </summary>
public static partial class DiagnosticRedactor
{
    public static string RedactForExport(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        string redacted = value;
        redacted = SensitiveJsonOrSettingRegex().Replace(redacted, "$1***REDACTED***");
        redacted = AuthorizationHeaderRegex().Replace(redacted, "$1: ***REDACTED***");
        redacted = CookieHeaderRegex().Replace(redacted, "$1 ***REDACTED***");
        redacted = UrlUserInfoRegex().Replace(redacted, "$1***REDACTED***@");
        redacted = MacAddressRegex().Replace(redacted, "***REDACTED-MAC***");
        redacted = IpAddressRegex().Replace(redacted, "***REDACTED-IP***");
        redacted = UserProfilePathRegex().Replace(redacted, "C:\\Users\\***REDACTED***");
        return redacted;
    }

    public static string FailureCategory(Exception exception) => exception switch
    {
        OperationCanceledException => "cancelled",
        TimeoutException => "timeout",
        HttpRequestException => "http-error",
        System.Text.Json.JsonException => "invalid-json",
        UnauthorizedAccessException => "access-denied",
        _ => "operation-failed"
    };

    [GeneratedRegex("(?im)(\\\"?(?:password|encryptedpassword|token|authorization|cookie|secret|credential|apikey|api_key|session)\\\"?\\s*[=:]\\s*)[^,\\r\\n}]+")]
    private static partial Regex SensitiveJsonOrSettingRegex();

    [GeneratedRegex("(?im)^((?:Authorization|Proxy-Authorization))\\s*:\\s*.*$")]
    private static partial Regex AuthorizationHeaderRegex();

    [GeneratedRegex("(?im)^((?:Cookie|Set-Cookie)\\s*:)\\s*.*$")]
    private static partial Regex CookieHeaderRegex();

    [GeneratedRegex("(?i)(https?://)[^/@\\s]+@")]
    private static partial Regex UrlUserInfoRegex();

    [GeneratedRegex("(?i)\\b(?:[0-9a-f]{2}:){5}[0-9a-f]{2}\\b")]
    private static partial Regex MacAddressRegex();

    [GeneratedRegex("(?<![\\d.])(?:25[0-5]|2[0-4]\\d|1?\\d?\\d)(?:\\.(?:25[0-5]|2[0-4]\\d|1?\\d?\\d)){3}(?![\\d.])")]
    private static partial Regex IpAddressRegex();

    [GeneratedRegex("(?i)C:\\\\Users\\\\[^\\\\\\r\\n]+")]
    private static partial Regex UserProfilePathRegex();
}
