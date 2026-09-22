# RouterPilot 3.0 Slice 02 — Data Statistics Capability Semantics Foundation

## 1. Purpose

Establish a small, explicit, read-only and router-context-scoped semantic boundary for Data Statistics. The boundary will distinguish:

- whether the connected router exposes the required Data Statistics capability;
- the observed operating/configuration state of that capability; and
- whether the current read produced usable data or is temporarily unavailable.

The slice will preserve the existing Data Statistics presentation and router contracts. It will add the boundary alongside the existing `DataStatisticsReadResult` / `DataStatisticsAvailability` compatibility surface, then migrate only `DataStatisticsViewModel` to consume it.

This is a capability-semantics proving ground, not an application-wide capability system.

## 2. Why This Slice Is Next

Slice 01 proved a useful migration pattern: characterize current behaviour, add a narrow typed boundary beside compatibility state, make publication context-safe, migrate one read-side consumer, and retain old APIs for all other consumers.

Data Statistics is the next suitable boundary because it is read-only on its normal refresh path, has an existing deterministic parser/traffic harness, and has a concrete semantic problem today. `DataStatisticsAvailability` currently represents several different facts in one value:

- `Unsupported` means the required router interface is not established as available;
- `Disabled` means the router reported flow statistics disabled;
- `DpiInactive` means the router reported flow statistics but its DPI engine is inactive;
- `TemporarilyUnavailable` means a Data Statistics RPC read failed without proving unsupported;
- `Available` means the status conditions allowed a successful top-applications read.

Those facts are useful, but they are not interchangeable. Making them explicit before Network Health consumes them prevents a future health projection from treating a disabled or transiently unreadable feature as unsupported.

## 3. Current Repository Behaviour

### Read and classification flow

The current normal flow is:

`DataStatisticsViewModel.RefreshAsync`
→ `DataStatisticsService.ReadAsync`
→ `IRouterManagerProvider.GetRouterManagerAsync`
→ `RouterManager.GetNetworkTrafficSnapshotAsync` (optional)
→ `RouterManager.GetDataStatisticsStatusAsync`
→ optionally `RouterManager.GetTopAppFlowStatisticsAsync`
→ `DataStatisticsReadResult`
→ `DataStatisticsViewModel.Apply` presentation.

Concrete current behaviour:

- `RouterManager.DataStatistics.cs` uses the established session machinery and the existing router methods `system.get_status`, `flow_statistics.get_top_app_flow_statistics`, `flow_statistics.get_flow_statistics`, and `flow_statistics.get_app_flow_statistics`. Parsing remains in `DataStatisticsParser`.
- `DataStatisticsService.ReadAsync` treats traffic-counter failure as optional: DPI statistics can still be useful without a `NetworkTrafficSnapshot`.
- A status with no `FlowStatisticsEnabled` value becomes legacy `Unsupported`.
- A status with `FlowStatisticsEnabled == false` becomes legacy `Disabled`.
- A status with flow statistics enabled but `DpiStatus != "1"` becomes legacy `DpiInactive`.
- An active status proceeds to the top-applications read and returns legacy `Available` when that read succeeds.
- `DataStatisticsRpcException` with `-32601` becomes legacy `Unsupported`; another `DataStatisticsRpcException` becomes legacy `TemporarilyUnavailable`.
- `DataStatisticsViewModel.Apply` maps those legacy values to its existing titles, details, status colour/state, empty collections, and period text. These user-visible mappings are the compatibility oracle.

### Current ownership and router switching

`DataStatisticsService` is a singleton read service registered in `App.xaml.cs`; it has no cache and does not currently receive `IActiveRouterContext`. `DataStatisticsViewModel` owns the visible presentation, traffic-session accumulator, loaded state, and refresh gate.

`DataStatisticsViewModel.RefreshAsync` captures `_activeRouter.Version` before awaiting `ReadAsync` and returns without applying the result if the version changed. This already prevents the main read result from being applied after an observed version change, but the returned result itself is not context-stamped and the current harness does not characterize every capability-state and ViewModel-context case.

`RouterSwitchCoordinator` invalidates `IActiveRouterContext`, clears the client inventory, calls `DataStatisticsViewModel.ResetForRouterSession`, resets the manager provider, then changes profile where requested. `ResetForRouterSession` clears visible Data Statistics state and traffic history. The slice must retain this order and behaviour.

