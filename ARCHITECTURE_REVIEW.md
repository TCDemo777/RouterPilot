# RouterPilot Architecture Review

## Scope and Disclaimer

This document records an internal architecture and code-health review of RouterPilot. It is intended to guide maintenance and future development.

It should not be interpreted as an independent security audit, penetration test, or formal certification. The review is based on static inspection of the RouterPilot source tree at the commit identified below.

The review intentionally omits environment-specific configuration, personal information, network identifiers, router responses, and credential material.

## Review Baseline

- Branch: `master`
- Baseline commit: `6c6564c`
- Review date: 2026-08-21
- Scope: tracked RouterPilot source and project files, excluding generated code, dependencies, and build output.

## Executive Summary

RouterPilot is a substantial WPF application with a recognisable MVVM/service architecture. It has useful central boundaries for router access, refresh scheduling, local persistence, diagnostics, and state reconciliation. Recent VPN and device-history work demonstrates careful treatment of authoritative identity and configuration-versus-live state.

The main maintenance risk is concentration of responsibility: router access remains heavily concentrated in `RouterPilot/Services/RouterManager.cs` and `RouterPilot/Services/RouterManager.AdGuard.cs`; dashboard orchestration is split across a very large ViewModel and window code-behind; and client reconciliation contains several local identity helpers. These are maintainability risks rather than evidence of immediate defects.

No P0 finding was identified by this static review.

## Architecture Overview

- WPF views and code-behind provide presentation, navigation, and UI event handling.
- ViewModels hold page state, reconciliation, command state, and presentation mapping.
- Services provide router access, diagnostics, persistence, notifications, scheduling, and derived network state.
- `RouterPilot/App.xaml.cs` composes application services through dependency injection.
- `RouterPilot/Services/RouterManager*.cs` is the principal router boundary, organised partly through partial classes for LAN clients, DHCP mutations, port forwarding, VPN, and AdGuard.
- `RouterPilot/Services/RefreshCoordinator.cs` owns named periodic refresh loops and uses per-task serialization.
- JSON-backed services persist local application state such as client profiles, presence history, timeline entries, preferences, notifications, schedules, and metrics.

## Codebase Metrics

Static line classification is comment-only-line based; mixed code/comment lines count as code.

| Area | Files | Lines | Code lines | Blank lines | Comment-only lines |
|---|---:|---:|---:|---:|---:|
| C# | 183 | 33,149 | 28,415 | 4,188 | 546 |
| XAML | 28 | 8,849 | 8,159 | 629 | 61 |
| Source total | 211 | 41,998 | 36,574 | 4,817 | 607 |

Top 15 largest C# files:

| File | Lines |
|---|---:|
| `RouterPilot/Services/RouterManager.AdGuard.cs` | 2,945 |
| `RouterPilot/Services/RouterManager.cs` | 2,342 |
| `RouterPilot/ViewModels/DashboardViewModel.cs` | 2,047 |
| `RouterPilot/Views/DashboardWindow.xaml.cs` | 1,644 |
| `RouterPilot/ViewModels/ClientsViewModel.cs` | 1,598 |
| `RouterPilot/Views/AboutView.xaml.cs` | 795 |
| `RouterPilot/ViewModels/ProtectionViewModel.cs` | 758 |
| `RouterPilot/ViewModels/ClientDetailsViewModel.cs` | 699 |
| `RouterPilot/Services/InternetSpeedTestService.cs` | 598 |
| `RouterPilot/Services/NotificationService.cs` | 563 |
| `RouterPilot/ViewModels/SettingsViewModel.cs` | 525 |
| `RouterPilot/Services/RouterInfoService.cs` | 512 |
| `RouterPilot/Views/ClientsView.xaml.cs` | 474 |
| `RouterPilot/Services/DiagnosticsExecutionService.cs` | 456 |
| `RouterPilot/Services/GLInetSessionService.cs` | 455 |

Top 10 largest XAML files:

