using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

public partial class RouterManager
{
    internal async Task<IReadOnlyList<LanClientInfo>> GetLanClientsAsync(CancellationToken token)
    {
        List<WifiClientInfo> inventory = await GetGlClientInventoryAsync();
        List<WifiRadioInfo> radios = await GetWifiRadiosAsync();
        var wifiMacs = radios.SelectMany(radio => radio.Clients).Select(client => LanClientClassifier.NormalizeMac(client.MacAddress)).Where(mac => mac.Length == 12).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // DHCP only enriches an already-proven Ethernet client. It is never a
        // fallback source for deciding that a device is wired.
        DhcpSnapshot dhcp;
        try
        {
            dhcp = await GetDhcpSnapshotAsync();
        }
        catch
        {
            dhcp = new DhcpSnapshot();
        }

        return inventory
            // hostapd is authoritative for current Wi-Fi associations. Only the
            // exact structured GL.iNet value "cable" is admitted as wired.
            .Where(client => LanClientClassifier.Classify(client.MacAddress, wifiMacs, client.Interface) == LanConnectionType.Wired)
            .GroupBy(client => LanClientClassifier.NormalizeMac(client.MacAddress), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Key.Length == 12)
            .Select(group => group.First())
            .Select(client =>
            {
                string mac = LanClientClassifier.NormalizeMac(client.MacAddress);
                DhcpLeaseInfo? lease = dhcp.Leases.FirstOrDefault(item => LanClientClassifier.NormalizeMac(item.MacAddress) == mac);
                DhcpReservationInfo? reservation = dhcp.Reservations.FirstOrDefault(item => LanClientClassifier.NormalizeMac(item.MacAddress) == mac);
                string name = !string.IsNullOrWhiteSpace(client.Name) && client.Name != "Unknown device" ? client.Name : lease?.ClientName ?? reservation?.Hostname ?? "Unknown device";
                return new LanClientInfo { Name = name, IpAddress = WifiClientInfo.Useful(client.IpAddress) ? client.IpAddress : lease?.IpAddress ?? "—", MacAddress = client.MacAddress, Interface = "Ethernet", IsStaticReservation = reservation is not null || lease?.IsStatic == true };
            })
            .OrderBy(client => client.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string NormalizeLanMac(string value) => new string((value ?? string.Empty).Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
}