### Existing downstream consumers

`DataStatisticsViewModel` is the only consumer to migrate in this slice. It remains the source used by the existing Data Statistics UI. Dashboard/Network Health projections, other analytics, application details, and application protection are not migration targets.

`RouterPilot.DataStatisticsHarness` currently proves parser tolerance, top/full/detail statistics parsing, period alignment, application-protection verification helper behaviour, and traffic-session accumulation. It does not currently prove the service's full support/configuration/transient classification matrix or a delayed router-context result at the ViewModel boundary. Existing `NetworkHealthHarness` must be inspected/reused only where its current fixtures actually consume Data Statistics semantics; it is not evidence by itself that those cases are covered.

## 4. Current Semantic Problem

The current legacy enum is a convenient presentation discriminator, but it mixes three dimensions:

| Current legacy value | Capability/support fact | Configuration/operating fact | Read/data fact |
| --- | --- | --- | --- |
| `Available` | supported for this read | flow statistics enabled; DPI active | top-app read available |
| `Disabled` | support was observed | flow statistics disabled | no top-app read attempted |
| `DpiInactive` | support was observed | DPI inactive | no top-app read attempted |
| `Unsupported` | unsupported was observed or inferred from method/service absence | not established | no data |
| `TemporarilyUnavailable` | not proven unsupported | not established | read temporarily failed |

This conflation is the specific inconsistency Slice 02 addresses. The slice must not reinterpret old states or promise distinctions that the router does not provide. In particular, a transient error must not become a permanent unsupported claim, and a disabled/DPI-inactive feature must not be represented as unsupported.

## 5. Architectural Boundary

Introduce one Data Statistics-specific, immutable semantic read fact/snapshot. It may contain small Data Statistics-specific value types or enums, but it must remain local to this domain and must not be a reusable application-wide capability framework.

The minimum boundary contains:

1. **Capability/support** — what this read can establish about the required Data Statistics interface for the captured router context.
2. **Observed configuration/operating state** — the current flow-statistics/DPI state when the status call supplied it.
3. **Read/data availability** — whether a usable top-applications data read was obtained, was not applicable because of observed configuration, or could not currently be completed.
4. **Context stamp** — active router profile identity and `IActiveRouterContext.Version` captured for the read.
5. **Existing compatibility payloads** — `DataStatisticsStatus`, `DataStatisticsSnapshot`, and optional `NetworkTrafficSnapshot`, without moving parser, profile, UI, or persistence concerns into the new type.

The exact concrete names are an implementation decision, but the representation must be small enough that its three dimensions cannot be collapsed accidentally. It must not own mutation outcomes, router access, user-facing strings, health interpretation, or persisted state.

## 6. Target Capability / Configuration / Availability Semantics

The target facts must express only repository-supported distinctions.

| Dimension | Minimum states | Evidence and meaning |
| --- | --- | --- |
| Capability/support | `Supported`, `Unsupported`, `Unknown` | `Supported` follows a readable status containing flow-statistics state; `Unsupported` follows missing flow-statistics state or the established method/service-unavailable condition; `Unknown` is used when a transient read did not establish support. |
| Configuration/operating state | `EnabledAndDpiActive`, `Disabled`, `DpiInactive`, `Unknown` | Derived only from `DataStatisticsStatus`. `Unknown` is required when no authoritative status was read. No new configuration policy is inferred. |
| Read/data availability | `Available`, `NotReadBecauseDisabled`, `NotReadBecauseDpiInactive`, `TemporarilyUnavailable`, `Unknown` | `Available` requires a successful top-applications read. The two not-read values describe current control flow rather than a new user-visible state. `TemporarilyUnavailable` is for the existing non-unsupported RPC-failure path. |

The typed fact must preserve a lossless mapping back to the legacy `DataStatisticsAvailability` meanings during coexistence:

- supported + enabled/DPI active + available data → `Available`;
- supported + disabled → `Disabled`;
- supported + DPI inactive → `DpiInactive`;
- unsupported → `Unsupported`;
- unknown capability with transient read failure → `TemporarilyUnavailable`.

If characterization shows a current exception/status combination does not fit this table without guessing, implementation stops for review. No status should be manufactured merely to make a complete enum matrix.

