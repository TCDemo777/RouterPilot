# RouterPilot 3.0 Slice 07 — Overview Current Device Snapshot

## 1. Purpose

Slice 07 adds a compact, read-only operational snapshot to Overview so a user can immediately see the devices RouterPilot currently and authoritatively knows are present. It is a bounded presentation of the shared typed device inventory, not a second inventory, presence engine, or Clients screen.

RouterPilot 3.0's mission remains: **make home-network management understandable**.

## 2. Current UX problem

Overview presents router, AdGuard, Internet, resource, Router Health, and Network Health information. It does not answer the ordinary operational question, “which devices are on my network now?” without the user navigating to Clients or Network Map.

Clients remains the complete management surface: filtering, selection, personalisation, favourite behaviour, details, and activity stay there. Network Map remains the detailed topology surface. Slice 07 adds only the missing compact Overview layer.

## 3. Exact visible outcome

Place a compact **Devices on your network** section after Overview's System health area and before deeper diagnostic sections. For a healthy accepted inventory it shows a truthful summary and up to four entries, for example:

```text
Devices on your network                         4 devices currently observed

Living Room TV                                  Wi-Fi · Home
Alice's Phone                                   Wi-Fi · 5 GHz
Office laptop                                   Ethernet
```

The entries are informational and non-interactive. No device-management control, favourite action, identity address, action menu, chart, avatar system, or per-device detail link is introduced.

## 4. Authoritative source

The only source is the Slice 01 shared typed inventory:

```text
existing router Wi-Fi / client reconciliation
  -> accepted DeviceInventorySnapshot
  -> ClientInventoryState
  -> OverviewCurrentDeviceProjection
  -> DashboardViewModel read-only presentation property
  -> OverviewView
```

The projection consumes only `DeviceInventorySnapshot`, `DeviceObservation`, and the accepted `ClientInfo` carried by each observation. It does not inspect raw router payloads, MAC strings as display identity, Dashboard `LanClients`, Network Map data, profile-only history, or transport status.

`DashboardViewModel.LanClients` is deliberately not the source. It is a separate Network Map presentation collection built from Dashboard DHCP/Wi-Fi state and does not carry the shared inventory's explicit router profile/version stamp.

## 5. Existing inventory flow and context-stamp findings

`ClientInventoryCoordinator` is the authoritative context-safe reconciliation seam. It captures the active router profile ID and version before its existing read, confirms both still match before publication, and calls:

```csharp
ClientInventoryState.Update(clients, capturedProfileId, capturedContextVersion)
```

This produces a context-stamped `DeviceInventorySnapshot`. The coordinator's deterministic harness already proves a delayed router-A result cannot publish after router B becomes active and its inventory is cleared.

The repository also has two convenience publication paths:

- `ClientsViewModel` calls `ClientInventoryState.Update(_allClients)` after its existing accepted Client presentation refresh and after configured-name changes.
- Dashboard's session-validated Wi-Fi refresh calls `ClientInventoryState.AddMissing(observedClients)` through `SeedClientInventoryFromWifi`.

Today the convenience `Update` overload writes a `null` profile and version `0`; the Dashboard seed preserves whichever snapshot stamp it happens to receive, which is `null`/`0` when it initializes an empty inventory. Therefore not every currently published snapshot is explicitly stamped, and Overview must not accept the state as-is merely because it contains devices.

### Required narrow correction

Slice 07 requires the smallest correction that makes Dashboard's already accepted normal refresh available to Overview without changing inventory ownership:

1. Add a context-stamped `AddMissing` overload in `ClientInventoryState`.
2. In `RefreshWifiNetworksAsync`, after its existing router-session check, pass the already-current active profile ID and captured `routerSession` to `SeedClientInventoryFromWifi` and then the stamped `AddMissing` overload.
3. Change the convenience `Update(IEnumerable<ClientInfo>)` path to retain a pre-existing valid snapshot stamp rather than replace it with `null`/`0`. When there is no pre-existing stamp, it remains explicitly un-stamped and therefore ineligible for this Overview feature.

This correction does not classify devices, initiate a read, or change the Clients display. It lets a richer existing Clients-page update replace the accepted records without discarding the Dashboard session's already validated stamp. Router switching clears the shared inventory before a new session begins, so an old stamp cannot be carried into B. An un-stamped first publication remains conservatively hidden from Overview until the existing stamped Dashboard or coordinator publication arrives.

`DashboardWindow.xaml.cs` changes only to forward its already captured session context. `ClientInventoryState` changes only to retain or apply that context metadata. `ClientsViewModel`, `ClientInventoryCoordinator`, identity resolution, and source priority do not change.

## 6. Current-device inclusion and count semantics

`DeviceObservation.IsOnline` is nullable:

- `true` means an authoritative aggregate presence source explicitly established online.
- `false` means an authoritative aggregate presence source explicitly established offline.
- `null` means presence is unknown; absence from the presence map is deliberately not offline.

