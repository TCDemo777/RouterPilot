# RouterPilot 3.0 Slice 01: Device Identity Foundation

## Purpose

This is the implementation plan for the first RouterPilot 3.0 migration slice: a small, read-side device identity foundation. It follows [ROUTERPILOT_3.0_VISION.md](ROUTERPILOT_3.0_VISION.md) and [ROUTERPILOT_3.0_ARCHITECTURE.md](ROUTERPILOT_3.0_ARCHITECTURE.md).

It establishes a clear device-domain boundary while preserving RouterPilot 2.x behaviour. It intentionally produces **no user-visible behavioural change**, no router mutation change, and no new persistence format.

This document is a plan only. It does not authorize application, test, project, dependency, transport, VPN, persistence, or UI changes until reviewed.

## Slice Objective and Boundary

Slice 01 separates four concepts that already exist in several 2.x types and services:

| Concept | Slice 01 definition |
|---|---|
| Device identity | Normalized logical identity for the current proven MAC-based device path. |
| Device observation | Router/inventory observation of an identity, including explicit current presence when known. |
| Device profile / user metadata | Existing persisted `ClientProfile` data: user naming, notes, category, monitoring/favourite/review state, and remembered observations. |
| Device feature projection | A read-only feature representation derived from identity, observation, and profile data. |

The slice does not create a universal device platform, new device UI, generic identity framework, new persistence store, or speculative non-MAC identity support.

## Repository Findings

### Raw identity entry points

Raw client identity currently enters through concrete router paths:

- `RouterManager.GetGlClientInventoryAsync` and `GetWifiRadiosAsync` return GL.iNet inventory observations. `ClientsViewModel.LoadClientsAsync` merges radio clients with inventory-only clients.
- `ClientInventoryCoordinator.LoadAuthoritativeInventoryAsync` performs the same read-only Wi-Fi/inventory reconciliation for shared, session-level consumers.
- `RouterManager.GetAdGuardClientsAsync` provides optional AdGuard client records. `ClientsViewModel.ApplyAdGuardEnrichment` and `ClientInventoryCoordinator.ToClient` correlate MAC first, then normalized endpoint/IP.
- `RouterManager.GetGlClientPresenceSnapshotAsync` provides the explicit presence map used by VPN routing projection.
- `RouterManager.GetVpnRoutingPolicyAsync` and WireGuard assignment reads provide authoritative VPN-assigned identities.
- `DataStatisticsParser` supplies `ApplicationDeviceTraffic.NormalizedMac` when traffic input has a valid MAC.
- DHCP lease/reservation data supplies MAC-backed configured-name and client associations.

`Models/ClientInfo.cs` is the current compatibility record. It combines raw router fields, AdGuard enrichment, profile projection, connection information, and display properties. It remains valuable, but it is not a narrow identity-domain type.

### MAC and endpoint normalization

`Models/ClientIdentity.cs` is the current normalization authority:

- `NormalizeMac` produces historic uppercase separator-free alphanumeric profile/presence keys.
- `NormalizeHexMac` produces strict uppercase separator-free hexadecimal keys for LAN/DHCP/VPN reconciliation.
- `IsMacKey` currently accepts a normalized key of length twelve.
- `NormalizeEndpoint` normalizes IPv4, IPv4-mapped IPv6, bracketed endpoints with ports, and host-name case/trailing dots for AdGuard/router correlation.

The `NormalizeMac`/`NormalizeHexMac` distinction is established behaviour. Slice 01 must not simplify it away. New typed MAC identity creation should use the stricter twelve-character hexadecimal eligibility rule; endpoint correlation remains a distinct mechanism.

### Authoritative inventory ownership

`Services/ClientInventoryState.cs` is the shared session-level cache. It owns a normalized string-keyed `Snapshot`, explicit `PresenceSnapshot`, and `Changed` notification. `Update`, `AddMissing`, and `UpdateAuthoritativePresence` normalize keys; absence from the presence map is deliberately **Unknown**, not Offline.

`Services/ClientInventoryCoordinator.cs` owns read-only shared reconciliation. It serializes load with a semaphore, treats router Wi-Fi/inventory as primary, treats AdGuard as optional enrichment, applies profile information, updates shared inventory, and records `ClientPresenceHistoryService`. Its cached load is reused by cold deep-link consumers.

