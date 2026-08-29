namespace RouterPilot.Models;

public enum SshAuthenticationMethod
{
    Password,
    PrivateKey
}

public sealed class SshConnectionSettings
{
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 22;
    public string Username { get; init; } = string.Empty;
    public SshAuthenticationMethod AuthenticationMethod { get; init; } = SshAuthenticationMethod.Password;
    public string Password { get; init; } = string.Empty;
    public string PrivateKeyPath { get; init; } = string.Empty;
    public string PrivateKeyPassphrase { get; init; } = string.Empty;
}
