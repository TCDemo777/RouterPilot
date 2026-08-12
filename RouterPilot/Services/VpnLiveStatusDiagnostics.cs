using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace RouterPilot.Services;

/// <summary>
/// Safe, DEBUG-only breadcrumbs for the fixed VPN live-status transport.
/// It deliberately contains no endpoint, session, profile, or frame data.
/// </summary>
internal static class VpnLiveStatusDiagnostics
{
#if DEBUG
    private static readonly object Sync = new();
    private static readonly List<string> Steps = [];
    private static string _last = "VPN live status has not started.";
    private static bool _uriBuilt;
    private static bool _socketOpened;
    private static bool _subscriptionSent;
    private static bool _frameReceived;
    private static bool _eventReceived;
    private static bool _parsed;
    private static int? _statusListCount;
    private static bool? _tunnelMatched;
    private static int? _statusValue;
    private static bool _statusMapped;
    private static bool _uiDispatched;
    private static bool _tunnelUpdated;
    private static bool _summaryUpdated;
    private static bool _vpnViewRefreshEntered;
    private static bool _vpnSummaryRefreshEntered;
    private static bool _liveServiceEntered;
    private static bool _routerManagerEntered;
    private static bool _sessionSocketEntered;
    private static string? _socketStartupExceptionType;
    private static string? _socketStartupExceptionMessage;
    private static string? _socketStartupExceptionParamName;
    private static string? _socketStartupStage;
    private static bool? _routerInputPresent;
    private static string? _routerInputScheme;
    private static string? _routerInputHost;
    private static bool? _routerInputHasExplicitPort;
    private static int? _routerInputPort;
    private static bool? _routerInputHasPath;
    private static bool? _sidAvailable;
    private static int? _sidLength;

    internal static void Record(string message)
    {
        lock (Sync)
        {
            _uriBuilt |= message.StartsWith("VPN socket URI created: YES", StringComparison.Ordinal);
            _socketOpened |= message.StartsWith("VPN socket opened: YES", StringComparison.Ordinal);
            _subscriptionSent |= message.StartsWith("VPN subscription sent: YES", StringComparison.Ordinal);
            _frameReceived |= message.StartsWith("VPN frame received: YES", StringComparison.Ordinal);
            _eventReceived |= message.StartsWith("vpnclient.status event received: YES", StringComparison.Ordinal);
            _parsed |= message.StartsWith("VPN status event parsed: YES", StringComparison.Ordinal);
            _statusMapped |= message.StartsWith("VPN status=1 mapped to Connected: YES", StringComparison.Ordinal);
            _uiDispatched |= message.StartsWith("VPN UI dispatch completed: YES", StringComparison.Ordinal);
            _tunnelUpdated |= message.StartsWith("VpnTunnel live properties updated: YES", StringComparison.Ordinal);
            _summaryUpdated |= message.StartsWith("Shared VPN summary updated: YES", StringComparison.Ordinal);
            _vpnViewRefreshEntered |= message.StartsWith("VpnView.RefreshAsync entered: YES", StringComparison.Ordinal);
            _vpnSummaryRefreshEntered |= message.StartsWith("VpnSummaryService.RefreshAsync entered: YES", StringComparison.Ordinal);
            _liveServiceEntered |= message.StartsWith("VpnLiveStatusService.EnsureSubscribedAsync entered: YES", StringComparison.Ordinal);
            _routerManagerEntered |= message.StartsWith("RouterManager.EnsureVpnStatusSubscriptionAsync entered: YES", StringComparison.Ordinal);
            _sessionSocketEntered |= message.StartsWith("GLInetSessionService.EnsureVpnStatusSocketAsync entered: YES", StringComparison.Ordinal);
            if (message.StartsWith("VPN tunnel_id matched:", StringComparison.Ordinal))
                _tunnelMatched = message.Contains("YES", StringComparison.Ordinal);
            if (Steps.Count == 8) Steps.RemoveAt(0);
            Steps.Add(message);
            _last = string.Join("  →  ", Steps);
        }
        Debug.WriteLine($"[VPN Live Status] {message}");
    }