| File | Lines |
|---|---:|
| `RouterPilot/Views/NetworkView.xaml` | 961 |
| `RouterPilot/Views/ProtectionView.xaml` | 960 |
| `RouterPilot/Views/AnalyticsView.xaml` | 921 |
| `RouterPilot/Views/OverviewView.xaml` | 779 |
| `RouterPilot/Views/ClientDetailsWindow.xaml` | 722 |
| `RouterPilot/Views/AboutView.xaml` | 624 |
| `RouterPilot/Themes/DesignSystem.xaml` | 618 |
| `RouterPilot/Views/ClientsView.xaml` | 578 |
| `RouterPilot/Views/SettingsView.xaml` | 407 |
| `RouterPilot/Views/LogsView.xaml` | 384 |

Files over 1,000 lines: five. Files over 500 lines: twenty.

Other static indicators:

- `catch (Exception)`: 99 occurrences
- Empty catch blocks: 11 occurrences
- `async void`: 57 occurrences, predominantly WPF event handlers
- Blocking `.Result`: 0 occurrences
- Blocking `.Wait()`: 1 occurrence
- Timer/polling source files: 5
- TODO/FIXME markers: 0

## Strengths

- Router access is substantially centralised behind `RouterManager`, provider, session, and feature services rather than embedded in every view.
- `RefreshCoordinator` uses cancellation, lifecycle versioning, and a per-task semaphore to prevent overlapping executions of the same named refresh task.
- The VPN model distinguishes stable profile group identity, rotating peer identity, tunnel identity, configured state, and live state. Peer/client identifiers are not treated as durable profile identity.
- Diagnostics have purposeful boundaries: `DiagnosticsExecutionService`, `DiagnosticRedactor`, and the sanitised network snapshot path keep diagnostic output separate from normal application operation.
- Local notification, presence, timeline, and device-profile services isolate persisted application state from router configuration.
- Shared semantic XAML resources are used throughout. The recent Clients/Known Devices alignment now shares `ClientDeviceCard` and `ClientDeviceCardItem` styles.
- Router writes are generally routed through focused services such as DHCP reservation and port-forward services, which creates a natural validation and verification seam.

## Priority Findings

### P0

No immediate correctness, data-loss, or security finding was established through static inspection.

### P1

1. **Concentrated router boundary.** `RouterPilot/Services/RouterManager.cs` and `RouterPilot/Services/RouterManager.AdGuard.cs` together contain more than 5,000 lines across session handling, parsing, state discovery, and feature operations. Partial classes help organisation, but changes can still affect a broad shared object. Preserve the existing feature-specific files and continue moving cohesive operations behind narrow interfaces when work naturally touches them.

2. **Persistence durability is inconsistent.** Several stores use temporary-file replacement or backups, while `ClientProfileService` and `ClientPresenceHistoryService` contain direct whole-file writes. Client profiles and presence history are valuable local state. A shared atomic JSON write helper with consistent corruption recovery would reduce avoidable data-loss exposure.

   Status: Substantially addressed after baseline review.

3. **Dashboard orchestration spans ViewModel and code-behind.** `DashboardViewModel.cs` and `DashboardWindow.xaml.cs` together exceed 3,600 lines and coordinate refresh, state propagation, navigation, health, and UI lifetime. This makes cross-feature changes costly and raises regression risk. Extracting cohesive orchestration seams should precede major new dashboard complexity.

   Status: Partially addressed after baseline review. Deterministic health projection has been extracted from `DashboardViewModel.cs`, and traffic calculation state has been extracted from `DashboardWindow.xaml.cs`. Main refresh sequencing, navigation/lifetime ownership, freshness, health/metric side effects, and the remaining orchestration stay in place.

### P2

1. **Device identity normalization is duplicated.** `LanClientClassifier` provides a shared normalizer, but similar local methods remain in client reconciliation, dashboard matching, presence, notifications, port-forward intelligence, and router parsing. Divergence in accepted characters or casing could produce inconsistent device matching. Consolidate where semantics are identical; retain intentionally stricter validators.

   Status: Substantially addressed after baseline review.

