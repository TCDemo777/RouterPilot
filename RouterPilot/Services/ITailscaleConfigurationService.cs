using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

public interface ITailscaleConfigurationService
{
    Task<TailscaleConfigurationSnapshot> GetConfigurationAsync(CancellationToken cancellationToken = default);
    Task<TailscaleMutationResult> SetFieldAsync(TailscaleAccessField field, bool value, CancellationToken cancellationToken = default);
}

public enum TailscaleAccessField { Enabled, Lan, Wan }

public sealed record TailscaleMutationResult(bool Succeeded, TailscaleConfigurationSnapshot Snapshot, string Message);