The ordinary RouterPilot client/Wi-Fi reconciliation represents devices that were observed in the accepted current snapshot. It does not establish an explicit online value for every observation. For that reason Slice 07 does **not** say “online.”

An item qualifies when all conditions hold:

1. the snapshot has a non-empty router profile ID and non-zero context version matching the active router context;
2. the observation has a valid strict `DeviceIdentity` and accepted `ClientInfo`;
3. `IsOnline` is not explicitly `false`.

The resulting wording is **`<N> devices currently observed`**. It means the same thing for both count and preview list: accepted current-context observations that have not been authoritatively marked offline. It never equates inventory membership or absence from a later response with online status.

Profile-only and historical Known Devices are excluded because they are not `DeviceObservation` values in the accepted current inventory.

## 7. Preview ordering and limit

The preview maximum is **four** items.

Ordering is intentionally simple and stable:

1. observations with `IsOnline == true`;
2. observations with `IsOnline == null`;
3. accepted friendly display name using ordinal-ignore-case comparison;
4. canonical `DeviceIdentity.CanonicalMac` as the deterministic tie-break only.

Explicitly offline observations are excluded rather than ranked. This is presentation ordering, not a relevance, traffic, or device-health ranking.

## 8. Device display and unknown-safe behaviour

Each small presentation item carries only:

- `Name`: the accepted `ClientInfo.Name`, falling back to **Unknown device** only when the accepted name has no useful value;
- `Connection`: a concise accepted connection description or an empty value.

Connection presentation follows existing `ClientInfo` facts only:

- existing Ethernet connection summary displays **Ethernet**;
- existing Wi-Fi summary may display **Wi-Fi** with useful existing band and/or network context;
- unknown or unusable connection data displays no second line.

The projection does not show MAC addresses, IP addresses, raw interface identifiers, unknown device-type guesses, or fabricated attachment data. `null` presence stays eligible only as an observed-but-not-proven-online device; it is never labelled online.

## 9. Loading and empty states

The projection returns an explicit presentation state, not an empty collection with an implied meaning:

| Accepted inventory condition | Overview presentation |
|---|---|
| no current-context stamped snapshot | `Waiting for current device information…` with no count or placeholder entries |
| stamped snapshot accepted, zero qualifying observations | `No devices currently observed` |
| stamped snapshot with qualifying observations | `<N> device(s) currently observed` and the bounded preview |

The initial state must not say `0 devices currently observed`; RouterPilot simply has not accepted current inventory information yet. A cleared router-session snapshot returns to the waiting state immediately.

## 10. Presentation projection and ViewModel wiring

Add a small pure `OverviewCurrentDeviceProjection` in `Presentation`, with local presentation records sufficient for the View only, conceptually:

```csharp
OverviewCurrentDeviceSnapshot(state, summary, count, items)
OverviewCurrentDeviceItem(name, connection)
```

It takes `DeviceInventorySnapshot` and the active profile/version as inputs, validates context equality, applies the inclusion/order/limit rules above, and returns a conservative waiting result for a missing, mismatched, or un-stamped snapshot. It owns no cache and performs no I/O.

`DashboardViewModel` receives the existing singleton `ClientInventoryState` and `IActiveRouterContext`, subscribes to `ClientInventoryState.Changed`, and replaces a read-only observable/current snapshot presentation property from the pure projection. It must unsubscribe if the ViewModel becomes disposable. It does not call the coordinator, Clients ViewModel, router, or any refresh method.

The inventory's `Changed` event is the existing publication and reset signal. Dashboard's initial construction projects the current inventory once; each later accepted publication or reset replaces the entire overview presentation atomically.

## 11. Context and reset contract

The required deterministic contract is:

```text
accepted A snapshot stamped (A, version A)
  -> Overview projects A

router switch / ClientInventoryState.Clear()
  -> Overview immediately returns Waiting; A entries and count are gone

delayed A completion
  -> ClientInventoryCoordinator rejects publication; Overview remains reset for B

accepted B snapshot stamped (B, version B)
  -> Overview projects B normally
```

The projection's profile/version validation is a consumer check over the existing authoritative stamp, not a replacement context system. `RouterSwitchCoordinator` remains responsible for invalidating context and clearing inventory; its ordering is unchanged. Cancellation alone is not accepted as proof.

## 12. Navigation decision

**A. Entirely non-interactive snapshot** is selected.

Overview already has sidebar navigation to Clients, while adding a new navigation affordance would require a DashboardWindow navigation contract that is unrelated to the snapshot's core value. Individual entries must not be clickable in Slice 07.

## 13. XAML, theme, and responsive layout

Use a compact existing Overview card/section after System health and before Router Health. It includes a textual title, textual summary, and an `ItemsControl` with naturally wrapping or stacking compact entries.

The design uses existing `Card`, `Text.SectionTitle`, `Text.Secondary`, and muted/dynamic resources. It does not hard-code light or dark colours and does not communicate currentness using colour alone.