2. **Transient ViewModel event lifetime should be reviewed.** `KnownDevicesViewModel` subscribes to inventory and profile notification events in its constructor. It is registered as transient. The review did not prove a leak because navigation lifetime determines reachability, but a disposable/unsubscribe pattern would make lifetime ownership explicit.

   Status: Addressed after baseline review.

3. **Generic exception handling is widespread.** Most broad catches provide user feedback, debug logging, or defensive isolation. A smaller subset silently returns fallback state. Establish a lightweight convention for operation-result reporting so expected router unavailability, persistence failure, and programming errors remain distinguishable without showing raw exceptions to users.

   Status: Substantially addressed after baseline review.

4. **UI operation code is concentrated.** Several large XAML views and code-behind files combine layout, dialogs, command orchestration, and refresh logic. This is understandable for a desktop application but makes visual changes harder to review. Prefer extracting reusable controls or commands only for repeated patterns.

### P3

1. **Client and Known Device card reuse is partial.** The shared card shell and grid item style now provide consistent size, spacing, and theme resources. Their internal templates still differ because live clients show live activity while historical devices show last-observed data. A reusable header or device-card control could reduce remaining duplicated layout if future card work expands.

2. **Large XAML views merit incremental composition.** The largest view files are not automatically defective. Where a visual section is changed repeatedly, extracting that section into a focused control would improve reviewability and reduce layout fragility.

## Architecture Hotspots

| Location | Approximate size | Responsibilities observed | Concern |
|---|---:|---|---|
| `RouterPilot/Services/RouterManager.AdGuard.cs` | 2,945 | Router operations, parsing, AdGuard integration, diagnostics support | P1 maintenance hotspot |
| `RouterPilot/Services/RouterManager.cs` | 2,342 | Shared router lifecycle, discovery, network state, parsing | P1 maintenance hotspot |
| `RouterPilot/ViewModels/DashboardViewModel.cs` | 2,047 | Dashboard state, reconciliation, summaries, presentation | P1 maintenance hotspot |
| `RouterPilot/Views/DashboardWindow.xaml.cs` | 1,644 | Window lifetime, refresh orchestration, navigation, UI event handling | P1 maintenance hotspot |
| `RouterPilot/ViewModels/ClientsViewModel.cs` | 1,598 | Live client reconciliation, profiles, identity, filters, diagnostics | P1/P2 hotspot |
| `RouterPilot/Views/NetworkView.xaml` | 961 | Network, DHCP, port-forward, and related layout | P2 layout hotspot |
| `RouterPilot/Views/ProtectionView.xaml` | 960 | Protection controls and service scheduling layout | P2 layout hotspot |

File size is not itself a defect. These locations are priorities because they combine multiple changing responsibilities.

The Dashboard hotspot remains, but deterministic health policy and traffic calculation state have now been separated into focused presentation helpers. The baseline sizes above remain historical review data.

## State and Identity Management

RouterPilot maintains several representations of the same network domain. The central identity themes are sound but deserve continued discipline:

- Live clients and Known Devices are reconciled using normalized device identity, with profile metadata held locally.
- DHCP and port-forward intelligence combine address, reservation, and device identity; this is a natural source of stale-reference risk when leases change.
- Presence history and monitored-device state are local lifecycle data and should remain keyed to the same authoritative device key as profiles.
- VPN profile group identity is the stable configuration identity. Peer/client identifiers can rotate and must remain transitional data only. Tunnel association and live status are separate facts.
- Notification and timeline state should remain lifecycle-based so reappearance and forgotten-device flows do not inherit obsolete local state.

The main risk is not the chosen model but duplicated normalization and repeated conversion between live, persisted, and presentation models. New features should identify the authoritative input before adding a new cache or derived key.

## Refresh and Concurrency

`RefreshCoordinator` is a strong architectural point: named tasks use `PeriodicTimer`, cancellation, lifecycle reconciliation, and a zero-wait semaphore to prevent concurrent callbacks for the same task.

Additional page-level timers exist in client, protection, log, and detail flows. They are reasonable for page-specific state, but their ownership should remain explicit so a central refresh and a page refresh do not independently read the same router domain. The main risks are duplicate reads, stale state arriving after newer state, and UI-thread work during large collection rebuilds.

