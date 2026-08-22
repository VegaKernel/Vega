# Vega project guide

[Русская версия](../ru/project-guide.md) · [Repository README](../../README_EN.md) · [Roadmap](../roadmap.md) · [ADRs](../decisions/)

## 1. Project overview

Vega is an independent Terraria server runtime and framework built for .NET 10. The process host uses OTAPI for low-level Terraria integration while Vega owns the server-facing contracts, policies, module lifecycle, observability, and application boundaries.

The project is designed around three rules:

1. Mutable Terraria state stays on the game thread.
2. Core behavior depends on typed contracts and immutable snapshots, not Terraria globals.
3. New abstractions arrive with a working consumer instead of as speculative framework surface.

Vega does not load TShock or TSAPI plugins and does not preserve their API shapes. Historical projects remain useful only as behavioral evidence and regression input.

## 2. Repository map

| Path | Responsibility |
|---|---|
| `src/Vega.Contracts` | Public identifiers, snapshots, events, command, player, world, networking, operations, and cluster contracts |
| `src/Vega.Core` | Server/application behavior independent of Terraria and OTAPI |
| `src/Vega.Terraria` | Terraria/OTAPI hooks, packet translation, player operations, game-thread scheduling, and world integration |
| `src/Vega.Runtime` | Executable host, composition root, module startup, runtime options, and assembly resolution |
| `src/Vega.Operations` | Structured logging, metrics, read models, and safe command actions |
| `src/Vega.TerminalUI` | Optional Terminal.Gui dashboard and command palette |
| `src/Vega.Cluster` | Optional typed inter-server scenarios and the in-memory reference transport |
| `src/Vega.Persistence` | Domain persistence, schema migrations, and SQLite/PostgreSQL/MySQL providers |
| `src/Vega.Modules.Pvp` | PvP authority, projectile attribution, death windows, and per-session state |
| `src/Vega.Modules.CustomItems` | Typed custom-item definitions and targeted Terraria delivery |
| `tests/Vega.Architecture.Tests` | Dependency and ownership guardrails |
| `tests/Vega.Runtime.Tests` | Unit and integration tests for runtime behavior |
| `benchmarks/Vega.WorldImage.Benchmarks` | Reproducible `.wld` versus `.vworld` measurements |
| `docs/decisions` | Architecture Decision Records and their status |

## 3. Runtime architecture

```text
Terraria
   ↓
OTAPI
   ↓
Vega.Runtime ───────→ Vega.TerminalUI
   │                       ↓
   ├──→ Vega.Terraria → Vega.Operations
   ├──→ Vega.Core          ↓
   ├──→ Vega.Cluster   Vega.Contracts
   ├──→ Vega.Persistence   ↑
   └──→ Server modules ────↑
```

`Vega.Contracts` is the shared semantic boundary. `Vega.Core` cannot reference Terraria, OTAPI, TSAPI, or TShockAPI. `Vega.Terraria` owns all conversions between mutable game objects and Vega snapshots or operations. Architecture tests fail when forbidden dependencies cross that boundary.

The runtime composes bundled modules explicitly. `ServerPlugins/` also supports a separate unload-safe contract for opt-in administrative hot replacement; it is disabled by default and never treats ordinary `IVegaModule` assemblies as unloadable.

## 4. Prerequisites

- .NET 10 SDK;
- network access to configured NuGet feeds for the first restore;
- Git when building from a clone;
- a platform supported by the selected Terraria/OTAPI runtime packages.

The repository pins its important package versions in project files. Do not add the old MonoMod development feed or restore removed TSAPI submodules.

## 5. Build and test

Run from the repository root:

```powershell
dotnet restore Vega.sln
dotnet build Vega.sln --configuration Release --no-restore
dotnet test Vega.sln --configuration Release --no-build --no-restore
```

Build the world-image benchmark harness separately:

```powershell
dotnet build benchmarks/Vega.WorldImage.Benchmarks/Vega.WorldImage.Benchmarks.csproj `
  --configuration Release `
  --no-restore
```

MonoMod emits an expected warning because Vega explicitly opts into the target runtime. The build must still complete with zero errors.

## 6. Publish and distribution layout

```powershell
dotnet publish src/Vega.Runtime/Vega.Runtime.csproj `
  --configuration Release `
  --output artifacts/server
```

The resulting directory is arranged as follows:

```text
artifacts/server/
├── Vega.Server.exe
├── Vega.Server.dll
├── Vega.Server.deps.json
├── Vega.Server.runtimeconfig.json
├── libs/                  shared Vega and third-party runtime libraries
├── ServerPlugins/         bundled Vega.Modules.* assemblies
├── config/                database, TUI, world-clock, autosave, cleaner, and module settings
├── runtimes/              platform-specific package assets when present
└── <culture>/             satellite resources when present
```

