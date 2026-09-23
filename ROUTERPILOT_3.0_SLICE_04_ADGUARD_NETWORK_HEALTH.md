# RouterPilot 3.0 Slice 04 — AdGuard Home Availability-State Network Health Projection

## 1. Objective

Migrate only the existing Network Health `DNS / AdGuard` row from a broad available/unavailable interpretation to a pure projection of the already-authoritative `AdGuardAvailabilityState`, existing AdGuard freshness, and confirmed protection state.

The visible outcome is a truthful explanation of whether AdGuard Home is optional, still loading, stale, available with a particular filtering state, not configured, authentication-failed, or unavailable. This is a read-side presentation migration, not an AdGuard subsystem redesign.

## 2. Current Behavior

`AdGuardAvailabilityState` is a scoped enum with exactly these values:

- `Available`
- `Unavailable`
- `NotConfigured`
- `AuthenticationFailed`

The current Network Health row has title `DNS / AdGuard`, navigation target `protection`, and the following broad projection:

- If AdGuard is excluded from Router Health: `Checking` while freshness is Loading; otherwise `Not in use`; both are non-scoring.
- If included: Loading becomes `Loading` / Pending; Stale becomes `Stale` / Pending.
- Any availability other than `Available` becomes `Unavailable` / Error with `AdGuard Home is configured for Router Health but is currently unavailable.`
- Available plus unknown protection becomes `Protection state unavailable` / Pending.
- Available plus paused, protected, or disabled becomes `Paused` / Pending, `Protected` / Active, or `Disabled` / Disabled respectively.

Consequently, `NotConfigured`, `AuthenticationFailed`, and `Unavailable` currently share the same included-row text, status, and Error severity. The established Phase 3 Network Health harness verifies the generic unavailable and optional cases, the protection states, navigation, and aggregate behavior.

## 3. Authoritative Source of Truth

The availability source is `DashboardWindow.RefreshAdGuardAsync`:

1. It reads the existing AdGuard status and, after router-session and resume-generation checks, records Freshness success and running/version/process data.
2. A confirmed running service then executes the existing statistics, rankings, and protection reads. All succeeding produces `Available`; a failure is classified as `AuthenticationFailed` or `Unavailable` by the existing refresh owner.
3. A confirmed stopped service produces `Unavailable`.
4. `MarkAdGuardUnavailable` publishes the same state to `DashboardViewModel.AdGuardAvailability` and `AdGuardAvailabilityService`, clears stale statistics, and marks the service as not running.

`NotConfigured` is an existing domain state used by configuration/recovery policy. Slice 04 must consume it if it reaches Network Health; it must not manufacture it from failed transport.

Availability is distinct from confirmed protection inputs already in `NetworkHealthViewInput`:

- `AdGuardProtectionKnown`
- `AdGuardProtected`
- `AdGuardPaused`

Protection is only meaningful when availability is `Available`. A reachable instance with disabled filtering is not unavailable.

## 4. Current Data Flow

```text
existing AdGuard status/API refresh and failure classification
  -> DashboardViewModel.AdGuardAvailability and confirmed protection fields
  -> NetworkHealthViewModel.Rebuild
  -> NetworkHealthViewInput
       (AdGuardFreshness, IncludeAdGuardHomeInRouterHealth,
        AdGuardAvailability, AdGuardProtectionKnown,
        AdGuardProtected, AdGuardPaused)
  -> NetworkHealthViewProjection.AdGuard
  -> Network Health DNS / AdGuard row
```

The meaning is currently discarded inside `NetworkHealthViewProjection.AdGuard` by its `AdGuardAvailability != Available` catch-all.

## 5. Target Data Flow

```text
same existing authoritative AdGuard refresh and classification
  -> same existing NetworkHealthViewInput fields
  -> pure, ordered AdGuard projection of freshness, availability,
     and confirmed protection state
  -> factual Network Health DNS / AdGuard row
```

No new fact, cache, service, probe, HTTP request, router read, or mutation is introduced. `AdGuardAvailabilityState` remains the authority for availability meaning; Network Health must not inspect HTTP errors, exception messages, raw payloads, or display strings.

## 6. Semantic Projection Matrix

All rows retain title `DNS / AdGuard` and navigation target `protection`.

