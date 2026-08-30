# RouterPilot Changelog

# RouterPilot v2.2.0 (Unreleased)

## Fixed
- Improved Clients-page DNS attribution for canonical IPv4, IPv6 and IPv4-mapped IPv6 endpoint formats returned by AdGuard Home.
- Preserved unavailable/no-data semantics when DNS activity is not observable, including clients that bypass AdGuard Home with external encrypted DNS.

# RouterPilot v2.1.1

## Changed
- Reorganized the About page to keep Support Development easy to find while grouping project, help and legal links in a dedicated Resources area and moving Updates into the lower utility area.
- Added an optional Buy Me a Coffee support link to the About page.

## Fixed
- Fixed missing magnifying-glass icons and the missing empty-state placeholder on Protection Blocklists, Protection Blocked Services, Clients, Global Search, and Logs search fields.
- Standardized these search controls with the DNS Activity search presentation while preserving the existing Search placeholder and filtering behavior.

# RouterPilot v2.1.0

## Added
- Added Saved Routers for keeping multiple router configurations while monitoring one active router at a time.

## Router Profiles & SSH
- Added configurable SSH ports for individual router profiles.
- Added per-router SSH authentication method support.
- Added support for password and private-key SSH authentication.
- Improved credential isolation when switching between router profiles using different SSH configurations.

## Changed
- Improved active-router switching, profile-aware Settings, and credential isolation when changing routers.
- Improved Protection configuration presentation, blocklist ordering, Notification Centre control sizing, and general UI consistency.

## Fixed
- Prevented stale router-session results from appearing after a router switch or reconnect.
- Corrected AdGuard Home Query Log retention display.

# RouterPilot v2.0.3

## Fixed
- Fixed inconsistent Network Health status between the Dashboard and Network → Health.
- Fixed a startup recursion issue that could terminate RouterPilot with a System.StackOverflowException.
- Fixed Firmware health remaining on “Checking...” and its View action so it opens Maintenance → Firmware directly.
- Improved Dashboard Network Health navigation and removed the redundant navigation action.
- Fixed misleading data-refresh logging during normal transient or optional-service conditions.
- Fixed false “Router firmware changed” events caused by comparing LuCI/OpenWrt release information with the GL.iNet firmware state.
- Fixed false “Public IP changed” events during startup and VPN state changes when the confirmed public IP had not changed.

## Changed
- Added an explicit preference controlling whether AdGuard Home participates in overall Router Health.
- Excluded optional AdGuard Home from Router Health percentage and attention status when the preference is off, with clearer not-in-use and unavailable explanations.

## Thanks
- Thank you to Lastimosa for highlighting the Network Health issue and the misleading data refresh delayed log entry addressed in this release.

# RouterPilot v2.0.2

## Security
- Proactively updated SSH.NET from 2025.1.0 to 2026.0.0 as dependency security maintenance.
- Updated the resolved System.Drawing.Common dependency from vulnerable 4.7.0 to patched 4.7.2.
- NuGet security audit now reports no known package security advisories.

# RouterPilot v2.0.1

## Fixed
- Fixed Protection Custom Filtering Rules and Network Port Forwarding drop-downs so their closed controls, menus and items follow the active RouterPilot theme.
- Added visual Search watermarks to Protection filtering and Analytics Data Statistics application search fields without changing their filter input.
- Made the Analytics All Applications search field more compact and removed the unused Settings-page search box.

# RouterPilot v2.0.0

