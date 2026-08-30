using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>Small, read-only mDNS PTR resolver for targeted live-client identity enrichment.</summary>
public sealed class MdnsIdentityService : IMdnsIdentityService
{
    private const int MdnsPort = 5353;
    private static readonly IPAddress MulticastAddress = IPAddress.Parse("224.0.0.251");
    private static readonly IPAddress MulticastAddressV6 = IPAddress.Parse("ff02::fb");
    private readonly ConcurrentDictionary<string, (string? Hostname, DateTime ExpiresUtc)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<string?> ResolveHostnameAsync(string ipAddress, CancellationToken cancellationToken = default)
    {
        if (!IPAddress.TryParse(ClientIdentity.NormalizeEndpoint(ipAddress), out IPAddress? address) || address is null ||
            address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            return null;

        string key = address.ToString();
        if (_cache.TryGetValue(key, out (string? Hostname, DateTime ExpiresUtc) cached) && cached.ExpiresUtc > DateTime.UtcNow)
            return cached.Hostname;

        string reverseName = BuildReverseName(address);
        try
        {
            using UdpClient client = new(address.AddressFamily);
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            client.Client.ReceiveTimeout = 1500;
            byte[] query = BuildQuery(reverseName);
            IPAddress multicast = address.AddressFamily == AddressFamily.InterNetwork ? MulticastAddress : MulticastAddressV6;
            await client.SendAsync(query, query.Length, new IPEndPoint(multicast, MdnsPort)).ConfigureAwait(false);

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(1500));
            UdpReceiveResult result = await client.ReceiveAsync(timeout.Token).ConfigureAwait(false);
            string? hostname = ParsePtrResponse(result.Buffer);
            _cache[key] = (hostname, DateTime.UtcNow.Add(hostname is null ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(10)));
            return hostname;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }

        _cache[key] = (null, DateTime.UtcNow.AddMinutes(1));
        return null;
    }

    private static string BuildReverseName(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            const string hex = "0123456789abcdef";
            return string.Join('.', bytes.Reverse().SelectMany(value => new[] { hex[value & 0xF], hex[value >> 4] })) + ".ip6.arpa";
        }
        return string.Join('.', bytes.Reverse().Select(value => value.ToString())) + ".in-addr.arpa";
    }

    private static byte[] BuildQuery(string name)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write((byte)0x52); writer.Write((byte)0x50); // deterministic query id
        writer.Write((byte)0); writer.Write((byte)0);
        writer.Write((byte)0); writer.Write((byte)1);
        writer.Write((byte)0); writer.Write((byte)0);
        writer.Write((byte)0); writer.Write((byte)0);
        writer.Write((byte)0); writer.Write((byte)0);
        foreach (string label in name.Split('.'))
        {
            writer.Write((byte)label.Length);
            writer.Write(Encoding.ASCII.GetBytes(label));
        }
        writer.Write((byte)0);
        WriteUInt16(writer, 12); // PTR
        WriteUInt16(writer, 1);  // IN
        return stream.ToArray();
    }

    private static string? ParsePtrResponse(byte[] packet)
    {
        if (packet.Length < 12) return null;
        int questions = ReadUInt16(packet, 4);
        int answers = ReadUInt16(packet, 6);
        int offset = 12;
        for (int i = 0; i < questions; i++)
        {
            if (!SkipName(packet, ref offset) || offset + 4 > packet.Length) return null;
            offset += 4;
        }
        for (int i = 0; i < answers; i++)
        {
            if (!SkipName(packet, ref offset) || offset + 10 > packet.Length) return null;
            ushort type = ReadUInt16(packet, offset);
            ushort cls = ReadUInt16(packet, offset + 2);
            offset += 8;
            ushort length = ReadUInt16(packet, offset); offset += 2;
            if (offset + length > packet.Length) return null;
            if (type == 12 && (cls & 0x7FFF) == 1)
            {
                int targetOffset = offset;
                string? target = ReadName(packet, ref targetOffset);
                return CleanHostnameForDisplay(target);
            }
            offset += length;
        }
        return null;
    }

    internal static string? CleanHostnameForDisplay(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string name = value.Trim().TrimEnd('.');
        if (name.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) name = name[..^6];
        if (string.IsNullOrWhiteSpace(name) || name.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            IPAddress.TryParse(name, out _)) return null;
        return name;
    }

    private static bool SkipName(byte[] packet, ref int offset) => ReadName(packet, ref offset) is not null;
    private static string? ReadName(byte[] packet, ref int offset)
    {
        List<string> labels = new();
        int cursor = offset;
        bool jumped = false;
        int guard = 0;
        while (cursor < packet.Length && guard++ < 128)
        {
            byte length = packet[cursor++];
            if (length == 0) { if (!jumped) offset = cursor; return string.Join('.', labels); }
            if ((length & 0xC0) == 0xC0)
            {
                if (cursor >= packet.Length) return null;
                int pointer = ((length & 0x3F) << 8) | packet[cursor++];
                if (!jumped) offset = cursor;
                cursor = pointer; jumped = true; continue;
            }
            if (length > 63 || cursor + length > packet.Length) return null;
            labels.Add(Encoding.UTF8.GetString(packet, cursor, length));
            cursor += length;
        }
        return null;
    }

    private static ushort ReadUInt16(byte[] buffer, int offset) => (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
    private static void WriteUInt16(BinaryWriter writer, ushort value) { writer.Write((byte)(value >> 8)); writer.Write((byte)value); }
}
