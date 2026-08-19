namespace RouterPilot.Models;

/// <summary>Local convenience values for the port-forward editor; never sent to the router as metadata.</summary>
public sealed record PortForwardPreset(string Name, string Protocol, string ExternalPort, string InternalPort, string? Description = null)
{
    public bool IsCustom => string.IsNullOrEmpty(Protocol);
}

public static class PortForwardPresetCatalog
{
    public static PortForwardPreset Custom { get; } = new("Custom", string.Empty, string.Empty, string.Empty);

    public static IReadOnlyList<PortForwardPreset> All { get; } =
    [
        Custom,
        new("Minecraft Java", "tcp", "25565", "25565", "Default dedicated-server port"),
        new("Minecraft Bedrock", "udp", "19132", "19132", "Default Bedrock server port"),
        new("Web Server (HTTP)", "tcp", "80", "80"),
        new("Web Server (HTTPS)", "tcp", "443", "443"),
        new("SSH", "tcp", "22", "22"),
        new("FTP", "tcp", "21", "21"),
        new("Remote Desktop (RDP)", "tcp", "3389", "3389"),
        new("Plex Media Server", "tcp", "32400", "32400")
    ];

    public static PortForwardPreset Match(string? protocol, string? externalPort, string? internalPort) =>
        All.FirstOrDefault(preset => !preset.IsCustom &&
            string.Equals(preset.Protocol, protocol, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(preset.ExternalPort, externalPort, StringComparison.Ordinal) &&
            string.Equals(preset.InternalPort, internalPort, StringComparison.Ordinal)) ?? Custom;
}