VPN also receives live-status events. Its reconciliation already separates router configuration from live status, which reduces the risk that delayed live peer data overwrites current configured location.

One synchronous gate wait was found in `RouterPilot/Services/GLInetSshService.cs`. It should be reviewed in the context of call sites to ensure it cannot be reached from the UI thread. No blocking `.Result` usage was found.

## UI / XAML

The XAML design system centralises semantic brushes and common card styles. This supports Dark and Light themes and reduces hard-coded colour risk.

The main XAML risks are scale and duplication rather than a single confirmed defect:

- Several views approach or exceed 700 lines.
- Nested layout panels and inline templates increase the chance of ownership mistakes, especially around expandable sections and selection containers.
- Static versus dynamic resource usage should remain deliberate. Shared semantic colours should continue to use dynamic resources where theme changes are expected.
- The Clients and Known Devices views now share card shell and item-container styles. Remaining internal-template duplication is justified by different live versus historical content, but should be monitored if it grows.

## Persistence

RouterPilot uses local JSON persistence with feature-specific services. Several services demonstrate useful practices: typed serialization, restricted exception handling, backup or temporary-file replacement, and in-memory snapshots before writes.

The principal improvement is consistency. Client profile and presence-history paths should adopt the strongest existing atomic-write and corruption-recovery pattern. Deletion across device-local stores should continue to be failure-safe, especially where profiles, history, notifications, and search state are reconciled together.

Schema/version handling is limited in the inspected stores. This is acceptable for small local stores today, but explicit lightweight versioning becomes worthwhile before substantial model evolution.

## Diagnostics and Privacy

Intentional diagnostics are valuable and should be preserved:

- VPN State Capture supports safe before/after configuration analysis.
- `DiagnosticsExecutionService` provides a diagnostics execution boundary.
- `DiagnosticRedactor` provides defence in depth for diagnostics and export.
- The sanitised network snapshot separates current state capture, pseudonymisation, formatting, and final redaction.

The architecture should continue to keep credentials and router-specific sensitive values behind the router/session boundary, avoid raw exception display, and apply whole-document redaction to shareable diagnostics. This review did not reproduce or publish sensitive values.

## Performance

The likely performance hotspots are collection reconciliation and visual density rather than isolated algorithms:

- `ClientsViewModel` performs broad reconciliation, grouping, profile application, and collection rebuilds.
- Known Device filtering and sorting rebuild presentation collections from persisted profiles and live inventory.
- Dashboard refresh and UI code coordinate several feature summaries.
- Large card grids and large XAML trees can become expensive for unusually large inventories.

No evidence of a current P0 performance failure was found. Before optimizing, profile refresh duration and UI-thread allocation under realistic device inventories. The best near-term protection is preserving stable selection, avoiding unnecessary full rebuilds, and keeping router reads centrally coordinated.

## Reliability and Error Handling

Broad exception handling appears largely intentional at router, persistence, and UI operation boundaries. The review found 99 `catch (Exception)` occurrences and 11 empty catches. Many catch blocks log a type, produce safe UI feedback, or isolate optional diagnostics; they should not be treated as automatically incorrect.

The maintainability issue is consistency: a shared operation-result convention would make it clearer which failures are user-actionable, transient, expected during shutdown, or diagnostic-only. Raw exception details should remain out of normal UI and public exports.

Most `async void` methods are appropriate WPF event handlers. Non-event asynchronous flows should continue to return `Task`, be awaited, and accept cancellation where they own longer operations.

## Maintainability

The application has clear feature names and numerous narrow services, but a few central types carry disproportionate responsibility. The most valuable maintenance strategy is incremental: extract cohesive seams only when related work is underway, preserve existing router-write safety, and add targeted tests for pure reconciliation and persistence behaviour.

The recent Dashboard work demonstrates this approach: pure deterministic policy was extracted first, calculation state second, and refresh/orchestration ownership was deliberately left untouched. Further Dashboard decomposition should continue only after a fresh, focused boundary decision.

