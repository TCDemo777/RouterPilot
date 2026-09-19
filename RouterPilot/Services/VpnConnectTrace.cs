using System.Diagnostics;
using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>DEBUG-only sanitized trace for one user Connect operation.</summary>
public sealed class VpnConnectTrace : IDisposable
{
    private readonly bool _enabled;
    private string _baseline = "FAILED";
    private int _baselineCount;
    private string _baselineClass = "UNAVAILABLE";
    private string _after = "NOT_RUN";
    private int _afterCount;
    private string _comparison = "NOT_APPLICABLE";
    private string _stuck = "UNKNOWN";
    private string _outcome = "<unavailable>";
    private string _serialization = "NO";
    private string _dispatch = "NO";
    private string _completed = "NO";
    private string _rpc = "NO";
    private string _postRead = "NO";
    private string _postEnabled = "<unavailable>";
    private string _postStatus = "<unavailable>";
    private bool _emitted;

    public VpnConnectTrace(bool enabled = true) => _enabled = enabled;
    public void Baseline(VpnWireGuardHandshakeSnapshot snapshot)
    {
        _baseline = snapshot.IsAvailable ? "SUCCESS" : "FAILED";
        _baselineCount = snapshot.LatestHandshakeTimestamps.Count;
        _baselineClass = !snapshot.IsAvailable ? "UNAVAILABLE" : snapshot.LatestHandshakeTimestamps.All(value => value == 0) ? "ZERO" : snapshot.LatestHandshakeTimestamps.All(value => value > 0) ? "POSITIVE" : "MIXED";
    }
    public void Applicability(bool applicable) => Debug.WriteLine($"CONNECT.HandshakeDetectorApplicable={(applicable ? "YES" : "NO")}");
    public void InterfaceAvailable(bool available) => Debug.WriteLine($"CONNECT.HandshakeInterfaceAvailable={(available ? "YES" : "NO")}");
    public void After(VpnWireGuardHandshakeSnapshot snapshot) { _after = snapshot.IsAvailable ? "SUCCESS" : "FAILED"; _afterCount = snapshot.LatestHandshakeTimestamps.Count; }
    public void Comparison(VpnWireGuardHandshakeState state) { _comparison = state switch { VpnWireGuardHandshakeState.NoHandshake => "NO_PROGRESS", VpnWireGuardHandshakeState.Successful => "ADVANCED", VpnWireGuardHandshakeState.Unavailable => "AMBIGUOUS", _ => "NOT_APPLICABLE" }; _stuck = state == VpnWireGuardHandshakeState.NoHandshake ? "YES" : state == VpnWireGuardHandshakeState.Unavailable ? "UNKNOWN" : "NO"; }
    public void Outcome(string value) => _outcome = value;
    public void Serialization(bool valid) => _serialization = valid ? "YES" : "NO";
    public void DispatchAttempted() => _dispatch = "YES";
    public void DispatchCompleted(bool succeeded) { _completed = "YES"; _rpc = succeeded ? "YES" : "NO"; }
    public void PostRpc(VpnLiveStatusInfo? status)
    {
        _postRead = status is null ? "NO" : "YES";
        _postEnabled = status?.Enabled == true ? "true" : status is null ? "<unavailable>" : "false";
        _postStatus = status?.Status.ToString() ?? "<unavailable>";
    }
    public void EmitIdentity(int tunnelId, bool preRead, bool enabled, string viaType, int? groupId, int? configId) { if (_enabled) Debug.WriteLine($"CONNECT.PreReadSucceeded={(preRead ? "YES" : "NO")}\nCONNECT.PreReadEnabled={(enabled ? "YES" : "NO")}\nCONNECT.PreReadViaType={viaType}\nCONNECT.PreReadGroupId={groupId?.ToString() ?? "<unavailable>"}\nCONNECT.PreReadConfigId={configId?.ToString() ?? "<unavailable>"}"); }
    public void Dispose()
    {
        if (!_enabled || _emitted) return; _emitted = true;
        Debug.WriteLine($"CONNECT.SerializationValidated={_serialization}\nCONNECT.DispatchAttempted={_dispatch}\nCONNECT.DispatchCompleted={_completed}\nCONNECT.RpcSucceeded={_rpc}\nCONNECT.PostRpcReadSucceeded={_postRead}\nCONNECT.PostRpcEnabled={_postEnabled}\nCONNECT.PostRpcStatus={_postStatus}\nCONNECT.HandshakeBaselineRead={_baseline}\nCONNECT.HandshakeBaselinePeerCount={_baselineCount}\nCONNECT.HandshakeBaselineClass={_baselineClass}\nCONNECT.HandshakeAfterRead={_after}\nCONNECT.HandshakeAfterPeerCount={_afterCount}\nCONNECT.HandshakeComparison={_comparison}\nCONNECT.StuckClassification={_stuck}\nCONNECT.FinalOutcome={_outcome}");
    }
}
