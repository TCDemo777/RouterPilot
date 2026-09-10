using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using System.Windows;
using RouterPilot.Configuration;
using RouterPilot.Models;

namespace RouterPilot.Services;

public interface IRouterCertificateTrustService
{
    RouterCertificateTrustDecision Evaluate(
        string host,
        X509Certificate2 certificate,
        SslPolicyErrors validationErrors);

    void ReportCertificateUnavailable(string host);
}

public enum RouterCertificateTrustDecision
{
    Trusted,
    TrustedAfterFirstUse,
    CertificateChanged,
    Expired,
    NotYetValid,
    Rejected
}

public enum RouterCertificateTrustState
{
    TrustRequired,
    Trusted,
    CertificateChanged,
    Expired,
    NotYetValid,
    CertificateUnavailable
}

/// <summary>Pure classification; it never accepts a certificate.</summary>
public static class RouterCertificateTrustPolicy
{
    public static RouterCertificateTrustState Determine(
        X509Certificate2 certificate,
        SslPolicyErrors validationErrors,
        string? trustedFingerprint,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (validationErrors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
            return RouterCertificateTrustState.CertificateUnavailable;
        if (now < certificate.NotBefore.ToUniversalTime())
            return RouterCertificateTrustState.NotYetValid;
        if (now > certificate.NotAfter.ToUniversalTime())
            return RouterCertificateTrustState.Expired;

        string fingerprint = BuildFingerprint(certificate);
        if (string.IsNullOrWhiteSpace(trustedFingerprint))
            return RouterCertificateTrustState.TrustRequired;
        return string.Equals(trustedFingerprint, fingerprint, StringComparison.Ordinal)
            ? RouterCertificateTrustState.Trusted
            : RouterCertificateTrustState.CertificateChanged;
    }

    public static string BuildFingerprint(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        byte[] hash = SHA256.HashData(certificate.RawData);
        return "SHA256:" + Convert.ToHexString(hash);
    }

    public static string DescribeValidationErrors(SslPolicyErrors validationErrors)
    {
        if (validationErrors == SslPolicyErrors.None)
            return "Windows trust succeeded.";

        List<string> details = [];
        if (validationErrors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
            details.Add("hostname mismatch");
        if (validationErrors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
            details.Add("untrusted or self-signed certificate chain");
        if (validationErrors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
            details.Add("certificate not available");

        return details.Count == 0
            ? "unknown TLS validation failure"
            : string.Join("; ", details);
    }
}

/// <summary>
/// Stores per-router HTTPS certificate pins and obtains explicit user consent
/// before accepting a self-signed or otherwise untrusted router certificate.
/// </summary>
public sealed class RouterCertificateTrustService : IRouterCertificateTrustService
{
    private readonly SettingsService _settingsService;
    private readonly object _sync = new();
    private readonly HashSet<string> _reportedCertificateWarnings = new(StringComparer.Ordinal);

    public RouterCertificateTrustService(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public RouterCertificateTrustDecision Evaluate(
        string host,
        X509Certificate2 certificate,
        SslPolicyErrors validationErrors)
    {
        string endpoint = BuildEndpointKey(host);
        string fingerprint = BuildFingerprint(certificate);

        if (string.IsNullOrWhiteSpace(endpoint) ||
            string.IsNullOrWhiteSpace(fingerprint))
        {
            return RouterCertificateTrustDecision.Rejected;
        }

        RouterCertificateTrustState initialState = RouterCertificateTrustPolicy.Determine(
            certificate,
            validationErrors,
            trustedFingerprint: null,
            DateTimeOffset.UtcNow);
        if (initialState is RouterCertificateTrustState.Expired or RouterCertificateTrustState.NotYetValid)
        {
            ReportInvalidCertificate(
                endpoint,
                certificate,
                fingerprint,
                validationErrors,
                initialState);
            return initialState == RouterCertificateTrustState.Expired
                ? RouterCertificateTrustDecision.Expired
                : RouterCertificateTrustDecision.NotYetValid;
        }

        lock (_sync)
        {
            AppSettings settings = _settingsService.Load();
            settings.TrustedRouterCertificateFingerprints ??=
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            settings.TrustedRouterCertificateFingerprints.TryGetValue(
                endpoint,
                out string? trustedFingerprint);
            RouterCertificateTrustState state = RouterCertificateTrustPolicy.Determine(
                certificate,
                validationErrors,
                trustedFingerprint,
                DateTimeOffset.UtcNow);

            if (state == RouterCertificateTrustState.TrustRequired)
            {
                if (!PromptForTrust(
                        endpoint,
                        certificate,
                        fingerprint,
                        validationErrors,
                        null))
                {
                    return RouterCertificateTrustDecision.Rejected;
                }

                settings.TrustedRouterCertificateFingerprints[endpoint] = fingerprint;
                _settingsService.Save(settings);
                return RouterCertificateTrustDecision.TrustedAfterFirstUse;
            }

            if (state == RouterCertificateTrustState.Trusted)
            {
                return RouterCertificateTrustDecision.Trusted;
            }

            if (state == RouterCertificateTrustState.CertificateUnavailable)
            {
                return RouterCertificateTrustDecision.Rejected;
            }

            // A replacement is recorded only after explicit consent, and the
            // active HTTPS request is still rejected. A retry is required.
            if (PromptForTrust(
                    endpoint,
                    certificate,
                    fingerprint,
                    validationErrors,
                    trustedFingerprint))
            {
                settings.TrustedRouterCertificateFingerprints[endpoint] = fingerprint;
                _settingsService.Save(settings);
            }

            return RouterCertificateTrustDecision.CertificateChanged;
        }
    }

    public void ReportCertificateUnavailable(string host)
    {
        string endpoint = BuildEndpointKey(host);
        if (string.IsNullOrWhiteSpace(endpoint)) return;
        ShowWarningOnce(
            endpoint + "|unavailable",
            "RouterPilot could not obtain a certificate from the router HTTPS endpoint. Certificate trust could not be established.",
            "Security warning: router certificate unavailable");
    }

    private void ReportInvalidCertificate(
        string endpoint,
        X509Certificate2 certificate,
        string fingerprint,
        SslPolicyErrors validationErrors,
        RouterCertificateTrustState state)
    {
        string validityDetail = state == RouterCertificateTrustState.NotYetValid
            ? "The router HTTPS certificate is not yet valid."
            : "The router HTTPS certificate has expired.";
        ShowWarningOnce(
            endpoint + "|" + fingerprint,
            "RouterPilot rejected the router HTTPS certificate because it is not currently valid.\n\n" +
            validityDetail + "\n\n" +
            BuildCertificateDescription(endpoint, certificate, fingerprint, validationErrors) + "\n\n" +
            "Check the certificate validity period and correct the router or system clock only if it is inconsistent with those dates.",
            "Security warning: router certificate invalid");
    }

    private void ShowWarningOnce(string warningKey, string message, string title)
    {
        lock (_sync)
        {
            if (!_reportedCertificateWarnings.Add(warningKey))
            {
                return;
            }
        }

        ShowMessage(
            message,
            title,
            MessageBoxImage.Warning);
    }

    private static string BuildEndpointKey(string host)
    {
        string normalisedHost = RouterConnectionOptions.NormaliseHost(host)
            .ToLowerInvariant();
        return normalisedHost.Length == 0
            ? string.Empty
            : "https://" + normalisedHost + ":443";
    }

    private static string BuildFingerprint(X509Certificate2 certificate) =>
        RouterCertificateTrustPolicy.BuildFingerprint(certificate);

    private static bool PromptForTrust(
        string endpoint,
        X509Certificate2 certificate,
        string receivedFingerprint,
        SslPolicyErrors validationErrors,
        string? previousFingerprint)
    {
        bool changed = !string.IsNullOrWhiteSpace(previousFingerprint);
        string message = changed
            ? "RouterPilot detected that the router HTTPS certificate changed. " +
              "This can indicate a router reset or an intercepted connection.\n\n" +
              $"Router endpoint: {endpoint}\n" +
              $"Previous fingerprint: {previousFingerprint}\n" +
              $"New fingerprint: {receivedFingerprint}\n\n" +
              BuildCertificateDescription(endpoint, certificate, receivedFingerprint, validationErrors) + "\n\n" +
              "Select Yes to Trust New Certificate. The current connection will be blocked; retry the operation to use the new certificate."
            : "RouterPilot has not connected to this router HTTPS endpoint before. " +
              "Verify the certificate details before trusting it.\n\n" +
              BuildCertificateDescription(endpoint, certificate, receivedFingerprint, validationErrors) + "\n\n" +
              "RouterPilot requires your explicit approval before pinning this certificate. Select Yes to trust it.";

        return ShowMessage(
            message,
            changed
                ? "Security warning: router certificate changed"
                : "Trust router certificate",
            changed ? MessageBoxImage.Warning : MessageBoxImage.Question,
            confirm: true);
    }

    private static string BuildCertificateDescription(
        string endpoint,
        X509Certificate2 certificate,
        string fingerprint,
        SslPolicyErrors validationErrors = SslPolicyErrors.None) =>
        $"Router endpoint: {endpoint}\n" +
        $"Subject: {certificate.Subject}\n" +
        $"Issuer: {certificate.Issuer}\n" +
        $"Valid from: {certificate.NotBefore.ToLocalTime():u}\n" +
        $"Valid until: {certificate.NotAfter.ToLocalTime():u}\n" +
        $"SHA-256 fingerprint: {fingerprint}\n" +
        $"Windows certificate validation: {RouterCertificateTrustPolicy.DescribeValidationErrors(validationErrors)}";

    private static bool ShowMessage(
        string message,
        string title,
        MessageBoxImage image,
        bool confirm = false)
    {
        bool Show()
        {
            MessageBoxResult result = MessageBox.Show(
                message,
                title,
                confirm ? MessageBoxButton.YesNo : MessageBoxButton.OK,
                image);
            return confirm && result == MessageBoxResult.Yes;
        }

        Application? application = Application.Current;
        if (application?.Dispatcher is null ||
            application.Dispatcher.HasShutdownStarted ||
            application.Dispatcher.HasShutdownFinished)
        {
            return false;
        }

        return application.Dispatcher.CheckAccess()
            ? Show()
            : application.Dispatcher.Invoke(Show);
    }
}
