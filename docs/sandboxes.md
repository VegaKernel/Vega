# Isolated sandbox worlds

Vega runs each active sandbox as a supervised `Vega.Server` process. Terraria globals,
the game thread, entities and world mutation remain process-local; the primary process
only manages descriptors, files, lifecycle and sampled immutable status. A backend
never starts another control plane.

## Configuration and storage

`config/sandboxes.json` defines admission and failure budgets. The defaults allow eight
active backends, serialize starts, require signed readiness within 60 seconds, consider
a heartbeat stale after 15 seconds, allow 15 seconds for graceful shutdown, and cap
automatic crash restarts at three with five-second backoff. CPU, working-set and world
image sizes have explicit limits. Temporary worlds default to a 30-minute idle TTL.

Descriptors are atomically stored in `data/sandboxes/catalog.json`. The generated
256-bit `data/sandboxes/control.key` authenticates backend heartbeats and transfer
tickets and must be protected as a secret. Managed instances live at
`data/sandboxes/instances/<WorldId>/world.wld`. Creation and reset copy an immutable
`.wld` template to a staging file, flush it, and atomically promote it; the template is
never modified. A failed reset retains the previous instance and its `.previous`
backup. `external` worlds are never reset or deleted by Vega.

Descriptors record `WorldId`, display name, source and instance paths, temporary,
persistent or external ownership, capacity, public/permission/allow-list access, idle
TTL, SSC policy, enabled bundled modules, listener port, revision and template hash.
An empty enabled-module set means all bundled modules. Backend command and region
contexts carry the sandbox `WorldId`; accounts, bans and baseline authorization use the
same configured database, while regions and module policy remain world-scoped.

Sandbox transfer SSC modes are explicit. `Inherit` uses the shared primary account scope,
`CopyOnEnter` seeds a world-local copy and preserves the primary baseline on return,
`Isolated` keeps a world-local character scope, and `Disabled` leaves character authority
to the client. A cross-process file-locked store provides revisions and transfer leases;
Terraria capture/apply persists inventory, armor/dyes, banks, trash, loadouts, life/mana,
spawn, appearance and permanent character flags. Conflicting revisions fail closed rather
than overwriting newer state. Periodic and transfer captures are bound to the original
connection session. A final capture is taken synchronously before the native slot reset;
if that hook cannot capture, the last persisted checkpoint remains unchanged. Normal primary-world
login/initial character provisioning and complete live-client SSC authority are still pending.

The `config/primary-character-persistence.json` option (`enabled: false` by default) restores and saves authenticated primary sessions. Legacy persistence without `authoritative` uses `data/primary-characters.json`; opt-in authority sets the native SSC flag, keeps a blank server-owned pending character, checks readiness before gameplay, and uses the shared `data/sandboxes/ssc.json` primary scope. Non-disabled sandbox SSC modes set the native flag as well. Initial data is server-created rather than implicitly imported from the client inventory. Use the [offline migration command](migration.md) before switching from legacy primary state. The server-side lifecycle and synthetic migration are tested; stock-client login, restore and cross-world transfer remain qualification gates.

## Lifecycle and commands

The authorized `sandbox` command supports:

```text
sandbox list
sandbox status <id>
sandbox create <id> <name> <template.wld> <temporary|persistent|external> <capacity> [disabled|inherit|copyonenter|isolated] [port]
sandbox create-schematic <id> <name> <base.wld> <file.trschem> <x> <y> <temporary|persistent> <capacity> [ssc-mode] [port]
sandbox start|stop|restart <id>
sandbox reset <id> [replacement.wld] confirm
sandbox destroy <id> confirm
sandbox rolling-update confirm
worldtransfer <exact-player> <target-world>
```

Create/start/stop retries are idempotent. Reset and destroy are explicit, confirmed
operations. Shutdown first submits Vega's normal `shutdown` command and force-kills only
after the configured deadline. Readiness and two-second heartbeats are HMAC signed and
bound to `WorldId`, process, instance, state and version. Stale heartbeats, process
exits, resource pressure and version mismatches are degraded/failed states, not silent
reroutes. Parent restart adopts a live signed PID; orphan instance directories are
reported and preserved for manual recovery.

`create-schematic` is a one-shot creation action for a new managed world ID,
not an in-place edit or an idempotent retry on an existing ID. A portable
schematic is a region, not a complete `.wld`; provide a compatible base world
and destination tile coordinates with no existing registered objects or active
entities. The supervisor runs a separate native child in a fresh template
directory, with only a private loopback listener, random password and no
ordinary Vega modules or database. It copies the base, then uses the same
validated idle-world native placement and saves the staged copy. Only a
successful child exit and native save completion marker permit descriptor and
instance publication. Failed children and uncommitted instance/template files
are cleaned up after the child exits; if a failed child cannot be terminated,
its incomplete directory is retained for recovery. Existing world IDs and
orphaned instance directories are refused.

