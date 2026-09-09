# RouterPilot v2.4.0 — Release notes draft

## Highlights

- Choose how RouterPilot names your devices. Keep detected names, prefer router DHCP reservation names, prefer configured AdGuard Home client names, or use a router-first configured-name mode. The selected naming preference is used consistently across supported client views.
- Open the right client directly from DNS Activity or Network Map. Navigation uses the underlying client identity, so duplicate friendly names remain safe.
- Find a clearer Clients toolbar with a compact naming selector, grouped actions and filters, and responsive wrapping for narrower windows.

## Maintenance improvements

- Added dedicated AdGuard Home and Tailscale Maintenance workspaces with installed/latest version information, service status, update checks and guarded update actions.
- Added a built-in interactive Maintenance console for supported maintenance workflows, with visible output and interactive input.
- Added read-only recovery-state information for the optional community updater integrations, including backup and restore preconditions and a guided firmware-binary Tailscale restore workflow where available.
- Added the router's running kernel release to Maintenance System Information and removed duplicate updater controls from Maintenance Overview.

## VPN and reliability

- Configured GL.iNet VPN client profiles now remain visible even when they are inactive or live runtime state is unavailable. RouterPilot also distinguishes no configured profiles from an unavailable profile inventory.
- Fixed a crash that could occur when returning to Clients after Maintenance or other network activity.
- Improved Maintenance console reliability, AdGuard Home Maintenance refreshes during router switching, update availability gating, and Plug-ins dark-theme styling.

## Notes

- Optional AdGuard Home and Tailscale updater integrations are independent community tools by [Admon](https://admon.me). They are never launched automatically; the applicable third-party notices remain included with RouterPilot.
- RouterPilot is licensed under GPL-3.0-only.

## Downloads

The final v2.4.0 release will provide the established Windows x64 MSI and portable ZIP. SHA-256 checksums will be generated from the final approved release artifacts before publication.
