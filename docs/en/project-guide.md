# Vega project guide

[Русская версия](../ru/project-guide.md) · [Repository README](README.md) · [Roadmap](../roadmap.md) · [ADRs](../decisions/)

## 1. Project overview

Vega is an independent server framework for Terraria 1.4.6.1 built for .NET 11. The process host uses TerrariaRuntime 1.1.8 and the patched official dedicated server while Vega owns the server-facing contracts, policies, module lifecycle, observability, and application boundaries.

The project is designed around three rules:

1. Mutable Terraria state stays on the game thread.
2. Core behavior depends on typed contracts and immutable snapshots, not Terraria globals.
3. New abstractions arrive with a working consumer instead of as speculative framework surface.

Vega does not load TShock or TSAPI plugins and does not preserve their API shapes. Historical projects remain useful only as behavioral evidence and regression input.

## 2. Repository map

| Path | Responsibility |
|---|---|
| `src/Vega.Contracts` | Public identifiers, snapshots, events, command, player, world, networking, operations, and cluster contracts |
| `src/Vega.PluginSdk` | Thin ordinary-plugin entry point and curated command/player/task/storage/log facades |
| `src/Vega.Core` | Server/application behavior independent of Terraria and TerrariaRuntime |
| `src/Vega.Terraria` | Terraria/TerrariaRuntime hooks, packet translation, player operations, game-thread scheduling, and world integration |
| `src/Vega.Runtime` | Executable host, composition root, module startup, runtime options, and assembly resolution |
| `src/Vega.Operations` | Structured logging, metrics, read models, and safe command actions |
| `src/Vega.TerminalUI` | Optional Terminal.Gui dashboard and command palette |
| `src/Vega.Cluster` | Optional typed inter-server scenarios and the in-memory reference transport |
| `src/Vega.Persistence` | Domain persistence, Vega 1.0 baseline schema, and SQLite/PostgreSQL/MySQL providers |
| `src/Vega.Regions` | World-scoped region commands and packet ACL decisions; storage remains in Persistence |
| `src/Vega.Modules.Pvp` | PvP authority, projectile attribution, death windows, and per-session state |
| `src/Vega.Modules.CustomItems` | Typed custom-item definitions and targeted Terraria delivery |
| `tests/Vega.Architecture.Tests` | Dependency and ownership guardrails |
| `tests/Vega.Runtime.Tests` | Unit and integration tests for runtime behavior |
| `benchmarks/Vega.WorldImage.Benchmarks` | Reproducible `.wld` versus `.vworld` measurements |
| `docs/decisions` | Architecture Decision Records and their status |

For external plugin development, start with the short [first plugin guide](../plugin-sdk.md). It comes before the internal architecture details intentionally.

## 3. Runtime architecture

```text
Terraria
   ↓
TerrariaRuntime
   ↓
Vega.Runtime ───────→ Vega.TerminalUI
   │                       ↓
   ├──→ Vega.Terraria → Vega.Operations
   ├──→ Vega.Core          ↓
   ├──→ Vega.Cluster   Vega.Contracts
   ├──→ Vega.Persistence   ↑
   ├──→ Vega.Regions       ↑
   └──→ Server modules ────↑
```

`Vega.Contracts` is the shared semantic boundary. `Vega.Core` cannot reference Terraria, TerrariaRuntime, TSAPI, or TShockAPI. `Vega.Terraria` owns all conversions between mutable game objects and Vega snapshots or operations. Architecture tests fail when forbidden dependencies cross that boundary.

The runtime composes bundled modules explicitly. `ServerPlugins/` also supports a separate unload-safe contract for opt-in administrative hot replacement; it is disabled by default and never treats ordinary `IVegaModule` assemblies as unloadable.

## 4. Prerequisites

- .NET SDK 11.0.100, pinned by `src/global.json`;
- network access to configured NuGet feeds for the first restore;
- Git when building from a clone;
- a platform supported by the selected Terraria/TerrariaRuntime runtime packages.

