# RouterPilot 3.0 Slice 03 — Network Health Data Statistics Typed-Fact Projection

## Objective

Migrate only the existing Network Health Data Statistics check from presentation-level Data Statistics state to the existing, context-stamped `DataStatisticsCapabilityReadFact`. This is a read-side consumer migration and the first intentionally visible Slice 03 improvement.

The slice must add no router read, capability probe, mutation, cache, global capability registry, or broad Network Health redesign.

## Current Problem

`DataStatisticsService.ReadAsync` already performs one classification and returns both a `DataStatisticsCapabilityReadFact` and compatible `DataStatisticsReadResult`. The fact has support, operating state, read availability, compatibility payloads, and the active router profile/version stamp.

`DataStatisticsViewModel` uses that fact for Analytics presentation. However, `NetworkHealthViewModel` passes only `HasLoaded`, `RouterPilotStatus`, and display detail into `NetworkHealthViewProjection`. Its current Data Statistics check maps Active to Available, Disabled to Disabled, and Pending/all other states to generic Unavailable. It cannot distinguish DPI inactive, unsupported, temporary failure, or unknown state.

## Current Data Flow

```text
normal read-only router calls
  -> IDataStatisticsReader / RouterManagerDataStatisticsReader
  -> DataStatisticsService.ReadAsync
  -> DataStatisticsReadResult + DataStatisticsCapabilityReadFact
  -> DataStatisticsViewModel current-context acceptance
  -> HasLoaded + RouterPilotStatus + StatusDetail
  -> NetworkHealthViewModel.Rebuild
  -> NetworkHealthViewProjection.DataStatistics
  -> visible Network Health Data Statistics row
```

Typed meaning is discarded between the Data Statistics and Network Health ViewModels.

## Target Data Flow

```text
normal read-only router calls
  -> DataStatisticsService, the sole classifier
  -> context-stamped DataStatisticsCapabilityReadFact
  -> DataStatisticsViewModel current-context acceptance and exposure
  -> NetworkHealthViewModel with existing lazy-load state
  -> NetworkHealthViewProjection.DataStatistics typed projection
  -> factual Network Health Data Statistics row
```

`NetworkHealthService`, its timeline/notification behavior, Dashboard score projection, and refresh ownership remain unchanged.

## Fact Exposure Contract

The smallest proposed addition is an observable read-only `CurrentCapabilityFact` property on `DataStatisticsViewModel` (final name to follow local conventions).

- It is null before an accepted normal Data Statistics result.
- The existing profile/version/fact-stamp guard stays the authoritative acceptance gate. Only an accepted result assigns and notifies the property.
- The property is the exact service-produced fact. The ViewModel does not recreate, map, enrich, or classify it.
- `ResetForRouterSession` clears it before returning the existing pending/reset presentation.
- A stale result rejected by the existing guard must not expose, clear, or replace it.
- This is accepted ViewModel-local state, not a global cache or state service.

`HasLoaded` remains the minimum lazy-load signal. A null fact before load is not evidence of unsupported capability.

## Network Health Input Contract

Change only the Data Statistics fields in `NetworkHealthViewInput`:

```text
Before: DataStatisticsLoaded, DataStatisticsStatus, DataStatisticsDetail
After:  DataStatisticsLoaded, DataStatisticsCapabilityReadFact?
```

`NetworkHealthViewModel.Rebuild` supplies `_dataStatistics.HasLoaded` and `_dataStatistics.CurrentCapabilityFact`. The projection pattern-matches typed facts only for the Data Statistics row. It does not inspect RPC errors, raw payloads, legacy availability, or unrelated health inputs.

## Semantic Projection Matrix

The title remains `Data Statistics`; the navigation target remains `analytics`; the row remains non-scoring.

