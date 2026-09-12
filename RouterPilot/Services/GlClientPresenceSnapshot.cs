namespace RouterPilot.Services;

/// <summary>
/// A successfully parsed, aggregate GL.iNet client-presence response. This is
/// intentionally distinct from an empty response: unavailable data must not
/// be presented as every device being offline.
/// </summary>
internal sealed class GlClientPresenceSnapshot
{
    public static readonly GlClientPresenceSnapshot Unavailable = new(false,
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase));

    public GlClientPresenceSnapshot(bool isAvailable, IReadOnlyDictionary<string, bool> presence)
    {
        IsAvailable = isAvailable;
        Presence = presence;
    }

    public bool IsAvailable { get; }
    public IReadOnlyDictionary<string, bool> Presence { get; }
}