## Added
- Added Network Health to the Network page: a compact read-only summary of router reachability, WAN, DNS protection, VPN, Wi-Fi, DHCP, resources, firmware and Data Statistics using existing application state.
- Added production DHCP reservation management with validation, client-aware selectors and direct navigation between DHCP rows and Client Details.
- Added production port-forwarding management with add, edit and delete workflows, capability-aware controls, rule validation, client context and an attention filter.
- Added GL.iNet VPN management with live tunnel status, connection-state feedback, diagnostics and local VPN schedules that run while RouterPilot is active.
- Added Data Statistics / DPI application analytics, including router application traffic, per-application device traffic, DNS/application context and supported per-application blocking controls.
- Added Internet Speed Test with history, preferences and clear availability/failure feedback.
- Added persistent Known Devices, favourites, device-presence history, offline client context and favourite-device availability monitoring.
- Added public-IP visibility, persistent WAN/CPU/memory metrics, internet reliability insights and network-instability alerts.
- Added dashboard card preferences and a compact five-action Quick Actions row.
- Added Protection Insights, expanded filter and blocked-service controls, DNS rewrite management and direct navigation from domains to DNS Activity.
- Added broader Global Search coverage and direct client navigation from search, DHCP, port-forwarding and DNS-related views.
- Added a sanitised network-snapshot export and lightweight Client Navigation, Data Statistics and Network Health regression harnesses.

## Changed
- Redesigned the Dashboard and Overview around clearer router, Internet, AdGuard and VPN status, configurable cards and actionable health context.
- Improved Network and Client Details so DHCP leases, reservations, Wi-Fi intelligence, port forwards and router client data reconcile into a consistent device view.
- Expanded Analytics into a single place for traffic, reliability, DNS activity, Internet Speed Test and Data Statistics.
- Expanded Protection into focused protection, filters, blocked services, rules/rewrites and Insights views without duplicating DNS Activity.
- Clarified firmware presentation by keeping the GL.iNet router-firmware update state separate from the LuCI/OpenWrt board-release value.
- Updated RouterPilot branding, installer metadata and the authoritative solution to include the lightweight regression harnesses.

## Fixed
- Fixed client DNS Activity availability so an empty enrichment result is not reported as an AdGuard Home outage.
- Fixed client identity reconciliation, category selection, detail navigation and offline known-device presentation across dashboard, Network and Clients views.
- Fixed VPN profile/tunnel reconciliation and live connection-state feedback for unlinked, disconnected and failed states.
- Fixed loading, stale, unavailable, disabled and recovery presentation across Network Health, Wi-Fi, DHCP, Dashboard, VPN, Protection, Clients and Data Statistics.
- Fixed shared Data Statistics lifetime handling so leaving Analytics does not dispose state used by other views.
- Fixed page refresh timers and transient view subscriptions so hidden pages do not continue unnecessary UI refresh work or retain stale view state.
- Fixed dashboard startup presentation so unresolved connection and VPN state is shown as loading rather than as an immediate failure or empty configuration.

## Performance
- Reused shared router, client, VPN, AdGuard and Data Statistics state across views rather than adding parallel polling paths.
- Centralised client inventory reconciliation and network-traffic accumulation to reduce duplicate work and keep client data consistent between pages.
- Made page-specific refresh timers visibility-aware and kept Network Health as a lightweight projection of existing state.

## Reliability
- Added explicit freshness and availability semantics so loading, stale, disabled, unsupported and unavailable states are distinguished instead of being treated as healthy data.
- Improved shared AdGuard availability ordering so an older hidden Protection refresh cannot overwrite newer state.
- Added atomic JSON persistence for local application data and standardised user-facing operation failure handling.
- Expanded deterministic regression coverage for client navigation, Data Statistics sharing and Network Health aggregation/lifecycle behaviour.

# RouterPilot v1.9.0

## Added
- Added the Event Timeline: a persistent, unified history with local category, severity and date filters, search, read state and safe CSV, JSON and text export.
- Added Overview Quick Actions, contextual client actions and Maintenance overflow actions that reuse the established maintenance, diagnostics, backup and firmware workflows.
- Added GL.iNet firmware update discovery, background firmware-state refresh, installed-version change tracking and per-version update-notification deduplication.
- Added Router Health with concise health explanations and Internet Quality based on existing connectivity and latency data.

## Changed
- Redesigned the application shell with a compact global header showing labelled Router, Internet and AdGuard Home states and refresh status.
- Applied the shared one-section/one-outer-box presentation pattern and consistent page content/scrollbar alignment across updated RouterPilot pages.
- Split diagnostics into Run Diagnostics, which updates the shared safe output, and Backup Diagnostics, which creates the existing redacted ZIP export.
- Clarified router information by showing the LuCI snapshot separately from the installed GL.iNet router firmware.
- Improved Timeline/notification relevance and deduplication so routine checks do not create repeated history or notification noise.

