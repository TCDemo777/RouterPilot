using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

public interface IVpnLiveStatusService
{
    event Action<IReadOnlyList<VpnLiveStatusInfo>>? StatusChanged;
    Task EnsureSubscribedAsync(CancellationToken cancellationToken);
    IReadOnlyList<VpnLiveStatusInfo> Current { get; }
    void Clear();
}