| Case | Check status / severity | Detail | Wording disposition |
| --- | --- | --- | --- |
| Not loaded: `HasLoaded == false`, fact null | `Not loaded` / NotAvailable | `Open Analytics to load its existing Data Statistics state.` | Preserve current behavior and wording. |
| Supported + EnabledAndDpiActive + Available | `Available` / Active | `Application traffic classified by the router's DPI engine.` | Preserve current active meaning. |
| Supported + Disabled + NotReadBecauseDisabled | `Disabled` / Disabled | `Data Statistics is disabled on the router.` | Preserve current wording; configuration is not a health fault. |
| Supported + DpiInactive + NotReadBecauseDpiInactive | `DPI inactive` / Pending | `The router's DPI engine is not currently active.` | Intentional replacement for generic Unavailable; retain established explanation. |
| Unsupported | `Unsupported` / NotAvailable | `This router does not expose the required Data Statistics read interface.` | Intentional replacement for Disabled/Unavailable; retain established explanation. |
| Unknown + TemporarilyUnavailable | `Temporarily unavailable` / NotAvailable | `RouterPilot could not read Data Statistics. Try Refresh again.` | Intentional replacement for generic Unavailable; never claims unsupported or router failure. |
| Null after loaded, Unknown, incomplete, or impossible tuple | `Unknown` / NotAvailable | `Data Statistics capability or current read state has not been established.` | Intentional conservative fallback; never claims Active, Disabled, Unsupported, or router failure. |

Phase 1 must characterize current exact row state, severity, text, lazy behavior, and aggregate effect for existing legacy states. That is the record separating preserved wording from intentional visible changes.

## Context and Reset Contract

Slice 03 reuses Slice 02 protection:

1. Data Statistics captures profile ID/version before the read.
2. The service stamps its fact with that context.
3. `DataStatisticsViewModel` compares current profile/version and stamp before accepting/publishing.
4. `RouterSwitchCoordinator` already invalidates context and calls `ResetForRouterSession`; reset must clear the exposed fact.
5. Network Health reads only the accepted exposed fact and existing `HasLoaded` state.

Required deterministic A -> B proof:

1. Hold an A refresh through `FakeDataStatisticsReader`.
2. Switch to B and invoke the normal reset path.
3. Assert Network Health sees B/reset/not-loaded, never an A fact.
4. Release A. It may finish service work but cannot set loaded state, expose its fact, alter B/reset presentation, add traffic/collections, or trigger stale follow-up publication.
5. Refresh B and assert a B-stamped fact projects normally.

No independent context mechanism, RouterSwitchCoordinator ordering change, or global event system is permitted.

## Lazy-Load and Score Contracts

Analytics remains lazy. Network Health must not call `EnsureLoadedAsync`, `RefreshAsync`, `DataStatisticsService`, a reader, or a capability probe. Before Analytics loads, the row remains `Not loaded`.

No aggregate or score change is intended:

- Data Statistics is currently created with `AffectsOverall = false`.
- `DashboardHealthProjection` has no Data Statistics input or deduction.
- `NetworkHealthService.Evaluate` receives no Data Statistics input.

The migration must preserve these facts; Dashboard score, aggregate state, attention reasons, timeline, and notifications must not change.

## Deterministic Proof Plan

### Phase 1 — characterize current behavior

Harness-only work. In `RouterPilot.NetworkHealthHarness`, characterize current not-loaded and each legacy Data Statistics row: title, status, severity, detail, navigation target, `AffectsOverall`, overall health snapshot, and Dashboard score. Extend `RouterPilot.DataStatisticsHarness` only if a missing typed/legacy source mapping must be recorded. No production behavior changes.

### Phase 2 — expose accepted fact

Use the existing Data Statistics harness, `FakeDataStatisticsReader`, `HarnessActiveRouterContext`, actual service, and actual ViewModel to prove:

- null before accepted load and after reset;
- an accepted result exposes the same fact/payload that drove Analytics;
- a delayed A fact remains A-stamped after switching;
- held A completion cannot expose A after B reset; and
- B refresh exposes a B-stamped fact.

Retain existing presentation, traffic, full-table/detail follow-up, read-only seam, and stale rejection assertions.

### Phase 3 — typed health projection

Change only the Data Statistics input/projection. In the Network Health harness assert every matrix row, especially distinct disabled, DPI inactive, unsupported, temporary, and unknown states. Assert all rows remain non-scoring.

