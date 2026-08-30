namespace RouterPilot.Services;

public interface IMdnsIdentityService
{
    Task<string?> ResolveHostnameAsync(string ipAddress, CancellationToken cancellationToken = default);
}
