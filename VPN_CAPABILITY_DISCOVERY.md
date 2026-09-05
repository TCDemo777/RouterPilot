# RouterPilot VPN management capability discovery

Status: read-only repository audit. No router session was available to this
workspace during this investigation, so live Flint 2 model, firmware, UCI,
ubus, frontend and process results are explicitly **UNPROVEN**, not inferred.
No router command was issued by this document-only pass and no mutation
contract is approved solely because a generic command may exist.

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

## Live router discovery status

The repository's existing sanitized capability probe is intentionally bounded,
but it currently inspects `system`, `network.interface`, `firewall`, `service`
and `system` ubus schemas plus selected UCI packages (`network`, `wireless`,
`firewall`, `dhcp`, `mwan3`, `ddns`, `sqm`, `upnp`, `zerotier`, `glconfig`,
`glinet`, `nas`, `adguard`) and command/service presence. It does **not** yet
prove Flint 2 Tailscale/WireGuard/OpenVPN management contracts. In particular,
its command list does not include `tailscale`, `tailscaled`, `wg`,
`wg-quick`, `openvpn`, or `vpn-policy`.

Therefore the following live values remain unknown until a sanitized report is
collected from the selected router:

- Model: unknown
- Firmware/OpenWrt release: unknown
- Tailscale version: unknown
- Installed packages, init scripts, GL.iNet frontend/backend files and
  Tailscale UCI/RPC schema: unknown
- Whether the GL.iNet GUI wraps Tailscale CLI, UCI, ubus, or another helper:
  unknown

The safe next discovery operation is one aggregate, read-only capability
probe expanded to those command names and bounded file/RPC locations. It must
redact identities, addresses, credentials, keys, cookies and tokens before any
report leaves the router.

## Tailscale capability matrix

Confidence describes repository evidence, not a claim that every GL.iNet
firmware exposes the same contract.

| Capability | Read source | Read confidence | Write contract | Write confidence | Read-back | Risk | Recommendation |
|---|---|---:|---|---:|---|---|---|
| Installed | `command -v tailscale` over SSH | PROVEN | None | UNAVAILABLE | Same command | Low | Keep read-only |
| Enabled | No independent enabled flag; inferred only from status/process | PARTIAL | None | UNPROVEN | Not defined | Medium | Do not edit |
| Running | `pidof tailscaled` when status is empty; status backend otherwise | PROVEN | None | UNPROVEN | Status JSON | Medium | Keep read-only |
| Logged in | `BackendState` (`Running` vs `NeedsLogin`/`NoState`) | PROVEN | None | UNPROVEN | Status JSON | Medium | Keep read-only |
| Connect | None | UNAVAILABLE | No GL.iNet contract discovered | UNPROVEN | None | High | Do not add control |
| Disconnect | None | UNAVAILABLE | No GL.iNet contract discovered | UNPROVEN | None | High | Do not add control |
| Login | None | UNAVAILABLE | Auth-key/browser flow not discovered | UNPROVEN | None | Critical | Never expose credentials |
| Logout | None | UNAVAILABLE | No contract discovered | UNPROVEN | None | High | Do not add control |
| Accept DNS | Not parsed from status | UNAVAILABLE | No contract discovered | UNPROVEN | None | High | Read-only/unknown |
| Accept Routes | Not parsed from status | UNAVAILABLE | No contract discovered | UNPROVEN | None | High | Read-only/unknown |
| Advertised Routes | Not parsed from status | UNAVAILABLE | No contract discovered | UNPROVEN | None | High | Read-only/unknown |
| Advertise Exit Node | Not parsed from status | UNAVAILABLE | No contract discovered | UNPROVEN | None | Critical | Do not add control |
| Use Exit Node | Not parsed from status | UNAVAILABLE | No contract discovered | UNPROVEN | None | Critical | Do not add control |
| LAN Access | Not parsed from status | UNAVAILABLE | No contract discovered | UNPROVEN | None | High | Do not infer |
| WAN Access | Not parsed from status | UNAVAILABLE | No contract discovered | UNPROVEN | None | Critical | Do not infer |
| Tailscale SSH | Not parsed from status | UNAVAILABLE | No contract discovered | UNPROVEN | None | Critical | Do not add control |
| Shields Up | Not parsed from status | UNAVAILABLE | No contract discovered | UNPROVEN | None | High | Do not add control |
| Device Name | `Self.HostName` | PROVEN | None | UNPROVEN | Status JSON | Low | Display only |
| Tailnet Identity | No account/tailnet field is parsed | UNAVAILABLE | None | UNAVAILABLE | None | Privacy-sensitive | Do not collect yet |
| IPv4 | `Self.TailscaleIPs`, fallback `tailscale ip` | PROVEN | None | UNAVAILABLE | Status/CLI | Low | Display only |
| IPv6 | `Self.TailscaleIPs`, fallback `tailscale ip` | PROVEN | None | UNAVAILABLE | Status/CLI | Low | Display only |
| Peers | `Peer` object map | PROVEN | None | UNAVAILABLE | Status JSON | Medium | Display read-only |
| Peer Online | Peer `Online` | PROVEN | None | UNAVAILABLE | Status JSON | Low | Display read-only |
| Peer Routes | Not parsed | UNAVAILABLE | None | UNAVAILABLE | None | High | Do not infer from peer addresses |
| Version | `tailscale version` | PROVEN | None | UNAVAILABLE | CLI output | Low | Display only |
| Operator | Not parsed | UNAVAILABLE | None | UNAVAILABLE | None | High | Do not expose |
| Stateful filtering | Not parsed | UNAVAILABLE | None | UNAVAILABLE | None | High | Do not infer |
| Netfilter mode | Not parsed | UNAVAILABLE | None | UNAVAILABLE | None | High | Do not infer |