The repository pins its important package versions in project files. Do not add the old MonoMod development feed or restore removed TSAPI submodules.

All first-party projects compile with .NET 11 Runtime Async through the central `runtime-async=on` feature in `Directory.Build.props`. Do not disable or replace that feature in an individual project; the architecture suite verifies the emitted runtime-async metadata.

## 5. Build and test

Run from the repository root:

```powershell
Push-Location src
dotnet restore Vega.sln
dotnet build Vega.sln --configuration Release --no-restore
dotnet test Vega.sln --configuration Release --no-build --no-restore
Pop-Location
```

Build the world-image benchmark harness separately:

```powershell
dotnet build benchmarks/Vega.WorldImage.Benchmarks/Vega.WorldImage.Benchmarks.csproj `
  --configuration Release `
  --no-restore
```

Run the Phase 22 synchronous packet baseline separately; it emits machine-readable throughput, allocation, CPU, GC and tick-latency data:

```powershell
dotnet run --project benchmarks/Vega.PacketPipeline.Benchmarks/Vega.PacketPipeline.Benchmarks.csproj `
  --configuration Release
```

See [packet pipeline](../packet-pipeline.md) for the classification and current baseline.

MonoMod emits an expected warning because Vega explicitly opts into the target runtime. The build must still complete with zero errors.

## 6. Publish and distribution layout

```powershell
Push-Location src
dotnet publish Vega.Runtime/Vega.Runtime.csproj `
  --configuration Release `
  --output ../artifacts/server
Pop-Location
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

### Runtime modes

`Vega.Server` is a CoreCLR application with tiered compilation and dynamic PGO. ReadyToRun is an optional measured CoreCLR publish variant; NativeAOT publication of the server is rejected because managed `ServerPlugins`, collectible hot reload, soft-restart generations and the TerrariaRuntime/MonoMod runtime are first-class capabilities. The Phase 22 packet path remains in-process.

There is currently no separate shipping executable suitable for NativeAOT. `Vega.Contracts` is compiled with trimming/AOT compatibility analyzers so a future fixed-function tool can reuse the pure contract boundary without importing Terraria, the plugin host or UI. See the [runtime specialization audit](../native-aot.md) for the candidate order, comparison gate and fallback policy.

### Database provider

SQLite is enabled by default and creates `data/vega.db`. Select `postgresql` or `mysql` in `config/database.json` and supply `VEGA_DATABASE_CONNECTION_STRING` for an external database. Provider connection and Vega 1.0 baseline initialization run after cheap runtime/module registration but before world loading; failure stops startup. See [the persistence guide](../persistence.md) for configuration, schema locking, backup and recovery.

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

The supported environment variables are `VEGA_WORLD`, `VEGA_PORT`, `VEGA_MAX_PLAYERS`, `VEGA_PASSWORD`, `VEGA_DEFAULT_CULTURE`, `VEGA_UI` and `VEGA_WORLD_IMAGE`. Invalid JSON, ports, slot counts, booleans or cultures stop startup before persistence and world services initialize. `config show` reports every effective non-secret value and its source, `config validate` checks the file, and `config reload` validates but explicitly reports these listener/world settings as restart-only. Password values are always displayed only as `<set>`. Use `help` to list only commands available to the caller and `help <command>` to inspect canonical syntax, aliases and permission requirements; the catalog follows hot-loaded command registrations automatically.

Moderation uses persistent typed services. `ban <online-player> [reason]` records name and available IP identities and disconnects the player. Offline or network identities use `ban add <name|account|uuid|ip|cidr> <value> <30m|12h|7d|permanent> [reason]`. Inspect active entries with `ban list` or `ban show <id>` and revoke one with `unban <id>`. Chat moderation uses `mute <online-player> <30m|12h|7d> [reason]`, `mute add <name|account> <value> <duration> [reason]`, `mute list`, `mute show <id>` and `unmute <id>`. A muted player can still run commands, but ordinary chat is rejected with the expiry and reason. IP, UUID and CIDR ban subjects are redacted unless the caller also has `vega.moderation.sensitive`; use `:show moderation` for the bounded TUI view.

