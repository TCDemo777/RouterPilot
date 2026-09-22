# RouterPilot 3.0 Architecture

## Purpose and Status

This document defines the technical architecture and engineering contracts for incremental RouterPilot 3.0 development. It implements the product direction in [ROUTERPILOT_3.0_VISION.md](ROUTERPILOT_3.0_VISION.md); it is not an implementation plan, backlog, release schedule, or permission to change RouterPilot 2.x behaviour.

RouterPilot 3.0 is an **evolution, not a rewrite**. It will strengthen boundaries around proven RouterPilot 2.x behaviour while retaining GL.iNet RPC knowledge, session handling, SSH behaviour, AdGuard communication, trust protections, mutation safeguards, persistence, diagnostic sanitization, and deterministic harnesses.

The central architectural distinction is:

```text
router-authoritative state
        -> RouterPilot derived/projected state
        -> local user intent and UI state
```

These are related, but never implicitly interchangeable.

## 1. Architecture Goals

RouterPilot 3.0 should make the following easier to achieve consistently.

1. **Accurate router-state representation.** Router observations carry their router context, observation time, freshness, and degree of authority.
2. **Explicit capability.** Features distinguish supported, unsupported, unavailable, temporarily unavailable, and domain-specific states without inferring support from a convenient UI state.
3. **Safe mutation.** Significant router changes use authoritative validation, minimal writes, readback verification, and clear failure semantics.
4. **Stale-operation protection.** A result obtained for an older router context cannot overwrite state for the active router.
5. **Coherent devices.** One logical device can be represented safely across inventory, profiles, DNS, traffic, VPN routing, notifications, and history.
6. **Predictable router switching.** One active router remains the product model; switching invalidates dependent work and state predictably.
7. **Deterministic testing.** Important stateful router behaviours can be simulated at an appropriate boundary without requiring a live router.
8. **Safe compatibility.** Existing local data and trust decisions are preserved or migrated explicitly and without router mutation.
9. **Useful observability.** User activity, developer diagnostics, transport evidence, and maintenance history remain distinct but correlate safely.
10. **Incremental adoption.** Migrated and unmigrated 2.x subsystems coexist safely during the 3.0 transition.

The architecture does **not** optimize for simultaneous fleet management, theoretical abstraction purity, eliminating every large class, forcing all features into identical concrete models, replacing stable transports, or zero duplication at any cost.

## 2. Architectural Layers and Responsibilities

The target is a logical layering model, not a mandatory directory reorganization. Existing files may remain in place while their dependency direction becomes clearer.

| Layer | Responsibility | Must not contain | Dependency direction | 2.x mapping / migration |
|---|---|---|---|---|
| Presentation | Views, ViewModels, commands, local selection, visible status, accessibility, local editor state | Raw RPC interpretation, router payload construction, implicit router mutation from selection | Depends on application/workflow contracts | Keep CommunityToolkit MVVM and established views; migrate feature-by-feature |
| Application / workflow | Captures user intent, coordinates reads/validation/mutations/reconciliation, owns operation lifetime | Raw JSON parsing, visual layout details, transport credentials | Depends on router-domain contracts and diagnostics | Wrap/extract from existing services and code-behind where lifecycle logic is currently mixed |
| Router domain | Typed router facts, logical identity, capability facts, domain outcomes, verified snapshots | UI controls, WPF dispatcher assumptions, raw transport implementation | Depends on adapters only through focused contracts | Extract incrementally from existing models, parsers, services, and gateway seams |
| Router adapters | Translates domain requests/facts to proven GL.iNet RPC, SSH, AdGuard HTTP, UCI, and parser behaviour | Page state and UI intent | Depends on existing transport and router-specific parsing | Wrap existing `RouterManager` capabilities before extracting domain-focused adapters |
| Transport and trust | JSON-RPC sessions, SSH execution, HTTP, authentication, certificates, host keys, cancellation/timeouts | Product-state interpretation or UI projections | Lowest runtime layer | Keep `GLInetSessionService`, SSH, AdGuard transport, trust services, and provider invalidation unless evidence proves a defect |
| Persistence and diagnostics | Settings, profiles, histories, schedules, backups, trust stores, activity, Devlog, exports | Router mutation as a side effect of loading/migration | Used by appropriate higher layers | Keep existing atomic stores and sanitization; introduce versioned migrations only when needed |