| Condition, in precedence order | Current row | Proposed row | Disposition | Affects overall / aggregate effect |
| --- | --- | --- | --- | --- |
| Excluded; freshness Loading | `Checking` / Pending; `AdGuard Home is optional and is excluded from the overall health score.` | Same | PRESERVED | false; overall remains initializing under the existing special `Checking` rule |
| Excluded; not Loading | `Not in use` / NotAvailable; `AdGuard Home is optional and is not included in the overall health score.` | Same | PRESERVED | false; no aggregate effect |
| Included; freshness Loading | `Loading` / Pending; `Waiting for AdGuard status.` | Same | PRESERVED | true; existing overall initialization behavior |
| Included; freshness Stale | `Stale` / Pending; `AdGuard status has not refreshed.` | Same | PRESERVED | true; existing attention behavior |
| Included; Available + protection known + protected | `Protected` / Active; `AdGuard: Running · Filtering: Protected` | Same | PRESERVED | true; no attention from this row |
| Included; Available + protection known + paused | `Paused` / Pending; `AdGuard: Running · Filtering: Paused` | Same | PRESERVED | true; existing attention behavior |
| Included; Available + protection known + disabled | `Disabled` / Disabled; `AdGuard: Running · Filtering: Disabled` | Same | PRESERVED | true; existing attention behavior |
| Included; Available + protection not known | `Protection state unavailable` / Pending; `AdGuard Home is running; protection state is not yet available.` | Same | PRESERVED | true; existing attention behavior |
| Included; NotConfigured | `Unavailable` / Error; generic configured/unavailable detail | `Not configured` / NotAvailable; `AdGuard Home is not configured. Router monitoring remains active.` | INTENTIONAL VISIBLE CHANGE; the enum explicitly distinguishes configuration from failed reachability | true; remains attention, preserving aggregate outcome |
| Included; AuthenticationFailed | `Unavailable` / Error; generic configured/unavailable detail | `Authentication failed` / Error; `AdGuard Home authentication failed. Check the configured credentials.` | INTENTIONAL VISIBLE CHANGE; existing classification has established an authentication failure | true; remains attention |
| Included; Unavailable | `Unavailable` / Error; `AdGuard Home is configured for Router Health but is currently unavailable.` | Same | PRESERVED; current enum covers stopped service and unavailable refresh outcomes, so the row must not claim a narrower cause | true; remains attention |
| Included; unrecognised enum value or contradictory future input | currently falls into generic unavailable | `Unknown` / NotAvailable; `AdGuard Home availability or protection state has not been established.` | INTENTIONAL conservative fallback, only for a value not represented by the current enum or a future impossible input | true; remains attention |

The current enum has no `Unsupported` or `Unknown` member. The fallback must not invent either conclusion. Existing `Available` plus `AdGuardProtectionKnown == false` is the supported not-established protection case and retains its established wording.

## 7. Freshness and Loading Precedence

Projection order is authoritative and must be tested in this order:

1. Optional/excluded behavior wins, preserving its existing Loading/Checking distinction and non-scoring contract.
2. For included AdGuard, Loading wins over availability and protection values.
3. Stale wins over availability and protection values; a stale observation must not look current.
4. The explicit availability state is projected: `NotConfigured`, `AuthenticationFailed`, then `Unavailable`.
5. Only `Available` reaches the confirmed protection-state projection.
6. An unrecognised future value or contradictory tuple uses the conservative fallback.

`DataFreshnessState` has exactly `Loading`, `Fresh`, `Stale`, and `Unavailable`. The current AdGuard projection has explicit Loading and Stale handling; Phase 1 must characterize the included `Unavailable` freshness interaction before implementation. It must not silently present it as Fresh or override explicit availability meaning without evidence.

## 8. Scoring Contract

When excluded, the row has `AffectsOverall = false`; `Checking` also triggers the existing initialization rule. When included, the row currently affects overall Network Health. Its generic unavailable Error makes the aggregate `Attention needed`; the proposed NotConfigured / NotAvailable state also remains an attention condition under the existing aggregate rule.

Slice 04 does not alter the aggregate algorithm, `AffectsOverall` ownership, Dashboard score, Dashboard health projection, attention-reason architecture, `NetworkHealthService`, its timeline, or notifications. The existing harness establishes that Dashboard score and `NetworkHealthService` are structurally independent of Data Statistics; Phase 1 must separately record the current AdGuard row/aggregate and Dashboard-score boundaries before asserting the same for Slice 04.

## 9. Context and Reset Contract

`DashboardWindow.RefreshAdGuardAsync` captures router session and resume generation, checks both before publishing refresh results, and treats an old-session cancellation as non-publication. `RouterSwitchCoordinator` invalidates the active session and sets `AdGuardAvailabilityService` to `Unavailable` before router reset/profile switch. `DashboardWindow` also has its existing router-switch reset/recovery path.

Slice 04 introduces no context mechanism. Its projection consumes the current existing inputs only. Characterization must prove:

1. router A begins a delayed AdGuard refresh;
2. the active context switches/resets to B;
3. B's reset row reflects current reset inputs, not A's availability or protection state;
4. releasing A cannot republish availability, protection, or freshness into B; and
5. a later accepted B refresh projects normally.

If current deterministic seams cannot demonstrate this without changing refresh ownership, `RouterSwitchCoordinator`, DashboardWindow, or transport code, Slice 04 must stop for review rather than broaden scope.

## 10. Read-Only Contract

Network Health remains an observer. The projection and its ViewModel must add no:

- AdGuard HTTP request or credential operation;
- router read, capability probe, or refresh trigger;
- mutation, restart, schedule, updater, or maintenance operation;
- persistence or configuration write.

The normal existing Dashboard refresh remains the sole producer of AdGuard availability and protection data.

## 11. Implementation Phases

### Phase 1 — Characterize current AdGuard Network Health behavior

Harness-only. Extend `RouterPilot.NetworkHealthHarness` to record the exact current matrix: title, status, severity, detail, navigation, `AffectsOverall`, aggregate snapshot, optional behavior, Loading/Stale precedence, and Dashboard-score/`NetworkHealthService` boundaries. Add a context/reset fixture only if it can reuse existing deterministic seams without production change.

### Phase 2 — Migrate the pure AdGuard projection

Change `NetworkHealthViewProjection.AdGuard` only, unless Phase 1 proves the existing `NetworkHealthViewInput` omits an authoritative existing field. Replace the broad availability catch-all with ordered matching from this matrix. Do not change availability classification, Dashboard refresh, services, transports, or UI.

### Phase 3 — Closure

Run the full semantic matrix and all regression/build gates. Confirm preserved states retain exact current wording; intentional changes are limited to NotConfigured, AuthenticationFailed, and the conservative future fallback. Perform a manual visible review of the existing Network Health row without redesigning its layout.

## 12. Expected File Scope

Expected characterization/test file:

- `RouterPilot.NetworkHealthHarness/Program.cs`

Expected production file:

- `RouterPilot/Presentation/NetworkHealthViewProjection.cs`

`RouterPilot/ViewModels/NetworkHealthViewModel.cs` may change only if Phase 1 proves an existing authoritative input is not already supplied. Any need to change DashboardWindow, RouterSwitchCoordinator, a service, transport, persistence, XAML, or another production area is a stop condition.

## 13. Deterministic Test Strategy

`RouterPilot.NetworkHealthHarness` must assert every row in the matrix, including exact title, status, severity, detail, navigation target, `HasNavigationTarget`, `AffectsOverall`, and aggregate snapshot. It must explicitly prove precedence for excluded/Loading/Stale inputs and that available protection states stay distinct from availability failures.

The harness must retain structural assertions that Network Health owns no refresh/service read and add equivalent no-AdGuard-read/probe/mutation assertions for the migrated row. It must characterize the Dashboard score and `NetworkHealthService` boundaries without changing either system.

Reuse the existing context-safe refresh test infrastructure where practical. Retain regression runs for Network Health, Data Statistics, Client Navigation, and VPN deterministic harnesses; Debug solution build; Release RouterPilot build; and `git diff --check`. VPN live mutation validation must be aborted/skipped.

## 14. Reuse of Existing 3.0 Foundations

- Slice 03: pure `NetworkHealthViewProjection`, explicit ordered semantic matrix, non-I/O structural proof, and deterministic presentation harness pattern.
- Existing AdGuard domain: `AdGuardAvailabilityState` is the scoped availability boundary; confirmed protection flags remain a separate authoritative dimension.
- Existing freshness/context infrastructure: `DataFreshnessService`, router session, resume generation, and router-session reset stay authoritative.

No global capability registry, generic result framework, global health store, or new state-management abstraction is introduced.

## 15. Risks

- `Unavailable` currently has more than one producer (stopped service and failed refresh), so its wording must remain broad rather than claim a temporary transport-only condition.
- `AuthenticationFailed` is presently classified in the Dashboard refresh owner. Network Health must consume the enum only and never duplicate that exception interpretation.
- `NotConfigured` must be tested as an existing state injection even if the ordinary successful refresh path does not currently produce it in the same way as other values.
- Existing optional/Checking behavior has a special aggregate initialization rule and must not be accidentally reordered.

## 16. Stop Conditions

Stop rather than expand scope if implementation requires a new AdGuard/router read, capability probe, HTTP/RPC contract change, refresh-ownership change, RouterSwitchCoordinator redesign, Dashboard or scoring redesign, `NetworkHealthService` redesign, mutation, persistence, XAML/layout/navigation change, VPN change, or broader production scope than Phase 1 justifies.

## 17. Explicit Non-Goals

- VPN work.
- AdGuard mutation, maintenance, updater, scheduling, credential, or transport-security work.
- New AdGuard API requests or RouterManager/RPC/HTTP contract changes.
- DNS Activity migration.
- Dashboard redesign or scoring redesign.
- XAML/layout/navigation redesign.
- Wi-Fi, WAN/Multi-WAN, client/device, or persistence migration.

## 18. Definition of Complete

Slice 04 is complete when the existing Network Health AdGuard row consumes its authoritative availability, freshness, and confirmed protection inputs with the ordered matrix above; NotConfigured, AuthenticationFailed, and Unavailable are no longer conflated; preserved states remain exact; the row remains read-only; context/reset safety and aggregate contracts are proven; all deterministic regressions/builds pass; and no unrelated production surface has changed.