## Fixed
- Fixed duplicate firmware update notifications and duplicate Timeline events for the same available firmware version.
- Fixed stale firmware health state after a router firmware update by scheduling one non-blocking check after a confirmed connection.
- Fixed Overview status-value layout so standard connection states fit without clipping.
- Fixed Clients context-menu/XAML regressions and page scrollbar/right-margin inconsistencies.

## Security
- v1.9 preserves DPAPI credential protection, HTTPS certificate TOFU/pinning, SSH host-key verification, diagnostics redaction, command allow-listing and trusted external URL validation introduced in v1.8.1.

# RouterPilot v1.8.1

## Security
- Removed unconditional GL.iNet HTTPS certificate acceptance and added explicit certificate trust-on-first-use with persistent SHA-256 certificate pinning.
- Blocked changed router certificates and SSH host keys until the user explicitly trusts a replacement fingerprint.
- Added first-use SSH host-key confirmation using SHA-256 fingerprints.
- Updated AdGuard endpoint construction to honour the configured HTTP/HTTPS scheme and port, without HTTPS-to-HTTP downgrade.
- Identified HTTP AdGuard compatibility mode as unencrypted, removed raw AdGuard/RPC response-body logging, and improved safe exception and diagnostic redaction.
- Restricted update and release URLs to trusted HTTPS GitHub hosts and hardened `.gitignore` for user data, backups, diagnostics and key material.
- Added a privacy warning before exporting unencrypted `.rpb` backup archives.

## Changed
- TLS trust prompts now show certificate subject, issuer, validity, SHA-256 fingerprint and Windows certificate-validation context.
- Diagnostics exports minimise sensitive network and client information while retaining useful troubleshooting context.
- Backup export clearly explains that `.rpb` files are not encrypted.
- Security-sensitive connection trust is persisted per router endpoint.

## Fixed
- Fixed insecure TLS validation bypass and missing SSH host verification.
- Fixed hard-coded AdGuard transport configuration and unsafe update URL acceptance.
- Fixed sensitive raw response logging and remaining full-exception logging in hardened lifecycle paths.

## Security Notes
- Stock Flint 2 AdGuard Home compatibility may still use HTTP on port 3000. RouterPilot warns when this unencrypted mode is enabled, supports configured HTTPS, and never silently downgrades HTTPS to HTTP.
- `.rpb` backups are not encrypted. Stored RouterPilot passwords remain DPAPI-protected, but backup archives can contain readable configuration, client, notification and schedule metadata.

## Documentation
- Added `SECURITY.md`.
- Added `SECURITY-AUDIT-v1.8.1.md`.

## [1.8.0] - 2026-08-06

### Added
- Added the Maintenance Centre with supported Restart Wi-Fi, Restart AdGuard Home, Reconnect WAN, Reboot Router, Refresh All and diagnostics actions.
- Added shared maintenance history with action outcomes and safe user-facing messages.
- Added Windows notification delivery, Notification Centre and Windows-delivery preferences, quiet hours and Send Test Notification.
- Added portable RouterPilot `.rpb` backups with a manifest, SHA-256 validation and selective restore.
- Added automatic pre-restore backups, staged replacement and restore rollback protection.

### Changed
- Unified diagnostics execution and history between the About page and Maintenance Centre.
- Improved Maintenance summaries, action-result presentation, backup metadata and restore preview validation feedback.
- Improved notification preferences and About-page actions while retaining dynamic assembly version display.
- Applied consistent Pending, Active and Error state presentation to maintenance workflows.

### Fixed
- Fixed diagnostics launched from Maintenance bypassing the shared diagnostics history.
- Fixed AdGuard Home restart state and wording so completion is verified before success is reported.
- Fixed notification test delivery so it respects current global, channel and quiet-hours preferences.
- Fixed router and scheduled-service notifications to use stable event types for preference routing.
- Hardened restore processing by revalidating selected archive content immediately before replacement.

