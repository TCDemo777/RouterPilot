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
}

public enum RouterCertificateTrustDecision
{
    Trusted,
    TrustedAfterFirstUse,
    CertificateChanged,
    Expired,
    Rejected
}

/// <summary>
/// Stores per-router HTTPS certificate pins and obtains explicit user consent
/// before accepting a self-signed or otherwise untrusted router certificate.
/// </summary>
public sealed class RouterCertificateTrustService : IRouterCertificateTrustService
{
    private readonly SettingsService _settingsService;
    private readonly object _sync = new();
    private readonly HashSet<string> _reportedExpiryWarnings = new(StringComparer.Ordinal);

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

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now < certificate.NotBefore.ToUniversalTime() ||
            now > certificate.NotAfter.ToUniversalTime())
        {
            ReportExpiredCertificate(
                endpoint,
                certificate,
                fingerprint,
                validationErrors);
            return RouterCertificateTrustDecision.Expired;
        }

        lock (_sync)
        {
            AppSettings settings = _settingsService.Load();
            settings.TrustedRouterCertificateFingerprints ??=
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!settings.TrustedRouterCertificateFingerprints.TryGetValue(
                    endpoint,
                    out string? trustedFingerprint) ||
                string.IsNullOrWhiteSpace(trustedFingerprint))
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

            if (string.Equals(trustedFingerprint, fingerprint, StringComparison.Ordinal))
            {
                return RouterCertificateTrustDecision.Trusted;
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

    private void ReportExpiredCertificate(
        string endpoint,
        X509Certificate2 certificate,
        string fingerprint,
        SslPolicyErrors validationErrors)
    {
        string warningKey = endpoint + "|" + fingerprint;
        lock (_sync)
        {
            if (!_reportedExpiryWarnings.Add(warningKey))
            {
                return;
            }
        }

        ShowMessage(
            "RouterPilot rejected the router HTTPS certificate because it is not currently valid.\n\n" +
            BuildCertificateDescription(
                endpoint,
                certificate,
                fingerprint,
                validationErrors) + "\n\n" +
            "Correct the router certificate or system clock before retrying.",
            "Security warning: router certificate invalid",
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

    private static string BuildFingerprint(X509Certificate2 certificate)
    {
        byte[] hash = SHA256.HashData(certificate.RawData);
        return "SHA256:" + Convert.ToHexString(hash);
    }

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
              "Select Yes to trust this certificate.";

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
        $"Windows certificate validation: {DescribeValidation(validationErrors)}";

    private static string DescribeValidation(SslPolicyErrors validationErrors)
    {
        if (validationErrors == SslPolicyErrors.None)
        {
            return "No errors reported.";
        }

        List<string> details = [];
        if (validationErrors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
        {
            details.Add("hostname mismatch");
        }

        if (validationErrors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
        {
            details.Add("untrusted, self-signed, or otherwise invalid certificate chain");
        }

        if (validationErrors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            details.Add("certificate not available");
        }

        return details.Count == 0
            ? validationErrors.ToString()
            : string.Join("; ", details) + ". Explicit trust is required.";
    }

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
