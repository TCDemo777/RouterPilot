# Router profile groundwork (v2.1)

RouterPilot persists `RouterProfile` records in the existing application
settings store. Each profile has a stable generated identifier, and
`ActiveRouterProfileId` identifies the one profile that supplies the current
router connection configuration. Passwords and private-key passphrases remain
DPAPI-protected values; private-key contents are never stored by RouterPilot.

This groundwork is deliberately configuration-only. It does not include a
profile-management UI, router switching, simultaneous monitoring, or any
background connections to inactive profiles. The legacy single-router fields
remain as a compatibility projection of the active profile while older
settings consumers are progressively moved behind `IActiveRouterContext`.

`RouterManagerProvider` is the active transport boundary and resolves its RPC,
SSH, and AdGuard connection settings from `IActiveRouterContext`. Future
switching must be coordinated as one lifecycle operation: cancel or invalidate
work for the old session, dispose/reset router-scoped state, persist the new
active profile, then initialize the normal services for that profile.

The future switch lifecycle must reset or cancel state owned by Dashboard and
connection status, Clients and client details, Data Statistics, Network
Health, VPN and its WebSocket state, DHCP, Port Forwarding, Firmware,
Protection/AdGuard/DNS activity, Maintenance, refresh timers, and pending
async refreshes. `IActiveRouterContext.Version` is reserved as the generation
boundary that future operations can capture before applying results.