`ViewModels/ClientsViewModel.cs` remains the richer authoritative Clients-page producer. Beyond its router reads it performs configured display-source selection, profile maintenance, new-device detection, activity recording, async manufacturer/mDNS enrichment, filtering, sorting, and UI state. After successful reconciliation it updates `ClientInventoryState`.

**Current behaviour to preserve:** the coordinator is intentionally a smaller read-only reconciliation path. It is not identical to the full Clients-page projection.

### Profiles and friendly names

`ClientProfileService` persists `ClientProfile` through `AtomicJsonFileStore` in `client-profiles.json` and includes legacy favourites migration. `ClientProfile` owns nickname, notes, category, favourite/monitor/known/review flags, first/last seen times, and remembered name/IP/connection fields.

Name policy has real feature-specific differences today:

1. `DeviceIdentityResolver.ResolveFriendlyName(DeviceIdentitySignals)` prioritizes user nickname, specific router name, DHCP hostname, mDNS hostname, AdGuard name, persisted name, then `Unknown device`. It rejects operating-system, generic device, service, internal/raw, and generated-IP labels. An explicit user nickname remains authoritative.
2. `ClientsViewModel.EnrichClient` invokes that resolver, then `ApplyProfile` makes a non-empty profile nickname final. Its mDNS continuation can improve a non-unknown name.
3. `ClientDisplayNameService` implements the persisted bulk name-source setting (`Automatic`, `Router`, `AdGuard`, `ConfiguredNames`). `ClientConfiguredNameResolver` maps router reservations by MAC and configured AdGuard names by MAC or exact IP; `ClientNamePresentation` selects the configured source. This changes `ClientInfo.Name`, not identity.
4. `KnownDeviceInfo.Name` falls back from a current useful name to `DeviceIdentityResolver` using profile/router/AdGuard/persisted signals.
5. `ApplicationTrafficDetailsViewModel.Project` uses a narrower rule: profile nickname, live client name, statistics hostname, then `Unknown device`.

Slice 01 preserves these differences. A single product-wide friendly-name rule is a **FUTURE DESIGN DECISION**, not an implicit migration outcome.

### Online, offline, known, and unknown

- `KnownDeviceInfo` treats a current inventory record as online; a profile missing from that inventory is “Not currently observed.”
- `ClientPresenceHistoryService` derives transitions from authoritative aggregate snapshots with a five-minute absence grace period. Unobserved time remains unknown.
- `ClientInventoryState.PresenceSnapshot` communicates only explicit router presence. A missing key means unknown, not offline.
- Profile-only known devices keep remembered display/IP/connection data but do not fabricate live or DNS data.
- `VpnService.ApplyRoutingPolicy` and `VpnDeviceEditorProjection` preserve unresolved authoritative assignments and safely label them `Unknown device N` without exposing raw identity.

### Deduplication and enrichment

- `ClientsViewModel.BuildRouterClients` groups valid MAC-backed observations by MAC and otherwise uses an IP display fallback.
- `ClientInventoryCoordinator` and `ClientInventoryState` publish only valid MAC-backed clients, grouped by normalized MAC.
- `ClientConfiguredNameResolver.Unique` refuses to guess where one normalized identity has duplicate configured names.
- AdGuard enrichment is MAC-first then endpoint/IP fallback. Failure is optional enrichment failure and must not erase primary router inventory.
- `ClientIdentityEnrichmentCoordinator`, manufacturer lookup, and mDNS enrichment are additive/best effort.

### Router context and stale work

`ActiveRouterContext` owns active profile and monotonically increasing version. `RouterManagerProvider` invalidates by connection signature. `RouterSwitchCoordinator.SwitchCoreAsync` invalidates session, clears `ClientInventoryState`, resets `ClientInventoryCoordinator`, and resets other router-dependent state before switching profile.

`ProtectionViewModel` demonstrates the desired context pattern: capture profile ID/version plus local epoch, then reject stale completion before publication. `ClientInventoryCoordinator` currently serializes refreshes and is reset during switch, but does not carry an `IActiveRouterContext` stamp through read completion. This shared-inventory stale-publication gap is appropriate for Slice 01 to close.

## Current Behaviour Contract

Slice 01 must preserve these repository-supported behaviours.

### Identity

- Valid MAC input differs only by supported case/separators where current normalizers consider it equal.
- Only strict valid twelve-character hexadecimal values become shared MAC-backed inventory/profile/VPN keys where the current path requires hardware identity.
- Endpoint correlation remains separate from MAC identity.
- Equal names never establish identity equivalence; MAC resolution still selects the correct client where names collide.

