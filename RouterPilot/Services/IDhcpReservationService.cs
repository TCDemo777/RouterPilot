using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

public interface IDhcpReservationService
{
    Task<IReadOnlyList<DhcpReservationInfo>> GetReservationsAsync(CancellationToken cancellationToken);
    Task<DhcpReservationOperationResult> AddReservationAsync(DhcpReservationRequest request, CancellationToken cancellationToken);
    Task<DhcpReservationOperationResult> UpdateReservationAsync(DhcpReservationIdentity identity, DhcpReservationRequest request, CancellationToken cancellationToken);
    Task<DhcpReservationOperationResult> DeleteReservationAsync(DhcpReservationIdentity identity, CancellationToken cancellationToken);
}