No Tailscale write is approved by this matrix. Generic `tailscale up`,
`tailscale down`, and `tailscale set` are not a GL.iNet management contract.

## GL.iNet VPN RPC evidence

The source contains these authenticated RPC calls; no mutation call was
invoked during discovery:

| Object | Method | Parameters | Classification | Evidence |
|---|---|---|---|---|
| `vpn-client` | `get_tunnel` | `{}` | Read | Used by `GetVpnTunnelsAsync` |
| `vpn-client` | `get_all_config_list` | `{}` | Read | Used for WireGuard/OpenVPN profile metadata |
| `vpn-client` | `set_tunnel` | `{ tunnel_id, enabled }` | Write | Existing `SetVpnTunnelEnabledAsync`; must be live-tested before expanding |

The inventory exposes tunnel ID/name, enabled state, protocol, interface,
kill-switch flag, linked profile groups, masquerade, local access and service
policy fields when present. It does not prove that each field is editable or
that a setting maps one-to-one to the GL.iNet GUI.

## WireGuard and OpenVPN

The same `vpn-client` RPC inventory enumerates `wireguard` and `openvpn`
profile groups, peer/config IDs, names, locations, provider markers, tunnel
links and counts. The live WebSocket may enrich a tunnel with status,
protocol, endpoint/domain, port, virtual address and RX/TX counters.

| Area | Current evidence | Management conclusion |
|---|---|---|
| Profiles | `get_all_config_list` | Read contract proven in code; live schema still required |
| Active profile | Correlated profile-group IDs and live status | Observable, not a write contract |
| Connected/disconnected | Tunnel `enabled` plus live status `Status == 1` | Read contract proven in code |
| Endpoint/interface/handshake/RX/TX | Some endpoint/interface and RX/TX fields are parsed; handshake is not | Partial read; do not claim full WireGuard contract |
| Enable/disable | `set_tunnel` | Existing narrow write; require Flint 2 read-back validation |
| Profile selection/connect/disconnect | No RPC method in repository | Unproven; do not implement |

Private keys, preshared keys, certificates and embedded OpenVPN credentials
are neither collected nor recorded.

## VPN policy and ZeroTier

VPN policy is only partially observable through tunnel fields such as
`service_policy`, `local_access`, `masq`, `from`, `to`, linked profiles and
kill-switch metadata. No authoritative per-client policy read contract was
found in the repository, and no client identity should be added to diagnostics
without a privacy review. Policy writes are **UNPROVEN**.

ZeroTier currently has aggregate installed/enabled visibility through advanced
router snapshots. No ZeroTier management RPC, profile read-back, connect,
disconnect or settings write contract was found. Keep it read-only.

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

- Secrets encountered: no live router data was available; no secrets were
  recorded.
- New timers: 0.
- Per-client probes: 0.
- N+1 SSH patterns: 0 added.
- Router mutations: 0.
- Tailscale/WireGuard/OpenVPN/ZeroTier writes invoked: 0.

## Required live follow-up

Run the existing sanitized capability report against the selected Flint 2,
then add bounded read-only probes for `tailscale`, `tailscaled`, `wg`,
`wg-quick`, `openvpn`, relevant init scripts, UCI namespaces, GL.iNet RPC
objects and frontend/backend references. Capture only method names, option
names, types and redacted values. Do not invoke `set_tunnel` or any candidate
mutation while discovering contracts.