### Profile/name behaviour

- `ClientProfile` remains the persistence owner; no format migration is expected.
- User nickname behaviour remains unchanged.
- Configured router/AdGuard display-source choice remains controlled by `ClientDisplayNameService` and `ClientNamePresentation`.
- Unsafe/generic/generated labels remain excluded from friendly-name resolution.
- Existing feature-specific precedence differences remain unchanged.

### Inventory/presence behaviour

- Router Wi-Fi/inventory remains the shared inventory’s primary source.
- AdGuard failure cannot erase router-derived inventory.
- Known-offline profile projection remains available.
- Missing explicit presence remains Unknown.
- Existing first-record duplicate reconciliation remains unchanged; no merge/guessing is introduced.

### Context/persistence behaviour

- Router switch clears active inventory before new-context results publish.
- A prior-context inventory completion must not repopulate inventory after a switch.
- Existing normalized profile keys and profile-store failure semantics remain compatible.
- No RPC/SSH/AdGuard request, VPN assignment, payload, or mutation behaviour changes.

## Target Slice-1 Domain Contract

### DeviceIdentity

Introduce one small MAC-only domain value, tentatively `DeviceIdentity` in `RouterPilot.Models`.

- Factory creation delegates to current `ClientIdentity.NormalizeHexMac` and existing strict validation.
- It carries only canonical MAC value and `MacAddress` kind in Slice 01.
- It does not replace `ClientIdentity`; legacy normalizers remain for endpoint, profile, DHCP, VPN, and parser consumers.
- It does not implement IP, hostname, mDNS, AdGuard, or inferred identity kinds.

### DeviceObservation and snapshot

Introduce a minimal immutable typed inventory boundary, tentatively `DeviceObservation` plus `DeviceInventorySnapshot`.

An observation holds a typed identity, existing `ClientInfo` compatibility record, router-inventory source, and explicit presence if available. A snapshot carries the accepted observations together with observation timestamp and active router-context version.

Only metadata already justified by current code and stale-publication needs belongs here. This is not a user profile, UI selection, or generic global store.

`ClientInventoryState.Snapshot` and `PresenceSnapshot` remain unchanged compatibility views. They must be derived from the exact same accepted typed observations so old/new identity counts cannot diverge.

### Device profile and projection

`ClientProfile` and `ClientProfileService` remain unchanged. A narrow read-only adapter may translate `DeviceIdentity` to existing normalized profile lookup; it must preserve `LastLoadSucceeded` semantics and never write while resolving.

Known Devices becomes the only first consumer. Its projection input becomes typed identity plus optional current observation plus optional existing profile, while preserving the existing `KnownDeviceInfo` naming, filtering, sorting, selected-device, and profile-only behaviour. This is not a universal feature DTO.

## Type and File Proposal

| Area | Purpose and dependencies | Why needed now |
|---|---|---|
| New `Models/DeviceIdentity.cs` | Strict MAC-backed value/factory built on `ClientIdentity.NormalizeHexMac`; no transport dependency | Existing strings do not distinguish arbitrary input from validated device identity |
| New `Models/DeviceObservation.cs` or colocated snapshot model | Identity, `ClientInfo` compatibility data, source, explicit presence, context/timestamp | Separates router observation from profile and UI state without replacing existing `ClientInfo` |
| `Services/ClientInventoryState.cs` | Publish immutable typed snapshot alongside existing string maps | Gives migrated consumers a coherent current inventory boundary while preserving all old consumers |
| `Services/ClientInventoryCoordinator.cs` | Capture/revalidate `IActiveRouterContext` around shared refresh; build typed snapshot | Prevents stale shared inventory publication without changing router reads |
| Optional narrow profile lookup near `ClientProfileService` | Read existing profile map through typed identity | Avoids repeated normalized-string lookup in migrated consumer without duplicating persistence |
| `Models/KnownDeviceInfo.cs` and `ViewModels/KnownDevicesViewModel.cs` | Consume typed observation/profile input | Demonstrates a useful read-side migration with strong existing coverage |
| `ClientDetailsNavigationPreparation.cs`, only if necessary | Internal compatibility adapter while retaining string public input | Keep deep-link compatibility; do not expand scope if no clear benefit |
| Existing harnesses only | Characterization/equivalence fixtures | No new project/framework is justified |