### Dependency rules

- Presentation may express **intent** and consume projected state. It does not claim router state merely because a control is selected.
- Workflows may request authoritative facts and command domain mutations. They publish reconciled projections only after context checks.
- Router domain concepts should not expose raw JSON, UCI fragments, SSH output, or GL.iNet method names to presentation/application consumers unless the technical detail is explicitly a diagnostic artifact.
- Adapters may preserve router-specific quirks where correctness requires them. Abstraction must not erase proven router behaviour.
- Transport security remains beneath all domain adapters. Extracting a domain must not bypass certificate, host-key, session, timeout, or sanitization protections.

## 3. Router Context Contract

### Current 2.x pattern

`ActiveRouterContext` supplies an active profile and monotonically increasing context version. `RouterManagerProvider` ties its cached manager to a connection signature and invalidates/rebuilds it when the active profile or relevant connection configuration changes. `RouterSwitchCoordinator` currently resets and reconciles dependent services and ViewModels during a switch.

### Target 3.0 contract

Every router-dependent operation captures a context stamp before it begins:

```text
active router identity + context version + operation purpose
```

Before publishing a read result, the operation verifies that the captured context is still current. Before a significant mutation, it obtains or revalidates current authoritative state for that same context. After mutation and readback, it verifies context again before publishing reconciled state.

The context stamp is a correctness mechanism, not merely diagnostic metadata. It prevents a late response from router A being displayed after the user has switched to router B.

### Cancellation versus stale-result rejection

Cancellation is desirable: operations should cancel where the transport and feature lifetime permit it. It reduces unnecessary work and avoids delayed UI updates.

Cancellation alone is insufficient. A request may already be in flight, an external process may not be cancellable, or a completion may race with context switching. Therefore every context-sensitive publication must reject stale results even when cancellation was requested.

### Router switch behaviour

On a router switch:

1. increment/invalidate the router context;
2. cancel owned background and page operations where practical;
3. invalidate router-manager/session instances through the existing provider mechanism;
4. mark router-dependent projections unavailable/loading rather than presenting them as facts for the new router;
5. allow new-context refreshes to publish only after their own context validation.

The current explicit reset fan-out in `RouterSwitchCoordinator` is safe but manual. 3.0 may reduce it incrementally by giving migrated state owners a router-context lifecycle contract. It must not introduce a global event mechanism that leaves ordering, failure, or ownership ambiguous.

## 4. State Model

RouterPilot 3.0 uses common semantics rather than a universal mega-state object. Each domain chooses suitable concrete types but should be able to express the following.

| Concept | Meaning |
|---|---|
| Authoritative state | A fact freshly read from the active router, validated by its domain adapter. |
| Cached snapshot | Previously acquired RouterPilot data; useful, but not automatically current. |
| Derived/projected state | RouterPilot interpretation or aggregation of authoritative/cached facts. |
| Capability | What the current router context positively supports or cannot currently establish. |
| Freshness | Whether an observation is fresh, stale, loading, or unavailable, with an observation time where applicable. |
| Local UI state | View selection, expanded section, search text, editor draft, and other non-authoritative presentation state. |
| Pending intent | A user-requested action captured for a workflow but not yet verified as router state. |
| Unknown state | Information not established safely; it must not be silently converted to a positive fact. |

### Publication and reconciliation rules

- **Initial load:** publish loading/unknown until an applicable read completes. Do not equate an empty list with unsupported or unavailable without domain evidence.
- **Refresh:** retain a marked stale snapshot when useful, but associate every replacement with its context and timestamp.
- **Partial failure:** preserve valid independent facts. A failed optional enrichment must not erase an authoritative primary observation.
- **Unsupported:** publish only when positive evidence supports that conclusion; do not label transient transport failure as unsupported.
- **Temporarily unavailable:** represent transport/authentication/service-read failures separately from unsupported capability where the domain can distinguish them.
- **Router switch:** old snapshots are not authoritative for the new context; prevent their publication and clear/mark them accordingly.
- **Mutation completion:** publish only the authoritative readback and its verified domain interpretation, never an optimistic approximation.
- **Verification failure:** preserve the explicit failure outcome and reconcile from the router’s actual observed state. Do not claim the requested state because a write returned success.

