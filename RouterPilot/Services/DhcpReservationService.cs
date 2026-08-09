using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

public sealed class DhcpReservationService : IDhcpReservationService
{
    private readonly IRouterManagerProvider _provider;
    private readonly DhcpReservationValidator _validator;
    private readonly TimelineService _timeline;
    public DhcpReservationService(IRouterManagerProvider provider, DhcpReservationValidator validator, TimelineService timeline) { _provider=provider; _validator=validator; _timeline=timeline; }
    public async Task<IReadOnlyList<DhcpReservationInfo>> GetReservationsAsync(CancellationToken token) => (await (await _provider.GetRouterManagerAsync(token)).GetDhcpSnapshotAsync()).Reservations;
    public async Task<DhcpReservationOperationResult> AddReservationAsync(DhcpReservationRequest r,CancellationToken t)=>await CompleteAsync(await Execute(r,null,"Add",t),"Add",r.Hostname,t);
    public async Task<DhcpReservationOperationResult> UpdateReservationAsync(DhcpReservationIdentity i,DhcpReservationRequest r,CancellationToken t)=>await CompleteAsync(await Execute(r,i,"Update",t),"Update",r.Hostname ?? i.FriendlyName,t);
    public async Task<DhcpReservationOperationResult> DeleteReservationAsync(DhcpReservationIdentity i,CancellationToken t){var rm=await _provider.GetRouterManagerAsync(t);return await CompleteAsync(await rm.DeleteDhcpReservationAsync(i,t),"Delete",i.FriendlyName,t);}
    private async Task<DhcpReservationOperationResult> Execute(DhcpReservationRequest r,DhcpReservationIdentity? i,string op,CancellationToken t){var sw=Stopwatch.StartNew();var rm=await _provider.GetRouterManagerAsync(t);var snap=await rm.GetDhcpSnapshotAsync();var validationReservations=i is null?snap.Reservations:snap.Reservations.Where(existing=>!(string.Equals(existing.MacAddress,i.MacAddress,StringComparison.OrdinalIgnoreCase)&&string.Equals(existing.IpAddress,i.IpAddress,StringComparison.OrdinalIgnoreCase))).ToList();var v=_validator.Validate(r.MacAddress,r.IpAddress,snap.Scopes,validationReservations,snap.Leases);if(!v.IsValid)return new(){Operation=op,RequestedMac=r.MacAddress,RequestedIp=r.IpAddress,FailureCategory=v.Code.ToString(),Duration=sw.Elapsed};var safe=new DhcpReservationRequest{MacAddress=v.NormalizedMac,IpAddress=v.IpAddress!,Hostname=r.Hostname};var x=i is null?await rm.AddDhcpReservationAsync(safe,t):await rm.UpdateDhcpReservationAsync(i,safe,t);return new DhcpReservationOperationResult{Success=x.Success,Operation=x.Operation,RequestedMac=x.RequestedMac,RequestedIp=x.RequestedIp,VerifiedMac=x.VerifiedMac,VerifiedIp=x.VerifiedIp,VerifiedIdentity=x.VerifiedIdentity,FailureCategory=x.FailureCategory,RollbackAttempted=x.RollbackAttempted,RollbackVerified=x.RollbackVerified,Duration=sw.Elapsed};}
    private async Task<DhcpReservationOperationResult> CompleteAsync(DhcpReservationOperationResult result,string operation,string? friendlyName,CancellationToken token){string device=string.IsNullOrWhiteSpace(friendlyName)?"DHCP reservation":friendlyName;bool success=result.Success;string verb=operation switch{"Add"=>"added","Update"=>"updated","Delete"=>"removed",_=>"changed"};try{await _timeline.AddAsync(new TimelineEvent{Category=TimelineCategory.Router,EventType=success?TimelineEventType.MaintenanceCompleted:TimelineEventType.MaintenanceFailed,Title=success?$"DHCP reservation {verb}":"DHCP reservation change failed",Message=device,Severity=success?TimelineSeverity.Success:(result.FailureCategory=="RollbackVerificationFailed"?TimelineSeverity.Error:TimelineSeverity.Warning),Source="DHCP"},token);}catch{}return result;}
}
