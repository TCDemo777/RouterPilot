# RouterPilot VPN Mutation Harness

Developer-only validation for the proven `enabled`, `lan_enabled` and `wan_enabled` Tailscale
setting. The project is not referenced by the installer or production UI.

The runtime path loads the active RouterPilot profile through
`RouterManagerProvider`, confirms a GL-MT6000/Flint 2 identity, reads
`tailscale.get_config`, which returns the configuration directly under
`result`, and
requires the exact confirmation text `VALIDATE` before writing. It sends the
complete supported settings object directly as the `tailscale.set_config` parameters
(`enabled`, `lan_enabled`, `wan_enabled`, and `exit_node_ip` only when reported), reads back the
temporary value, restores the original object in a `finally` path, and reads
back the original value again.

Both fields are proven on the live Flint 2; the harness remains developer-only
for future contract checks. Unit tests use fakes and never contact a router.

Run self-tests with:

```powershell
dotnet run --project .\RouterPilot.VpnMutationHarness -c Release -- --self-test
```

Run the read-only GL.iNet package/plugin discovery probe with:

```powershell
dotnet run --project .\RouterPilot.VpnMutationHarness -c Release -- --plugins
```

The probe reports sanitized package-manager counts, feed/index presence, the
GL.iNet `/usr/libexec/opkg-call` frontend evidence, and package-manager
storage shape. It never runs `opkg update`, install, remove, or upgrade.

Run the guarded live validation from Visual Studio by selecting
`RouterPilot.VpnMutationHarness` as the startup project. The harness will
abort if no profile, identity, response schema, or generation check is valid.