## 5. Capability Model

### Target 3.0 contract

A capability is a read-only, router-context-scoped fact or qualified observation about whether a router feature can be used. Capability is richer than a boolean when the domain requires it.

At a minimum, a capability contract should be able to express applicable distinctions such as:

- supported and usable;
- supported but presently unavailable;
- unsupported/not present;
- not configured;
- authentication or trust blocked;
- unknown because evidence is incomplete; and
- domain-specific restricted/read-only modes.

The existing feature patterns demonstrate why this matters: AdGuard has availability states; SQM has native, legacy read-only, and unavailable modes; Data Statistics differentiates unsupported, disabled, DPI inactive, and temporary unavailability; Tailscale has field-level support; and VPN distinguishes PIA provider capability from current-config resolution.

### Required rules

- **Capability is not current configuration.** A positively identified provider or feature remains a capability even when a current configuration is absent or an expected transaction intermediate state temporarily clears it.
- **Discovery never mutates the router.** It may query documented RPC, SSH, or HTTP state only.
- **Capability is context-scoped.** A capability result for an old router context cannot enable features for the active context.
- **Incomplete evidence fails safe.** Do not turn absence of a convenient marker into proof of support.

### Migration approach

3.0 does not require one enormous capability registry before work can begin. Migrate individual domains to explicit capability contracts when their current availability/capability rules are touched. A later shared capability snapshot is warranted only if several domain contracts demonstrate stable common needs.

## 6. Router Domain Boundary

### Current 2.x pattern

`RouterManager` is a proven, broad router boundary spanning core reads, telemetry, DHCP, port forwarding, VPN, AdGuard, SQM, Tailscale, firmware, logs, and SSH-backed operations. It combines valuable router knowledge with several domain responsibilities.

### Target 3.0 contract

Presentation and workflow code should increasingly consume typed domain concepts instead of constructing or interpreting raw router payloads. Domain seams may form around existing areas such as network, devices, DNS/protection, VPN, SQM, packages, maintenance, telemetry, firmware, and router logs.

The rule is not “rewrite RouterManager.” The migration path is:

1. characterize a proven existing `RouterManager` operation;
2. wrap it behind a narrow domain contract;
3. move parsing/identity/verification decisions to the appropriate adapter/domain boundary;
4. migrate consumers; and
5. retain the established transport method and router-specific behaviour unless tests prove change is needed.

Raw GL.iNet method names, JSON structures, UCI details, SSH command output, AdGuard transport structures, and ephemeral router IDs belong in adapters or diagnostics. Typed domain concepts may retain router-specific logical identity where it is essential for correctness; “clean” abstraction must not hide important differences between router behaviours.

## 7. Operation and Outcome Model

RouterPilot should share outcome **semantics**, not impose one giant generic `Result<T>` type. A feature may keep richer domain-specific result records.

The following vocabulary is justified by current 2.x behaviour:

| Outcome | Meaning |
|---|---|
| Success | Requested domain effect was established and, where required, verified. |
| Unsupported | Evidence shows the feature or operation is unavailable on this router/context. |
| Unavailable | Required service/data could not be obtained now. |
| AuthenticationFailure | Credentials/session/trust prevented the operation where distinguishable. |
| Timeout | An operation exceeded its defined bound. |
| Cancelled | Caller/lifetime cancelled the work. |
| Stale | Router context or operation generation changed; result was deliberately not published/applied. |
| RouterRejected | Router explicitly rejected a valid attempted request. |
| MutationFailed | Write did not succeed. |
| VerificationFailed | A write may have succeeded, but authoritative readback did not prove the requested state. |
| InvalidUserIntent | Input/selection was invalid for the current domain state. |
| PartialSuccess | Independent useful facts succeeded while optional enrichment or a non-critical part did not. |

Transport outcome answers whether JSON-RPC/SSH/HTTP communication occurred. Domain outcome answers what the router fact or operation means. User-facing explanation translates the domain outcome into actionable language without exposing sensitive/internal data. These layers must not be collapsed accidentally.

