using System.Threading;
using System.Threading.Tasks;

namespace RouterPilot.Services;

public interface IRouterManagerProvider : IAsyncDisposable
{
    Task<RouterManager> GetRouterManagerAsync(
        CancellationToken cancellationToken = default);

    void Invalidate();
    Task ResetAsync(CancellationToken cancellationToken = default);
}
