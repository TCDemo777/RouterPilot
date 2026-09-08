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
- Applications → Plug-ins package management with Installed, Available and Updates inventory, search/filtering, a Selected Package inspector, manual package-index refresh, and guarded package actions
- GL.iNet VPN management with live tunnel status, diagnostics and local schedules while RouterPilot is running
- Event Timeline, Notification Centre, configurable Windows notifications and quiet hours
- Maintenance hub with separate Router Firmware, AdGuard Home and Tailscale maintenance, diagnostics, safe network-snapshot export and portable `.rpb` backup/restore
- Secure password storage, SSH host-key verification, HTTPS certificate trust-on-first-use and diagnostic redaction
- Light, dark and system themes plus notification-area close-to-tray behaviour

## What's new in RouterPilot 2.3.2

RouterPilot 2.3.2 improves package management in **Applications → Plug-ins**. [Read the full release notes](https://github.com/TCDemo777/RouterPilot/releases/tag/v2.3.2).

- Browse Installed, Available and Updates package inventory, with search and filtering.
- Review package version, architecture, status and available dependency information in the Selected Package inspector.
- Use guarded Install, Uninstall and Update actions, with dependency-aware uninstall protection and protection for critical GL.iNet/OpenWrt system packages.
- Refresh package indexes manually when needed; RouterPilot does not provide an Upgrade All action.
- Eligible protected packages can use the advanced Force Update confirmation flow. Force Update overrides RouterPilot's action policy only and never uses dangerous `opkg --force-*` flags.

Package changes can affect router stability. Review package details carefully and use trusted package sources; RouterPilot may protect critical packages from mutation.

## Maintenance community tools

Maintenance can check the installed and official latest-stable versions of AdGuard Home and Tailscale. Optional update and Tailscale firmware-binary restore workflows open a visible SSH terminal and copy a reviewed command for the user to run interactively; RouterPilot never treats opening that terminal as a successful update.

The AdGuard Home and GL.iNet Tailscale updater integrations use independent community tools by [Admon](https://admon.me). They are not maintained by RouterPilot, GL.iNet, AdGuard, or Tailscale. Both updater projects are MIT licensed; RouterPilot remains GPL-3.0-only. See [THIRD_PARTY_NOTICES.txt](RouterPilot/THIRD_PARTY_NOTICES.txt) for attribution and licensing details.

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

## Installing and getting started

1. Download the latest release from [GitHub Releases](https://github.com/TCDemo777/RouterPilot/releases/latest).
2. Run the Windows x64 MSI, or extract the portable Windows x64 ZIP.
3. Launch RouterPilot and enter the router IP address or hostname, SSH username and password.
4. Keep **Remember password securely** enabled for automatic startup if desired.
5. Open the dashboard from the notification-area icon.

User settings are stored under `%LocalAppData%\RouterPilot`. Passwords are protected for the current Windows user. Existing supported settings, notification, client-profile and AdGuard schedule files are copied automatically from `%LocalAppData%\AdGuardTray` when no RouterPilot replacement exists.

Release assets are published as `RouterPilot-2.3.2-x64.msi` and `RouterPilot-v2.3.2-win-x64.zip`.

## Upgrading

Install the latest MSI over an existing RouterPilot installation, or replace the files in a portable installation. Existing `%LocalAppData%\RouterPilot` settings, profiles and supported application data remain in place.

## Compatibility and backups

Available telemetry and controls vary by router model, GL.iNet firmware, OpenWrt environment and enabled router services. RouterPilot reports supported, unsupported, unavailable and unknown capabilities rather than fabricating values.

RouterPilot backup files use the portable `.rpb` format and can be created or restored from Maintenance. Archives are integrity-checked but are not encrypted, so store them securely.

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

RouterPilot v2.3.2 is released under the GNU General Public License v3.0 only (GPL-3.0-only). Previously distributed versions remain available under the licence terms under which they were originally distributed. See [LICENSE](LICENSE) and [THIRD_PARTY_NOTICES.txt](RouterPilot/THIRD_PARTY_NOTICES.txt) for details.
