namespace RouterPilot.Services;

public sealed class RouterCapabilityDiscoveryService
{
    private readonly IRouterManagerProvider _provider;
    public RouterCapabilityDiscoveryService(IRouterManagerProvider provider) => _provider = provider;

    public async Task<string> CollectAsync(CancellationToken cancellationToken = default)
    {
        RouterManager manager = await _provider.GetRouterManagerAsync(cancellationToken).ConfigureAwait(false);
        const string command = "printf '__SECTION__ ROUTER BOARD\\n'; ubus call system board 2>/dev/null; printf '__SECTION__ UBUS OBJECTS\\n'; ubus list 2>/dev/null; printf '__SECTION__ UBUS METHOD SCHEMAS\\n'; for o in network.interface firewall service system; do ubus -v list \"$o\" 2>/dev/null; done; printf '__SECTION__ UCI PACKAGES\\n'; ls -1 /etc/config 2>/dev/null; printf '__SECTION__ SELECTED UCI SCHEMA\\n'; for c in network wireless firewall dhcp mwan3 ddns sqm upnp zerotier glconfig glinet nas adguard; do [ -f \"/etc/config/$c\" ] && { printf '\\n[%s]\\n' \"$c\"; uci show \"$c\" 2>/dev/null; }; done; printf '__SECTION__ COMMAND PRESENCE\\n'; for c in fw4 nft iw logread zerotier-cli tailscale tor sqm opkg; do command -v \"$c\" 2>/dev/null || true; done; printf '__SECTION__ SERVICE PRESENCE\\n'; ps w 2>/dev/null | grep -E '[z]erotier|[t]or|[s]qm|[d]pi|[w]ebdav|[d]lna|[d]dns' | head -n 40";
        return await manager.RunReadOnlySshCommandAsync(command, cancellationToken).ConfigureAwait(false);
    }
}