High-confidence dead-code candidates were not identified. There are no TODO/FIXME markers in the inspected source. VPN State Capture, diagnostics execution, redaction, and network snapshot facilities are active architectural capabilities, not obsolete debug clutter.

## Quick Wins

1. **Unify atomic JSON writes.** Create one internal helper and migrate profile and presence stores. Risk: low. Benefit: consistent durability and recovery.
2. **Document broad-catch policy.** Define when to log, surface safe feedback, rethrow, or intentionally suppress. Risk: low. Benefit: clearer failure ownership.
3. **Add disposable lifetime handling for transient event subscribers.** Start with Known Devices. Risk: low. Benefit: explicit event ownership.
4. **Consolidate MAC normalization where semantics match.** Route generic device keys through the shared classifier. Risk: medium. Benefit: fewer reconciliation differences.
5. **Add focused tests for VPN configuration-health mapping.** Cover group/tunnel association and rotating peer identifiers. Risk: low. Benefit: protects a proven reliability rule.
6. **Add persistence corruption tests.** Cover malformed local JSON and interrupted writes. Risk: low. Benefit: validates fallback behaviour.
7. **Add a card visual regression checklist.** Cover Clients and Known Devices in both themes and narrow widths. Risk: low. Benefit: protects shared visual language.

## Larger Refactor Candidates

1. **Router operation facades.** Evidence: the main router files exceed 5,000 lines combined. Direction: retain the shared session but expose narrow feature interfaces for discovery, LAN, DHCP, forwarding, VPN, and AdGuard. Scope: medium. Timing: before another major router feature.

2. **Dashboard orchestration decomposition.** Evidence: dashboard ViewModel and code-behind jointly handle many unrelated concerns. Direction: extract refresh/state adapters and navigation-independent presentation services. Scope: medium to large. Timing: before substantial dashboard expansion.

3. **Client reconciliation service seam.** Evidence: `ClientsViewModel` owns identity normalization, profile application, collection rebuilding, and presentation enrichment. Direction: move pure reconciliation into a testable service while retaining ViewModel UI state. Scope: medium. Timing: when client inventory logic changes again.

4. **Reusable device-card content.** Evidence: card shell is shared but live and historical templates retain similar header and status markup. Direction: introduce a small reusable header/content control only if both cards continue to evolve. Scope: small to medium. Timing: optional, after visual behaviour stabilises.

## Recommended Next Three Maintenance Commits

1. **Use an atomic JSON persistence helper for client profiles and presence history.** Add focused recovery tests; do not change stored semantics.
2. **Make transient Known Devices event subscriptions disposable.** Add explicit unsubscribe/lifetime coverage and verify no duplicate rebuilds after navigation.
3. **Extract and test pure client identity reconciliation helpers.** Start with normalized device-key matching and profile/live merge cases; preserve existing ViewModel behaviour.

## Progress Since Review

The first three recommended maintenance items have been completed after the baseline review. The original findings above are retained for historical context.

### Atomic JSON persistence

`AtomicJsonFileStore` now provides same-directory temporary-file writes, flush-before-replacement, atomic overwrite moves, and path-keyed write serialization. `ClientProfileService` and `ClientPresenceHistoryService` use it while retaining their existing JSON schema. Malformed JSON remains failure-isolated. This maintenance work introduced no router behaviour changes or new polling.

### Transient device UI lifetimes

`KnownDevicesViewModel`, `KnownDevicesView`, `ClientDetailsViewModel`, and `ClientDetailsWindow` now have explicit lifetime ownership where needed. Long-lived inventory and static profile-notification subscriptions are removed during idempotent disposal. The owned DispatcherTimer is stopped and detached when Client Details closes, and late asynchronous refresh completion is guarded. Router and persistence behaviour are unchanged.

### Client identity reconciliation

`ClientIdentity` centralises the existing normalized MAC rules used for device reconciliation. Sixteen duplicated identity implementations were identified and eight redundant private helpers were removed. Presence history, Known Devices, Client Details, monitored/favourite lookup, Port Forward matching, Global Search, and notifications now use the shared helper where their prior semantics matched.

