# RouterPilot v2.4.6

RouterPilot 2.4.6 focuses on VPN reliability, PIA server management, safe operational diagnostics and UI polish.

## Highlights

- New PIA Server Management provides Refresh Servers, Apply, Apply & Connect and Regenerate VPN Server for an authoritatively detected Private Internet Access setup.
- Apply uses a verified provider lifecycle: current logical target resolution, one generation, fresh metadata read-back, Primary assignment verification, and a disconnected result.
- Apply & Connect uses that verified Apply lifecycle before the existing Connect path. It does not add a new tunnel-mutation shortcut, retry loop or automatic recovery.
- PIA provider configuration IDs are treated as ephemeral. RouterPilot resolves fresh current configuration metadata rather than assuming a numeric ID is a durable server identity.
- Advanced server management is PIA-specific. Standard compatible VPN controls remain available for other configured providers.

## Reliability and diagnostics

- Improved WireGuard operation reconciliation, stale/cancellation protection and the existing incomplete-handshake recovery experience.
- Devlog now provides safe operational visibility for router/authentication, RPC activity, Dashboard, VPN and PIA workflows, including timings and outcomes.
- Repetitive RouterManager reuse trace noise was removed; useful RPC dispatch/completion entries remain available.
- Added a DEBUG-only sanitized Capture PIA State snapshot for troubleshooting without exposing credentials, keys, endpoints, addresses or MAC values.

## Interface and privacy

- Fixed Dark and Light theme ComboBox popup, item, hover and selected-state readability for Devlog filters and PIA location selection.
- Devlog and diagnostics deliberately exclude passwords, provider credentials, sessions, tokens, cookies, keys, MAC values, raw RPC requests/responses and DNS query/client data.

## Downloads

- `RouterPilot-2.4.6-x64.msi`
- `RouterPilot-v2.4.6-win-x64.zip`
- `RouterPilot-2.4.6-SHA256.txt`

Verify downloaded artifacts against the SHA-256 checksums published with the release.

## Upgrade note

Install the MSI over an existing installation, or replace the files in a portable installation. Existing RouterPilot settings remain in `%LocalAppData%\RouterPilot`.

## Licence

RouterPilot is released under the GNU General Public License v3.0 only (GPL-3.0-only).
