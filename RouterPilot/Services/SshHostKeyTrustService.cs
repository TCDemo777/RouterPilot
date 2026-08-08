using System.Windows;
using RouterPilot.Configuration;
using RouterPilot.Models;

namespace RouterPilot.Services;

public interface ISshHostKeyTrustService
{
    SshHostKeyTrustDecision Evaluate(string host, string fingerprintSha256);
}

public enum SshHostKeyTrustDecision
{
    Trusted,
    TrustedAfterFirstUse,
    FingerprintChanged,
    Rejected
}

/// <summary>
/// Persists endpoint-bound SSH host-key fingerprints and obtains explicit user
/// consent before trusting a new key.
/// </summary>
public sealed class SshHostKeyTrustService : ISshHostKeyTrustService
{
    private readonly SettingsService _settingsService;
    private readonly object _sync = new();

    public SshHostKeyTrustService(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public SshHostKeyTrustDecision Evaluate(
        string host,
        string fingerprintSha256)
    {
        string endpoint = RouterConnectionOptions.NormaliseHost(host)
            .ToLowerInvariant();
        string fingerprint = NormaliseFingerprint(fingerprintSha256);

        if (string.IsNullOrWhiteSpace(endpoint) ||
            string.IsNullOrWhiteSpace(fingerprint))
        {
            return SshHostKeyTrustDecision.Rejected;
        }

        lock (_sync)
        {
            AppSettings settings = _settingsService.Load();
            settings.TrustedSshHostFingerprints ??=
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!settings.TrustedSshHostFingerprints.TryGetValue(
                    endpoint,
                    out string? trustedFingerprint) ||
                string.IsNullOrWhiteSpace(trustedFingerprint))
            {
                if (!PromptForTrust(endpoint, fingerprint, null))
                    return SshHostKeyTrustDecision.Rejected;

                settings.TrustedSshHostFingerprints[endpoint] = fingerprint;
                _settingsService.Save(settings);
                return SshHostKeyTrustDecision.TrustedAfterFirstUse;
            }

            if (string.Equals(
                    NormaliseFingerprint(trustedFingerprint),
                    fingerprint,
                    StringComparison.Ordinal))
            {
                return SshHostKeyTrustDecision.Trusted;
            }

            // A changed key is never trusted by the current connection. If
            // the user deliberately accepts it, persist the replacement and
            // require a new connection attempt using that explicit choice.
            if (PromptForTrust(endpoint, fingerprint, trustedFingerprint))
            {
                settings.TrustedSshHostFingerprints[endpoint] = fingerprint;
                _settingsService.Save(settings);
            }

            return SshHostKeyTrustDecision.FingerprintChanged;
        }
    }

    private static string NormaliseFingerprint(string fingerprint)
    {
        string value = fingerprint.Trim();
        return value.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase)
            ? "SHA256:" + value[7..]
            : "SHA256:" + value;
    }

    private static bool PromptForTrust(
        string endpoint,
        string receivedFingerprint,
        string? previousFingerprint)
    {
        bool Prompt()
        {
            bool changed = !string.IsNullOrWhiteSpace(previousFingerprint);
            string message = changed
                ? "RouterPilot detected that the SSH host key changed. " +
                  "This can indicate that the router was reset or that the connection is being intercepted.\n\n" +
                  $"Router: {endpoint}\n" +
                  $"Previous fingerprint: {NormaliseFingerprint(previousFingerprint!)}\n" +
                  $"New fingerprint: {receivedFingerprint}\n\n" +
                  "Select Yes to Trust New Host Key. The current connection will be stopped; retry the operation to connect using the new trusted key."
                : "RouterPilot has not connected to this router by SSH before. Verify the host key fingerprint with your router before trusting it.\n\n" +
                  $"Router: {endpoint}\n" +
                  $"SHA-256 fingerprint: {receivedFingerprint}\n\n" +
                  "Select Yes to trust this host key.";

            return MessageBox.Show(
                message,
                changed
                    ? "Security warning: SSH host key changed"
                    : "Trust SSH host key",
                MessageBoxButton.YesNo,
                changed ? MessageBoxImage.Warning : MessageBoxImage.Question) == MessageBoxResult.Yes;
        }

        Application? application = Application.Current;
        if (application?.Dispatcher is null ||
            application.Dispatcher.HasShutdownStarted ||
            application.Dispatcher.HasShutdownFinished)
        {
            return false;
        }

        return application.Dispatcher.CheckAccess()
            ? Prompt()
            : application.Dispatcher.Invoke(Prompt);
    }
}