    internal static string Last
    {
        get { lock (Sync) return _last; }
    }

    internal static void SetStatusListCount(int count)
    {
        lock (Sync) _statusListCount = count;
    }

    internal static void SetStatusValue(int status)
    {
        lock (Sync) _statusValue = status;
    }

    internal static void SetSocketStartupException(Exception exception, string stage)
    {
        lock (Sync)
        {
            if (stage == "Awaiting VPN socket startup" && _socketStartupStage is not null)
                return;
            _socketStartupExceptionType = exception.GetType().Name;
            _socketStartupExceptionMessage = SafeExceptionMessage(exception.Message);
            _socketStartupExceptionParamName = (exception as ArgumentException)?.ParamName;
            _socketStartupStage = stage;
        }
    }

    internal static void SetRouterInput(Uri? routerInput)
    {
        lock (Sync)
        {
            _routerInputPresent = routerInput is not null;
            _routerInputScheme = routerInput?.Scheme;
            _routerInputHost = routerInput?.Host;
            _routerInputHasExplicitPort = routerInput is not null && !routerInput.IsDefaultPort;
            _routerInputPort = routerInput?.Port;
            _routerInputHasPath = routerInput is not null && routerInput.AbsolutePath is not "/";
        }
    }

    internal static void SetSidAvailability(string? sid)
    {
        lock (Sync)
        {
            _sidAvailable = !string.IsNullOrWhiteSpace(sid);
            _sidLength = sid?.Length;
        }
    }

    internal static string? SocketStartupExceptionType { get { lock (Sync) return _socketStartupExceptionType; } }
    internal static string? SocketStartupExceptionMessage { get { lock (Sync) return _socketStartupExceptionMessage; } }
    internal static string? SocketStartupExceptionParamName { get { lock (Sync) return _socketStartupExceptionParamName; } }
    internal static string? SocketStartupStage { get { lock (Sync) return _socketStartupStage; } }
    internal static bool? RouterInputPresent { get { lock (Sync) return _routerInputPresent; } }
    internal static string? RouterInputScheme { get { lock (Sync) return _routerInputScheme; } }
    internal static string? RouterInputHost { get { lock (Sync) return _routerInputHost; } }
    internal static bool? RouterInputHasExplicitPort { get { lock (Sync) return _routerInputHasExplicitPort; } }
    internal static int? RouterInputPort { get { lock (Sync) return _routerInputPort; } }
    internal static bool? RouterInputHasPath { get { lock (Sync) return _routerInputHasPath; } }
    internal static bool? SidAvailable { get { lock (Sync) return _sidAvailable; } }
    internal static int? SidLength { get { lock (Sync) return _sidLength; } }