Capability discovery/status reading is read-only. It must never call `SetApplicationContentProtectionAsync` or any other router mutation.

## 7. Router Context and Freshness Contract

All Data Statistics capability facts are scoped to the active router context at the start of the read.

The required publication flow is:

```text
capture active profile ID + context version
        ↓
perform existing read/reconciliation
        ↓
return/carry semantic fact with captured stamp
        ↓
revalidate active profile ID + version at ViewModel publication
        ↓
apply only when still current; otherwise reject without presentation change
```

`IActiveRouterContext.Version` is already the authoritative invalidation signal used by `DataStatisticsViewModel`; profile identity must be considered as well where the implementation can obtain it safely. The slice must not change `RouterSwitchCoordinator` ordering, `RouterManagerProvider` invalidation, session handling, or router selection behaviour.

Cancellation remains desirable for disposal and superseded work, but it is not the correctness proof. A held read for router A that ignores or outlives cancellation must still be rejected after a router A → B switch. A stale read must not update the legacy presentation, traffic session/history, full-table/detail follow-up work, loaded state, or context-labelled typed result for B.

Phase 1 will determine whether the existing ViewModel version check fully covers this behaviour or whether the smallest additive context-stamp/guard belongs with the typed read result. It must not create a new global cancellation or event system.

## 8. Compatibility Strategy

The migration is additive first:

- retain `DataStatisticsAvailability` and `DataStatisticsReadResult` for all unmigrated callers;
- retain `DataStatisticsStatus`, parser behaviour, optional traffic observation, and all current router calls;
- create the semantic fact from the same status/read classification that produces the compatibility result, not from an independently classified second read;
- preserve the current availability mapping until no consumers require it; and
- migrate only `DataStatisticsViewModel` to use the explicit dimensions while asserting identical visible output for each legacy state.

No persisted settings, profile data, cached history format, navigation route, XAML resource, or router configuration changes are part of the migration. Existing user-visible strings such as “Data Statistics is disabled”, “The router's DPI engine is not currently active”, and “Data Statistics temporarily unavailable” remain unchanged unless deterministic characterization proves that the existing ViewModel already uses different text.

## 9. DataStatisticsViewModel Migration Boundary

`DataStatisticsViewModel` is the single proving consumer. After migration it should:

- use the typed fact for capability/configuration/read-state projection;
- continue to use existing `DataStatisticsStatus`, snapshot, and traffic values for current presentation and accumulation;
- retain its refresh gate, reset behaviour, command readiness, full-table/detail loading, and Client Details navigation behaviour;
- reject stale stamped results before applying state; and
- preserve the same `RouterPilotStatus`, title, detail, current-period, collection, and traffic presentation for every existing legacy state.

The ViewModel must not become a capability registry or health interpreter. It must not make a new router request simply to reconstruct a compatibility value. It must not migrate Dashboard, Network Health, Application Traffic Details, or other consumers.

## 10. Mutation Boundary — Explicitly Out of Scope

`DataStatisticsService.SetApplicationContentProtectionAsync` is outside Slice 02. Its existing validation, non-blocking mutation gate, router write, authoritative detail readback, verification, and `ApplicationProtectionMutationResult` semantics remain unchanged.

This slice must not change:

- `dpi.mod_app_content_protection` payloads or method names;
- application-protection validation, error handling, readback, or verification;
- full-application/detail reads except where a compile-safe typed read boundary explicitly shares existing read-only data;
- any mutation outcome model; or
- router write behaviour during load, refresh, switch, upgrade, or startup.

## 11. Deterministic Characterization Matrix

