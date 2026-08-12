using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

public sealed partial class GLInetSessionService
{
    private readonly SemaphoreSlim _vpnSocketGate = new(1, 1);
    private ClientWebSocket? _vpnSocket;
    private CancellationTokenSource? _vpnSocketCancellation;
    internal event Action<IReadOnlyList<VpnLiveStatusInfo>>? VpnStatusReceived;

    internal async Task EnsureVpnStatusSocketAsync(CancellationToken cancellationToken)
    {
        VpnLiveStatusDiagnostics.Record("GLInetSessionService.EnsureVpnStatusSocketAsync entered: YES");
        string startupStage = "Acquiring socket startup gate";
        await _vpnSocketGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            startupStage = "Checking existing socket";
            if (_vpnSocket?.State == WebSocketState.Open)
            {
                VpnLiveStatusDiagnostics.Record("GLInetSessionService.EnsureVpnStatusSocketAsync returned early: socket already open");
                return;
            }
            await StopVpnStatusSocketAsync().ConfigureAwait(false);
            startupStage = "Reading router input";
            Uri rpc = new(_rpcUrl);
            VpnLiveStatusDiagnostics.SetRouterInput(rpc);
            startupStage = "Reading authenticated session";
            string sid = GetCurrentSessionId();
            VpnLiveStatusDiagnostics.SetSidAvailability(sid);
            startupStage = "Building socket URI";
            var builder = new UriBuilder
            {
                Scheme = Uri.UriSchemeWss,
                Host = rpc.Host,
                Port = rpc.IsDefaultPort ? -1 : rpc.Port,
                Path = "/ws",
                Query = "sid=" + Uri.EscapeDataString(sid)
            };
            Uri socketUri = builder.Uri;
            VpnLiveStatusDiagnostics.Record("VPN socket URI created: YES");
            var socket = new ClientWebSocket();
            socket.Options.RemoteCertificateValidationCallback = ValidateVpnSocketCertificate;
            try
            {
                startupStage = "Opening socket";
                VpnLiveStatusDiagnostics.Record("VPN socket connecting: YES");
                await socket.ConnectAsync(socketUri, cancellationToken).ConfigureAwait(false);
                VpnLiveStatusDiagnostics.Record("VPN socket opened: YES; TLS trusted");
                byte[] frame = Encoding.UTF8.GetBytes("{\"cmd\":\"subscribe\",\"name\":\"vpnclient.status\"}");
                await socket.SendAsync(frame, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
                VpnLiveStatusDiagnostics.Record("VPN subscription sent: YES");
                _vpnSocket = socket;
                _vpnSocketCancellation = new CancellationTokenSource();
                _ = Task.Run(() => ReceiveVpnStatusAsync(socket, _vpnSocketCancellation.Token));
            }
            catch (Exception exception)
            {
                socket.Dispose();
                VpnLiveStatusDiagnostics.SetSocketStartupException(exception, startupStage);
                VpnLiveStatusDiagnostics.Record($"VPN socket failed: {exception.GetType().Name}");
                throw;
            }
        }
        catch (Exception exception)
        {
            VpnLiveStatusDiagnostics.SetSocketStartupException(exception, startupStage);
            throw;
        }
        finally { _vpnSocketGate.Release(); }
    }

    private bool ValidateVpnSocketCertificate(object _, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        bool trusted = certificate is X509Certificate2 certificate2 && ValidateRouterCertificate(null!, certificate2, chain, errors);
        VpnLiveStatusDiagnostics.Record(trusted ? "VPN socket TLS validation: trusted" : "VPN socket TLS validation: rejected");
        return trusted;
    }

