# RouterPilot Security

## Supported Versions

| Version | Support status |
| --- | --- |
| 1.9.0 | Supported |
| 1.8.1 | Update recommended |
| Older releases | Update recommended |

## Reporting a Vulnerability

Please report vulnerabilities privately where possible. Use GitHub's private vulnerability-reporting feature if it is enabled for this repository; otherwise use the repository's existing maintainer contact channels. Do not open a public issue containing credentials, sensitive diagnostics or exploit details.

## Security Model

- Router passwords are protected with Windows DPAPI for the current Windows user.
- SSH connections use SHA-256 host-key trust-on-first-use and pin the trusted key per router endpoint.
- Router HTTPS connections use SHA-256 certificate trust-on-first-use and pin the trusted certificate per endpoint.
- Router certificate and SSH host-key replacements are never accepted automatically.
- Maintenance operations use predefined, allow-listed router commands.
- Diagnostics use redaction to minimise authentication and unnecessary network/client information.
- Backup archives validate their manifest, filenames and SHA-256 file hashes before restore.
- The update checker does not download or execute release assets automatically.
- Windows notifications contain text only; they do not implement command or deep-link activation.
- Timeline exports contain only the safe, user-visible event fields. They exclude credentials, tokens, cookies, raw responses and diagnostic reports.

## Known / Accepted Risks

1. Stock Flint 2 AdGuard Home compatibility may use `http://<router>:3000`. This traffic is unencrypted on the LAN. RouterPilot warns when this mode is active, supports configured HTTPS, and never silently downgrades HTTPS to HTTP.
2. `.rpb` backups are not encrypted. Passwords remain DPAPI-protected, but backups can contain readable configuration, client, notification and schedule metadata. Store backup files securely.
