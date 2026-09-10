# RouterPilot v2.4.2

RouterPilot v2.4.2 is a maintenance and usability release improving AdGuard Home recovery, DNS visibility, Protection state handling and several interface areas.

## AdGuard Home & Protection

- Improved AdGuard Home connection recovery after Windows sleep/resume.
- Protection Refresh All can establish a fresh connection after stale post-resume state once connectivity is available.
- Fixed timed Protection actions remaining stuck in Pending after an operation completed.
- Protection action availability is reconciled with the current state after timed operations.

## DNS Activity

- Replaced the redundant Search summary tile with AdGuard Home Average processing time.
- The value comes from the existing authoritative AdGuard Home statistics data rather than visible DNS Activity rows.
- Simplified the card subtitle to "AdGuard Home DNS".

## Router Health & Notifications

- Fixed Router Health View actions for supported attention items.
- Welcome/update notifications marked as read are remembered for the current RouterPilot version rather than appearing on every startup.

## Clients, Analytics & Applications

- Recent traffic samples now show the five most recent entries.
- Client Details recent DNS activity now shows the ten most recent entries.
- Fixed Client Details text clipping.
- Centered Application Traffic details over the RouterPilot window.

## Interface

- Improved dark-mode text contrast across affected Network, Applications and Clients areas.
- Fixed clipping of the Tailscale summary button.

## Downloads

- `RouterPilot-2.4.2-x64.msi`
- `RouterPilot-v2.4.2-win-x64.zip`
- `SHA256SUMS.txt`

## Requirements

- Windows 10 build 19041 or later, or Windows 11
- A supported GL.iNet router
- SSH access for router functionality that requires it
- AdGuard Home for AdGuard-specific features

## Licence

RouterPilot v2.4.2 is released under the GNU General Public License v3.0 only (GPL-3.0-only).

Community updater projects remain independent community projects and retain their own upstream licences.