## 8. Mutation Contract

### Significant router writes

For consequential router mutations, RouterPilot 3 follows this contract:

```text
capture user intent
-> capture router context
-> authoritative pre-read
-> validate intent and prerequisites
-> revalidate after asynchronous boundary where needed
-> perform the minimal router mutation
-> authoritative readback
-> verify requested effect
-> publish reconciled router and UI state
```

The workflow records a safe operation ID and domain outcome. It explicitly reports failure if verification cannot prove the result.

Expected safeguards include:

- no selection-driven mutations;
- no automatic substitute/first-item fallback for ambiguous state;
- no retry that can duplicate a consequential write unless the domain has an explicit idempotency contract;
- rollback only when it is unambiguous, safe, and itself verifiable;
- context revalidation before significant writes;
- UI reconciliation from authoritative readback, not RPC success alone.

VPN, port forwarding, SQM, and backup/restore demonstrate strong 2.x versions of this approach. Their proven contracts should be retained and used as references.

### Lighter-weight operations

Not every operation needs full transaction machinery. Read-only refresh, local preferences, harmless view state, and independently recoverable local presentation changes may use lighter contracts. The decision depends on router impact, ambiguity risk, recoverability, and the cost of a stale or duplicate action.

## 9. Async and Concurrency Contract

RouterPilot 3 uses the following correctness rules for asynchronous work.

- Named periodic work should prevent overlap where `RefreshCoordinator` already provides that guarantee.
- User-triggered refresh may supersede an older same-purpose refresh; the old completion must not publish stale state.
- Page/navigation lifetime should cancel owned work where practical. Disposal/cancellation never substitutes for context validation.
- Router switch invalidates all prior-context publication.
- A mutation conflicts with refresh only where they touch the same authoritative domain; the domain workflow decides whether to serialize, pause, or re-read.
- Busy state is scoped to an operation/domain. It must clear after completion and trigger command re-evaluation from reconciled state.
- Background polling must associate observations with router context, use safe overlap prevention, and stop cleanly on application shutdown.
- Reconnect recovery may re-establish reads and sessions, but must not repeat a mutation automatically unless that behaviour is explicitly part of a verified operation contract.

There is no requirement for one global scheduler. `RefreshCoordinator`, domain gates, cancellation sources, and operation generations may coexist when their ownership is explicit.

## 10. Device Domain

The device domain should retain current proven MAC-based behaviour while allowing the logical identity model to grow beyond it if future router evidence requires that.

| Concept | Responsibility |
|---|---|
| Device identity | Normalized stable identity key plus identity kind/provenance; current MAC normalization remains the principal proven identity. |
| Device observation | Router/AdGuard/traffic/DNS/presence observations with source, timestamp, availability, and context. |
| Device profile | User-owned friendly name, category, notes, monitoring preferences, and other local metadata. |
| Device feature projection | A safe feature-specific representation, such as VPN assignment, DNS activity, traffic, notification, or historical presence. |

`ClientInventoryState`, `ClientInventoryCoordinator`, `DeviceIdentityResolver`, client profiles, presence history, and Assigned Devices unknown-preservation are the starting assets.

Rules:

- authoritative router assignments are preserved even when the current inventory cannot resolve an identity;
- normal UI displays friendly, safe projections rather than raw identifiers;
- unknown devices remain representable and never become a reason to drop authoritative state;
- offline, absent, and unknown remain different conditions;
- identity enrichment is additive and must not overwrite user-owned profile data;
- feature projections must not invent identity equivalence merely because names or IP addresses happen to match.

## 11. Network Health

Health is a deterministic interpretation layer, not an opaque score and not a remediation engine.

```text
observation -> fact/state -> health interpretation -> user explanation
```

- **Observation:** timestamped source data, for example WAN reachability, router telemetry, VPN handshake/readback, or AdGuard availability.
- **Fact/state:** validated domain information including freshness and capability.
- **Health interpretation:** domain-aware conclusion such as healthy, degraded, unavailable, unsupported, temporarily unavailable, or needs attention.
- **User explanation:** concise reason, evidence scope, and optional explicit action.

