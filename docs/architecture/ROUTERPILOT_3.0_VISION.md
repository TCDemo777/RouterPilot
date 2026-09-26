# RouterPilot 3.0 Vision

## Mission

**Make home-network management understandable.**

RouterPilot should understand what the connected router can do, maintain an accurate representation of what it is doing, explain that state in useful human terms, and let the user change it safely. It should retain technical depth for people who need it without making the normal experience feel like a raw router administration interface.

RouterPilot 2.x established that a desktop application can safely expose a broad range of GL.iNet router capability. RouterPilot 3.0 exists to make those capabilities feel like one coherent product rather than a collection of individually useful tools.

## Why 3.0

RouterPilot 2.x is a successful, working generation. It accumulated real knowledge about GL.iNet router behaviour, security boundaries, router-state quirks, safe mutation workflows, device identity, and deterministic testing. Those are assets, not baggage.

RouterPilot 3.0 is an evolution of that work. It should consolidate the lessons learned in 2.x so that the application can represent router capability and state more consistently, explain network condition more clearly, and safely grow without re-learning the same lifecycle problems in each feature.

This does not require a ground-up rewrite. Proven GL.iNet RPC behaviour, SSH handling, trust protections, compatibility knowledge, and mutation safeguards should remain in service while improved boundaries are introduced incrementally around them.

## What RouterPilot Should Feel Like

RouterPilot should feel like a capable, trustworthy companion for one home router.

At any point, a user should be able to understand:

- what is happening now;
- whether the observed condition is healthy, degraded, unavailable, unsupported, or needs attention;
- what evidence RouterPilot has for that conclusion;
- what action is available; and
- which advanced details can be inspected if needed.

The interface should start with meaning rather than implementation structure. A user should not need to understand GL.iNet APIs, UCI, SSH commands, provider-specific identifiers, or router-internal state machines to understand their network. Those details should remain available where they help diagnosis or advanced administration.

RouterPilot 3.0 should still feel recognizably like RouterPilot. Improvements to hierarchy, navigation, and presentation should have a clear user benefit; a larger version number alone is not a reason to make the product unfamiliar.

## Product Principles

### Capability-driven, not assumption-driven

RouterPilot should adapt to the active router according to positively established capability, rather than assumptions based only on a router model name. Known and tested models or firmware versions remain useful documentation, but they are not the sole source of truth.

Capability discovery must be read-only. Discovering that a feature is available, unavailable, unsupported, or temporarily inaccessible must never silently change router configuration.

### One active router context

RouterPilot 3.0 continues to operate around one active router at a time. Simultaneous fleet monitoring and multi-router dashboards are not 3.0 goals.

The product should nevertheless avoid unnecessary global assumptions that would make future evolution impossible. Switching the active router must leave no stale state, pending action, or misleading presentation from the previous context.

### Progressive depth

The normal experience should answer practical questions first: what is happening, whether it is okay, what needs attention, and what the user can do.

Advanced information, diagnostics, recovery tools, and technical detail should remain available when useful. Simplicity must not be achieved by removing useful power or concealing important safety context.

### Human-readable activity

RouterPilot should make meaningful network events understandable: WAN loss and recovery, VPN connection outcomes, relevant router-service changes, meaningful device arrivals/departures, RouterPilot-performed configuration actions, and recovery from earlier problems.

User-facing activity must remain separate from developer diagnostics and the flight recorder. Activity should explain meaningful outcomes without exposing credentials, keys, session data, private identifiers, or raw router payloads.

### Deterministic understanding before AI

AI is not a RouterPilot 3.0 requirement. RouterPilot should first make deterministic observations, diagnosis, and explanations reliable, private, and safe. AI-assisted functionality may be considered later only where it offers a concrete benefit within those same privacy and safety expectations.

## Network Understanding and Health

RouterPilot should progress from displaying telemetry toward explaining network condition, while remaining honest about uncertainty.

Useful states may include healthy, degraded, unavailable, unsupported, temporarily unavailable, and needs attention. Meaningful conditions may include a WAN interruption, expected VPN connectivity not becoming established, unavailable DNS protection, unavailable router services, or observable connectivity degradation.

A health conclusion must be grounded in deterministic observable state. RouterPilot must not manufacture certainty from missing data, and it should distinguish an unsupported router feature from a temporary read failure or a confirmed unhealthy condition.

Health should be an explanation layer over authoritative observations, not an opaque score that hides them.

## Devices as First-Class Objects

The same physical or logical device appears across clients, known/offline devices, traffic, DNS Activity, AdGuard, VPN routing, notifications, and history. RouterPilot 3.0 should work toward one coherent device identity across those places.

It should build on the successful 2.x sequence:

raw identity
→ normalization
→ authoritative and inventory reconciliation
→ enrichment and friendly naming
→ safe display projection

Unknown identities must be preserved safely rather than discarded or destructively normalized. RouterPilot should not require raw identifiers to be shown to users merely to preserve correctness. Friendly names and useful context belong in the normal UI; technical identities belong behind appropriate advanced or diagnostic boundaries.