    private async Task ReceiveVpnStatusAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        try
        {
            byte[] buffer = new byte[8192];
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using var stream = new MemoryStream();
                WebSocketReceiveResult result;
                do { result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false); if (result.MessageType == WebSocketMessageType.Close) { VpnLiveStatusDiagnostics.Record("VPN socket closed by router"); return; } stream.Write(buffer, 0, result.Count); }
                while (!result.EndOfMessage);
                if (result.MessageType != WebSocketMessageType.Text) continue;
                VpnLiveStatusDiagnostics.Record("VPN frame received: YES");
                ProcessVpnStatusFrame(stream.ToArray());
            }
        }
        catch (OperationCanceledException) { VpnLiveStatusDiagnostics.Record("VPN socket closed"); }
        catch (Exception exception) { VpnLiveStatusDiagnostics.Record($"VPN socket receive failed: {exception.GetType().Name}"); }
    }

    private void ProcessVpnStatusFrame(byte[] frame)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(frame);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("name", out JsonElement name) || name.GetString() != "vpnclient.status" || !root.TryGetProperty("data", out JsonElement data) || !data.TryGetProperty("status_list", out JsonElement list) || list.ValueKind != JsonValueKind.Array) return;
            VpnLiveStatusDiagnostics.Record($"vpnclient.status event received: YES; status_list count: {list.GetArrayLength()}");
            VpnLiveStatusDiagnostics.SetStatusListCount(list.GetArrayLength());
            var statuses = new List<VpnLiveStatusInfo>();
            foreach (JsonElement item in list.EnumerateArray()) if (item.ValueKind == JsonValueKind.Object && ReadSocketInt(item, "tunnel_id") > 0) { int statusValue = ReadSocketInt(item,"status"); VpnLiveStatusDiagnostics.SetStatusValue(statusValue); statuses.Add(new VpnLiveStatusInfo { TunnelId=ReadSocketInt(item,"tunnel_id"), Enabled=ReadSocketBool(item,"enabled"), Status=statusValue, Protocol=ReadSocketString(item,"type") ?? "Unknown", TxBytes=ReadSocketLong(item,"tx_bytes"), RxBytes=ReadSocketLong(item,"rx_bytes"), PeerName=ReadSocketString(item,"peer_name"), Domains=ReadSocketStrings(item,"domain"), GroupId=ReadSocketNullableInt(item,"group_id"), PeerId=ReadSocketNullableInt(item,"peer_id"), Via=ReadSocketString(item,"via"), Port=ReadSocketNullableInt(item,"port"), TunnelName=ReadSocketString(item,"name"), VirtualIpv4=ReadSocketString(item,"ipv4") }); }
            if (statuses.Count > 0)
            {
                bool fieldsPresent = statuses.All(status => status.TunnelId > 0 && !string.IsNullOrWhiteSpace(status.Protocol));
                VpnLiveStatusDiagnostics.Record($"VPN status event parsed: {(fieldsPresent ? "YES" : "PARTIAL")}; usable tunnel statuses: {statuses.Count}");
                VpnStatusReceived?.Invoke(statuses);
            }
            else VpnLiveStatusDiagnostics.Record("VPN status event parsed: NO usable tunnel status entries");
        }
        catch (JsonException) { VpnLiveStatusDiagnostics.Record("VPN status event parsing failed"); }
    }

    private static string? ReadSocketString(JsonElement item, string field) => item.TryGetProperty(field, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int ReadSocketInt(JsonElement item, string field) => ReadSocketNullableInt(item, field) ?? 0;
    private static int? ReadSocketNullableInt(JsonElement item, string field) => item.TryGetProperty(field, out JsonElement value) && (value.TryGetInt32(out int number) ? true : int.TryParse(value.GetString(), out number)) ? number : null;
    private static long ReadSocketLong(JsonElement item, string field) => item.TryGetProperty(field, out JsonElement value) && (value.TryGetInt64(out long number) ? true : long.TryParse(value.GetString(), out number)) ? number : 0;
    private static bool ReadSocketBool(JsonElement item, string field) => item.TryGetProperty(field, out JsonElement value) && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out bool parsed) && parsed);
    private static IReadOnlyList<string> ReadSocketStrings(JsonElement item, string field) => item.TryGetProperty(field, out JsonElement value) ? value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!).Where(value => !string.IsNullOrWhiteSpace(value)).ToList() : value.ValueKind == JsonValueKind.String ? [value.GetString()!] : [] : [];
    private async Task StopVpnStatusSocketAsync()
    {
        _vpnSocketCancellation?.Cancel(); _vpnSocketCancellation?.Dispose(); _vpnSocketCancellation = null;
        if (_vpnSocket is not null) { _vpnSocket.Dispose(); _vpnSocket = null; }
        await Task.CompletedTask;
    }

    private void DisposeVpnStatusSocket() => StopVpnStatusSocketAsync().GetAwaiter().GetResult();
}
