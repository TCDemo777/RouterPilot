using System;
using System.Text.Json;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services
{
    public class NetworkService
    {
        private readonly GLInetSshService _ssh;


        public NetworkService(GLInetSshService ssh)
        {
            _ssh = ssh;
        }


        public async Task<NetworkInfo> GetNetworkInfoAsync()
        {
            var info =
                new NetworkInfo();



            //
            // WAN
            //

            try
            {
                string wanJson =
                    await _ssh.RunCommandAsync(
                        "ubus call network.interface.wan status");


                using JsonDocument doc =
                    JsonDocument.Parse(wanJson);


                JsonElement root =
                    doc.RootElement;


                if (root.TryGetProperty(
                    "up",
                    out JsonElement up))
                {
                    info.Connected =
                        up.GetBoolean();
                }


                if (root.TryGetProperty(
                    "ipv4-address",
                    out JsonElement ipv4))
                {
                    if (ipv4.GetArrayLength() > 0)
                    {
                        info.WanIp =
                            ipv4[0]
                            .GetProperty("address")
                            .GetString()
                            ?? "-";
                    }
                }
            }
            catch
            {
                info.Connected = false;
            }



            //
            // Gateway
            //

            info.Gateway =
                (await _ssh.RunCommandAsync(
                    "ip route | grep default | awk '{print $3}'"))
                .Trim();



            //
            // External DNS
            //

            try
            {
                string dns =
                    await _ssh.RunCommandAsync(
                        "grep '^nameserver' /tmp/resolv.conf.d/resolv.conf.auto | awk '{print $2}'");


                info.ExternalDns =
                    dns.Trim()
                    .Replace("\r\n", ", ")
                    .Replace("\n", ", ");
            }
            catch
            {
                info.ExternalDns = "-";
            }



            //
            // Router LAN address
            //

            try
            {
                string advertised =
                    await _ssh.RunCommandAsync(
                        "uci get network.lan.ipaddr");


                info.RouterLanAddress =
                    advertised.Trim();
            }
            catch
            {
                info.RouterLanAddress = "-";
            }



            //
            // Latency
            //

            try
            {
                string ping =
                    await _ssh.RunCommandAsync(
                        "ping -c 1 1.1.1.1 | grep 'time='");


                int index =
                    ping.IndexOf("time=");


                if (index >= 0)
                {
                    string value =
                        ping.Substring(index + 5);


                    int end =
                        value.IndexOf(" ms");


                    if (end > 0)
                    {
                        info.Latency =
                            value.Substring(0, end)
                            + " ms";
                    }
                }
            }
            catch
            {
                info.Latency = "-";
            }


            return info;
        }
    }
}
