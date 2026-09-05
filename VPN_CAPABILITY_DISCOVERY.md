# RouterPilot VPN management capability discovery

Status: bounded, sanitized, read-only Flint 2 capture plus repository audit.
The active RouterPilot profile reached the router over its existing SSH and
authenticated GL.iNet session infrastructure. No mutation method was invoked.
The capture was saved only temporarily for analysis and is not committed.

## Evidence and current architecture

| Area | Current implementation | Router operation | Lifecycle | Mutability |
|---|---|---|---|---|
| GL.iNet VPN client inventory | `VpnService` -> `RouterManager.Vpn` | Authenticated GL.iNet JSON-RPC `call` to object `vpn-client`, methods `get_tunnel` and `get_all_config_list` | `VpnView.RefreshAsync`, shared summary refresh, active profile generation/cancellation | Read inventory |
| VPN tunnel enable | `VpnService.SetTunnelEnabledAsync` | `vpn-client.set_tunnel` with `{ tunnel_id, enabled }` | Explicit user operation, serialized by a gate, read-back and rollback-on-enable failure | Existing write; live Flint 2 confirmation still required |
| VPN live status | `IVpnLiveStatusService` / session WebSocket | Session-bound VPN status subscription | Refresh/reconciliation and live status events | Read-only |
| Tailscale | `TailscaleStatusService` | Read-only SSH commands: `command -v tailscale`, `tailscale version`, `tailscale status --json`, `pidof tailscaled`, and fallback `tailscale ip` | VPN page refresh; cancellation and router/profile generation checks | Read-only |
| ZeroTier | Advanced/state snapshots | Existing aggregate telemetry parser exposes installed/enabled state | Shared router snapshot | Read-only |

The existing mutation pipeline is `VpnService.SetTunnelEnabledAsync`: acquire
the operation gate, validate a unique tunnel identity, apply one RPC, poll the
same authoritative inventory for read-back, and record the result. Future
writes should extend this pattern rather than introducing shell editing or a
second client.

### Current Tailscale fields

The VPN page currently displays state/detail, device name, version, IPv4,
IPv6, DNS name, peer count/online count, peer names/addresses/online state,
and a session history/summary. These map to `TailscaleStatus` and
`TailscalePeer`, populated by `TailscaleStatusService.ParseStatus` from the
router's `tailscale status --json` output. Peer route, exit-node, DNS-policy,
operator, netfilter and account fields are not parsed or displayed.

The Tailscale reads are guarded by a per-view semaphore, cancellation token,
and `IActiveRouterContext` profile/version checks. Navigation cancels refresh;
there is no Tailscale polling loop in this discovery pass.

## Live Flint 2 capture

The selected profile reached `192.168.1.1` through RouterPilot's existing
`RouterManagerProvider` and SSH connection factory. The sanitized board result
proves:

- Model: `GL.iNet GL-MT6000` / board `glinet,gl-mt6000`
- Kernel: `5.4.238`
- Architecture/system: `ARMv8 Processor rev 4`, target `mediatek/mt7986`
- OpenWrt: `21.02-SNAPSHOT`, revision `r15812+1092-46b6ee7ffc`
- GL.iNet product firmware version: not exposed by `system board` or the
  bounded version reads; do not substitute the OpenWrt release
- Tailscale: `/usr/sbin/tailscale`, version `1.92.5-1 (OpenWrt)`
- `tailscaled` binary exists, but no daemon process was observed and
  `tailscale.settings.enabled='0'`; `tailscale status --json` and `tailscale
  ip` returned no status/address data because the service is disabled/stopped

Relevant live UCI namespaces are present: `tailscale`, `route_policy`,
`wireguard`, `openvpn`, `ovpnclient`, `ovpnserver`, and `zerotier`. The live
Tailscale settings include `enabled=0`, `lan_enabled=0`, `wan_enabled=0`,
`run_exit_node=0`, and `masq=0`. The live route-policy config contains a
disabled client rule plus process/tunnel rules; client identities and private
addresses were redacted.

Relevant live backend files include `/etc/init.d/tailscale`,
`/usr/bin/gl_tailscale`, `/usr/lib/lua/gl/vpn_client.lua`,
`/etc/init.d/vpn-client`, and compressed frontend bundles
`gl-sdk4-ui-tailscaleview.common.js.gz` and
`gl-sdk4-ui-vpn-client.common.js.gz`.