Players can use `/lang`, `/lang list`, `/lang en-US`, `/lang ru-RU` and `/lang reset`. Selection is applied to that recipient immediately and never changes process-global culture; guest selection lasts for the current connection, while an authenticated account's selection is persisted and restored at the next login. Module catalogs use their module namespace and are removed safely on unload.

Account and group administration is part of the Vega 1.0 persistence baseline for SQLite, PostgreSQL and MySQL. From the server console, bootstrap an administrator with `account create <name> <password> administrator`. Players use `/register <password>` (or `/register <account> <password>`), `/login <password>` (or `/login <account> <password>`), `/logout` and `/password <current> <new>`. Account bans are checked at login, duplicate concurrent use is rejected, and permissions update from the persistent group and role-lease cache without reconnecting. Use `account list`, `account show <name>`, `account group <name> <group>`, `account password <name> <new-password>` and `account delete <name>` for operator lifecycle actions. Temporary overrides use `account temp <name> <group> <30m|2h|7d>`, `account temp-clear <name>` and `account temp-list`; a new grant atomically replaces the previous lease and expiry needs no player timer. Groups support `group list`, `group show <name>`, `group create <name> [parent]`, `group parent <name> <parent|none>`, `group grant|deny|revoke <name> <permission>` and `group delete <name>`. Applied changes use optimistic revisions and write an atomic audit row; passwords are stored only as salted PBKDF2-SHA256 hashes. Non-console operators cannot delegate permissions they do not themselves hold.

World-scoped regions are stored in the Vega 1.0 persistence baseline. Create one with `region create <name> <x> <y> <width> <height> [priority] [owner]`; inspect it with `region list` and `region show <name>`. The remaining mutations are `region resize`, `region priority`, `region owner`, `region member <name> add|remove <account>`, `region group <name> add|remove <group>`, `region flag <name> <flag> <true|false>` and `region delete`. Regions protect build and chest-use mutations by default. Owners, listed members, listed groups and players with `vega.regions.bypass` may act; `protected false`, `build true` and `use true` open the corresponding access. Chest IDs, coordinates and active-player chest state are validated before ACL evaluation. Packet checks use a pre-warmed in-memory snapshot and do not query SQL on the game-thread hot path. Overlaps resolve by priority and then smaller area, and the command's explicit world context keeps sandbox-local names independent. Operations exposes the same bounded immutable snapshots; use `:show regions` in the TUI, while every mutation continues through the authorized `region` command.

TShock-compatible region branches include `info <name>`, `z <name> <priority>`, `protect <name> <true|false>`, `allow|remove <account> <region>`, `allowg|removeg <group> <region>`, `resize|expand <region> <u/d/l/r> <amount>` (signed amount), `rename <region> <new-name>` and `list [page]`. The aliases validate existing account/group names; the original `member`/`group` commands remain available for explicit ACL maintenance. Renaming preserves the region ID, ACL, flags and revision history. Geometry changes are bounded to the loaded world. `region tp <region>` uses the exact current player session and native navigation to the center tile; it requires both `vega.regions.manage` and `vega.navigation.tp`. For selections, `region set <1/2> [x y]` takes the current player tile or explicitly supplied tile coordinates; `region define <name>` creates an inclusive rectangle owned by the authenticated account and consumes the selected points on success. `region clear` clears points; `region name [x y]` resolves the top region at a position. Points are isolated by world/session and expire after ten minutes. These selectors intentionally do not intercept block-hit packets; TShock's hit-to-select and `info -d` graphical boundary display remain separate qualification work.

Server-wide optional gameplay rules live separately in `config/security.json`. They are disabled by default; when enabled they cover living-ghost/spectate restrictions, repeated-death timing and interaction proximity with the `vega.security.gameplay.bypass` permission. Database, module hot reload, TUI layout, autosave, cleaner and world-clock settings remain in their domain-specific files.

