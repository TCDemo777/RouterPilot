# RouterPilot

[![Release](https://img.shields.io/github/v/release/TCDemo777/RouterPilot?label=release&sort=semver)](https://github.com/TCDemo777/RouterPilot/releases/latest)
[![Build](https://img.shields.io/badge/build-verified-brightgreen.svg)](https://github.com/TCDemo777/RouterPilot)
[![License](https://img.shields.io/badge/license-GPL--3.0--only-blue.svg)](LICENSE)

Companion for GL.iNet Routers & AdGuard Home

## Features

- Router overview with connection, WAN, public-IP, Wi-Fi, resource, firmware and Network Health status
- Client inventory, favourites, Known Devices, connection history, client details and direct navigation from search, DHCP and DNS activity
- Network management for Wi-Fi, DHCP reservations and port-forwarding rules where supported by the router
- AdGuard Home protection controls, DNS activity, client DNS context, Insights, filters, blocked services and DNS rewrites
- Analytics for live and historical WAN, DNS, CPU and memory data, Internet Speed Test history and internet reliability
- Read-only Data Statistics / DPI application analytics, including per-application device traffic and supported application blocking controls
- GL.iNet VPN management with live tunnel status, diagnostics and local schedules while RouterPilot is running
- Event Timeline, Notification Centre, configurable Windows notifications and quiet hours
- Maintenance actions, diagnostics, firmware awareness, safe network-snapshot export and portable `.rpb` backup/restore
- Secure password storage, SSH host-key verification, HTTPS certificate trust-on-first-use and diagnostic redaction
- Light, dark and system themes plus notification-area close-to-tray behaviour

## What's new in RouterPilot 2.3.0

RouterPilot 2.3.0 expands read-only router intelligence while keeping configuration changes user initiated. [Read the full release notes](https://github.com/TCDemo777/RouterPilot/releases/tag/v2.3.0).

- Expanded Router telemetry for identity, ports, Wi-Fi, Multi-WAN, DNS, performance, temperature and storage.
- Added Network Configuration intelligence covering mode, Guest/IoT, NAT, IGMP, SQM, DPI and traffic processing.
- Improved Wi-Fi, DHCP, port-forwarding, Network Map and Internet Quality views.
- Added richer client identification using router, DHCP, Wi-Fi, mDNS, vendor and AdGuard observations.
- Expanded VPN and read-only Tailscale visibility, including connection state, addresses, version and peers.
- Expanded Protection and AdGuard Home observability with DNS activity, filters, blocklists, blocked services and rewrites.
- Improved Data Statistics, DPI application analytics and traffic-session accumulation with safe counter rebaselining.
- Redesigned Maintenance with Health, snapshots, change history, firmware status, logs, reports and Support tools.
- Added GL.iNet firmware catalog checks and an in-app release-notes viewer while keeping OpenWrt system information separate.
- Added external-storage, Samba/share, NAS, WebDAV and DLNA service visibility where authoritative.
- Reorganized Settings into tabbed, responsive sections while preserving existing settings and persistence.
- Hardened refresh, cancellation, router/profile switching, disconnect/reconnect and Windows sleep/resume recovery.
- Improved semantic status wording, navigation, search, responsive layouts and support/report privacy handling.

## Security

RouterPilot includes SSH host-key verification, HTTPS certificate trust-on-first-use, Windows DPAPI-protected stored credentials, diagnostic redaction, hardened update URL handling and validated backup/restore archives. See [SECURITY.md](SECURITY.md) and [SECURITY-AUDIT-v1.8.1.md](SECURITY-AUDIT-v1.8.1.md) for the security model and documented compatibility considerations.

The public repository is [TCDemo777/RouterPilot](https://github.com/TCDemo777/RouterPilot). RouterPilot now uses `%LocalAppData%\RouterPilot`; on first startup it safely copies supported legacy files from `%LocalAppData%\AdGuardTray` without changing or deleting the legacy folder.

## Requirements

- Windows 10 version 2004 (build 19041) or later, or Windows 11
- A supported GL.iNet router reachable over the local network
- SSH access enabled on the router
- AdGuard Home installed on the router for DNS filtering, query activity and protection controls
- Data Statistics application analytics require a router and firmware that expose the required GL.iNet Data Statistics / DPI interface
- .NET 9 Desktop Runtime when using a framework-dependent build

## Getting started

1. Download the latest release.
2. Launch RouterPilot.
3. Enter the router IP address or hostname, SSH username and password.
4. Keep **Remember password securely** enabled for automatic startup.
5. Open the dashboard from the notification-area icon.

User settings are stored under `%LocalAppData%\RouterPilot`. Passwords are protected for the current Windows user. Existing supported settings, notification, client-profile and AdGuard schedule files are copied automatically from `%LocalAppData%\AdGuardTray` when no RouterPilot replacement exists.

Release assets are published as `RouterPilot-2.3.0-x64.msi` and `RouterPilot-2.3.0-win-x64.zip`.

## Upgrading to 2.0

When upgrading from v1.9.0, install the MSI or replace the portable application files. Existing `%LocalAppData%\RouterPilot` data remains in place. RouterPilot continues to copy supported legacy AdGuardTray data only when a RouterPilot replacement does not already exist.

Data Statistics, VPN, DHCP reservation and port-forwarding capabilities vary by router model, firmware and enabled router services; RouterPilot shows unavailable or unsupported states when the required router interface is not available. Backup files use the portable `.rpb` format and can be created or restored from Maintenance. `.rpb` archives are integrity-checked but not encrypted; store them securely.

## Building from source

```powershell
dotnet restore .\RouterPilot.sln
dotnet build .\RouterPilot.sln -c Release
dotnet build .\RouterPilot\RouterPilot.csproj -c Release
```

The application executable is `RouterPilot.exe`.

## Support and diagnostics

The About page includes system information, redacted diagnostics, support logs and export tools. Please remove any information you do not want to share before attaching diagnostics to an issue.

Report issues through the [GitHub issue tracker](https://github.com/TCDemo777/RouterPilot/issues).

## ❤️ Support Development

RouterPilot is free and open source under the GNU General Public License v3.0 only (GPL-3.0-only).

If you find RouterPilot useful and would like to support its continued development, bug fixes and new features, you can support the project through:

- ❤️ [GitHub Sponsors](https://github.com/sponsors/TCDemo777)
- ☕ [Buy Me a Coffee](https://buymeacoffee.com/tcdemo777)

Support is completely optional. Using RouterPilot, reporting bugs, suggesting improvements and contributing to the project are all greatly appreciated ways to help.

## Licence

RouterPilot v2.3.0 is released under the GNU General Public License v3.0 only (GPL-3.0-only). Previously distributed versions remain available under the licence terms under which they were originally distributed. See [LICENSE](LICENSE) and [THIRD_PARTY_NOTICES.txt](RouterPilot/THIRD_PARTY_NOTICES.txt) for details.
