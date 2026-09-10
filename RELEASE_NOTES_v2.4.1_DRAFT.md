# RouterPilot v2.4.1 — Release notes draft

RouterPilot v2.4.1 is a maintenance release improving VPN connection-state handling and HTTPS certificate-validation diagnostics.

## Fixed

- Fixed stale VPN connection-failure messages remaining after the relevant VPN configuration or authoritative connection state changed.
- Improved HTTPS certificate-validation diagnostics for self-signed trust, changed certificates, certificate validity failures, hostname mismatches and unknown TLS failures.

## Security

- Explicit certificate trust remains required.
- Changed certificates are never silently accepted.
- TLS validation has not been weakened.

## Downloads

The release will provide `RouterPilot-2.4.1-x64.msi` and `RouterPilot-v2.4.1-win-x64.zip`. SHA-256 checksums will be generated from the final release artifacts before publication.