The existing normalized identity rules were preserved: profile/presence identity remains uppercase separator-free alphanumeric text, while existing LAN/DHCP paths retain their stricter hexadecimal normalization. Device merging, persistence schema, router calls, and polling are unchanged. Existing limited IP fallback in client/detail flows and display-name fallback in Wi-Fi presentation identity remain pre-existing behaviour; neither was introduced by this maintenance work.

### Dashboard decomposition

`RouterPilot/Presentation/DashboardHealthProjection.cs` extracts deterministic router-health and Internet-quality presentation policy from `RouterPilot/ViewModels/DashboardViewModel.cs`. Approximately 94 lines of policy moved, reducing the ViewModel from approximately 2,044 to 1,973 lines at the time of extraction. Router-health score, state/summary, attention reasons, healthy conditions, and Internet-quality classification/presentation moved while public ViewModel properties and PropertyChanged ownership remained unchanged. Health thresholds, precedence, reason wording/order, and Internet-quality thresholds were preserved. The projection has no router/service or WPF/Dispatcher dependency; `DashboardWindow`, XAML, router calls, and polling were unchanged. Debug and Release builds passed.

`RouterPilot/Presentation/NetworkTrafficAccumulator.cs` extracts approximately 78 lines of network-traffic calculation state from `RouterPilot/Views/DashboardWindow.xaml.cs`: previous observation/baseline state, peaks, running totals, and sample count. First-sample, actual elapsed-time, counter-decrease/reset, peak, average, reset, and startup/reconnect spike-protection behaviour were preserved. The accumulator has no router, service, or WPF/Dispatcher dependency. Chart ownership, DashboardWindow traffic scheduling, polling cadence, router reads, metric recording, Timeline behaviour, traffic units, and rounding remain unchanged. `DashboardHealthProjection` and XAML were unchanged. Debug and Release builds passed.

### Operation failure handling

`OperationFailurePolicy` now provides a lightweight convention for concise, safe user-operation feedback together with categorised Debug diagnostics. The maintenance work audited 112 broad `catch (Exception)` occurrences and 6 empty catches; 31 catches were updated while 81 defensive broad catches were intentionally retained. Empty catches were reduced to zero.

Cancellation handling now separates expected shutdown or operation cancellation from genuine failure where the existing operation contract supports it. Router-transient, persistence, and user-operation boundaries now distinguish safe fallback or user feedback from unexpected faults more consistently. Unsafe raw exception text in normal UI was found and removed, while diagnostic isolation for VPN State Capture, snapshot export, and other optional diagnostics remains intact.

Two non-event `async void` tray flows now delegate to Task-based internal handlers, and the SSH disposal path no longer performs a synchronous UI-thread wait for an active command. No router behaviour, persistence schema, or polling was changed. Debug and Release builds passed.

This does not eliminate every broad catch. Remaining broad catches are intentionally defensive at defined boundaries and should be reviewed as their surrounding features evolve.

### Next maintenance priorities

The next priorities remain the existing review findings: reduce concentrated responsibility at the router boundary and incrementally improve UI/XAML composition. Dashboard deterministic projection work has begun, but refresh orchestration remains intentionally deferred; a fresh boundary decision should precede further Dashboard decomposition. The broad-exception/operation-result item is no longer an outstanding priority following the maintenance work above.

## Review Limitations

- This was static source inspection, not a penetration test, runtime soak test, performance profile, or independent security assessment.
- The review did not exercise a router, private network, or user data.
- UI theme and layout conclusions are based on source/resource inspection rather than exhaustive visual testing.
- No claim is made about all possible runtime races, persistence failures, or external router behaviour.

## Review Status

Review date: 2026-08-21

Baseline commit: `6c6564c`

Review type: Internal architecture/code-health review

Maintenance progress updated: 2026-08-21

Current maintenance state: first three recommended maintenance items, operation failure handling maintenance, and the first two Dashboard decomposition seams completed.