Generated native `.wld` templates are retained under
`data/sandboxes/templates/<id>-<unique-id>/world.wld` as the immutable source of
the new managed instance. Normal `start` loads the instance without replaying
the schematic. Normal `reset <id> confirm` copies the generated template, not
the original base. The source base and `.trschem` file are unchanged. Destroy
removes only the instance; generated templates remain available for reset of
other instances or operator recovery. Native `.wld` saves do not persist
dropped world items, so `create-schematic` refuses such input; they can be
materialized separately into a ready idle sandbox. See [schematics](schematics.md)
for supported object types and placement constraints.

Temporary backends stop after their idle TTL. Persistent instance files are retained;
controller shutdown stops processes but does not remove world data. Rolling update
restarts desired-running worlds one at a time and stops at the first failed readiness
check.

## Routing and security

Transfer admission checks target readiness, capacity and access policy before issuing a
30-second HMAC ticket bound to account, origin, target, nonce and ticket ID. Because
Dimensions packet 67/type 3 carries only host and port, Vega does not expose the ticket
to the client. The source instead writes a short-lived shared rendezvous bound to the
trusted forwarded client IP and exact bounded opaque Terraria UUID; the receiving sandbox must atomically
consume that rendezvous after trusted type-0 forwarding and packet 68. Acceptance is
target-bound and single-use; replay, expiry, wrong target and backend loss fail closed. Shared identity/bans remain server-wide. SSC and world-local region/rule
policy are named in the descriptor rather than inferred from the process.

Dimensions compatibility is disabled by default in `config/security.json`. When
enabled, packet 67/type 0 forwarding is accepted only from an exact
`dimensionsTrustedProxies` IP. Vega preserves both the transport peer and effective
client IP, consumes the metadata packet before Terraria, and rejects spoofing or
malformed addresses. `worldtransfer` sends Dimensions packet 67/type 3 only when the
player connection has trusted-forwarder provenance; a direct client is never told to
switch through this privileged path.

## Operations and capacity baseline

Operations/TUI shows every descriptor and lifecycle state together with endpoint, PID,
players, uptime timestamps, TPS, CPU, working set, instance bytes, restart count,
template revision and TTL. Backend stdout/stderr is copied into bounded parent logs and
each backend writes a separate JSONL diagnostic under `logs/sandboxes/<WorldId>/`.

Each backend launches in `data/sandboxes/instances/<WorldId>/runtime/` with a private `saves/` directory. Mutable plugin configuration/data, chest templates, warps and restrictions live in that runtime's `data/` directory. The executable and read-only deployment config remain shared; account/ban database and authenticated transfer state are intentionally shared control-plane services. Native socket activation/close owns connection metadata lifetime and the dedicated stopping hook withdraws readiness. The native console input encoding is set explicitly for graceful Windows/Linux backend shutdown. Sandbox TCP listeners bind to `127.0.0.1` by default (`backendBindAddress`); change the address only when the trusted proxy deployment requires it.

Whitelist and bans are deployment-wide, including already running backends. The sole whitelist is `data/whitelist.json` next to `data/whitelist.json.lock` at deployment root, not the worker runtime directory. The store serializes updates across processes, and new admissions read the latest committed policy. Existing clients are not kicked merely because the whitelist changes. Active bans use the shared account/ban database and periodic session-bound enforcement. Regions, build protection, warps, restrictions and plugin state remain world-local.

Run `dotnet run --project benchmarks/Vega.Sandbox.Benchmarks -c Release -- 32 4` for
the process-control regression harness. On the x64 .NET 10.0.11 development host, four
fake ready backends with 32 MiB template images measured:

| Workload | Result |
| --- | ---: |
| atomic materialization | 113.2 ms/world |
| signed process readiness | 269.8 ms/process |
| sampled four-world Operations snapshot | 206 us/snapshot |
| graceful process shutdown | 23.3 ms/process |

The harness is the automated control-plane regression gate. A separate published-layout
smoke run used a generated 4200x1200 Terraria 1.4.5.7 world: the real supervised backend
reached `WorldReady` in 3.804 seconds (`Terraria world load` 2.062 seconds), listened on
its assigned port, reported 536 MiB working set through `sandbox status`, and completed
the graceful stop handshake. CI also covers simultaneous-start admission, crash-loop
budgeting, full capacity, low disk, control-plane restart reconciliation and backend loss
while routing. Real-player/NPC soak remains deployment qualification before increasing
budgets; it is not simulated by the implementation benchmark. Snapshots are sampled and
bounded, with no per-tick IPC or world serialization. Single-process multi-world
virtualization remains a deferred research backend and is not a compatibility promise.
