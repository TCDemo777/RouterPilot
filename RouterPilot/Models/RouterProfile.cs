namespace RouterPilot.Models;

/// <summary>Persisted connection settings for one RouterPilot router.</summary>
public sealed class RouterProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "My Router";
    public string RouterHost { get; set; } = string.Empty;
    public int RouterPort { get; set; } = 80;
    public int AdGuardPort { get; set; } = 3000;
    public bool UseRouterHttps { get; set; }
    public bool UseAdGuardHttps { get; set; }
    public string Username { get; set; } = "root";
    public string EncryptedPassword { get; set; } = string.Empty;
    public bool RememberPassword { get; set; } = true;
    public int SshPort { get; set; } = 22;
    public SshAuthenticationMethod SshAuthenticationMethod { get; set; } = SshAuthenticationMethod.Password;
    public string PrivateKeyPath { get; set; } = string.Empty;
    public string EncryptedPrivateKeyPassphrase { get; set; } = string.Empty;

    public RouterProfile Clone() => (RouterProfile)MemberwiseClone();
}