### Internal
- Added application-scoped maintenance-operation locking, diagnostics execution and backup/restore services.
- Added stable Windows toast identity `TCDemo777.RouterPilot` while preserving RouterManagerProvider and RefreshCoordinator ownership.
- Preserved atomic backup creation, restore integrity checks, path-traversal protection, RouterPilot data paths and legacy AdGuardTray migration.

## [1.7.0] - 2026-08-05

### Added
- Completed the internal RouterPilot rebrand, including `RouterPilot.exe`, `RouterPilot.sln` and RouterPilot namespaces.
- Added automatic copy-based migration of supported legacy `%LocalAppData%\AdGuardTray` data into `%LocalAppData%\RouterPilot`.
- Added per-user single-instance application protection.
- Improved the DNS Activity page and added a configurable Clients auto-scroll option.

### Changed
- Standardised Connected, Active, Pending, Paused, Disabled, N/A and Error status vocabulary.
- Improved Overview, Analytics, Network, Protection and Clients layouts.
- Moved Resource Health Details to Analytics and limited DNS Activity to the newest 200 entries.
- Improved Protection and scheduled-service organisation.

### Fixed
- Corrected router CPU utilisation using `/proc/stat` deltas.
- Restored Analytics DNS Summary presentation and corrected the StatisticCard XAML regression.
- Corrected client selection and auto-scroll behaviour.
- Improved MSI packaging and installer reliability.
- Preserved router monitoring when AdGuard Home is unavailable and restored AdGuard-unavailable presentation.
- Restored Wi-Fi discovery and resolved UI alignment regressions.

### Internal
- RouterPilot application data now uses `%LocalAppData%\RouterPilot` while safely retaining legacy files untouched.
- Completed RouterPilot executable and project identity updates.
- Retained stable single-instance mutex identities and the existing WiX UpgradeCode.

## [1.6.1] - 2026-08-02

### Added
- Added first-class scheduled AdGuard allowed-time windows with Allow and Block actions managed as one setting.
- Added advanced one-time, daily and selected-day single-action schedules.
- Added internal Protection, Blocked Services and Schedules tabs to the Protection page.

### Changed
- Unified allowed-time-window creation, editing, duplication, enablement and deletion while preserving atomic paired actions.
- Improved schedule editors with the complete shared AdGuard service catalogue and full-width responsive service selectors.
- Simplified global protection actions to Enable and Disable.
- Reordered the Network page so summary information and Quick Maintenance precede Wi-Fi and detailed network data.
- Updated repository and release links for the renamed `TCDemo777/RouterPilot` GitHub repository.
- Standardised unavailable AdGuard-derived values as theme-safe `N/A` states across RouterPilot.

### Fixed
- Kept router health, WAN, Wi-Fi, internet and router information refreshing when AdGuard Home is stopped or unavailable.
- Kept router-derived clients visible while treating optional AdGuard DNS activity as unavailable.
- Prevented stale protection, DNS statistics and rankings from appearing as current during AdGuard outages.
- Corrected client-card `N/A` typography and alignment without changing card dimensions.
- Restored GL.iNet physical and virtual-interface Wi-Fi discovery, including Main, Guest and IoT mapping.
- Restored the complete blocked-service catalogue in the manual controls and both schedule editors.
- Constrained About-page scrolling to the dashboard content region.
- Corrected MSI harvesting so the installer packages the complete self-contained publish output and launches `AdGuardTray.exe` from the installed RouterPilot folder.
- Standardised page spacing and corrected Protection, Network and schedule-editor layout regressions.

### Internal
- Separated router-authoritative refresh results from optional AdGuard status, statistics and client enrichment.
- Preserved the last successful router and Wi-Fi data when optional subsystem refreshes fail.
- Preserved the `AdGuardTray.exe` executable, internal namespaces and `%LocalAppData%\AdGuardTray` compatibility paths.
- Synchronized application, assembly, file, informational and installer versions at 1.6.1.

