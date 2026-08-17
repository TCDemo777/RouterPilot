using System;
using RouterPilot.Models;

namespace RouterPilot.Services;

public interface INetworkHealthService
{
    NetworkHealthSnapshot Current { get; }
    event Action<NetworkHealthSnapshot>? SnapshotChanged;
    NetworkHealthSnapshot Evaluate(NetworkHealthInput input);
}