## Safe Router Management

RouterPilot is trusted with consequential router changes. Its preferred state-changing workflow is:

user intent
→ authoritative current context and state
→ validation
→ revalidation after asynchronous work
→ minimal mutation
→ authoritative readback
→ verification
→ state and UI reconciliation

A successful RPC response alone does not necessarily prove a state change succeeded. Selection in the UI must not implicitly mutate router configuration. Automatic fallback mutations should be avoided in safety-sensitive workflows, especially where router state is incomplete, stale, ambiguous, or unknown.

RouterPilot should preserve technical depth and existing maintenance/package functionality unless there is a specific compatibility, safety, security, or technical reason to change it. Architecture work is not a justification for arbitrary feature removal.

## Compatibility Promise

Existing RouterPilot users matter. Where practical, RouterPilot 3.0 should preserve or safely migrate router profiles, protected credentials, trusted SSH and HTTPS fingerprints, device/client profiles and friendly names, preferences, theme, notifications, refresh settings, schedules, useful history, and applicable backup/snapshot metadata.

Historical telemetry may be migrated, archived, or explicitly reset where preserving it would compromise the new architecture. Any such migration must be explicit, safe, and understandable.

**Installing, upgrading to, or launching RouterPilot 3.0 must not silently change router configuration merely because application architecture or persisted-data format changed.**

RouterPilot 3.0 should preserve proven GL.iNet behaviour and router-side safety contracts. Existing users should be able to recognize the product they upgraded to, not feel that an unrelated application replaced RouterPilot.

## Lessons From RouterPilot 2.x

RouterPilot 2.x evolved rapidly and developed strong patterns through real router behaviour rather than abstract preference. It taught us that:

- authoritative router state matters;
- a local UI selection is not router state;
- router identifiers and intermediate states can be ephemeral;
- provider capability is not the same as a currently selected configuration;
- successful transport does not always mean successful state change;
- asynchronous work can become stale when router context changes;
- authoritative readback verification materially improves safety;
- unknown router or device state should normally fail safe;
- deterministic fake-router testing is invaluable for complex mutations; and
- security and sanitization belong at diagnostic boundaries.

These lessons are especially visible in the mature VPN workflows, but they apply more broadly. Router-authoritative state, derived RouterPilot state, and local UI intent are distinct concepts. Capabilities should be explicit and associated with the current router context. Significant operations should verify their effect through authoritative readback. Raw GL.iNet RPC and SSH shapes should increasingly stop at suitable router-domain boundaries. Stale asynchronous work must not publish against a newer router context. Device identity should be normalized centrally while unknown identities remain protected.

RouterPilot 3.0 is the consolidation of these successful 2.x lessons, not a repudiation of 2.x.

## Non-Goals

RouterPilot 3.0 is not:

- a ground-up rewrite;
- a fleet-management or simultaneous multi-router dashboard;
- an opportunity to automatically reconfigure routers during migration;
- a broad replacement of proven GL.iNet transport code for elegance alone;
- a mandatory AI or chat-assistant release;
- an excuse to remove advanced or maintenance functionality arbitrarily;
- an exercise in adopting architectural patterns because they are fashionable;
- a major navigation redesign without demonstrated user benefit; or
- a reason to merge distinct router concepts merely to make models appear uniform.

## What Qualifies as a RouterPilot 3.0 Improvement

A proposed change should meaningfully improve one or more of these qualities:

- **Understanding:** helps a user understand the network or router.
- **Accuracy:** represents authoritative router state more reliably.
- **Safety:** reduces unintended or ambiguous router changes.
- **Coherence:** makes the same concept behave consistently across RouterPilot.
- **Capability:** adapts more accurately to what the current router supports.
- **Testability:** permits important router behaviour to be proven deterministically.
- **Maintainability:** reduces duplicated lifecycle/state logic without discarding proven behaviour.
- **Compatibility:** improves the product while respecting existing users and router configuration.

A change does not qualify merely because it is newer, adopts a fashionable framework or pattern, changes the appearance, rewrites working code, or increases the version number.

## Success Criteria

RouterPilot 3.0 succeeds when:

- existing 2.x users can upgrade safely and recognizably;
- upgrading or launching does not itself change router configuration;
- capability and router state are represented more explicitly and accurately;
- important mutations increasingly follow consistent validation, readback, and verification principles;
- device identity is more coherent across features while unknown state remains safe;
- health and status information are easier to understand without overstating certainty;
- meaningful network activity is easier to follow;
- advanced functionality remains available and understandable;
- proven GL.iNet behaviour, transport protections, and safety knowledge remain intact;
- major stateful workflows are increasingly deterministically testable; and
- RouterPilot still feels like RouterPilot.

## Relationship to the Architecture Specification

This document defines why RouterPilot 3.0 exists and the product principles that should guide it. It does not prescribe implementation structure, a backlog, release schedule, or a rewrite.

The companion [RouterPilot 3.0 Architecture](ROUTERPILOT_3.0_ARCHITECTURE.md) records the technical contracts that continue to guide RouterPilot's evolution.