## [1.6.0] - 2026-08-01

### Added
- Added a SQLite historical data platform with schema versioning and repository-based access.
- Added persistent device connection events and a recent-activity timeline in Client Details.
- Added historical WAN usage charts with minute aggregation, range selection, downsampling and 30-day retention.
- Added historical router CPU and memory charts using the existing health refresh data.
- Added a seven-day Weekly Network Summary built from persisted history.
- Added privacy-aware diagnostic ZIP export with redaction, database health information and optional device identifiers.
- Added automatic GitHub Releases update checks and manual update controls on the About page.
- Added a unified Network Timeline with filtering, searching, lazy loading and virtualized presentation.
- Added deterministic Network Intelligence observations and device behaviour profiles.
- Added scheduled AdGuard blocked-service controls for one-time, daily and selected-day changes.
- Added paired allowed-time windows, Run Now, duplication and schedule execution notifications.

### Changed
- Extended Analytics with historical WAN, CPU and memory views while preserving the existing live charts.
- Extended Client Details with persisted history, previous addresses and networks, and recent activity.
- Reused existing refresh results for historical collection, insights and summaries without additional router polling.
- Improved visual consistency, spacing, accessibility and light/dark theme presentation across RouterPilot.
- Serialized manual and scheduled blocked-service mutations to preserve unrelated AdGuard service settings.

### Fixed
- Restored Wi-Fi network discovery on GL.iNet firmware that requires the UCI/hostapd compatibility path.
- Preserved Main, Guest and IoT SSID mapping across physical and virtual hostapd interfaces.
- Prevented duplicate device connection events and repeated schedule executions.
- Improved shutdown flushing for pending historical aggregates and locally persisted services.
- Improved missed schedule handling after sleep or suspension without executing stale occurrences.

### Internal
- Added application-scoped historical collectors and repositories with atomic, serialized persistence where applicable.
- Added UTC-based retention and aggregation with local-time presentation at the UI boundary.
- Added injectable clock-based schedule evaluation and a single RefreshCoordinator schedule task.
- Preserved the internal `AdGuardTray` project, executable, repository and local-data folder names.
- Synchronized application, assembly, file, informational and installer versions at 1.6.0.

## [1.5.1] - 2026-08-01

### Added
- Rebranded the user-facing application as RouterPilot while retaining the internal AdGuardTray project, executable, settings folder and repository names.
- Added the persistent Notification Centre with unread filtering and local JSON storage.
- Added state-change notifications for router connectivity and AdGuard Home protection.
- Added session-aware new-device detection without reconnect notification spam.
- Added explicit WAN throughput axes, legends and tooltips, plus timestamp-aware DNS query-history presentation.

### Changed
- Stabilised the live WAN chart with persistent series and observable history collections.
- Updated DNS history incrementally instead of rebuilding chart collections on every refresh.
- Centralised recurring refresh scheduling through RefreshCoordinator with cancellation-safe task restarts.
- Centralised RouterManager session ownership and replacement through the application service provider.
- Standardised RouterPilot card spacing, typography, badges, buttons, empty states and light/dark theme presentation.

### Fixed
- Prevented stale traffic baselines and negative throughput values after restoring the dashboard from the notification area.
- Prevented overlapping refresh loops during enable, disable, interval-change and shutdown operations.
- Fixed router and AdGuard state-change notifications after manual or external changes.
- Fixed static client-refresh event subscriptions and notification persistence races.
- Ensured pending notification history and application-scoped services flush during awaited shutdown.

### Internal
- Preserved stable ObservableCollection instances and UI-thread-safe chart mutations.
- Improved asynchronous disposal, cancellation propagation and refresh re-entry protection.
- Encapsulated notification collections behind read-only observable views.
- Kept release, assembly, file, informational and installer versions synchronized at 1.5.1.

## [1.5.0] - 2026-08-01

Version 1.5.0 introduces the RouterPilot product identity.

