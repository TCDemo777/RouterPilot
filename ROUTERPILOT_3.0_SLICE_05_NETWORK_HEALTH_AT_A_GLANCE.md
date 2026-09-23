# RouterPilot 3.0 Slice 05 — Network Health At-a-Glance Core Condition Summary

## 1. Purpose

Slice 05 makes the existing Network Health page immediately useful during ordinary healthy operation. It adds a compact, read-only core-condition summary inside the existing top Network Health card. The detailed diagnostic rows remain below it unchanged.

RouterPilot 3.0's mission remains: **make home-network management understandable**.

## 2. Current UX problem

The current page accurately projects Router, Internet / WAN, DNS / AdGuard, VPN, Wi-Fi, DHCP, resources, firmware, and Data Statistics. A healthy user sees a prominent `Healthy` heading, but must scan a flat nine-row diagnostic list to answer the practical questions:

- Is RouterPilot connected to the router?
- Is the internet connection working?
- Is the configured DNS / AdGuard protection active?

The existing rows and their navigation remain valuable diagnostic depth. The missing layer is an immediately readable healthy-state summary.

## 3. User-visible outcome

On a normal healthy router with AdGuard participating in Router Health, the top card will show these ordered, textual conditions beneath the existing overall result:

`Router — Connected` · `Internet — Connected` · `AdGuard — Protected`

The conditions are not health badges that suppress bad news. Each condition displays the currently accepted semantic state of the existing corresponding health row. A non-healthy state stays non-healthy in the strip.

## 4. Authoritative semantic source

The source chain is deliberately narrow:

```text
existing authoritative dashboard/freshness/AdGuard inputs
  -> NetworkHealthViewProjection.Create
  -> accepted Router, Internet / WAN, DNS / AdGuard NetworkHealthViewCheck rows
  -> small core-condition projection
  -> NetworkHealthView
```

The new projection must select and rename accepted rows only. It must not inspect raw Dashboard state, router data, HTTP errors, credentials, display detail text, or Data Statistics facts. It must not recreate the Router, WAN, or AdGuard decision trees.

## 5. Core-condition presentation model

Add one local presentation record in `NetworkHealthViewProjection.cs`, for example:

```csharp
NetworkHealthCoreCondition(string Label, string Status, RouterPilotStatus Severity)
```

Add a small pure projection method which accepts the existing `NetworkHealthViewSnapshot` or its `Checks` collection and returns, in this fixed order:

1. the `Router` row as label `Router`;
2. the `Internet / WAN` row as label `Internet`;
3. the `DNS / AdGuard` row as label `AdGuard`, only when that row participates in Router Health.

`Status` and `Severity` must be passed through from the existing row. The model carries no detail, navigation target, score contribution, or independent state classifier. A missing required core row produces no fabricated item.

`NetworkHealthViewModel` exposes the current projected list as a read-only bindable property and raises its notification when the existing `Snapshot` changes. No new service, cache, global state, or router read is introduced.

## 6. Exact Router mapping

The summary maps the existing `Router` row directly:

| Existing accepted row status | Strip item |
|---|---|
| `Connected` | `Router — Connected` with existing Connected severity |
| `Stale` | `Router — Stale` with existing Pending severity |
| `Unavailable` | `Router — Unavailable` with existing Error severity |

When `RouterFreshness` is `Loading`, the current overall projection returns `Initializing` with no checks. The strip must be absent; it must not invent `Router — Checking`. The existing overall text already truthfully says it is waiting for the existing router refresh.

The current projection has no separate Router `Unknown` row. Missing or disconnected current router input becomes the existing `Unavailable` row; the strip preserves that result.

## 7. Exact Internet / WAN mapping

The summary maps the existing `Internet / WAN` row directly:

| Existing accepted row status | Strip item |
|---|---|
| `Connected` | `Internet — Connected` with existing Connected severity |
| `Loading` | `Internet — Loading` with existing Pending severity |
| `Stale` | `Internet — Stale` with existing Pending severity |
| `Unavailable` | `Internet — Unavailable` with existing Error severity |
| `Disconnected` | `Internet — Disconnected` with existing Error severity |

The current projection has no distinct WAN `Unknown` row. The strip must not manufacture one. If a future projection emits an unrecognised accepted WAN status, it must display that status verbatim with its existing severity rather than claim `Connected`.

## 8. Exact AdGuard mapping