At normal width, entries may share a compact row or two-column grid. At narrow width, entries wrap/stack; no fixed total width, horizontal scrollbar, clipping, or card-width increase is permitted. An empty preview produces only the intentional loading/empty message, with no blank entry slots.

## 14. Deterministic proof matrix

Extend `RouterPilot.ClientNavigationHarness` with pure projection and state-wiring coverage:

1. context-stamped accepted snapshot with several qualifying observations produces exact count and preview items;
2. preview list uses the exact same inclusion rule as the count;
3. explicit online precedes unknown presence; alphabetical name and canonical identity tie-breaks are deterministic;
4. fifth and later qualifying items do not exceed the four-item preview limit;
5. accepted friendly name and useful Ethernet/Wi-Fi connection descriptions are preserved;
6. missing connection facts omit the secondary description;
7. explicit offline observations are excluded;
8. `null` presence remains observed but is never labelled online;
9. profile-only/historical records never enter the projection;
10. empty/un-stamped/mismatched snapshot yields waiting, not zero or a current-device claim;
11. stamped accepted empty snapshot yields the distinct no-devices message;
12. reset clears A immediately;
13. delayed A-to-B coordinator completion cannot republish A; accepted B appears normally;
14. the narrow stamp-preserving convenience-update contract does not change Client record selection or existing shared inventory identity semantics;
15. no projection code references router manager, reader, HTTP, SSH, mDNS, refresh, mutation, or persistence APIs;
16. existing Clients filtering, name source, selection, favourite, details, and navigation assertions remain unchanged.

Add structural assertions only where useful to show that `OverviewView` binds the small snapshot property, displays textual name/connection/summary, uses a wrapping/stacking layout, and contains no interactive per-item control. Structural tests do not claim rendered quality.

## 15. Manual visual acceptance

After deterministic gates pass, inspect the normal Debug application:

- **Normal/light:** Devices section is immediately noticeable, count is understandable, names are recognisable, connection text is subordinate, and the existing Overview hierarchy is not crowded.
- **Normal/dark:** the same hierarchy and readable secondary text, with no light-theme artefacts.
- **Narrow/light:** entries stack or wrap cleanly, count remains readable, and there is no clipping or horizontal scrollbar.
- **Narrow/dark:** the same responsive behaviour and contrast.
- **Ordinary healthy router:** naturally available current devices populate the section; no fault injection is needed.

## 16. Expected file scope

Expected implementation scope:

- `RouterPilot/Presentation/OverviewCurrentDeviceProjection.cs`
- `RouterPilot/ViewModels/DashboardViewModel.cs`
- `RouterPilot/Views/OverviewView.xaml`
- `RouterPilot.ClientNavigationHarness/Program.cs`

Required only because the context-stamp audit proved it necessary:

- `RouterPilot/Services/ClientInventoryState.cs`
- `RouterPilot/Views/DashboardWindow.xaml.cs`

No other production file is expected. If implementation needs RouterSwitchCoordinator, ClientsViewModel, identity resolution, ClientInventoryCoordinator, service/transport changes, persistence, Network Health changes, or any additional subsystem, stop and reassess scope.

## 17. Explicit exclusions

- No client identity resolution or friendly-name priority change.
- No Clients or Known Devices behaviour change.
- No Network Map redesign or use of `LanClients` as source.
- No Network Health score/semantic change.
- No router, DHCP, Wi-Fi, AdGuard, mDNS, SSH, or capability read.
- No polling loop, cache, mutation, persistence, timeline, notification, or dashboard architecture rewrite.
- No AdGuard behaviour change.
- No VPN code, UI, routing, lifecycle, or harness change.

## 18. Implementation phases

### Phase 1 — Context-safe projection foundation

Characterize current typed inventory stamp/inclusion behaviour, make the narrow stamped Dashboard-seed/convenience-update correction, implement the pure bounded projection, and add deterministic projection/context assertions. No XAML change.

### Phase 2 — Dashboard state wiring

Wire `DashboardViewModel` to the existing inventory change signal, expose the read-only projected snapshot, and prove immediate reset plus delayed A-to-B rejection end-to-end at the presentation boundary. This may combine with Phase 1 if the new projection is naturally owned by the Dashboard ViewModel and the harness proves both contracts together.

### Phase 3 — Visible Overview integration and closure

Add the compact Overview section, structural XAML assertions where valuable, full deterministic regression validation, and user manual visual acceptance in light/dark and normal/narrow layouts.

## 19. Closure criteria

Slice 07 is complete only when:

- Overview visibly presents a bounded, useful current-device snapshot during ordinary healthy use;
- it is derived solely from the accepted shared typed inventory;
- the count and preview use the same conservative inclusion rule;
- no unknown, absent, historical, offline, un-stamped, or stale device becomes a current device claim;
- A is cleared before B and cannot republish after a switch;
- no new I/O, mutation, or second inventory exists;
- Clients, Known Devices, Network Health, Dashboard metrics, AdGuard, and VPN behaviour remain unchanged;
- deterministic validation passes and the rendered UI receives manual acceptance.