### Changed
- Updated user-facing application, dashboard, window, notification-area, About and diagnostics branding to RouterPilot.
- Added the subtitle: Companion for GL.iNet Routers & AdGuard Home.
- Preserved the `AdGuardTray.exe` executable name, settings paths, project structure and GitHub repository.
- Updated package, assembly, file and informational versions to 1.5.0.

## [1.4.0] - 2026-08-01

Version 1.4.0 is a reliability, performance and multi-network compatibility release built on the 1.3 series.

### Added
- Central router and AdGuard Home endpoint configuration with configurable schemes and ports.
- Automatic migration from the legacy `RouterIp` setting to `RouterHost`.
- GL.iNet IoT and Guest Wi-Fi client mapping, including role-aware labels such as `2.4G_Iot`.
- Additional wireless-client discovery using GL.iNet inventory, OpenWrt hostapd data, station tables and DHCP leases.
- Reproducible Windows build metadata.
- Dynamic About-page and diagnostics version reporting from assembly metadata.

### Changed
- Reused pooled HTTP connections for AdGuard Home control, client, statistics and query-log requests.
- Reused a reconnecting SSH session instead of opening a new connection for every command.
- Split `RouterManager` into router/network, AdGuard Home and operations partial implementation files while retaining its public API.
- Reused one dashboard `RouterManager` instance and replaced it automatically when connection settings change.
- Parallelised independent AdGuard Home dashboard requests.
- Prevented overlapping full-dashboard and live-traffic refreshes.
- Improved multi-SSID client matching by preserving network role and runtime-interface information.
- Updated package, assembly, file and informational versions to 1.4.0.
- Updated the About page and project documentation for the 1.4 release.

### Fixed
- Router address not being retained after saving settings.
- Router address being cleared during settings migration.
- Startup validation reading a different router property from the settings UI.
- Connection failures caused by inconsistent settings and endpoint models.
- Hard-coded router and AdGuard Home addresses throughout the application.
- Dashboard close and minimise actions bypassing notification-area lifecycle management.
- First-run setup creating an unmanaged dashboard without the tray manager.
- Repeated HTTP-client creation and unnecessary TCP setup.
- Repeated SSH connection setup during dashboard and traffic refreshes.
- Wi-Fi clients being assigned to the first SSID on a band when firmware omitted the SSID.
- GL-MT6000 2.4 GHz IoT clients being attached to the main 2.4 GHz network instead of the IoT SSID.
- Shell-command escaping errors in wireless station diagnostics.
- Malformed release project metadata and unresolved changelog merge markers.

### Existing 1.4 interface improvements
- Analytics v2 dashboard with responsive leaderboard-style rankings.
- Proportional activity bars for top clients, requested domains and blocked domains.
- Full-name tooltips and clearer request totals for ranked items.
- Client Details v2 with compact summary cards, copy buttons, top-five domain leaderboards and clearer request badges.
- Improved analytics spacing, typography, long-name handling and responsive layouts.

## v1.3.1 — Client details and tray usability

### Fixed
- Restored Recent DNS Requests in the Client Details window by matching query-log entries against their separate client name and address fields.
- Restored Top Requested Domains and Top Blocked Domains for the selected client.
- Merged configured client IP and MAC identifiers that share the same AdGuard Home client name, allowing the MAC address to appear on a single client record.

### Added
- Added a notification-area context menu with Open Dashboard, Refresh Dashboard and Exit RouterPilot actions.
- Added double-click support on the notification-area icon to restore the dashboard.
- Added a one-time notification explaining that RouterPilot remains active after the dashboard is hidden.

### Changed
- Closing the dashboard with the X now hides it to the notification area instead of exiting.
- Minimising the dashboard now hides it to the notification area.
- Updated application, assembly and file versions to 1.3.1.

## v1.3 — UI polish and historical changelog

### Fixed
- Restored vertical scrolling on Analytics.
- Prevented the DNS Query History chart and ranking panels from being clipped.
- Improved Top Clients rendering when a friendly name and IP address are both present.

