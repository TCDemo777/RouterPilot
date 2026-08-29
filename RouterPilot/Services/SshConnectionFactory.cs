using Renci.SshNet;
using Renci.SshNet.Common;
using RouterPilot.Models;
using System.IO;

namespace RouterPilot.Services;

public interface ISshConnectionFactory
{
    SshClient CreateClient(SshConnectionSettings settings);
    ConnectionInfo CreateConnectionInfo(SshConnectionSettings settings);
}

public sealed class SshConnectionFactory : ISshConnectionFactory
{
    public SshClient CreateClient(SshConnectionSettings settings) =>
        new(CreateConnectionInfo(settings));

    public ConnectionInfo CreateConnectionInfo(SshConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.Host))
            throw new InvalidOperationException("SSH host is required.");
        if (settings.Port is < 1 or > 65535)
            throw new InvalidOperationException("SSH port must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(settings.Username))
            throw new InvalidOperationException("SSH username is required.");

        AuthenticationMethod authentication = settings.AuthenticationMethod switch
        {
            SshAuthenticationMethod.Password => CreatePasswordAuthentication(settings),
            SshAuthenticationMethod.PrivateKey => CreatePrivateKeyAuthentication(settings),
            _ => throw new InvalidOperationException("The configured SSH authentication method is not supported.")
        };
        return new ConnectionInfo(settings.Host, settings.Port, settings.Username, authentication);
    }

    private static AuthenticationMethod CreatePasswordAuthentication(SshConnectionSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Password))
            throw new InvalidOperationException("SSH password authentication requires a password.");
        return new PasswordAuthenticationMethod(settings.Username, settings.Password);
    }

    private static AuthenticationMethod CreatePrivateKeyAuthentication(SshConnectionSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.PrivateKeyPath) || !File.Exists(settings.PrivateKeyPath))
            throw new InvalidOperationException("SSH private key could not be found or opened.");
        try
        {
            PrivateKeyFile key = string.IsNullOrEmpty(settings.PrivateKeyPassphrase)
                ? new PrivateKeyFile(settings.PrivateKeyPath)
                : new PrivateKeyFile(settings.PrivateKeyPath, settings.PrivateKeyPassphrase);
            return new PrivateKeyAuthenticationMethod(settings.Username, key);
        }
        catch (Exception ex)
        {
            // SSH.NET and its parser dependencies use several exception types for
            // malformed and incorrectly passphrase-protected key files. Keep all
            // of them in the SSH configuration boundary without exposing key data.
            throw new InvalidOperationException(
                "SSH private key could not be opened. Check its format and passphrase.",
                ex);
        }
    }
}