The GL.iNet frontend bundle explicitly references the Tailscale RPC methods
`tailscale.get_config`, `tailscale.set_config`, `tailscale.get_status`,
`tailscale.get_auth_url`, `tailscale.get_exit_node_list`, and
`tailscale.logout`. It also references `vpn-client.get_tunnel`,
`vpn-client.set_tunnel`, `vpn-client.add_tunnel`, `vpn-client.stop`, and
`ovpn-client` group/config methods. These are discovered contracts, not
permission to invoke writes.

## Tailscale capability matrix

Confidence describes repository evidence, not a claim that every GL.iNet
firmware exposes the same contract.

| Capability | Read source | Read confidence | Write contract | Write confidence | Read-back | Risk | Recommendation |
|---|---|---:|---|---:|---|---|---|
| Installed | Live `command -v tailscale` -> `/usr/sbin/tailscale` | PROVEN | None | UNAVAILABLE | Same command | Low | Keep read-only |
| Enabled | Live `tailscale.settings.enabled='0'` | PROVEN | `tailscale.set_config` in GL.iNet frontend | UNPROVEN | `tailscale.get_config` | High | Do not add control yet |
| Running | No `tailscaled` process; init script present | PROVEN | `tailscale.set_config` may restart service | UNPROVEN | `tailscale.get_status` | High | Keep read-only |
| Logged in | No backend status while disabled | UNAVAILABLE | `tailscale.get_auth_url`/logout references | UNPROVEN | `get_status` | High | Do not expose auth control |
| Connect | `get_config`/`get_status` references | PARTIAL | `tailscale.set_config` | UNPROVEN | `get_status` | Critical | Requires controlled test |
| Disconnect | Init script and `set_config` references | PARTIAL | `tailscale.set_config` or logout | UNPROVEN | `get_status` | Critical | Do not implement |
| Login | `get_auth_url` frontend reference | OBSERVABLE ONLY | Browser/auth flow not proven | UNPROVEN | `get_status` | Critical | Never handle auth keys |
| Logout | `tailscale.logout` frontend reference | OBSERVABLE ONLY | `tailscale.logout` | UNPROVEN | `get_status` | Critical | Do not implement |
| Accept DNS | No field in captured config; helper changes dnsmasq | PARTIAL | `set_config` shape incomplete | UNPROVEN | `get_config` | High | Keep unknown |
| Accept Routes | Not exposed by captured config | UNAVAILABLE | None proven | UNPROVEN | None | High | Do not infer |
| Advertised Routes | `gl_tailscale` route helpers only | OBSERVABLE ONLY | No proven UI parameter | UNPROVEN | None | Critical | Do not add control |
| Advertise Exit Node | `run_exit_node` field and confirmation UI | STRONG | `set_config` | UNPROVEN | `get_config` | Critical | Requires safety proof |
| Use Exit Node | `exit_node_ip` and `get_exit_node_list` | STRONG | `set_config` | UNPROVEN | `get_config` | Critical | Do not implement yet |
| LAN Access | Live `lan_enabled='0'`; frontend field | PROVEN | Complete direct `tailscale.set_config` object; target field isolated | PROVEN (live GL-MT6000) | `get_config` | High | Keep harness evidence; no production UI yet |
| WAN Access | Live `wan_enabled='0'`; frontend field | PROVEN | Complete direct `tailscale.set_config` object; target validation pending | UNPROVEN | `get_config` | Critical | Await local validation |
| Tailscale SSH | Not present in captured config/status | UNAVAILABLE | None proven | UNPROVEN | None | Critical | Do not add control |
| Shields Up | Not present in captured config/status | UNAVAILABLE | None proven | UNPROVEN | None | High | Do not add control |
| Device Name | Repository parses `Self.HostName`; daemon stopped | UNAVAILABLE | None | UNAVAILABLE | `status --json` when running | Low | Display when available |
| Tailnet Identity | No account data captured | UNAVAILABLE | None | UNAVAILABLE | None | Privacy-sensitive | Do not collect |
| IPv4 | `tailscale ip` returned no value while stopped | UNAVAILABLE | None | UNAVAILABLE | Status/CLI when running | Low | Display when available |
| IPv6 | `tailscale ip` returned no value while stopped | UNAVAILABLE | None | UNAVAILABLE | Status/CLI when running | Low | Display when available |
| Peers | No status JSON while stopped | UNAVAILABLE | None | UNAVAILABLE | `status --json` when running | Medium | Display when available |
| Peer Online | No status JSON while stopped | UNAVAILABLE | None | UNAVAILABLE | `status --json` when running | Low | Display when available |
| Peer Routes | Not parsed | UNAVAILABLE | None | UNAVAILABLE | None | High | Do not infer from peer addresses |
| Version | Live `tailscale version` -> `1.92.5-1 (OpenWrt)` | PROVEN | None | UNAVAILABLE | CLI output | Low | Display only |
| Operator | Not parsed | UNAVAILABLE | None | UNAVAILABLE | None | High | Do not expose |
| Stateful filtering | Not parsed | UNAVAILABLE | None | UNAVAILABLE | None | High | Do not infer |
| Netfilter mode | Not parsed | UNAVAILABLE | None | UNAVAILABLE | None | High | Do not infer |

