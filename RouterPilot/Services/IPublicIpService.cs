using System;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

public interface IPublicIpService
{
    PublicIpResult Current { get; }

    event Action<PublicIpResult>? ResultChanged;

    event Action<string?, string>? PublicIpChanged;

    Task<PublicIpResult> RefreshAsync(bool forceRefresh, CancellationToken cancellationToken = default);
}