`config/network.json` sets `pendingConnectionLimit` (default 128, range 1–1024) and `framesPerSlotPerPass` (default 32, range 1–256). Vega installs these limits through the TerrariaRuntime hooks independently of network metrics. Frame application remains ordered on the native game thread. `useOrderedTcp: true` enables the experimental accepted-socket adapter: pooled owned sends, FIFO ordering, per-connection/per-world budgets and generation-validated completion callbacks. `orderedCallbacksPerPass` (default 8192, range 1–8192) bounds its completion count while retaining the same cooperative 2 ms budget. Default transport is `false` (vanilla TCP); do not enable Ordered for production before representative real-client join and performance validation. Changes take effect at startup. See [native transport design](../native-transport-design.md).

`orderedDeferredWake: true` additionally enables opt-in synchronous producer burst batching for bound Ordered sockets; `orderedDeferredWakeMilliseconds` is the cooperative flush window (0.25–2 ms, default 1). Native tick completion and lifecycle changes flush deferred writers; a stalled game thread can delay the window. Do not equate the measured [255-peer mixed output fixture](../performance-native-borrowed-2026-11-27.md) with full joined-client TPS. With TerrariaRuntime 1.1.7 Ordered copies borrowed native serialized spans once into owned queue memory; mutable hooks and unknown sockets retain the old independent-array path.

`reload` / `config reload` (`vega.config.manage`) validates server startup configuration, reloads the deployment-wide whitelist, and independently reloads `restrictions.json` and `build-protection.json` from the runtime's world-local data directory. Invalid/missing world-local files retain their last active state. The shared whitelist is authoritative across processes; damage or loss of its file rejects new admissions rather than using divergent cached allowlists. Listener, password configured at startup, database, and world startup settings are not live-reloaded.

Optional primary-world REST: set `VEGA_REST_PORT` and `VEGA_REST_TOKEN_SHA256` (64 hexadecimal SHA-256 characters of a randomly generated bearer token, at least 32 characters long). The service binds only `127.0.0.1`; use the `Authorization: Bearer <token>` HTTP header for `GET /v1/status`, `/v1/players`, and `/v1/network`. A separate `VEGA_REST_MAINTENANCE_TOKEN_SHA256` enables the typed `POST /v1/world/save` (empty body, ready world); the read token cannot save. Without a configured port it remains disabled. `rest status`, `rest listusers`, `rest destroytokens` require `vega.operations.rest.manage`; revocation lasts until process restart. No account-password login, URL query tokens, arbitrary remote command execution, sensitive player endpoints or sandbox REST listeners are exposed.