The assembly resolver checks `libs/` first and then `ServerPlugins/`. The old `bin/` and `Plugins/` distribution names are not used.

### Database provider

SQLite is enabled by default and creates `data/vega.db`. Select `postgresql` or `mysql` in `config/database.json` and supply `VEGA_DATABASE_CONNECTION_STRING` for an external database. Provider connections and migrations run after cheap runtime/module registration but before world loading; failure stops startup. See [the persistence guide](../persistence.md) for configuration, migration locks, backup and recovery.

## 7. Starting the server

```powershell
artifacts/server/Vega.Server.exe --vega-ui=plain
```

Vega-specific options are removed before the remaining arguments are passed to Terraria.

### Server configuration and precedence

`config/server.json` is the deploy-time entry point for the world path, listener port, player slots, password, default Vega culture, UI mode and `.vworld` cache. Leave `worldPath` as `null` to retain Terraria's interactive world selector. The deterministic precedence is:

```text
command line > VEGA_* environment > config/server.json > Terraria/Vega default
```

The supported environment variables are `VEGA_WORLD`, `VEGA_PORT`, `VEGA_MAX_PLAYERS`, `VEGA_PASSWORD`, `VEGA_DEFAULT_CULTURE`, `VEGA_UI` and `VEGA_WORLD_IMAGE`. Invalid JSON, ports, slot counts, booleans or cultures stop startup before persistence and world services initialize. `config show` reports every effective non-secret value and its source, `config validate` checks the file, and `config reload` validates but explicitly reports these listener/world settings as restart-only. Password values are always displayed only as `<set>`.

Moderation uses one persistent typed-ban service for commands, connection admission and cluster replication. `ban <online-player> [reason]` records name and available IP identities and disconnects the player. Offline or network identities use `ban add <name|account|uuid|ip|cidr> <value> <30m|12h|7d|permanent> [reason]`. Inspect active entries with `ban list` or `ban show <id>` and revoke one with `unban <id>`. IP, UUID and CIDR subjects are redacted unless the caller also has `vega.moderation.sensitive`.

Server-wide optional gameplay rules live separately in `config/security.json`. They are disabled by default; when enabled they cover living-ghost/spectate restrictions, repeated-death timing and interaction proximity with the `vega.security.gameplay.bypass` permission. Database, module hot reload, TUI layout, autosave, cleaner and world-clock settings remain in their domain-specific files.

### Operations modes

| Option | Behavior |
|---|---|
| `--vega-ui=auto` | Use the TUI for a compatible interactive terminal, otherwise plain mode |
| `--vega-ui=tui` | Request the Terminal.Gui dashboard |
| `--vega-ui=plain` | Traditional console input and output |
| `--vega-ui=headless` | No interactive UI or operations stdout sink |

TUI initialization is best-effort and never blocks server readiness. Vega leaves the native Terraria console in control during interactive world selection, then starts Terminal.Gui at `WorldLoading` with a startup progress screen and switches to the operations dashboard at `NetworkReady`. In TUI mode the command palette exclusively owns terminal input; unknown Vega commands are queued to Terraria's native parser so commands such as `save` and `exit` remain usable. Routine logs stay in the dashboard/file sink instead of painting over the terminal. Structured file logs are written to `logs/vega-operations.jsonl` when the path is available.

The dashboard is a high-contrast desktop of movable and resizable server, performance, memory, network, player, module and log windows. TPS/CPU and memory histories are rendered as bounded graphs. Tick telemetry also exposes the rolling-minute average, lifetime extrema and p50/p95/p99 durations through the dashboard and `tps`/`tpsstats`. Visibility and percentage-based geometry are configured in `config/tui.json`; use `:windows`, `:show <name>`, `:hide <name>`, `:layout save` and `:layout reset` in the palette. See [the operations guide](../operations.md) for the complete window and filtering reference.

### World-image mode

`--vega-world-image=on` enables the experimental derived `.vworld` cache; `off` disables it. The default is off. `.wld` remains canonical, and stale, incompatible, or corrupt images automatically fall back to it.

Use `Vega.Server.exe --vega-world-image=on -world path/to/world.wld`, or directly select a prepared image with `Vega.Server.exe -world path/to/world.vworld`. Direct selection enables the cache automatically and resolves the companion `.wld` as the canonical save/recovery target, while validated startup reads from `.vworld`. Manual save, autosave, and shutdown save update `.wld`, capture prepared runtime tiles and liquid queues, then queue one coalesced atomic image publication. The next validated runtime-snapshot hit can bypass repeated liquid settling; any source, schema, checksum, queue, or initialization-sequence mismatch falls back to `.wld`.

### World autosave and cleanup