Where existing dispatcher infrastructure permits, add an actual `DataStatisticsViewModel -> NetworkHealthViewModel -> NetworkHealthViewProjection` fixture. Do not add a transport abstraction or second router seam merely for this test.

### Phase 4 — closure

Run the Data Statistics, Network Health, Client Navigation, and VPN deterministic harnesses; build the Debug solution and Release RouterPilot project; then run `git diff --check`. Abort/skip VPN's opt-in live mutation validation.

## Read-Only Safety

The implementation must prove no new call to `IDataStatisticsReader`, `DataStatisticsService.ReadAsync` from Network Health, capability probe, `SetApplicationContentProtectionAsync`, VPN operation, or router configuration write. The existing fake-reader ordered call assertions and production-adapter no-mutation source assertion remain required.

## Implementation Phases

### Phase 1 — characterize existing Network Health Data Statistics behavior

Harness changes only; capture the current row and score/lazy contract before a visible wording change.

### Phase 2 — expose accepted typed fact

Expected production scope: `DataStatisticsViewModel.cs` only. Add the smallest observable read-only property, clear it during existing reset, and prove A -> B behavior.

### Phase 3 — migrate one Network Health check

Expected production scope: `NetworkHealthViewModel.cs` and `NetworkHealthViewProjection.cs` only. Replace only the Data Statistics input/projection with the approved typed matrix.

### Phase 4 — equivalence and regression closure

No default production edits. Execute the complete typed matrix, stale/reset and read-only proofs, harness regressions, builds, and focused diff review.

Phases 2 and 3 may combine only if Phase 1 shows no separate exposure risk and the same deterministic fixture proves reset/publication. Do not combine merely to reduce commits.

## Expected File Scope

Expected production files:

- `RouterPilot/ViewModels/DataStatisticsViewModel.cs`
- `RouterPilot/ViewModels/NetworkHealthViewModel.cs`
- `RouterPilot/Presentation/NetworkHealthViewProjection.cs`

Expected test files:

- `RouterPilot.DataStatisticsHarness/Program.cs`
- `RouterPilot.NetworkHealthHarness/Program.cs`

This plan is the only changed file in this planning phase. Stop implementation rather than automatically expand scope if `NetworkHealthService`, Dashboard, DashboardWindow, RouterSwitchCoordinator, RouterManager, XAML, persistence, VPN, or an unrelated model would need to change.

## Compatibility Requirements

- Preserve Slice 02 classification, typed/legacy mapping, reader order, payloads, parser, and optional traffic behavior.
- Preserve Analytics presentation, lazy loading, full-table/detail behavior, traffic history, command readiness, and reset.
- Preserve all other Network Health checks, Dashboard score, and `NetworkHealthService` timeline/notification behavior.
- Preserve router profiles, settings, persistence, navigation, UI layout, trust, and VPN behavior.

## Non-Goals

- Broad Network Health, Dashboard, or score redesign.
- `NetworkHealthService` timeline/notification migration.
- XAML/layout/navigation redesign.
- A global capability registry or generic result framework.
- New router reads; RouterManager, transport, RPC, payload, or parser changes.
- AdGuard, Wi-Fi, WAN, device-domain, or VPN migration.
- Persistence/settings changes.
- Any mutation, including application protection or VPN changes.

## Stop Conditions

Stop for human review if implementation needs a second Data Statistics read/probe, raw RPC interpretation in Network Health, RouterSwitchCoordinator ordering/provider lifecycle change, score/timeline redesign, a false unsupported conclusion from unknown/temporary evidence, a Slice 02 contract change, or UI/persistence/AdGuard/Wi-Fi/WAN/device/VPN migration.

## Definition of Complete

Slice 03 is complete only when Network Health consumes the accepted context-stamped typed fact; the full matrix is proven; Analytics remains lazy; A cannot publish into B after reset; Data Statistics remains non-scoring; unrelated contracts are unchanged; and the regression/build gates pass.

## Visible 3.0 Outcome

The Network Health row becomes a truthful explanation of active data, intentional disablement, inactive DPI, unsupported capability, temporary read unavailability, or unresolved state. It does not treat disabled configuration as unhealthy, temporary uncertainty as permanent unsupported capability, or lazy/unread data as a router fault.
