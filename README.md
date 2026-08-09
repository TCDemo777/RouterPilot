# RouterPilot

[![Release](https://img.shields.io/github/v/release/TCDemo777/RouterPilot)](https://github.com/TCDemo777/RouterPilot/releases)
[![Build](https://github.com/TCDemo777/RouterPilot/actions/workflows/build.yml/badge.svg)](https://github.com/TCDemo777/RouterPilot/actions)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Companion for GL.iNet Routers & AdGuard Home

## Features

- Live router, AdGuard Home, network and storage status
- AdGuard protection controls, filtering options and blocked-service management
- Scheduled AdGuard blocked-service controls, including paired allowed-time windows
- DNS analytics, query history, live logs and client details
- Wi-Fi network and connected-client monitoring
- GL.iNet main, Guest and IoT network awareness
- Favourite clients, persistent device history and connection activity
- Live and historical WAN throughput, DNS, CPU and memory charts
- Event Timeline with persistent activity history, local filtering/search and safe export
- Persistent Notification Centre with router, AdGuard and new-device events
- Configurable Windows notifications, Notification Centre delivery and quiet hours
- Maintenance Centre with safe router actions, shared action history and diagnostics
- Portable `.rpb` Backup & Restore with manifest validation and pre-restore backups
- SSH host-key verification and HTTPS certificate trust-on-first-use for router connections
- Router diagnostics, safe diagnostic export, ping, traceroute and DNS lookup tools
- Automatic GitHub release update checks
- Secure password storage using Windows user-scoped encryption
- Light, dark and system theme support
- Notification-area integration with close-to-tray behaviour

## What's new in 1.9.0

RouterPilot v1.9.0 improves everyday visibility and management without changing router configuration automatically.

- Added a persistent Event Timeline with filtering, search, date filtering, read state and safe CSV/JSON/text export
- Added firmware update awareness, background checks after connection, installed/latest version presentation and deduplicated update notifications
- Added Router Health explanations and Internet Quality using existing router and connectivity data
- Added Overview Quick Actions, client context menus and Maintenance overflow actions that reuse established services
- Redesigned the global application header with labelled Router, Internet and AdGuard Home states and smart refresh feedback
- Clarified the distinction between LuCI snapshot and installed GL.iNet router firmware, and improved diagnostics with separate Run Diagnostics and Backup Diagnostics actions
- Standardised major page sections, spacing and page scrollbar alignment for a more cohesive desktop experience

## Security

RouterPilot includes SSH host-key verification, HTTPS certificate trust-on-first-use, Windows DPAPI-protected stored credentials, diagnostic redaction, hardened update URL handling and validated backup/restore archives. See [SECURITY.md](SECURITY.md) and [SECURITY-AUDIT-v1.8.1.md](SECURITY-AUDIT-v1.8.1.md) for the security model and documented compatibility considerations.

The public repository is [TCDemo777/RouterPilot](https://github.com/TCDemo777/RouterPilot). RouterPilot now uses `%LocalAppData%\RouterPilot`; on first startup it safely copies supported legacy files from `%LocalAppData%\AdGuardTray` without changing or deleting the legacy folder.

## Requirements

- Windows 10 or Windows 11
- A supported GL.iNet router reachable over the local network
- SSH access enabled on the router
- AdGuard Home installed on the router for DNS filtering, query activity and protection controls
- .NET 9 Desktop Runtime when using a framework-dependent build

## Getting started

1. Download the latest release.
2. Launch RouterPilot.
3. Enter the router IP address or hostname, SSH username and password.
4. Keep **Remember password securely** enabled for automatic startup.
5. Open the dashboard from the notification-area icon.

User settings are stored under `%LocalAppData%\RouterPilot`. Passwords are protected for the current Windows user. Existing supported settings, notification, client-profile and AdGuard schedule files are copied automatically from `%LocalAppData%\AdGuardTray` when no RouterPilot replacement exists.

Release assets are published as `RouterPilot-1.9.0-x64.msi` and `RouterPilot-1.9.0-win-x64.zip`.

When upgrading from v1.8.0, install the MSI or replace the portable application files. Existing `%LocalAppData%\RouterPilot` data remains in place. Backup files use the portable `.rpb` format and can be created or restored from Maintenance. `.rpb` archives are integrity-checked but not encrypted; store them securely.

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