For typed administration of accounts, groups, regions, moderation and sandbox lifecycle actions, configure a third distinct fingerprint `VEGA_REST_ADMIN_TOKEN_SHA256`. Existing read and maintenance tokens cannot access the administrative catalogs or mutate them. See the full [REST administration route table](../operations.md#typed-administration).

Setup uses the trusted console (`setup` prints instructions; `account create <name> <password> administrator` establishes an owner). Unlike TShock, Vega does not implement temporary unrestricted `su`/`sudo` substitution; use explicit bounded `tempgroup` leases or authorized console commands.

Additional native commands: `bossdamage [page]` reads recent finished native encounter history including historical character names; `evil [crimson|corruption]` changes the world evil flag only, `settle` starts native liquid settling, `grow` supports native tree/mushroom growth on compatible existing ground (console coordinates or authorized player). `annoy` schedules a bounded cosmetic sound, `firework` launches a native cosmetic projectile, `godmode`/`god` uses the native per-player Creative Power and clears admin grants on disconnect. `rocket` is conditional on opt-in native server-side-character authority. Player effects still require real-client confirmation.

Opt-in primary character persistence supports `savessc`, `overridessc <authenticated-player>` (`ossc`), and `uploadssc [player] confirm`. They are separate authorized revision-checked operations. Upload uses the original character-received snapshot from the native pre-WorldData hook and requires confirmation; missing or failed capture never falls back to current state. Enable native SSC on a new server in `config/primary-character-persistence.json`:

```json
{"enabled":true,"authoritative":true,"requireLogin":true,"requireCharacterReady":true}
```

All options default off. Authority requires persistence and character readiness. A blank server-owned character is used pending exact account authentication and successful restoration; pending client character synchronization cannot replace that state. Normal authentication chat, movement and protocol acknowledgements remain available. Explicit logout clears the character. Authoritative primary worlds share the sandbox SSC store; migrate an existing separate `primary-characters.json` with the offline `--vega-migrate-primary-ssc` command before enabling authority (see [migration guide](../migration.md)). Sandbox SSC modes other than `Disabled` also set the native flag. Current-schema TShock SQLite character migration is implemented and fixture-tested; stock-client join, restoration and reconnect qualification and full check equivalence remain open.

### Operations modes

| Option | Behavior |
|---|---|
| `--vega-ui=auto` | Use the TUI for a compatible interactive terminal, otherwise plain mode |
| `--vega-ui=tui` | Request the Terminal.Gui dashboard |
| `--vega-ui=plain` | Traditional console input and output |
| `--vega-ui=headless` | No interactive UI or operations stdout sink |

TUI initialization is best-effort and never blocks server readiness. Vega leaves the native Terraria console in control during interactive world selection, then starts Terminal.Gui at `WorldLoading` with a startup progress screen and switches to the operations dashboard at `NetworkReady`. In TUI mode the command palette exclusively owns terminal input; unknown Vega commands are queued to Terraria's native parser so commands such as `save` and `exit` remain usable. Native stdout/stderr is captured in the bounded Operations log as `terraria-console` instead of disappearing behind the dashboard; unterminated lines are capped at 8192 characters. Routine logs stay in the dashboard/file sink instead of painting over the terminal. Structured file logs are written to `logs/vega-operations.jsonl` when the path is available.

The dashboard is a high-contrast tiled desktop of server, performance, memory, network, player, region, module and log windows. Dragging a shared border reallocates space between adjacent tiles; dragging a title over another tile swaps them, and title double-click maximizes/restores one tile. Snapshot capture runs outside the UI thread with no request backlog, keeping input responsive during slower sampling. TPS/CPU and memory histories are rendered as bounded graphs. Tick telemetry also exposes the rolling-minute average, lifetime extrema and p50/p95/p99 durations through the dashboard and `tps`/`tpsstats`. Visibility and percentage-based geometry are configured in `config/tui.json`; use `:windows`, `:show <name>`, `:hide <name>`, `:layout save` and `:layout reset` in the palette. See [the operations guide](../operations.md) for the complete window and filtering reference.

### World-image mode

`--vega-world-image=on` enables the experimental derived `.vworld` cache; `off` disables it. The default is off. `.wld` remains canonical, and stale, incompatible, or corrupt images automatically fall back to it.

Use `Vega.Server.exe --vega-world-image=on -world path/to/world.wld`, or directly select a prepared image with `Vega.Server.exe -world path/to/world.vworld`. Direct selection enables the cache automatically and resolves the companion `.wld` as the canonical save/recovery target, while validated startup reads from `.vworld`. Manual save, autosave, and shutdown save update `.wld`, capture prepared runtime tiles and liquid queues, then queue one coalesced atomic image publication. Native world version 329 can use a cache from a completed native save. Liquid fast restore remains disabled for the current runtime; invalid images fall back to `.wld`.

### World autosave and cleanup

`config/world-maintenance.json` configures only the autosave enablement/interval; autosave defaults to 10 minutes. Optional item/projectile cleanup belongs to the bundled `Vega.Modules.AutoWorldCleaner` module and is configured independently in `config/auto-world-cleaner.json`. It defaults off and owns its interval, online-player requirement, item count/age thresholds, projectile threshold and minion/sentry exclusions. Snapshot, decision and guarded removal run atomically on the Terraria game thread. See [the world-maintenance guide](../world-maintenance.md) and [AutoWorldCleaner guide](../auto-world-cleaner.md).

### World-clock mode

`config/world-clock.json` optionally keeps in-game day/night progression tied to monotonic elapsed time instead of degraded TPS. It is disabled by default. Corrections run only on the Terraria game thread; a large explicit time change establishes a new anchor. Current drift and correction/re-anchor counts are visible in Operations/TUI.

The Administration module provides `time [day|noon|night|midnight|HH:mm]` (`vega.world.time`) and `wind <-40..40 mph>` (`vega.world.wind`) in console and chat. Mutations run on the game thread in the current world; an explicit time change resets the clock anchor, and wind changes both current and target speed. With no argument, `time` reports the current in-game time.

`Vega.Modules.Navigation` supplies in-game (not console) navigation commands:

| Command | Permission | Behavior |
| --- | --- | --- |
| `/spawn` | `vega.navigation.spawn` | Teleport to current world spawn |
| `/tp <player>` | `vega.navigation.tp` | Teleport to another player |
| `/tp <source> <destination>` | `vega.navigation.tp` + `vega.navigation.others` | Move another player |
| `/tphere <player>` | `vega.navigation.others` | Bring another player to the caller |
| `/tppos <x> <y>` | `vega.navigation.tppos` | Teleport to tile coordinates within safe world bounds |
| `/pos [player]` | `vega.navigation.pos` | Report tile coordinates |
| `/tpallow` | `vega.navigation.allow` | Toggle incoming teleports for the current session |

Selectors use exact names, unique prefixes or slot numbers. Incoming preferences are enforced unless the actor holds `vega.navigation.override`; movement is bound to the live session and world. `/home` (`vega.navigation.home`) validates the personal spawn and falls back to the world spawn. `/tpnpc` (`vega.navigation.tpnpc`) resolves an active NPC by name or `slot:<number>`. `/warp <name>` and `warp list [page]` use `vega.navigation.warp`; `add`, `del`, and `hide` additionally need `vega.navigation.warp.manage`, and `send` requires `vega.navigation.others`. Hidden warps are excluded from lists, not access-controlled. Files are scoped to the runtime and the persistent world UUID. Wildcard mass teleport remains pending.

Additional administration commands are `worldinfo`, `worldevent <rain|bloodmoon|eclipse|fullmoon> [on|off]`, `spawnmob <id|name> [1..20]`, `item <id|name> [stack] [prefix-id]`, `give <item> <player> [stack] [prefix-id]`, `heal [player] [amount]`, `buff <id|name> [1..3600 seconds]`, `gbuff <player> <buff> [seconds]`, `broadcast <message>`, and `clear <item|projectile|npc> [all]`. They act within the current world; `clear all` needs an additional permission and the default excludes minions/sentries and friendly/boss NPCs. Extended TShock branches and real-client synchronization are not fully validated.

`whitelist list|on|off|add <ip|cidr>|del <ip|cidr>` (`vega.admin.whitelist`) uses the single deployment-root `data/whitelist.json` in primary and every managed sandbox. Atomic serialized writes are visible to already running worlds on the next admission; effective IP is checked before WorldData. Preserve the associated `whitelist.json.lock` initialization marker. Previously created per-sandbox whitelist files are not merged or used. Existing connections are not evicted by whitelist changes. Bans use the shared database; active bans are rechecked against online sessions in each running world. `/itemban`, `/projban`, `/tileban` (`vega.restrictions.manage`) accept `list [page]`, `add <id>`, `del <id>` and persist in each runtime's `data/restrictions.json`. Restrictions run before game-thread application; group exceptions use `vega.restrictions.bypass.item`, `.projectile`, `.tile`. Complete SSC/inventory reconciliation is not yet claimed.

Chat commands: `/whisper` (`w`, `tell`, `pm`, `dm`), `/reply` (`r`) and `/wallow` (`wa`) use `vega.chat.whisper`; `/party` (`p`) uses `vega.chat.party`; `/me` uses `vega.chat.me`. Mute moderation applies to these commands. Delivery is scoped to the requested audience in the current world, and party messages only reach the sender's team. Whisper preferences and reply targets expire with the connection session.

`antibuild [on|off|status]` (`vega.world.antibuild`) disables world mutations and `protectspawn [on|off|status|radius <1..500>]` (`vega.world.protectspawn`) protects a square around the live native spawn. No arguments toggle the respective setting. Settings persist per runtime in `data/build-protection.json`. Separate bypasses are `vega.world.build.bypass` and `vega.world.spawn.bypass`; region bypass alone does not override them. Ordinary object use remains governed by the region use policy.

Native world settings: `worldmode [normal|expert|master]`, `spawnrate [1..60000]`, `maxspawns [0..200]`, `setspawn [x y]`, `setdungeon [x y]`, `forcehalloween [on|off|status]`, `forcexmas [on|off|status]`, `clearangler`. Journey mode cannot be switched by this command; world points use the current player if coordinates are omitted. `worldevent` also supports `slimerain`, `sandstorm`, `pumpkinmoon`, `frostmoon`, `goblinarmy`, `snowlegion`, `pirateinvasion`, and `martianmadness`; invasions start under vanilla conditions and cannot be forcibly stopped by this command.

`death`/`pvpdeath` display native character counters and `alldeath`/`allpvpdeath [page]` list online players in the current world (`vega.players.deaths.others`), not an all-time account leaderboard. `version` displays the Vega and patched Terraria builds. `motd` and `rules` read `data/motd.txt`/`data/rules.txt` with a shared `config/` fallback.

`kill` (`slay`), `slap`, and `respawn` send native requests to the targeted client/session; the outcome needs client confirmation. `hardmode status` reports state, `hardmode start confirm` starts native world transformation and cannot reverse it. `serverpassword set <password>`/`clear` changes the password for new connections until restart (`vega.cfg.password`), without echoing the value. `off-nosave` (`exit-nosave`, `stop-nosave`, permission `vega.cfg.maintenance.nosave`) suppresses the world save at exit; already completed saves are not rolled back.

`accountinfo <account>` (`ai`, `vega.accounts.inspect` or `vega.accounts.manage`) uses the existing read-only account service. `tempgroup <account> <group> <duration>` / `tempgroup <account> clear` / `tempgroup list` manage bounded persistent account role leases. `sync [radius:1..16]` sends the local native tile state to the caller's current session; `sync <player> [radius]` additionally requires `vega.world.sync.others`.

`displaylogs [count:1..20] [information|warning|error|critical]` (`vega.operations.logs`) reads a bounded recent log slice without exception stacks. `dump-reference-data [item|npc|buff|projectile|tile|all]` (`vega.world.reference`) exports the native symbolic ID catalog to `data/reference/<kind>.json` in the current runtime, without reading world or character data.

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

Independent product domains follow the same rule. `Vega.Regions` owns its command and ACL application slice while consuming Contracts directly; Persistence owns SQL and Terraria owns mutable game adapters. A new domain project must move a complete vertical slice and must not use Core as a service locator or transit dependency. See [the Core cohesion audit](../core-cohesion-audit.md) and ADR 0023.

An external hot-loadable plugin directly implements `IHotReloadableVegaModule` and has one public parameterless entry point. Enable the administrative function in `config/modules.json`; the filesystem watcher then applies create/change/delete/rename events automatically. `plugin list`, `plugin reload <name-or-dll>` and `plugin unload <name-or-dll>` provide explicit control. Commands and hooks must be registered through the supplied `IHotModuleContext`; the host removes them before disposal and verifies collection of the isolated load context. Failed cleanup requires restart. Bundled `IVegaModule` revisions always require restart.

## 10. Operations and cluster behavior

`Vega.Operations` exposes immutable server, player, module, log, and metric projections. The Terminal UI is only a consumer; administrative actions still use the normal command and authorization boundaries.

`Vega.Sandboxes` supervises isolated `Vega.Server` backend processes for additional worlds. Use `sandbox create/start/stop/restart/reset/destroy`, and use `worldtransfer` through a trusted Dimensions forwarder. Descriptors, limits and TTL live in `config/sandboxes.json`; forwarding is disabled by default in `config/security.json`. Accounts and bans remain server-wide while command and region contexts carry the backend's explicit `WorldId`. See [the sandbox operations guide](../sandboxes.md).

`Vega.Backups` creates consistent deploy-local world/database archives. `config/backups.json` contains only `enabled`, `interval` and `retainedBackups`; `backup` starts an authorized manual run and `backup status` exposes progress. The service joins the world-save and `.vworld` barrier, uses provider-owned SQLite/`pg_dump`/`mysqldump` capture, redacts configuration secrets and atomically publishes verified archives beneath `backups/`. See [backup and restore](../backups.md).

Bundled modules can be recreated in-process through the disabled-by-default `config/soft-restart.json` boundary. `softrestart confirm` preserves Terraria, the loaded world, connected players and persistence while replacing the generation-routed module graph. Native/runtime changes and unsafe plugin cleanup still require a process restart. See [in-process soft restart](../soft-restart.md).

Cluster support is optional. The current transport is an in-memory reference implementation for deterministic tests and local composition. A standalone server uses `UnavailableClusterClient` and remains functional. No production Redis, NATS, RabbitMQ, HTTP, or WebSocket transport is selected by the current ADR.

## 11. Packages and releases

`Directory.Build.props` is the version source. NuGet publication produces one `Vega` package containing the public first-party assemblies; component projects are not published separately. `Vega.Runtime` remains the deployable server archive rather than a NuGet executable.

`Vega` propagates Vega's explicit MonoMod `net11.0` opt-in to package consumers and therefore emits the same unsupported-runtime warning during a consumer build. Keep that warning visible until the dependency graph officially supports .NET 11; see the [.NET 11 dependency audit](../net11-dependency-audit.md).

The automatic `Release Tag` workflow validates a prerelease tag, restores, builds and tests the solution, packs the single `Vega` package as a validation artifact, and creates or refreshes the GitHub Release with the deployable server ZIP and checksums. NuGet.org publication and credential requirements are hard-disabled until a separate post-debugging release decision.

The separate `Publish Manual` workflow selects the newest valid tag and only rebuilds its deployable server archive and GitHub Release assets. It deliberately skips tests and NuGet publication and does not require the NuGet secret. See [the release procedure](../releasing.md).

## 12. Current limitations

- Dimensions forwarding and player transfer require an explicitly trusted proxy and remain disabled by default;
- Authoritative SSC is available by explicit configuration; stock-client lifecycle and existing-data migration remain release gates;
- Vega provides its own whitelist, home and warp; compatibility with legacy TShock storage, tpa/clan commands and TShock REST routes remains outside the implemented surface. Remote administration requires a separate interface;
- hot replacement supports only the unload-safe Vega contract and is disabled by default; legacy or bundled module DLLs require restart;
- cluster scenarios have no selected production transport;
- `.vworld` is experimental and disabled by default;
- single-process multi-world virtualization is research-only; production sandboxes use one process per active world.

The [roadmap](../roadmap.md) records delivered phases. The [ADRs](../decisions/) are normative design records, but accepted future-facing requirements are not automatically claims of completed implementation.

## 13. Documentation and change discipline

When a change moves ownership, alters threading, changes readiness semantics, adds a public contract, or changes package/runtime layout:

1. update the matching code and focused tests;
2. update the English and Russian project guides;
3. update the subsystem document or ADR;
4. update the roadmap only when the implementation and validation are complete.

Original Vega code is proprietary. Review the [Vega Proprietary License](../../licenses/LICENSE) before using or distributing source, binaries, or NuGet packages. Third-party materials keep their own terms.