No Tailscale write is approved by this matrix. Generic `tailscale up`,
`tailscale down`, and `tailscale set` are not a GL.iNet management contract.

## GL.iNet VPN RPC evidence

The live frontend and RouterPilot source contain these authenticated RPC
calls; no mutation call was invoked during discovery:

| Object | Method | Parameters | Classification | Evidence |
|---|---|---|---|---|
| `vpn-client` | `get_tunnel` | `{}` | Read | Used by `GetVpnTunnelsAsync` |
| `vpn-client` | `get_all_config_list` | `{}` | Read | Used for WireGuard/OpenVPN profile metadata |
| `vpn-client` | `set_tunnel` | `{ tunnel_id, enabled }` | Write | Existing `SetVpnTunnelEnabledAsync`; must be live-tested before expanding |

The live `get_tunnel` read returned one tunnel:

- tunnel ID `38`, protocol `WireGuard`, interface `wgclient1`
- enabled `false`, kill switch `true`, local access `false`, masquerade `true`
- linked profile group `5456`

The live `get_all_config_list` read returned 11 WireGuard profile groups; the
active linked group was `5456` and had one server entry. No OpenVPN tunnel was
active in this capture. This proves the read path and identifiers, not that a
write would be safe without a controlled test.

The GL.iNet frontend also references `vpn-client.add_tunnel`,
`vpn-client.stop`, `vpn-client.set_tap_s2s`, and separate `ovpn-client`
group/config methods (`get_group_list`, `get_config_list`, `set_group`,
`add_group`, `remove_group`, `check_config`, `confirm_config`). These methods
were not invoked; their presence is not approval to use them.

## WireGuard and OpenVPN

The same `vpn-client` RPC inventory enumerates `wireguard` and `openvpn`
profile groups, peer/config IDs, names, locations, provider markers, tunnel
links and counts. The live WebSocket may enrich a tunnel with status,
protocol, endpoint/domain, port, virtual address and RX/TX counters.

| Area | Current evidence | Management conclusion |
|---|---|---|
| Profiles | Live `get_all_config_list` (11 WireGuard groups) | Read contract PROVEN on this Flint 2 |
| Active profile | Live tunnel group `5456`, profile correlation | Observable, not a write contract |
| Connected/disconnected | Live tunnel `enabled=false`; live status `Status == 1` when available | Read contract proven; this capture did not invoke a write |
| Endpoint/interface/handshake/RX/TX | Some endpoint/interface and RX/TX fields are parsed; handshake is not | Partial read; do not claim full WireGuard contract |
| Enable/disable | `set_tunnel` | Existing narrow write; live identifier/scope is proven, safety/read-back after write is not |
| Profile selection/connect/disconnect | No RPC method in repository | Unproven; do not implement |

Private keys, preshared keys, certificates and embedded OpenVPN credentials
are neither collected nor recorded.

## VPN policy and ZeroTier

The live `route_policy` UCI namespace is present. It exposes global policy
flags (`service_policy_en`, `mode`, `enabled`), a default rule, a disabled
WireGuard client rule with tunnel/group/peer references, and process policy
rules. The `gl/vpn_client.lua` helper reads `route_policy.global.service_policy_en`
and legacy `vpnpolicy.global.service_policy`. This is authoritative router
configuration evidence for policy state, but not yet a stable aggregate
per-client UI contract; client identifiers were redacted.

VPN policy writes are **UNPROVEN**. Do not edit UCI or call route-policy
helpers directly.