Health must express uncertainty when the observation is missing, stale, unsupported, or insufficient. It must not change router configuration merely because it detects a problem. Any remediation is a separately invoked, explicit user workflow.

## 12. Activity and Observability

RouterPilot preserves distinct stores and audiences:

| Channel | Purpose |
|---|---|
| User-facing Activity | Meaningful user-understandable outcomes and changes. |
| Developer flight recorder / Devlog | Bounded technical sequencing, categories, timing, and safe operation evidence. |
| Transport logging | RPC/SSH/HTTP diagnostic evidence, sanitized at the boundary. |
| Maintenance history | Durable maintenance/operation history. |
| Diagnostic export | Deliberately generated support evidence with safe redaction. |

3.0 should increasingly correlate these with safe operation IDs, router-context identity, timestamps, and outcome categories. It must not merge all channels into one store or expose developer data in the normal activity timeline.

`RouterPilotDevLog` remains the reference for bounded entries, category, sequence, operation correlation, and sanitization. Sanitization must occur before logs reach shared diagnostic stores or user-visible activity.

## 13. Background Work

Each background task declares an owner, router-context association, cadence/trigger, cancellation lifetime, overlap policy, and publication rule.

Applicable work includes dashboard polling, traffic refresh, public IP refresh, scheduled actions, notification work, health observation, metric history, reconnect recovery, and feature-local refresh.

`RefreshCoordinator` should be retained where it already handles named task lifecycle and overlap correctly. Feature-local timers may remain when their view-specific lifetime and cadence are genuinely different. They must still obey context validation, shutdown, and stale-publication rules.

## 14. Persistence and Migration

### Compatibility contract

Where practical, preserve or safely migrate router profiles, DPAPI-protected credentials, trusted fingerprints, preferences/theme, client profiles, notifications, schedules, metrics/histories, and supported backup metadata.

Migration principles:

- versioned and deterministic;
- atomic where practical;
- non-destructive and recoverable where practical;
- independently testable;
- no router mutation;
- no silent credential-protection weakening;
- explicit user-facing outcome when a safe migration cannot complete.

Existing `SettingsService`, `ApplicationDataPathProvider`, atomic JSON persistence, backup manifest/hash validation, and pre-restore backup behaviour are assets to preserve. Historical telemetry may be migrated, archived, or explicitly reset when carrying it forward would compromise correctness. No new persistence format is assumed by this document.

If migration cannot safely complete, RouterPilot should retain source data where practical, avoid partially applying an uncertain migration, explain the local-data outcome, and never alter router state to compensate.

## 15. Security Contract

RouterPilot 3 retains DPAPI-protected credentials, explicit SSH host-key confirmation, HTTPS certificate trust/pinning, session invalidation, bounded/sanitized Devlog, backup allowlisting/validation, and package safety classification.

Architecture rules:

- secrets, keys, session material, and private identifiers do not enter user-facing activity;
- normal diagnostics and exports sanitize raw transport material;
- trust decisions remain explicit and endpoint-bound;
- migration cannot silently weaken trust or credential protection;
- domain extraction does not bypass RPC, SSH, or AdGuard transport security;
- fixtures and harnesses never contain real credentials, keys, or private network identities.

RPC, SSH, and AdGuard transport have different authentication/trust semantics. They should not be flattened into a generic transport model that erases those differences.

## 16. Test Architecture

Testing depth should match statefulness and router impact.

| Test layer | Purpose |
|---|---|
| Pure state/projection tests | Derived state, local selection, capability interpretation, freshness, and user explanation. |
| Adapter/parser tests | Router-specific response parsing, logical identity, and safe payload construction. |
| Fake domain/router tests | Controlled authoritative reads, router transitions, and outcome mapping. |
| Mutation lifecycle simulation | High-impact operation preconditions, exact call counts, writes, readback, stale context, and failure paths. |
| Persistence migration tests | Versioned/atomic/non-destructive data migration and failure recovery. |
| UI/ViewModel behaviour tests | Command enablement, local selection, lifecycle, and message presentation. |
| Opt-in live-router validation | Final compatibility evidence; never the only proof for complicated workflows. |

