# RouterPilot v1.8.1 Security Audit

## Scope

This development security review covered secrets and Git history, credential storage, TLS, SSH, AdGuard transport, command execution, logging, diagnostics, Backup & Restore, update security, Windows notifications, dependencies and installer/supply-chain behaviour.

## Security posture

**SAFE TO RELEASE WITH DOCUMENTED RESIDUAL RISK.** No Critical findings and no unresolved High findings were identified in the final review. This is a project-development security review, not an independent third-party audit.

## Findings Matrix

| ID | Final status | Summary |
| --- | --- | --- |
| SEC-001 | MITIGATED | HTTPS certificate TOFU/pinning replaces unconditional acceptance; local/self-signed validation context is disclosed. |
| SEC-002 | ACCEPTED RISK | AdGuard honours configured scheme/port. Stock Flint 2 HTTP compatibility remains unencrypted and is warned. |
| SEC-003 | RESOLVED | SSH host-key SHA-256 TOFU/pinning blocks changed keys until explicitly trusted. |
| SEC-004 | RESOLVED | Raw AdGuard and GL.iNet RPC response bodies are no longer logged. |
| SEC-005 | MITIGATED | Diagnostics redact authentication material and minimise unnecessary client/network data. |
| SEC-006 | RESOLVED | Repository exclusions cover user data, backups, diagnostics, keys and build output. |
| SEC-007 | ACCEPTED RISK | `.rpb` archives are integrity-checked but unencrypted; export warns users. |
| SEC-008 | ACCEPTED RISK | Transitive System.Drawing.Common 4.7.0 advisory remains reported; its advisory applies to macOS/Linux, while RouterPilot targets Windows. |
| SEC-009 | RESOLVED | Update/release URLs require HTTPS and trusted GitHub hosts. |
| SEC-010 | STILL OPEN (Informational) | Windows notifications have no command or deep-link activation surface by design. |
| SEC-011 | RESOLVED | Maintenance operations remain predefined and allow-listed; no arbitrary shell UI route was found. |

## Resolved / Mitigated Controls

- Windows DPAPI CurrentUser protects persisted router credentials.
- Router HTTPS certificate and SSH host-key SHA-256 trust are stored per endpoint and require explicit first-use/replacement approval.
- Diagnostics and lifecycle logging use redaction and safe failure categories.
- Update URLs are restricted to trusted HTTPS GitHub hosts.
- Backup archives validate manifest data, strict filenames, hashes and archive safety limits before staged restore.
- Maintenance commands remain allow-listed.

## Accepted Residual Risks

- Stock Flint 2 AdGuard Home compatibility can require unencrypted local HTTP on port 3000; RouterPilot displays a warning and supports configured HTTPS.
- `.rpb` archives are not encrypted. DPAPI-protected password blobs remain protected, but metadata is readable to someone with the archive.
- The dependency scanner reports transitive System.Drawing.Common 4.7.0 through CommunityToolkit.WinUI.Notifications 7.1.2. The advisory is documented as applicable to macOS/Linux and is not applicable to RouterPilot's Windows target.

## Release Decision

**SAFE TO RELEASE WITH DOCUMENTED RESIDUAL RISK.**