    internal static string Report()
    {
        lock (Sync)
        {
            string unavailable = "-";
            string listCount = _statusListCount?.ToString() ?? unavailable;
            string tunnelMatched = _tunnelMatched is null ? unavailable : YesNo(_tunnelMatched.Value);
            string statusValue = _statusValue?.ToString() ?? unavailable;
            return $"DEBUG VPN SOCKET BUILD A\n" +
                $"Executing assembly: {Environment.ProcessPath ?? "Unknown"}\n\n" +
                $"VpnView.RefreshAsync entered: {YesNo(_vpnViewRefreshEntered)}\n" +
                $"VpnSummaryService.RefreshAsync entered: {YesNo(_vpnSummaryRefreshEntered)}\n" +
                $"VpnLiveStatusService.EnsureSubscribedAsync entered: {YesNo(_liveServiceEntered)}\n" +
                $"RouterManager.EnsureVpnStatusSubscriptionAsync entered: {YesNo(_routerManagerEntered)}\n" +
                $"GLInetSessionService.EnsureVpnStatusSocketAsync entered: {YesNo(_sessionSocketEntered)}\n\n" +
                $"VPN socket exception type: {_socketStartupExceptionType ?? unavailable}\n" +
                $"VPN socket exception stage: {_socketStartupStage ?? unavailable}\n" +
                $"VPN socket exception message: {_socketStartupExceptionMessage ?? unavailable}\n" +
                $"VPN socket exception parameter: {_socketStartupExceptionParamName ?? unavailable}\n\n" +
                $"Router input present: {YesNo(_routerInputPresent)}\n" +
                $"Router input scheme: {_routerInputScheme ?? unavailable}\n" +
                $"Router input host: {_routerInputHost ?? unavailable}\n" +
                $"Router explicit port: {FormatPort(_routerInputHasExplicitPort, _routerInputPort)}\n" +
                $"Router has path: {YesNo(_routerInputHasPath)}\n\n" +
                $"SID available: {YesNo(_sidAvailable)}\n" +
                $"SID length: {_sidLength?.ToString() ?? unavailable}\n\n" +
                $"Socket URI built: {YesNo(_uriBuilt)}\n" +
                $"Socket opened: {YesNo(_socketOpened)}\n" +
                $"Subscription sent: {YesNo(_subscriptionSent)}\n" +
                $"Frame received: {YesNo(_frameReceived)}\n" +
                $"vpnclient.status received: {YesNo(_eventReceived)}\n" +
                $"status_list parsed: {YesNo(_parsed)}\n" +
                $"status_list count: {listCount}\n" +
                $"Tunnel matched: {tunnelMatched}\n" +
                $"Status value: {statusValue}\n" +
                /*
                $"status_list count: {_statusListCount?.ToString() ?? \"—\"}\n" +
                $"Tunnel matched: {(_tunnelMatched is null ? \"—\" : YesNo(_tunnelMatched.Value))}\n" +
                $"Status value: {_statusValue?.ToString() ?? \"—\"}\n" +
                */
                $"Status mapped to Connected: {YesNo(_statusMapped)}\n" +
                $"UI dispatcher update: {YesNo(_uiDispatched)}\n" +
                $"Tunnel ViewModel updated: {YesNo(_tunnelUpdated)}\n" +
                $"Shared summary updated: {YesNo(_summaryUpdated)}";
        }
    }

    private static string YesNo(bool value) => value ? "YES" : "NO";
    private static string YesNo(bool? value) => value is null ? "-" : YesNo(value.Value);
    private static string FormatPort(bool? hasExplicitPort, int? port) => hasExplicitPort is null ? "-" : hasExplicitPort.Value ? port?.ToString() ?? "-" : "default";
    private static string SafeExceptionMessage(string message) =>
        message.Contains("sid=", StringComparison.OrdinalIgnoreCase)
            ? "Socket URI details redacted."
            : message;
#else
    internal static void Record(string message) { }
    internal static string Last => string.Empty;
    internal static void SetStatusListCount(int count) { }
    internal static void SetStatusValue(int status) { }
    internal static void SetSocketStartupException(Exception exception, string stage) { }
    internal static void SetRouterInput(Uri? routerInput) { }
    internal static void SetSidAvailability(string? sid) { }
    internal static string? SocketStartupExceptionType => null;
    internal static string? SocketStartupExceptionMessage => null;
    internal static string? SocketStartupExceptionParamName => null;
    internal static string? SocketStartupStage => null;
    internal static bool? RouterInputPresent => null;
    internal static string? RouterInputScheme => null;
    internal static string? RouterInputHost => null;
    internal static bool? RouterInputHasExplicitPort => null;
    internal static int? RouterInputPort => null;
    internal static bool? RouterInputHasPath => null;
    internal static bool? SidAvailable => null;
    internal static int? SidLength => null;
    internal static string Report() => "Low-level VPN socket diagnostics are available in Debug builds.";
#endif
}
