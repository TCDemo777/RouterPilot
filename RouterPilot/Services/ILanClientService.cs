using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

public interface ILanClientService
{
    Task<IReadOnlyList<LanClientInfo>> GetWiredClientsAsync(CancellationToken cancellationToken);
}

public sealed class LanClientService : ILanClientService
{
    private readonly IRouterManagerProvider _provider;
    public LanClientService(IRouterManagerProvider provider) => _provider = provider;
    public async Task<IReadOnlyList<LanClientInfo>> GetWiredClientsAsync(CancellationToken cancellationToken) =>
        await (await _provider.GetRouterManagerAsync(cancellationToken)).GetLanClientsAsync(cancellationToken);
}