No change is proposed to `VpnService`, `VpnDeviceEditorProjection`, VPN Views/ViewModels/gateways, RouterManager, GLInet session/SSH, AdGuard transport, `ClientProfile` serialization, navigation, or UI layout.

## Consumer Migration Boundary

| Consumer | Slice decision | Rationale |
|---|---|---|
| `ClientInventoryState` / coordinator | MIGRATE | They are the additive typed inventory boundary and shared read-side source. |
| `KnownDevicesViewModel` / `KnownDeviceInfo` | MIGRATE | Read-only, identity-heavy, profile-aware, and already covered by deterministic fixtures. |
| `ClientDetailsNavigationPreparation` | COMPATIBILITY ADAPTER ONLY | Keep string deep-link contract and cold/warm/profile fallback behaviour. |
| `ClientsViewModel` | LEAVE UNCHANGED | Remains rich authoritative producer with much broader UI/profile lifecycle. |
| VPN Assigned Devices | LEAVE UNCHANGED | Frozen consumer and regression oracle. |
| Traffic, DNS/Protection, Client Details, Global Search, notifications, dashboard/DHCP/port-forward projections | DEFER | Each owns feature-specific correlation or name/presentation policy. |

## Test-First Plan

### Existing deterministic protection

- `RouterPilot.ClientNavigationHarness` covers DHCP/AdGuard configured names, configured-name precedence, resolver safety, cold/warm deep links, profile-only offline navigation, duplicate-name resolution, and concurrent shared reconciliation.
- `RouterPilot.NetworkHealthHarness` covers endpoint normalization, known/offline projection, generated-IP-name rejection, numeric nickname preservation, and same-MAC IP change.
- `RouterPilot.DataStatisticsHarness` covers baseline MAC normalization.
- `RouterPilot.VpnMutationHarness` covers MAC equivalence, unknown assignment projection/preservation, editor presence, zero-effective-assignment safety, and routing-device state.

### Tests required before production migration

Add deterministic fixtures to the existing client-navigation harness unless a current harness owns a more exact seam:

1. `DeviceIdentity` accepts the exact current valid MAC variants and emits the exact existing canonical key.
2. Invalid/non-hex/wrong-length input fails identity creation and cannot become a typed device identity.
3. Typed snapshot and legacy `ClientInventoryState.Snapshot` contain identical accepted identity keys after update, including separator/case duplicates.
4. Duplicate input preserves current first-record reconciliation; no field merge is invented.
5. Existing profile keys resolve through typed identity/profile adapter unchanged.
6. Known Devices current/online, profile-only/offline, generated-IP rejection, numeric nickname, and IP-change projection remain equivalent.
7. Explicit presence projects Online/Offline; missing explicit presence remains Unknown.
8. Optional AdGuard enrichment failure preserves primary router observation.
9. A delayed coordinator read begun in context A cannot publish after context switches to B.
10. Legacy `ClientDetailsNavigationPreparation` remains equivalent for case/separator variants and live/profile fallback.

### Known gap

The coordinator’s existing test constructor accepts only a reconciliation delegate and does not model `IActiveRouterContext`. Slice 01 must add a deterministic context-stamp seam before changing stale publication behaviour. Current reset/cancellation is not a substitute for this proof.

## VPN Regression Gate

After every shared-inventory phase, run `RouterPilot.VpnMutationHarness`. It must prove unchanged:

- Assigned Devices inventory/editor population;
- known online/offline projection;
- unresolved authoritative assignment preservation and safe `Unknown device N` labels;
- local selection and zero-effective-assignment guard;
- set-tunnel payload/call-count behaviour; and
- PIA, Make Primary, Connect/Disconnect lifecycle.

No VPN source modification is expected. If implementation requires any VPN source, fake-gateway, mutation payload, routing, PIA, or UI change, stop for human review.

## Implementation Phases

### Phase 1 - Characterization tests

- **Likely files:** `RouterPilot.ClientNavigationHarness/Program.cs`; possibly existing Network Health/Data Statistics harness fixtures.
- **Behaviour:** codify strict identity, compatibility-map equivalence, profile lookup, and stale-context expectations before consumer migration.
- **Stop:** observed name/identity behaviour differs across consumers and cannot be preserved without a new product decision.
- **Untouched:** production source, VPN, persistence, UI.

### Phase 2 - Minimum typed identity/observation boundary