The VPN mutation harness is the reference case. For a high-impact write, deep simulation is appropriate when router intermediate state is meaningful, mutation can be duplicated/destructive, logical identity is non-trivial, or readback is necessary to establish success. Such tests should verify authoritative pre-state, exact/minimal payload, exact call count where relevant, router transition caused by that payload, authoritative readback, verification failure, stale context, cancellation where meaningful, and preservation of unknown state.

Trivial presentation or local preference code does not need fake-router simulation.

## 17. Compatibility Boundary

| Classification | Contract |
|---|---|
| Must preserve | Router profiles, protected credentials, trust fingerprints, supported schedules/preferences, user device profiles, proven router mutation semantics, and supported backup readability/migration. |
| Should preserve | Useful histories, notifications, local metrics, maintenance metadata, and established presentation conventions where they remain accurate. |
| May reset or migrate explicitly | Historical telemetry or derived caches whose direct migration would reduce correctness or safety. |
| Must never happen | Silent router configuration mutation on upgrade; silent loss/weakening of credentials or trust; automatic acceptance of changed SSH/HTTPS trust; destructive normalization of unknown router/device state; changing proven router/VPN mutation semantics only to fit an abstraction. |

Compatibility includes router-side safety, not only local file conversion. A new internal architecture must not cause different router writes merely because a request passed through a new boundary.

## 18. Incremental Adoption Model

Migrated and unmigrated domains may coexist. A subsystem migration follows this repeatable pattern:

1. characterize current behaviour, router contracts, compatibility constraints, and existing tests;
2. add deterministic coverage where the existing seam is insufficient for the risk;
3. define the domain’s typed state, capability, identity, and outcome semantics;
4. wrap the proven existing adapter/`RouterManager` behaviour rather than replace it;
5. move workflow and ViewModel consumption to the new boundary;
6. preserve authoritative readback and context validation;
7. compare old/new observable behaviour and regression outcomes;
8. remove obsolete duplicated logic only after equivalence is proven.

Coexistence rules:

- no migrated domain may require unrelated domains to migrate first;
- shared context/version, diagnostics, persistence, and device semantics remain compatible with 2.x consumers;
- one domain’s capability model cannot claim authority for another;
- source/adapter replacement happens only behind a stable, tested domain boundary;
- broad folder or namespace movement is optional and never a migration goal by itself.

## 19. Keep / Wrap / Extract / Replace Matrix

| Current component/pattern | Direction | Rationale |
|---|---|---|
| `GLInetSessionService` | KEEP | Proven authentication, cancellation, timeout, certificate, and RPC handling. |
| `GLInetSshService` | KEEP | Proven SSH behaviour and host-key trust integration. |
| AdGuard HTTP transport/security | KEEP | Separate security/authentication semantics must remain explicit. |
| `RouterManagerProvider` | KEEP + WRAP | Keep connection-signature invalidation; expose it through context-aware domain workflows. |
| `RouterManager` | WRAP + EXTRACT | Preserve proven operations; incrementally expose domain-focused adapters rather than rewrite it. |
| `ActiveRouterContext` | KEEP + EXTRACT | Retain versioning as correctness mechanism; make the context-stamp contract broadly consumable. |
| `RouterSwitchCoordinator` | KEEP + EXTRACT | Retain safe reset behaviour while migrating domains toward owned lifecycle handling. |
| `RefreshCoordinator` | KEEP | Existing named-task lifecycle/overlap solution is useful. |
| `ClientInventoryState` / coordinator | KEEP + EXTRACT | Strong foundation for a broader device-domain contract. |
| `RouterPilotDevLog` | KEEP | Bounded, correlated, sanitized diagnostic foundation. |
| `SettingsService` and stores | KEEP + WRAP | Preserve migrations, DPAPI, and atomic writes; add explicit versioned migration contracts only as needed. |
| VPN gateways/service/harness | KEEP | Reference implementation for state separation, verified mutation, and fake-router testing. |
| Feature ViewModels | WRAP + EXTRACT | Keep presentation behaviour; move router workflows/state interpretation out incrementally where coupling is high. |
| `DashboardWindow` coordination | EXTRACT | Gradually reduce combined navigation/refresh/lifecycle orchestration without forcing navigation redesign. |
| Proven transport contracts | REPLACE only with evidence | Replacement is exceptional and requires behaviour characterization plus compatibility proof. |

