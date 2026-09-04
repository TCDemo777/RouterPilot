namespace RouterPilot.Models;

public sealed record GlInetFirmwareRelease(string Version, string Stage, DateTimeOffset? ReleaseDate, string? DownloadUrl);