| Required behaviour | Current source and expected result | Current deterministic coverage | Missing proof | Planned proof |
| --- | --- | --- | --- | --- |
| Supported + active/readable | `DataStatisticsService.ReadAsync`: readable status, flow statistics enabled, DPI active, successful top-app read → `Available`; ViewModel shows active presentation | Parser fixtures prove active status parsing; no direct service/VM classification fixture found | service classification and unchanged ViewModel presentation | Fake/proven router seam returns active status and snapshot; assert typed dimensions, legacy mapping, and ViewModel visible fields |
| Supported + disabled | status `FlowStatisticsEnabled == false` → `Disabled`; ViewModel shows disabled | Parser fixture proves false parses; no direct classification fixture | disabled is supported, not unsupported; presentation equality | Assert typed `Supported` + `Disabled`, legacy `Disabled`, no top-app read, existing ViewModel text/status |
| Supported + DPI inactive | readable enabled status with `DpiStatus != "1"` → `DpiInactive`; ViewModel shows unavailable/pending | Active parsing exists; no direct inactive fixture found | semantic/presentation classification | Assert typed supported + DPI inactive, no top-app read, legacy/ViewModel equality |
| Unsupported capability | absent flow-statistics state or `-32601` → `Unsupported` | Parser tolerance exists, not service result fixture | both existing unsupported routes and unchanged presentation | Exercise both status and RPC routes where existing fake seam permits; assert `Unsupported` fact and legacy/ViewModel result |
| Transient read failure | non-`-32601` `DataStatisticsRpcException` → `TemporarilyUnavailable` | no direct service fixture found | capability remains unknown rather than unsupported; presentation equality | Assert typed unknown capability + temporary read state, legacy/ViewModel result |
| Disabled is not unsupported | existing status branch order | not direct | explicit semantic distinction | Assert separate typed values and legacy `Disabled` |
| DPI inactive is not unsupported | existing status branch order | not direct | explicit semantic distinction | Assert separate typed values and legacy `DpiInactive` |
| Transient failure is not unsupported | existing exception filters | not direct | explicit semantic distinction | Assert unknown/temporary fact, never `Unsupported` |
| Parser semantics | `DataStatisticsParser` handles top/full/detail/status data and malformed input as current code defines | `RouterPilot.DataStatisticsHarness` | none for existing parser fixture set | Retain and run unchanged parser fixtures; add only a directly missing status fixture if needed for semantic characterization |
| Existing ViewModel presentation | `DataStatisticsViewModel.Apply` maps five legacy states to titles/details/status | no direct complete ViewModel matrix found | all five presentations are equivalent after migration | Exercise actual ViewModel with controlled read facts; assert established titles, details, status, period, and collections |
| A delayed result after A → B switch | `RefreshAsync` captures version and returns when version differs; `RouterSwitchCoordinator.ResetForRouterSession` clears state | code evidence, but no identified direct deterministic Data Statistics fixture | direct delayed-read/ViewModel proof | Hold A read, invalidate/switch/reset to B, release A, assert no A presentation/traffic/loaded publication; then prove B result is applied |
| Successful B read is B-stamped/published | active-context version exists; result currently has no stamp | no coverage | typed stamp and publication proof | Assert accepted B result carries B profile/version and ViewModel applies it |
| Router reset leaves no stale presentation | `ResetForRouterSession` clears collections/history and returns pending presentation | no dedicated listed harness fixture | reset plus late A completion | Assert reset state and stale rejection through actual ViewModel |
| Read-only discovery | `ReadAsync` invokes read methods; write method is separate | code evidence, not an explicit call-count fixture | no-write proof for read/status path | Fake router boundary records calls; assert no `SetApplicationContentProtectionAsync`/write method during every characterization case |

The implementation phase must identify the narrowest existing deterministic harness that can instantiate the actual service/ViewModel with a controlled router-manager seam. `RouterPilot.DataStatisticsHarness` is the first choice for parser and semantic classification. If the existing WPF/ViewModel fixture infrastructure is in another current harness, reuse it rather than adding a project. Creating a new transport abstraction only for tests is a stop condition.

## 12. Implementation Phases

### Phase 1 — Characterize current semantics and context behaviour

- **Objective:** add deterministic fixtures for the matrix gaps without changing production behaviour.
- **Production scope:** none, except the smallest test-only-controllable seam if direct ViewModel delayed-read proof is impossible otherwise.
- **Test scope:** `RouterPilot.DataStatisticsHarness` and the existing harness that already hosts usable ViewModel/context fixtures, if required.
- **Acceptance criteria:** all five legacy states, parser compatibility, no-write reading, and A → B stale-publication behaviour are explicitly characterized against current presentation.
- **Non-goals:** no typed capability production type, no Data Statistics UI change, no mutation work.
- **Stop conditions:** a proof requires RouterManager/transport redesign, a production router-contract change, or a broader Dashboard/Network Health migration.
- **Human review point:** review the confirmed state matrix and any demonstrated stale-context gap before production semantics are introduced.

