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

SSC modes are explicit compatibility policy: `Inherit` uses the shared account
character record, `CopyOnEnter` starts from that record but resolves disconnect or
failure conflicts by the newest committed revision, `Isolated` keys state by account
and `WorldId`, and `Disabled` leaves character authority to the client. The descriptor
is authoritative even while the complete SSC product store remains part of the
persistence phase; a backend must reject a mode it cannot provide rather than silently
select another one.

## Lifecycle and commands

The authorized `sandbox` command supports:

```text
sandbox list
sandbox status <id>
sandbox create <id> <name> <template.wld> <temporary|persistent|external> <capacity> [port]
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

Temporary backends stop after their idle TTL. Persistent instance files are retained;
controller shutdown stops processes but does not remove world data. Rolling update
restarts desired-running worlds one at a time and stops at the first failed readiness
check.

## Routing and security

Transfer admission checks target readiness, capacity and access policy before issuing a
30-second HMAC ticket bound to account, origin, target, nonce and ticket ID. Acceptance
is target-bound and single-use; replay, expiry, wrong target and backend loss fall back
to the origin. Shared identity/bans remain server-wide. SSC and world-local region/rule
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

Run `dotnet run --project benchmarks/Vega.Sandbox.Benchmarks -c Release -- 32 4` for
the process-control regression harness. On the x64 .NET 10.0.11 development host, four
fake ready backends with 32 MiB template images measured:

| Workload | Result |
| --- | ---: |
| atomic materialization | 113.2 ms/world |
| signed process readiness | 269.8 ms/process |
| sampled four-world Operations snapshot | 206 us/snapshot |
| graceful process shutdown | 23.3 ms/process |

The harness proves control-plane costs and process semantics; it deliberately does not
pretend to measure Terraria ticks, NPC load or player transfer latency. A separate
published-layout smoke run used a generated 4200x1200 Terraria 1.4.5.7 world: the real
supervised backend reached `WorldReady` in 3.804 seconds (`Terraria world load` 2.062
seconds), listened on its assigned port, reported 536 MiB working set through
`sandbox status`, and completed the graceful stop handshake. Before raising
the default limits, run real `.wld` cold/warm starts and soak tests on the deployment
host, keeping tick p95/p99, GC pauses, disk I/O and working set within the configured
budgets. Snapshots are sampled and bounded; there is no per-tick IPC or world
serialization. Single-process multi-world virtualization remains a deferred research
backend and is not a compatibility promise.