- **Likely files:** new device model file(s), `ClientInventoryState`, `ClientInventoryCoordinator`, optional narrow profile lookup adapter.
- **Behaviour:** typed MAC identity and immutable context-stamped snapshot, with legacy maps unchanged.
- **Tests:** normalization, invalid identity, duplicate reconciliation, snapshot/map equivalence, optional-enrichment resilience.
- **Stop:** any need to turn IP/hostname-only input into MAC identity, change profile keys, or change `ClientInfo` display/persistence semantics.

### Phase 3 - Context-safe shared publication

- **Likely files:** `ClientInventoryCoordinator`, `ClientInventoryState`, context test seam.
- **Behaviour:** capture active router context/version before reads; reject stale completion; publish only current-context snapshot.
- **Tests:** delayed A-to-B switch, cancellation independent of stale rejection, reset compatibility, concurrent-load coalescing.
- **Stop:** implementation requires RouterManager refactor, host-string inference, or switch-order change that cannot be characterized.

### Phase 4 - Known Devices migration

- **Likely files:** `KnownDevicesViewModel`, `KnownDeviceInfo`, optional internal deep-link adapter.
- **Behaviour:** consume typed observation/profile input with no changed visible name, filter, order, selection, or offline behaviour.
- **Tests:** existing Known Devices/deep-link fixtures plus new typed-boundary equivalence fixtures.
- **Stop:** changes name precedence, profile edit flow, Clients page behaviour, or requires traffic/DNS/VPN migration.

### Phase 5 - Equivalence/regression validation

- **Behaviour:** compare typed snapshot and legacy projections; review final source diff for unintended scope.
- **Automated:** all selected harnesses, Debug/Release build, `git diff --check`.
- **Manual:** non-mutating visual check of online, known-offline, unknown-name, configured-name, and router-switch presentations. Manual inspection does not replace deterministic stale/VPN proof.

## Build and Regression Gates

After implementation, run at minimum:

```text
dotnet run --project RouterPilot.ClientNavigationHarness -c Debug
dotnet run --project RouterPilot.NetworkHealthHarness -c Debug
dotnet run --project RouterPilot.DataStatisticsHarness -c Debug
dotnet run --project RouterPilot.VpnMutationHarness -c Debug
dotnet build RouterPilot.sln -c Debug
dotnet build RouterPilot.sln -c Release
git diff --check
```

Use the repository’s established harness options if a project requires a more specific target. Review the final diff to confirm no RouterManager/RPC/SSH/AdGuard transport change, no VPN source/harness lifecycle change, no `ClientProfile` format change, no router payload change, and no intended user-visible behaviour change.

## Stop Conditions

Stop and request human review if Slice 01 requires:

- router RPC, SSH, AdGuard transport, or router payload changes;
- any VPN mutation/routing/unknown-preservation/fake-gateway/PIA/UI change;
- incompatible profile persistence change or implicit migration;
- destructive normalization, guessed identity equivalence, or IP/hostname-to-MAC conversion;
- a friendly-name precedence change;
- broad RouterManager, transport, Clients ViewModel, dashboard, or UI refactor;
- weaker router-context stale-result protection;
- trust/credential/diagnostic sanitization changes; or
- raw identity exposure in normal UI.

Repository-specific stop: if context-safe inventory publication cannot be added without changing `RouterSwitchCoordinator` ordering or router-manager lifecycle semantics, limit the slice to characterization plus typed identity and obtain approval before proceeding.

## Success Criteria

Slice 01 is successful only if:

- a narrow typed MAC identity boundary exists;
- a context-stamped observation snapshot coexists with unchanged legacy inventory views;
- current normalization, duplicate reconciliation, profile/name behaviour, online/offline/unknown distinctions, and unknown safety are preserved;
- Known Devices uses the boundary without visible behavioural change;
- old/new consumers coexist safely;
- stale prior-context inventory publication is deterministically rejected;
- relevant harnesses and the frozen VPN harness pass;
- router contracts/payloads and persistence format remain unchanged;
- Debug/Release builds pass; and
- the final diff remains narrow.

## Recommended First Implementation Step

First add characterization fixtures to `RouterPilot.ClientNavigationHarness` for a proposed strict `DeviceIdentity` factory and typed-snapshot-to-legacy-map equivalence. In the same test-first change, introduce a controllable active-router context seam proving that a delayed shared inventory refresh cannot publish after router switching. Do not migrate production consumers until those tests define the contract.