### Phase 2 — Add the minimum semantic, context-stamped read boundary

- **Objective:** add the immutable, Data Statistics-specific support/configuration/read-state fact and context stamp beside legacy results.
- **Production scope:** likely `Models/DataStatistics.cs` and `DataStatisticsService`; use the smallest context-capture shape justified by Phase 1. `DataStatisticsReadResult` remains available.
- **Test scope:** extend Phase 1 fixtures to compare typed dimensions with legacy mappings from the same accepted status/read result.
- **Acceptance criteria:** one status/read reconciliation produces coherent typed and legacy views; parser and RPC calls are unchanged; no unsupported claim is made for a transient failure.
- **Non-goals:** no global capability vocabulary, no persistence, no capability cache/registry, no consumer migration yet.
- **Stop conditions:** context stamping requires changing RouterManagerProvider lifecycle, session semantics, router payloads, or creating an independent second classification read.
- **Human review point:** review the additive data model and exact legacy mapping before a ViewModel changes.

### Phase 3 — Context-safe publication, if Phase 1 shows it is not already fully satisfied

- **Objective:** ensure the typed and legacy presentation path cannot accept an A result after an A → B context switch.
- **Production scope:** preferably only `DataStatisticsViewModel`, using its existing `IActiveRouterContext` and refresh gate; include service changes only if Phase 2's stamp needs a minimal carrier.
- **Test scope:** delayed A/B fixtures assert legacy presentation, typed fact, loaded state, traffic accumulation, and follow-up reads are not published stale.
- **Acceptance criteria:** cancellation/reset is supplemental; version/profile revalidation is the correctness guard.
- **Non-goals:** no `RouterSwitchCoordinator`, `RouterManagerProvider`, or global scheduling change.
- **Stop conditions:** satisfying the proof needs broad constructor churn, changes switch ordering, or creates a global background-work framework.
- **Human review point:** this phase may be merged into Phase 2 only if the same additive implementation and tests prove the full contract.

### Phase 4 — Migrate DataStatisticsViewModel only

- **Objective:** use explicit typed dimensions for existing Data Statistics projection while producing byte-for-byte-equivalent user-facing state where tests can observe it.
- **Production scope:** `DataStatisticsViewModel` only, plus a narrow existing model adapter if compilation requires it.
- **Test scope:** actual ViewModel matrix for available, disabled, DPI inactive, unsupported, temporary failure, reset, and context switch.
- **Acceptance criteria:** titles/details/status, collections, period, traffic presentation, command behaviour, full-table/detail follow-up conditions, and reset behaviour remain compatible.
- **Non-goals:** no Dashboard/Network Health/Application Details/Protection migration; no XAML/UI redesign.
- **Stop conditions:** a second production consumer must migrate, friendly presentation policy changes, or full/detail/mutation behaviour needs redesign.
- **Human review point:** manual read-only Data Statistics check only after deterministic equivalence passes; no live mutation test is required.

### Phase 5 — Final equivalence and regression validation

- **Objective:** validate the cumulative Slice 02 diff, not add cleanup.
- **Production scope:** none by default.
- **Test scope:** complete deterministic matrix; all directly related existing harnesses; frozen VPN regression; Debug and Release solution builds.
- **Acceptance criteria:** narrow diff, clean tree, compatibility mapping intact, no router writes, no persistence changes, and all regression gates pass.
- **Non-goals:** fixes discovered here require a separately reviewed correction task.
- **Stop conditions:** any semantic or user-visible difference that cannot be proved existing-compatible, or any unrelated source change.
- **Human review point:** approve Slice 02 closure only after final cumulative review.

## 13. Regression Strategy

### Automated proof

After each relevant phase, run:

- `dotnet run --project RouterPilot.DataStatisticsHarness -c Debug`;
- `dotnet run --project RouterPilot.NetworkHealthHarness -c Debug` if its existing Data Statistics dependency is affected or the fixture is used;
- `dotnet run --project RouterPilot.ClientNavigationHarness -c Debug` if context-switch fixture infrastructure is used;
- `dotnet run --project RouterPilot.VpnMutationHarness -c Debug` as the frozen regression oracle;
- `dotnet build RouterPilot.sln -c Debug`;
- `dotnet build RouterPilot.sln -c Release`;
- `git diff --check` and cumulative diff review.