The summary maps the existing `DNS / AdGuard` row directly when it participates in Router Health:

| Existing accepted row status | Strip item |
|---|---|
| `Protected` | `AdGuard — Protected` with existing Active severity |
| `Paused` | `AdGuard — Paused` with existing Pending severity |
| `Disabled` | `AdGuard — Disabled` with existing Disabled severity |
| `Protection state unavailable` | `AdGuard — Protection state unavailable` with existing Pending severity |
| `Not configured` | `AdGuard — Not configured` with existing Error severity |
| `Authentication failed` | `AdGuard — Authentication failed` with existing Error severity |
| `Unavailable` | `AdGuard — Unavailable` with existing Error severity |
| `Loading` | `AdGuard — Loading` with existing Pending severity |
| `Stale` | `AdGuard — Stale` with existing Pending severity |

The summary must retain the established row precedence: included Loading, then Stale, then authoritative availability, then confirmed protection state. It must not look at `DataFreshnessState.Unavailable` directly: Phase 1/2 characterization established that this input currently falls through to the accepted Protected row for an Available/protected input, and Slice 05 must faithfully project the accepted row rather than redesign freshness.

## 9. Optional-AdGuard decision

Choose **A: omit the AdGuard condition when it is explicitly excluded/not in use**.

The existing `DNS / AdGuard` row is non-scoring (`AffectsOverall == false`) when optional/excluded, whether its accepted status is `Not in use` or optional `Checking`. Omitting it keeps the strip focused on participating core conditions and avoids presenting excluded AdGuard as unhealthy. Router and Internet remain present.

The detailed optional row remains unchanged below the strip.

## 10. Freshness and unknown safety

- Only the accepted row may produce a condition.
- Loading never becomes Connected or Protected.
- Stale never becomes Connected or Protected.
- Unavailable, Disconnected, Not configured, Authentication failed, Paused, Disabled, and Protection state unavailable retain their existing non-healthy status text and severity.
- Initial router Loading has no strip because there are no accepted rows.
- An absent required row produces no condition rather than a guessed healthy state.
- Future/unrecognised accepted statuses are shown verbatim with their existing severity; no absence-of-failure inference is permitted.

## 11. XAML and layout design

Integrate the strip below the existing overall status/detail within `NetworkHealthView.xaml`'s top card and above the detailed rows.

- Use an `ItemsControl` with a `WrapPanel` items panel.
- Each compact item contains a textual label and textual status, for example `Router` and `Connected`.
- Use existing dynamic card/surface/text resources and the existing severity-to-colour converter for reinforcement only.
- Do not hard-code light/dark colours.
- Keep items non-interactive. Detailed rows retain the existing `View` navigation buttons.
- Do not add charts, gauges, animations, icons required to convey meaning, fixed-width overflow, or horizontal scrolling.

At narrow widths items wrap onto additional lines inside the card; the existing page `ScrollViewer` remains vertically scrollable and horizontally disabled.

## 12. Theme and accessibility requirements

- Meaning is expressed in text, never colour alone.
- Light and dark themes use existing dynamic resources and preserve readable contrast.
- Text must wrap rather than clip in narrow windows.
- The item ordering is stable: Router, Internet, then participating AdGuard.
- Existing detailed row controls and keyboard navigation remain unchanged.

## 13. Context and staleness preservation

The summary is derived only from the current `NetworkHealthViewModel.Snapshot`. Existing ViewModel subscriptions, UI dispatching, freshness ownership, router-session reset behavior, resume-generation protection, and stale publication rejection remain authoritative.

No second context mechanism, refresh owner, or cache is introduced. A stale router A result cannot create a strip item for router B unless it has already been accepted into the existing current snapshot; Slice 05 must not weaken that boundary.

## 14. Scoring and non-interference contract

The strip is presentation only. It must not:

- add or remove health checks;
- modify row title, status, detail, severity, navigation, or `AffectsOverall`;
- alter `NetworkHealthViewSnapshot` overall state/detail/severity;
- alter Dashboard score, attention reasons, `NetworkHealthService`, timeline, or notifications;
- alter optional/excluded semantics;
- include Data Statistics.

Data Statistics remains lazy, detailed-only, and non-scoring.

## 15. Deterministic test matrix

Extend `RouterPilot.NetworkHealthHarness` to prove:

| Case | Expected core conditions |
|---|---|
| Normal healthy included AdGuard | Router/Connected, Internet/Connected, AdGuard/Protected in order |
| Router stale | Router/Stale; accepted Internet and AdGuard conditions remain independent |
| Router unavailable | Router/Unavailable; no false Connected state |
| Initial router Loading | no strip items because current projection has no accepted rows |
| WAN Loading | Internet/Loading, never Connected |
| WAN Stale | Internet/Stale, never Connected |
| WAN Unavailable/Disconnected | matching existing status, never Connected |
| Included AdGuard Loading/Stale | matching existing status, never Protected |
| Included AdGuard Protected/Paused/Disabled/protection unknown | exact existing accepted status and severity |
| Included AdGuard Not configured/Auth failed/Unavailable | exact Slice 04 status and severity, never Protected |
| Optional/excluded AdGuard including optional Checking | no AdGuard strip item |
| `DataFreshnessState.Unavailable` + Available/protected AdGuard | accepted `Protected` item, preserving current characterized projection |
| Missing/unrecognised selected row | no fabricated condition / verbatim accepted status as applicable |

For every fixture retain existing assertions for row title, status, detail, severity, navigation, `AffectsOverall`, overall result, and aggregate behavior. Add structural assertions that the core-condition projection does not invoke refresh, readers, router/AdGuard services, probes, mutations, RPC/HTTP, or VPN operations.

## 16. Manual visual acceptance

Inspect after implementation:

### Normal width, light and dark

- The existing overall Healthy status remains dominant.
- Router, Internet, and participating protected AdGuard are visible without scanning detailed rows.
- The strip is visually subordinate to the overall result but above detailed diagnostics.
- No awkward duplicate wording or layout change to detailed rows appears.

### Narrow width, light and dark

- Conditions wrap cleanly.
- No clipping, horizontal scrollbar, or fixed-width overflow occurs.
- Overall header/detail remain readable.
- Detailed rows remain unchanged.

### Healthy router

No fault injection is required. A configured, authenticated, protected AdGuard installation produces an immediately visible three-condition difference.

## 17. Expected file scope

Expected production files only:

- `RouterPilot/Presentation/NetworkHealthViewProjection.cs`
- `RouterPilot/ViewModels/NetworkHealthViewModel.cs`
- `RouterPilot/Views/NetworkHealthView.xaml`

Expected test file only:

- `RouterPilot.NetworkHealthHarness/Program.cs`

A separate presentation-model file is not justified; the local record belongs with the projection it serves. Any additional production scope is a stop condition.

## 18. Explicit exclusions

- Row regrouping or row-action-label redesign.
- Dashboard redesign.
- NetworkHealthService, score, timeline, notification, or attention-reason changes.
- New router/AdGuard reads, probes, refreshes, RPC/HTTP, SSH, or mutations.
- Persistence, device work, VPN work, navigation architecture, or global visual redesign.
- Data Statistics presence in the strip.
- Documentation cleanup unrelated to this plan.

## 19. Implementation phases

### Phase 1 — Characterization and mapping proof

Harness-only. Add fixtures that freeze the exact existing selected rows and their overall/row non-interference before UI changes. Existing Slice 04 coverage already characterizes much of AdGuard; this phase focuses on the new summary selection and Router/WAN edge cases.

### Phase 2 — Pure summary projection and deterministic proof

Add the local core-condition model/projection and ViewModel exposure. Prove the complete matrix and read-only/non-scoring contracts. No XAML behavior redesign.

### Phase 3 — XAML integration and closure

Bind the existing top card to the exposed conditions through a wrapping, non-interactive strip. Run regression validation and perform the stated light/dark and normal/narrow manual review.

Phases 1 and 2 may be combined only if the implementation harness asserts the pre-existing selected rows before assertions for the new summary output, making the intentional presentation addition explicit.

## 20. Closure criteria

Slice 05 is complete only when:

- A healthy included-AdGuard router visibly shows Router Connected, Internet Connected, and AdGuard Protected in order.
- Every selected degraded/loading/stale/optional state is represented truthfully or omitted only under the documented optional-AdGuard rule.
- Detailed health rows and their semantics are unchanged.
- Aggregate/scoring/timeline/notification behavior is unchanged.
- No new I/O, mutation, context mechanism, or Data Statistics loading occurs.
- Network Health harness and the established regression/build suite pass.
- Light/dark and normal/narrow manual visual review passes.
