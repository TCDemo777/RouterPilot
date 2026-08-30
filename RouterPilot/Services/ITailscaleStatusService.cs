using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

public interface ITailscaleStatusService
{
    Task<TailscaleStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}
