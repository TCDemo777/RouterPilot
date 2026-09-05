using System;
using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>Tracks one explicit, bounded VPN operation for presentation only.</summary>
public sealed class VpnOperationIntentService
{
    private readonly object _sync = new();
    private int? _tunnelId;
    private VpnTransitionIntent _intent;
    private long _generation;

    public event Action? Changed;

    public long Begin(int tunnelId, bool connecting)
    {
        lock (_sync)
        {
            _tunnelId = tunnelId;
            _intent = connecting ? VpnTransitionIntent.Connecting : VpnTransitionIntent.Disconnecting;
            return ++_generation;
        }
    }

    public void Clear(int tunnelId, long generation)
    {
        bool changed;
        lock (_sync)
        {
            changed = _tunnelId == tunnelId && generation == _generation && _intent != VpnTransitionIntent.None;
            if (changed)
            {
                _tunnelId = null;
                _intent = VpnTransitionIntent.None;
            }
        }
        if (changed) Changed?.Invoke();
    }

    public void ClearAll()
    {
        bool changed;
        lock (_sync)
        {
            changed = _intent != VpnTransitionIntent.None;
            _tunnelId = null;
            _intent = VpnTransitionIntent.None;
            ++_generation;
        }
        if (changed) Changed?.Invoke();
    }

    public VpnTransitionIntent GetIntent(int tunnelId)
    {
        lock (_sync) return _tunnelId == tunnelId ? _intent : VpnTransitionIntent.None;
    }
}
