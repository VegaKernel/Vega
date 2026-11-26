# Operations API and Terminal UI

Vega exposes operational state through immutable contracts in `Vega.Contracts` and implementations in `Vega.Operations`. `Vega.TerminalUI` is an optional consumer of that API. Neither project reads or mutates Terraria globals; `Vega.Terraria` samples world and tick state on the game thread and publishes an immutable snapshot.

## Runtime modes

Select the operations mode with `--vega-ui`:

```text
--vega-ui=auto       Use TUI for an interactive terminal, otherwise plain mode.
--vega-ui=tui        Request the Terminal.Gui dashboard.
--vega-ui=plain      Keep normal console input/output for scripts and SSH.
--vega-ui=headless   Start without an interactive UI or operations stdout sink.
```

The Vega option is removed before arguments are passed to Terraria. TUI initialization is best-effort on a background thread. A missing or incompatible terminal never blocks server startup; structured file logging and normal server execution continue.

## Dashboard

The TUI uses separate high-contrast tiled windows instead of repainting one large text document. Its default desktop contains:

- **Server / world**: lifecycle, uptime, world/readiness, listener port and player count;
- **Performance**: bounded TPS and process CPU history; the server window also shows rolling-minute TPS, lifetime min/max and tick-duration p50/p95/p99;
- **Memory**: bounded working-set and managed-heap history, plus current GC data;
- **Network**: inbound/outbound rates and cumulative packet/byte counters;
- **Players**: immutable `PlayerSnapshot` entries;
- **Regions**: a bounded immutable world/name/bounds/priority/owner/ACL/revision projection (hidden by default and available through `:show regions`);
- **Moderation**: bounded active bans and mutes; sensitive IP/UUID/CIDR subjects are redacted outside an explicitly sensitive snapshot (hidden by default and available through `:show moderation`);
- **Modules**: version, load state, dependencies, startup duration and failures;
- **Logs**: recent structured records and degraded-capability warnings.

The default refresh interval is 250 ms. Snapshot capture runs off the UI thread with at most one request in flight, so slow metrics or persistence reads cannot queue up or block keyboard and mouse input. Static labels are updated only when their text changes; graph and log history is bounded. Dragging a shared panel edge grows one tile while shrinking its neighbour, and dragging a title onto another tile swaps their positions instead of leaving overlapping floating windows.

### Window layout

The desktop stays tiled: drag a shared border to grow one window while shrinking its adjacent window or windows, and drag a title over another tile to exchange their places. A rejected resize restores the prior geometry rather than overlapping panels; every tile keeps a usable minimum size. Double-click a title to maximize or restore that tile. The following local palette commands manage the desktop:

```text
:windows                 List available windows and visibility.
:show performance        Show a window.
:hide logs               Hide a window.
:layout save             Persist current positions, sizes and visibility.
:layout reset            Restore and persist the default desktop.
```

Layout is loaded from `config/tui.json`. `refreshIntervalMilliseconds` is clamped to 250–5000 ms, `historySamples` to 20–600, and unsafe window geometry is normalized. A missing or malformed file falls back to the built-in layout and produces a structured warning instead of preventing startup. The file is included in development and published server layouts.

The current network window intentionally exposes only counters that exist in the operations contract. Packet-type, handler-latency and security-action breakdowns will be added with the packet observability work in Phase 13; the TUI does not fabricate them from unavailable data.

The `tps` command (alias `tpsstats`, permission `tickrate.check`) reports the same immutable tick statistics outside the TUI. Below 15 TPS, the operations snapshot carries a critical warning. Tick statistics start at `NetworkReady`, so world-loading stalls do not corrupt the serving lifetime's extrema. Vega intentionally does not expose the legacy plugin's global reset/toggle switches because they could hide diagnostic history or alerts from other operations consumers.

Vega monitors top-level `ServerPlugins/*.dll` deployment changes. Administrative hot reload is disabled by default in `config/modules.json`:

```json
{
  "enabled": false,
  "debounceMilliseconds": 750,
  "unloadTimeoutMilliseconds": 5000
}
```

With the switch off, a DLL change produces a visible `restart required` state. With it on, create/change automatically loads or replaces a conforming plugin, delete unloads it, and rename is handled as an unload/load transition. An assembly is hot-loadable only when it directly exports exactly one public, parameterless `IHotReloadableVegaModule`. Use `plugin list`, `plugin reload <name-or-dll>` or `plugin unload <name-or-dll>` for explicit administration; the permission is `server.plugins.manage`.

An unloadable plugin must register commands through `IHotModuleContext.RegisterCommand` and every hook/resource through `Track` or `RegisterCleanup`. Vega first removes tracked registrations in reverse order so no new callbacks enter the plugin, then calls `StopAsync` to quiesce its own work, calls `DisposeAsync`, requests unload of the collectible context and verifies that it was collected. A static reference or failed cleanup produces `restart required`; Vega does not report a false successful unload. Bundled modules created as `IVegaModule` remain restart-only.

