using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

public partial class RouterManager
{
    private readonly SemaphoreSlim _dhcpMutationGate = new(1, 1);
    private static readonly HashSet<string> DhcpReservationRestoreOptions = new(StringComparer.OrdinalIgnoreCase) { "mac", "ip", "tag" };
    private sealed record DhcpReservationSnapshot(string MacAddress, string IpAddress, IReadOnlyDictionary<string, string> Options);

    internal async Task<DhcpReservationOperationResult> AddDhcpReservationAsync(DhcpReservationRequest request, CancellationToken token)
    {
        await _dhcpMutationGate.WaitAsync(token); try
        {
            string id = (await _ssh.RunCommandAsync("uci add dhcp host 2>/dev/null", token)).Split(new[] {'\r','\n'},StringSplitOptions.RemoveEmptyEntries).Select(x=>x.Trim()).FirstOrDefault(x=>Regex.IsMatch(x,"^[A-Za-z0-9_]+$")) ?? string.Empty;
            if (id.Length==0) return Fail("Add",request,"CreateFailed");
            if (!await DhcpCommandAsync($"uci set dhcp.{id}.mac='{request.MacAddress}' && uci set dhcp.{id}.ip='{request.IpAddress}'",token) || !await ApplyDhcpAsync(token)) return Fail("Add",request,"ApplyFailed");
            if (await HasExactlyOneAsync(request.MacAddress,request.IpAddress,token)) return Ok("Add",request);
            bool rollbackVerified = await DeleteByIdentityAsync(new(request.MacAddress,request.IpAddress),token);
            return Fail("Add",request,"VerificationFailed",true,rollbackVerified);
        } finally { _dhcpMutationGate.Release(); }
    }
    internal async Task<DhcpReservationOperationResult> UpdateDhcpReservationAsync(DhcpReservationIdentity identity,DhcpReservationRequest request,CancellationToken token)
    {
        await _dhcpMutationGate.WaitAsync(token); try { return await UpdateCoreAsync(identity,request,token); } finally { _dhcpMutationGate.Release(); }
    }
    internal async Task<DhcpReservationOperationResult> DeleteDhcpReservationAsync(DhcpReservationIdentity identity,CancellationToken token)
    {
        await _dhcpMutationGate.WaitAsync(token); try
        {
            DhcpUciSection? s=await ResolveAsync(identity,token); if(s is null) return Fail("Delete",new(){MacAddress=identity.MacAddress,IpAddress=identity.IpAddress},"NotFoundOrAmbiguous");
            DhcpReservationSnapshot snapshot = CaptureSnapshot(s);
            if(!await DhcpCommandAsync($"uci delete 'dhcp.{s.Id}'",token)||!await ApplyDhcpAsync(token)) return Fail("Delete",new(){MacAddress=identity.MacAddress,IpAddress=identity.IpAddress},"ApplyFailed");
            if(await ResolveAsync(identity,token) is null) return Ok("Delete",new(){MacAddress=identity.MacAddress,IpAddress=identity.IpAddress});
            bool rollbackVerified = await RestoreSnapshotAsync(snapshot,null,token);
            return Fail("Delete",new(){MacAddress=identity.MacAddress,IpAddress=identity.IpAddress},rollbackVerified?"DeleteVerificationFailed":"RollbackVerificationFailed",true,rollbackVerified);
        } finally { _dhcpMutationGate.Release(); }
    }
    private async Task<DhcpReservationOperationResult> UpdateCoreAsync(DhcpReservationIdentity identity,DhcpReservationRequest request,CancellationToken t)
    {
        DhcpUciSection? s=await ResolveAsync(identity,t); if(s is null)return Fail("Update",request,"NotFoundOrAmbiguous");
        DhcpReservationSnapshot snapshot = CaptureSnapshot(s);
        if(!await DhcpCommandAsync($"uci set 'dhcp.{s.Id}.mac={request.MacAddress}' && uci set 'dhcp.{s.Id}.ip={request.IpAddress}'",t)||!await ApplyDhcpAsync(t))return Fail("Update",request,"ApplyFailed");
        if (await HasExactlyOneAsync(request.MacAddress,request.IpAddress,t)) return Ok("Update",request);
        bool rollbackVerified = await RestoreSnapshotAsync(snapshot,new(request.MacAddress,request.IpAddress),t);
        return Fail("Update",request,rollbackVerified?"UpdateVerificationFailed":"RollbackVerificationFailed",true,rollbackVerified);
    }
    private async Task<DhcpUciSection?> ResolveAsync(DhcpReservationIdentity i,CancellationToken t) { var all=ParseDhcpUciSections(await _ssh.RunCommandAsync("uci show dhcp 2>/dev/null",t)).Values.Where(s=>s.Type=="host"&&NormaliseMacAddress(GetDhcpOption(s,"mac",string.Empty))==NormaliseMacAddress(i.MacAddress)&&GetDhcpOption(s,"ip",string.Empty)==i.IpAddress).ToList(); return all.Count==1?all[0]:null; }
    private async Task<bool> HasExactlyOneAsync(string mac,string ip,CancellationToken t)=>await ResolveAsync(new(mac,ip),t) is not null;
    private async Task<bool> ApplyDhcpAsync(CancellationToken t)
    {
        bool applied = await DhcpCommandAsync("uci commit dhcp",t) && await DhcpCommandAsync("/etc/init.d/dnsmasq reload",t);
        if (applied)
        {
            _dhcpConfigurationCache = null;
            _dhcpReservationCache = null;
        }
        return applied;
    }
    private async Task<bool> DhcpCommandAsync(string command,CancellationToken t)=>(await _ssh.RunCommandAsync($"{command}; rc=$?; printf '\\n__RP_RC:%s' \"$rc\"",t)).Contains("__RP_RC:0",StringComparison.Ordinal);
    private static DhcpReservationOperationResult Ok(string op,DhcpReservationRequest r)=>new(){Success=true,Operation=op,RequestedMac=r.MacAddress,RequestedIp=r.IpAddress,VerifiedMac=r.MacAddress,VerifiedIp=r.IpAddress,VerifiedIdentity=new(r.MacAddress,r.IpAddress)};
    private static DhcpReservationOperationResult Fail(string op,DhcpReservationRequest r,string category,bool attempted=false,bool verified=false)=>new(){Operation=op,RequestedMac=r.MacAddress,RequestedIp=r.IpAddress,FailureCategory=category,RollbackAttempted=attempted,RollbackVerified=verified};
    private static DhcpReservationSnapshot CaptureSnapshot(DhcpUciSection section)
    {
        var options = section.Options
            .Where(option => DhcpReservationRestoreOptions.Contains(option.Key))
            .ToDictionary(option => option.Key, option => option.Value, StringComparer.OrdinalIgnoreCase);
        string sourceMac = GetDhcpOption(section,"mac",string.Empty);
        string mac = NormaliseMacAddress(sourceMac);
        string ip = GetDhcpOption(section,"ip",string.Empty);
        options["mac"] = sourceMac;
        options["ip"] = ip;
        return new DhcpReservationSnapshot(mac,ip,options);
    }
    private async Task<bool> RestoreSnapshotAsync(DhcpReservationSnapshot snapshot,DhcpReservationIdentity? changedIdentity,CancellationToken callerToken)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken token = cleanup.Token;
        List<DhcpUciSection> originals = await FindSectionsAsync(snapshot.MacAddress,snapshot.IpAddress,token);
        if (originals.Count == 1) return true;
        if (originals.Count > 1) return false;