ZeroTier is installed (`/usr/bin/zerotier-cli`) but the live UCI state is
`zerotier.gl.enabled='0'`. No ZeroTier management RPC, profile read-back,
connect, disconnect or settings write contract was found. Keep it read-only.

## Future page and mutation safety model

The future page may contain read-only cards for Tailscale, WireGuard/OpenVPN,
ZeroTier and VPN policy, each marked Supported, Unsupported, Unknown or
Unavailable from capability evidence. Editable controls must remain disabled
when capability is Unknown.

Every future write must use one pipeline:

1. Validate the capability and active router/profile generation.
2. Ask for confirmation for connectivity-affecting changes.
3. Execute exactly one proven RPC through the existing operation gate.
4. Apply bounded cancellation/timeout handling.
5. Read back authoritative state.
6. Publish only the confirmed result; surface Applying/Failed without an
   optimistic permanent state.
7. Roll back only when the specific contract defines a safe rollback.

The canonical VPN state must preserve `Connecting`, `Disconnecting`,
`Connected`, `Off`, `Needs Login`, `Unavailable`, `Unsupported`, `Unknown` and
`Error`. Operation intent must remain separate from ambiguous backend states.
Client egress IP, router WAN address, router egress/public IP and VPN exit IP
must likewise remain separate concepts.

## Phased implementation plan (not implemented)

### Phase A — lowest risk

Validate the existing `vpn-client.set_tunnel` enable/disable call on supported
firmware with explicit confirmation, generation checks, bounded operation,
read-back and safe failure handling. Keep it capability-gated and do not add
Tailscale writes yet.

### Phase B — Tailscale configuration

Only after a live Flint 2 capture proves the GL.iNet GUI backend, parameters,
and read-back semantics for individual settings. Prefer the GL.iNet contract
over generic CLI flags.

### Phase C — WireGuard/OpenVPN

Expand only after profile-selection/connect/disconnect methods and their
read-back/rollback behavior are captured from the router frontend/backend.

### Phase D — VPN policy

Last, after privacy-safe aggregate policy reads and a proven atomic write
contract are available. No per-client polling or speculative UCI editing.

## Security and operation budget

- Secrets encountered: protected fields were present in router configuration,
  but were redacted before output; no secret values were recorded.
- New timers: 0.
- Per-client probes: 0.
- N+1 SSH patterns: 0 added.
- Router mutations: 0.
- Tailscale/WireGuard/OpenVPN/ZeroTier writes invoked during the original
  read-only capture: 0. The later controlled `lan_enabled` validation is
  recorded below; no other mutation was attempted.

## Controlled Tailscale mutation evidence

The developer-only mutation harness successfully validated `lan_enabled` on
the live GL-MT6000/Flint 2 using the complete direct `tailscale.set_config`
settings object:

1. Original value: `false`.
2. Temporary value: `true`.
3. Write: PASS.
4. Immediate authoritative read-back: PASS.
5. Restoration required: YES; restore: PASS.
6. Final authoritative read-back: PASS (`false`).
7. Router restored: YES.

`wan_enabled` remains **AWAITING LOCAL VALIDATION**. No production VPN UI
control has been added.

## Decision gate and next step

`vpn-client.set_tunnel` is **not yet proven safe enough** for a RouterPilot
enable/disable control: the live tunnel identifier and scope are known, and
`get_tunnel` is an available authoritative read-back, but no write was
intentionally invoked and rollback behavior has not been tested on this
router. The method applies to the live WireGuard tunnel path; the frontend
also supports OpenVPN, but no OpenVPN tunnel was active in this capture.

Authoritative read-back after a future write: **YES as a method** (`get_tunnel`),
but **not exercised after a write** in this read-only run.

Tailscale-specific writable contracts proven safe: **`lan_enabled` only**, on
the validated live GL-MT6000. `wan_enabled` remains unproven until the same
controlled harness sequence completes locally. Login/logout, exit-node,
masquerade, route, and enablement mutations remain unproven and potentially
disruptive.

The single largest safe next batch is a controlled, explicitly confirmed
Phase-A test of the existing WireGuard `set_tunnel` operation on a disposable
or maintenance window, with generation checks, bounded timeout, immediate
`get_tunnel` read-back, and a documented rollback plan. No Tailscale writes
should be implemented before an equivalent contract test.