No live router mutation validation is required: the normal Slice 02 read path is read-only and application-protection mutation is excluded. An optional manual, read-only observation against a supported and an unsupported/disabled router may provide confidence after deterministic proof, but is not a safety substitute and must not mutate configuration.

### Manual review

The final human check is limited to Data Statistics presentation on the existing UI: active data, disabled state where available, DPI-inactive/unavailable message where available, unsupported/temporary wording, Refresh, and router switch/reset. It must not claim support conditions that are not available on the review router.

## 14. Persistence and Router Contract Guarantees

Slice 02 must preserve all of the following:

- existing RPC method names, payloads, call ordering, and parsers in `RouterManager.DataStatistics.cs`;
- `RouterManager`, `RouterManagerProvider`, session, SSH, HTTP, certificate/trust, and authentication behaviour;
- `DataStatisticsStatus`, top/full/detail statistics compatibility data, and optional traffic-counter semantics;
- application-protection write/readback/verification contract;
- settings, router profiles, credentials, client profiles, history and cache formats;
- existing navigation and UI/XAML; and
- the frozen VPN subsystem and harnesses.

Installing, launching, or upgrading RouterPilot must not change router configuration because of this capability work. Capability discovery remains read-only.

## 15. Explicit Non-Goals

- No global capability registry or model/firmware whitelist.
- No generic `Result<T>` or application-wide outcome framework.
- No RouterManager, transport, session, trust, or parser rewrite.
- No Dashboard or Network Health migration/health interpretation.
- No migration of full application statistics, application detail, or application content protection.
- No Data Statistics UI/XAML, navigation, terminology, or visual redesign.
- No persistence migration or telemetry/history format change.
- No new router capability discovery beyond existing Data Statistics reads.
- No mutation, payload, readback, validation, or operation-outcome change.
- No VPN, device-domain, notification, maintenance, or other capability-domain migration.

## 16. Slice Stop Conditions

Stop implementation for human review if any needed change would require:

- a RouterManager rewrite or generic transport layer;
- a global capability registry, router-model whitelist, or speculative capability distinction;
- modification of a router read/write method name, payload, parser meaning, session, or trust contract;
- alteration of `SetApplicationContentProtectionAsync` or any mutation outcome;
- persistence migration or profile/history format change;
- Data Statistics XAML/navigation redesign or changed user-visible state meaning;
- Dashboard, Network Health, another analytics consumer, or another domain migration;
- RouterSwitchCoordinator ordering changes or weakened stale-result protection;
- VPN source, test, UI, gateway, routing, or PIA lifecycle changes; or
- a second independently reconciled capability read merely to populate the new model.

## 17. Slice Completion Criteria

Slice 02 is complete only when:

- an explicit, Data Statistics-specific capability/configuration/read-state boundary exists;
- its capability fact is read-only and context-scoped;
- disabled, DPI inactive, unsupported, and temporary unavailability remain semantically distinct;
- the legacy `DataStatisticsAvailability` compatibility surface remains intact for unmigrated callers;
- typed and legacy results derive from the same read/status reconciliation;
- `DataStatisticsViewModel` is the only migrated production consumer;
- its visible behaviour remains deterministically equivalent for all existing availability states;
- a delayed A result cannot publish to B after router switch/reset;
- no router mutation occurs during capability/status discovery;
- parser, router RPC/payload, RouterManager, persistence, navigation, UI/XAML, and VPN remain unchanged;
- relevant deterministic harnesses and Debug/Release builds pass; and
- the final diff is narrow and has no intentional user-visible behaviour change.

## 18. Deferred Follow-On Work

The following are intentionally deferred and must be separately selected, planned, and reviewed:

- Network Health consumption of explicit Data Statistics capability facts;
- other domain capability migrations (for example, SQM, AdGuard, Tailscale, packages, or maintenance) only when their repository evidence supports a narrow slice;
- an operation outcome/verified-mutation foundation for a separately chosen, non-VPN mutation workflow;
- further Data Statistics consumers, including Dashboard/Network Health and application detail/protection, if a later slice shows a concrete need; and
- any optional generic capability vocabulary only after repeated, compatible domain evidence proves that a common abstraction reduces real duplication without flattening meaningful distinctions.

None of these items are scheduled by this document or implied by the Slice 02 implementation boundary.
