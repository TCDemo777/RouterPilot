# RouterPilot VPN Mutation Harness

Developer-only validation for the single low-risk `lan_enabled` Tailscale
setting. The project is not referenced by the installer or production UI.

The runtime path loads the active RouterPilot profile through
`RouterManagerProvider`, confirms a GL-MT6000/Flint 2 identity, reads
`tailscale.get_config`, requires the `result.settings` response shape, and
requires the exact confirmation text `VALIDATE` before writing. It sends the
complete settings object directly as the `tailscale.set_config` parameters, reads back the
temporary value, restores the original object in a `finally` path, and reads
back the original value again.

The runtime result remains **AWAITING LOCAL VALIDATION** until a developer
runs it against the configured router. Unit tests use fakes and never contact
a router.

Run self-tests with:

```powershell
dotnet run --project .\RouterPilot.VpnMutationHarness -c Release -- --self-test
```

Run the guarded live validation from Visual Studio by selecting
`RouterPilot.VpnMutationHarness` as the startup project. The harness will
abort if no profile, identity, response schema, or generation check is valid.