### Added
- Added a tasteful Support Development section linked to GitHub Sponsors.
- Added a repository Sponsor button through `.github/FUNDING.yml`.
- Added GitHub Sponsors information and badge to the README.
- Added Credits & Acknowledgements for GL.iNet, AdGuard Home, Microsoft and direct open-source dependencies.
- Added GitHub, documentation, issue-reporting and local licence actions.
- Added LICENSE and THIRD_PARTY_NOTICES.txt to release output.

### Changed
- Top Clients now displays friendly names and addresses on separate lines.
- Clients opens sorted by **Blocked queries**, **Descending**.
- Restyled Logs with a cleaner search area, alternating rows, hover states,
  improved spacing and allowed/blocked status badges.
- Rebuilt the changelog from the complete GitHub commit history.

## v1.2 — Support diagnostics and client activity recovery

### Added
- Support area with About, Diagnostics, System, Logs and Changelog tabs.
- Redacted router and AdGuard Home diagnostics.
- One-click query-log repair while preserving retention and privacy settings.
- Copy and ZIP export of diagnostic information.
- Windows, .NET, architecture, memory and configured-router information.
- In-session support logging and manual Clients refresh.

### Fixed
- Restored per-client query totals by merging `/control/stats` `top_clients`.
- Added explicit unavailable states when query logging is disabled.
- Preserved query-log data as the source for blocked counts and last-seen times.

## 2026-07-28 — Logs, protection and layout stabilisation

### Added
- Live AdGuard Home log restoration and improved Protection status updates.
- Refined Protection management controls and user feedback.

### Fixed
- Multiple live-log polling and refresh regressions.
- Analytics scrolling, chart sizing and ranking layout regressions.
- Blocked Services spacing and final layout issues.
- Newest query-log page retrieval.
- Missing Protection API paths in `RouterManager`.
- Cumulative runtime changelog loading.
- General view, analytics and log defects.

## 2026-07-27 — Search, intelligence and application-wide polish

### Added
- Global search and domain-monitoring tools.
- Client intelligence, details, favourites, manufacturer and device-type enrichment.
- Client sorting and immediate sort refresh.
- Reliable live log filters and polling.
- Complete AdGuard Home protection-management suite.
- Dedicated Protection view and navigation.
- About page, branding and application polish.
- Improved startup flow and router storage-health parsing.
- Blocked Services management and dashboard integration.

### Changed
- Refined Network resource cards and analytics health presentation.
- Populated Network page data.
- Improved Settings, client details and dashboard presentation.
- Moved AdGuard protection controls into their dedicated view.

### Fixed
- Dashboard protection state and health colours.
- Analytics ranked-item compatibility.
- Overview, Logs and Analytics layout issues.
- Generated selected-sort property handling.
- Client sorting responsiveness.
- Live log polling reliability.

## 2026-07-26 — Clients, logs and primary navigation

### Added
- Live AdGuard Home DNS query-log viewer.
- Live AdGuard client statistics.
- Clients model, view model, retrieval and complete page UI.
- Settings page and settings navigation.
- Logs page and navigation.
- Clients page navigation.
- Network page navigation.
- Overview and Analytics navigation.
- Analytics view and restored query-history chart.
- Initial README project documentation.

### Changed
- Consolidated the Clients implementation through the main branch merge.

## 2026-07-25 — Dashboard and analytics foundations

### Added
- Dashboard navigation shell.
- Dashboard header actions.
- LiveCharts query-history binding.
- Query-history parsing from AdGuard Home.
- Early graph implementations.
- Router RPC hash authentication.
- Initial dashboard statistics and data flow.

## 2026-07-24 — Router and AdGuard connectivity

### Added
- Working GL.iNet router API access.
- Working SSH connection and dashboard integration.
- Settings-aware connection recovery.
- AdGuard Home API connectivity.
- Progressive RPC hash-authentication support.

## 2026-07-23 — Application shell

### Added
- Successful router login page.
- Working Windows tray application.

## 2026-07-22 — Project creation

### Added
- Initial WPF project.
- Base project files.
- Repository attributes and ignore rules.