`config/world-maintenance.json` configures only the autosave enablement/interval; autosave defaults to 10 minutes. Optional item/projectile cleanup belongs to the bundled `Vega.Modules.AutoWorldCleaner` module and is configured independently in `config/auto-world-cleaner.json`. It defaults off and owns its interval, online-player requirement, item count/age thresholds, projectile threshold and minion/sentry exclusions. Snapshot, decision and guarded removal run atomically on the Terraria game thread. See [the world-maintenance guide](../world-maintenance.md) and [AutoWorldCleaner guide](../auto-world-cleaner.md).

### World-clock mode

`config/world-clock.json` optionally keeps in-game day/night progression tied to monotonic elapsed time instead of degraded TPS. It is disabled by default. Corrections run only on the Terraria game thread; a large explicit time change establishes a new anchor. Current drift and correction/re-anchor counts are visible in Operations/TUI.

## 8. Lifecycle and readiness

The lifecycle progresses through:

```text
ProcessStart → Bootstrap → RuntimeReady → ModulesRegistered
→ WorldLoading → WorldReady → NetworkReady
→ BackgroundWarmup → FullyWarmed
```

`NetworkReady` is the operational readiness point. Terraria world loading is measured separately from Vega framework overhead. Lifecycle transitions, module state, TPS, network counters, memory, GC, and world-image measurements are available through operations snapshots and structured logs.

## 9. Modules and extension boundaries

Bundled modules implement `IVegaModule` and are created by the runtime composition root.

- `Vega.Modules.Pvp` owns PvP-specific policy and temporary session state.
- `Vega.Modules.CustomItems` owns custom-item definitions while `Vega.Terraria` performs packet delivery.

Modules should depend on the narrowest capability they need. They must not mutate `Terraria.Main`, retain mutable player objects, introduce a competing packet codec, or hide business behavior in global helpers. Temporary extension state is scoped to a concrete player session and removed deterministically on disconnect.

An external hot-loadable plugin directly implements `IHotReloadableVegaModule` and has one public parameterless entry point. Enable the administrative function in `config/modules.json`; the filesystem watcher then applies create/change/delete/rename events automatically. `plugin list`, `plugin reload <name-or-dll>` and `plugin unload <name-or-dll>` provide explicit control. Commands and hooks must be registered through the supplied `IHotModuleContext`; the host removes them before disposal and verifies collection of the isolated load context. Failed cleanup requires restart. Bundled `IVegaModule` revisions always require restart.

## 10. Operations and cluster behavior

`Vega.Operations` exposes immutable server, player, module, log, and metric projections. The Terminal UI is only a consumer; administrative actions still use the normal command and authorization boundaries.

Cluster support is optional. The current transport is an in-memory reference implementation for deterministic tests and local composition. A standalone server uses `UnavailableClusterClient` and remains functional. No production Redis, NATS, RabbitMQ, HTTP, or WebSocket transport is selected by the current ADR.

## 11. Packages and releases

`Directory.Build.props` is the version source. Library projects pack as `VegaKernel.*`; `Vega.Runtime` is distributed as the server archive instead of a NuGet library.

The automatic `Release Tag` workflow runs when a `vMAJOR.MINOR.PATCH` tag is pushed. It validates the tag against `VersionPrefix`, restores, builds and tests the solution, packs the public libraries and symbol packages, pushes them to NuGet.org, and creates or refreshes the GitHub Release with the deployable server ZIP and checksums. The repository must contain the `NUGET_API_KEY` Actions secret before a release tag is pushed.

The separate `Publish Manual` workflow selects the newest valid tag and only rebuilds its deployable server archive and GitHub Release assets. It deliberately skips tests and NuGet publication and does not require the NuGet secret. See [the release procedure](../releasing.md).

## 12. Current limitations

- per-player language selection and translation catalog persistence are not implemented;
- Dimensions packet forwarding is not implemented;
- sandbox backends and multi-world process management are not implemented;
- hot replacement supports only the unload-safe Vega contract and is disabled by default; legacy or bundled module DLLs require restart;
- cluster scenarios have no selected production transport;
- `.vworld` is experimental and disabled by default;
- several advanced inventory, chest, overlay, role-lease, and world-reset primitives remain roadmap work.

The [roadmap](../roadmap.md) records delivered phases. The [ADRs](../decisions/) are normative design records, but accepted future-facing requirements are not automatically claims of completed implementation.

## 13. Documentation and change discipline

When a change moves ownership, alters threading, changes readiness semantics, adds a public contract, or changes package/runtime layout:

1. update the matching code and focused tests;
2. update the English and Russian project guides;
3. update the subsystem document or ADR;
4. update the roadmap only when the implementation and validation are complete.

Original Vega code is proprietary. Review [LICENSE](../../LICENSE) before using or distributing source, binaries, containers, or NuGet packages. Third-party materials keep their own terms.