        DhcpUciSection? target = null;
        if (changedIdentity is not null)
        {
            List<DhcpUciSection> changed = await FindSectionsAsync(changedIdentity.MacAddress,changedIdentity.IpAddress,token);
            if (changed.Count > 1) return false;
            if (changed.Count == 1) target = changed[0];
        }

        if (target is null)
        {
            string created = (await _ssh.RunCommandAsync("uci add dhcp host 2>/dev/null",token)).Split(new[] {'\r','\n'},StringSplitOptions.RemoveEmptyEntries).Select(value=>value.Trim()).FirstOrDefault(value=>Regex.IsMatch(value,"^[A-Za-z0-9_]+$")) ?? string.Empty;
            if (created.Length == 0) return false;
            target = new DhcpUciSection(created) { Type = "host" };
        }

        if (!await RestoreOptionsAsync(target.Id,snapshot,token) || !await ApplyDhcpAsync(token)) return false;
        return (await FindSectionsAsync(snapshot.MacAddress,snapshot.IpAddress,token)).Count == 1;
    }
    private async Task<bool> RestoreOptionsAsync(string sectionId,DhcpReservationSnapshot snapshot,CancellationToken token)
    {
        if (!Regex.IsMatch(sectionId,"^[A-Za-z0-9_]+$")) return false;
        var commands = new List<string>();
        foreach (string option in DhcpReservationRestoreOptions)
        {
            if (snapshot.Options.TryGetValue(option,out string? value)) commands.Add($"uci set dhcp.{sectionId}.{option}='{EscapeShellSingleQuoted(value)}'");
            else commands.Add($"uci delete dhcp.{sectionId}.{option}");
        }
        return await DhcpCommandAsync(string.Join(" && ",commands),token);
    }
    private async Task<List<DhcpUciSection>> FindSectionsAsync(string mac,string ip,CancellationToken token) => ParseDhcpUciSections(await _ssh.RunCommandAsync("uci show dhcp 2>/dev/null",token)).Values.Where(section=>section.Type=="host"&&NormaliseMacAddress(GetDhcpOption(section,"mac",string.Empty))==NormaliseMacAddress(mac)&&GetDhcpOption(section,"ip",string.Empty)==ip).ToList();
    private static string EscapeShellSingleQuoted(string value) => (value ?? string.Empty).Replace("'", "'\"'\"'");
    private async Task<bool> DeleteByIdentityAsync(DhcpReservationIdentity i,CancellationToken t){var s=await ResolveAsync(i,t);if(s is null)return false;if(!await DhcpCommandAsync($"uci delete 'dhcp.{s.Id}'",t)||!await ApplyDhcpAsync(t))return false;return await ResolveAsync(i,t) is null;}
}