## 20. Architectural Invariants

Future RouterPilot work must not violate these rules:

1. Capability discovery never mutates router configuration.
2. Local selection is never treated as authoritative router state.
3. Results for stale router contexts cannot publish or mutate state.
4. Significant router mutations are validated and authoritatively verified.
5. Unknown authoritative assignments/identities are not silently discarded.
6. Router-specific intermediate state is not misclassified without domain evidence.
7. Secrets and private identifiers do not enter user-facing activity or unsanitized diagnostics.
8. Trust changes remain explicit; migration never silently weakens trust.
9. Upgrade/migration never silently changes router configuration.
10. A new abstraction does not change a proven router payload or lifecycle without explicit compatibility evidence.

## 21. Architecture Decision Record

### ADR-001: Evolution over rewrite

- **Decision:** Incrementally strengthen boundaries around 2.x behaviour.
- **Rationale:** Existing transport, trust, mutation, router-quirk knowledge, and harnesses are proven assets.
- **Consequence:** Migration requires coexistence and behaviour characterization; it avoids flag-day regression risk.

### ADR-002: Explicit authoritative/derived/intent separation

- **Decision:** Treat router facts, RouterPilot projections, and local user intent as distinct state categories.
- **Rationale:** VPN and device workflows show that conflating them causes unsafe or stale behaviour.
- **Consequence:** ViewModels publish explicit local intent and consume reconciled projections.

### ADR-003: Router-context versioning remains a correctness mechanism

- **Decision:** Retain and broaden active-router context/version checks.
- **Rationale:** Cancellation cannot prevent every late completion after a router switch.
- **Consequence:** Router-dependent work captures a context stamp and rejects stale publication.

### ADR-004: Incremental typed router-domain boundaries

- **Decision:** Wrap/extract domain seams around `RouterManager`; do not rewrite it wholesale.
- **Rationale:** Raw contracts are proven but broad; focused seams improve interpretation and testability.
- **Consequence:** Existing router adapters remain valid implementation underneath new contracts.

### ADR-005: Verified significant mutations

- **Decision:** Significant writes use authoritative pre-read, minimal mutation, readback, and verification.
- **Rationale:** RPC success alone does not prove router state changed as intended.
- **Consequence:** Some workflows incur additional reads; high-risk outcomes become explicit.

### ADR-006: Central logical device identity direction

- **Decision:** Build on normalized identity, inventory reconciliation, profile metadata, and unknown preservation.
- **Rationale:** Devices occur in multiple domains and must remain coherent without data loss.
- **Consequence:** Current MAC-based identity remains valid while the logical identity model stays extensible.

### ADR-007: Deterministic health interpretation

- **Decision:** Health derives from observable, fresh, domain-aware evidence rather than opaque scoring.
- **Rationale:** The product vision requires understandable conclusions without false certainty.
- **Consequence:** Health remains read-only; remediation is an explicit separate workflow.

### ADR-008: Compatibility-first migration

- **Decision:** Preserve/migrate local data and router-side semantics safely, explicitly, and without upgrade-triggered mutation.
- **Rationale:** Existing users, trust decisions, and router configurations are product assets.
- **Consequence:** Migration work needs deterministic tests, failure handling, and may archive/reset incompatible derived history explicitly.

## 22. Open Questions

The following require later human decisions rather than architectural assumption:

- What exact router model and firmware support policy should 3.0 publish?
- Which historical stores should migrate, archive, or reset when a new state representation is introduced?
- Which subsystem is the first incremental migration slice after architecture approval?
- Which non-VPN mutation should receive deep fake-router lifecycle simulation first?
- When is a formal aggregate capability snapshot justified after several domains adopt explicit capability contracts?
- What navigation/information-architecture changes, if any, have demonstrated enough user benefit to be planned separately?

## 23. Implementation Gate

No broad RouterPilot 3 implementation should begin until all of the following are approved:

- the product vision;
- this architecture document;
- the compatibility rules;
- the first incremental migration slice; and
- success and regression criteria for that slice.

After approval, the next planning artifact is a narrowly scoped incremental implementation plan. This document intentionally does not create that plan.