Structured operational events are written independently to the bounded in-memory store, stdout outside headless mode, and `logs/vega-operations.jsonl` when the file sink is available.

JSONL serialization and file writes run on a single background worker. It flushes available records in batches of up to 128, without waiting for a full batch. The file queue holds up to 4,096 records; if full, new records of any level are dropped from the file only, without waiting on the producer. A file write failure disables that sink for the rest of the process. The sink reports overflow or failure to stderr and reports the total dropped or unconfirmed records on shutdown. On orderly shutdown it stops accepting records and waits up to five seconds to drain; a forced exit or stalled disk can leave records unwritten. The in-memory history and console output are separate; this does not change console logging or add file rotation.

Optional TPS-independent world time is configured in `config/world-clock.json`. It is disabled by default. When enabled, the primary Terraria world is corrected from a monotonic per-world clock only on the game thread; drift above `correctionThresholdSeconds` is corrected and broadcast, while drift above `reanchorThresholdSeconds` is treated as an explicit time change. The overview exposes phase, predicted time, drift, correction count and re-anchor count.

## Command palette and logs

Enter a Vega command such as `serverinfo` or `kick <player> <reason>` in the palette to execute it through the normal `ICommandDispatcher` authorization and application boundary. Commands not owned by Vega are queued to Terraria's native server-console parser, so built-in operations such as `save`, `say`, `help` and `exit` remain available without a second thread reading terminal input.

While the TUI owns the terminal, stdout and stderr emitted by Terraria/native commands are captured line-by-line in the bounded Operations log under source `terraria-console` (stderr is Warning). Individual unterminated lines are capped at 8192 characters. Plain mode continues writing directly to the traditional console.

Local palette actions control only the view:

```text
:pause
:follow
:level warning
:level all
:source lifecycle
:find budget
:clear
:windows
:show <overview|performance|memory|network|players|regions|moderation|modules|logs|chat>
:hide <overview|performance|memory|network|players|regions|moderation|modules|logs|chat>
:layout save
:layout reset
```

Log retention is bounded and filtering supports minimum level, source and text. Pausing freezes the displayed immutable snapshot; it never pauses server execution or log ingestion.
# Local scoped REST

An optional primary-world operations listener binds `127.0.0.1` only (`VEGA_REST_PORT`, `VEGA_REST_TOKEN_SHA256`). Clients send a randomly generated bearer token in the Authorization header; the process retains only its SHA-256 fingerprint. `GET /v1/status`, `/v1/players`, `/v1/network`, `/v1/metrics`, `/v1/scheduler`, and `/v1/world` expose bounded immutable snapshots without endpoints or account secrets. Metrics include CPU time, working set and private memory, total allocations, GC collections and pause duration, plus game-thread pending/dropped work, capacity and scheduling budget. A distinct maintenance token (`VEGA_REST_MAINTENANCE_TOKEN_SHA256`) authorizes typed world mutations, never arbitrary commands. `rest status`, `rest listusers`, `rest destroytokens` are permission checked; token revocation lasts until restart. The default is disabled, and sandbox processes do not open this listener. See the [project guide](en/project-guide.md).

| Route | Scope | Contract |
| --- | --- | --- |
| `GET /v1/world` | Read or maintenance | Current world dimensions, spawn, time, moon, weather, mode and hardmode; no world file identity, seed or file paths. |
| `POST /v1/world/save` | Maintenance | Empty body; existing typed native save operation. |
| `POST /v1/world/time` | Maintenance | JSON `{"isDaytime":true,"timeTicks":12000}`; both fields required, native day/night tick range. |
| `POST /v1/world/weather` | Maintenance | JSON containing one or more of `raining` (Boolean), `rainIntensity` (0–1), `windSpeed` (-1–1). |

World changes operate only on the listener's owned ready world and return 204 on
success. JSON mutations require `application/json` with a nonempty, known length
of at most 4096 bytes; unknown or duplicate fields and out-of-range values are
rejected. A read token cannot mutate. World snapshot/body reads have a five-second
deadline; native mutations have a 30-second deadline. Unavailable world or
dispatcher returns 503; cancelled/timed-out work returns 504. The native game
thread remains the mutation authority. Each invoked maintenance action is audited
by world, action and outcome without logging credentials or raw request bodies.
World read/time/weather and audit are exercised by the clean deployment smoke.

`scripts/Measure-LiveVegaServer.ps1` samples readiness, actual connected-player count, p50/p95/p99 tick duration, GC/process/network counters and scheduler depth into a timestamped CSV through the local read API. Supply the read bearer token in `VEGA_REST_READ_TOKEN` in the measurement process. Its `-MinimumPlayers` gate fails qualification if any measured sample falls below the requested connected-player count; loopback transport peers alone do not count as in-game players.
