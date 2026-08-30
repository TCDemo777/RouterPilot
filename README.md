# RouterPilot

[![Release](https://img.shields.io/github/v/release/TCDemo777/RouterPilot)](https://github.com/TCDemo777/RouterPilot/releases)
[![Build](https://github.com/TCDemo777/RouterPilot/actions/workflows/build.yml/badge.svg)](https://github.com/TCDemo777/RouterPilot/actions)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

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

## What's new in RouterPilot 2.0

RouterPilot 2.0 adds deeper network visibility and management while keeping router configuration changes user initiated.

- Added Network Health, a compact read-only view of current router, WAN, DNS protection, VPN, Wi-Fi, DHCP, resources, firmware and Data Statistics state.
- Added Data Statistics / DPI application analytics, per-application device traffic and supported application protection controls.
- Added production DHCP reservation and port-forwarding management, with client-aware navigation and validation.
- Added live GL.iNet VPN status, management, diagnostics and local VPN schedules.
- Added Known Devices, favourites, connection history, Internet Speed Test history, public-IP visibility and reliability insights.
- Expanded Protection with Insights, filter and rewrite management, blocked-service controls and direct DNS-activity navigation.
- Improved freshness, loading, unavailable and recovery states across dashboard, clients, Network, VPN, Protection and Analytics.
- Made the Dashboard more adaptable with configurable cards and a compact five-action Quick Actions row.

RouterPilot v2.1.1 fixes missing magnifying-glass icons across several search fields while retaining the v2.1.0 saved-router management, reliable active-router switching, profile-aware settings, and focused UI polish.

## Security

RouterPilot includes SSH host-key verification, HTTPS certificate trust-on-first-use, Windows DPAPI-protected stored credentials, diagnostic redaction, hardened update URL handling and validated backup/restore archives. See [SECURITY.md](SECURITY.md) and [SECURITY-AUDIT-v1.8.1.md](SECURITY-AUDIT-v1.8.1.md) for the security model and documented compatibility considerations.

The public repository is [TCDemo777/RouterPilot](https://github.com/TCDemo777/RouterPilot). RouterPilot now uses `%LocalAppData%\RouterPilot`; on first startup it safely copies supported legacy files from `%LocalAppData%\AdGuardTray` without changing or deleting the legacy folder.

## Requirements

- Windows 10 or Windows 11
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

Release assets will be published as `RouterPilot-2.1.1-x64.msi` and `RouterPilot-2.1.1-win-x64.zip`.

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

## Licence

RouterPilot is released under the MIT Licence. See `LICENSE` and `THIRD_PARTY_NOTICES.txt` for details.
