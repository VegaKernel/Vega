# Vega roadmap

## Phase 0 — freeze the target architecture

- [x] Establish .NET 10 baseline.
- [x] Record production-derived requirements and world/threading constraints.
- [x] Rename the product/repository identity to Vega.
- [x] Adopt Vega as an independent runtime rather than a TShock compatibility modernization (ADR 0016).
- [x] Define startup lifecycle, readiness barriers and performance budgets (ADR 0017).
- [x] Define event/player/packet migration architecture and anti-framework constraints (ADR 0018).
- [x] Define optional typed cluster messaging and first production scenarios (ADR 0019).
- [x] Add architecture tests that prevent Terraria/TerrariaRuntime/TSAPI/TShockAPI references from entering Vega.Core.

## Phase 1 — native runtime boundary

- [x] Establish `Vega.Runtime` / `Vega.Terraria` as the Terraria/TerrariaRuntime integration boundary.
- [x] Move process bootstrap/readiness telemetry into the Vega runtime host.
- [x] Add Vega-owned TerrariaRuntime lifecycle/readiness hooks.
- [x] Add a bounded Vega-native game-thread dispatcher contract and TerrariaRuntime implementation.
- [x] Measure Vega overhead separately from Terraria world load time.
- [x] Replace the remaining compatibility hook dispatch with Vega-native TerrariaRuntime adapters.
- [x] Remove TSAPI after the native host no longer requires it.

## Phase 2 — typed event model and first migrated behavior

Implementation priority is intentionally vertical. Each new abstraction should land with a working migrated consumer or the immediately following slice.

- [x] Implement explicit event priorities: `Earliest`, `Early`, `Normal`, `Late`, `Latest`.
- [x] Implement structured monotonic cancellation with machine-readable reason and source.
- [x] Allow handlers to opt into execution after cancellation.
- [x] Implement a read-only `Monitor` stage that observes the final outcome without mutating it.
- [x] Keep final side-effect application owned by the runtime/integration component instead of exposing `Apply` as a generic handler priority.
- [x] Separate synchronous handlers from genuinely asynchronous handlers; never block the game thread on async work.
- [x] Register handlers explicitly through composition root; no reflection-heavy Core scanning.
- [x] Add one runtime-registerable typed `IAsyncEventHub` for Core/modules/plugins with the same priority, cancellation and monitor semantics; every hot-plugin subscription is lifetime-owned and detached automatically on unload.
- [ ] Migrate command/runtime lifecycle hooks such as login, reload, save and world transitions onto that shared hub as their behavior audits reach them; do not create per-command static hook buses or a TShock-style global service locator.
- [x] Migrate one real Terraria hook flow end-to-end through the new event pipeline (`WorldGen.StartHardmode`).

## Phase 3 — player boundary

- [x] Introduce demand-driven read-only `PlayerSnapshot`.
- [x] Introduce `IPlayerDirectory` for lookup/enumeration.
- [x] Introduce `IPlayerOperations` for kick/teleport/authoritative state changes.
- [x] Introduce `IPlayerMessenger` separately from mutation capabilities.
- [x] Ensure Terraria player mutation is implemented only in `Vega.Terraria` and scheduled on the game thread.
- [x] Migrate a real player/security/command scenario before adding more player abstraction surface.

The initial Terraria implementation publishes immutable player snapshots from the game thread and provides slot lookup, exact-name lookup, kick, message delivery and teleport without exposing `Terraria.Player` or `TSPlayer`. Queued operations complete only after the game-thread action has actually produced a result. The first player-facing command slice routes `/serverinfo` output through `IPlayerMessenger` while authorization is supplied through a narrow player-session contract.

## Phase 4 — packet processing context

- [x] Use typed packets from `TZ.Multiplicity`; do not introduce a competing packet codec.
- [x] Add packet direction, connection identity, player identity and required processing metadata.
- [x] Pass one typed context through protocol validation, security and Core policy stages.
- [x] Keep raw byte parsing/encoding in the Terraria/protocol integration boundary.
- [x] Migrate a real packet-sensitive behavior such as join/security or PvP validation through the context.

The first end-to-end packet slice validates inbound `PlayerHp` ownership. `Vega.Terraria` parses the TerrariaRuntime payload window through `TZ.Multiplicity`, the typed context flows into a Core anti-spoof policy, and a rejected decision cancels the native TerrariaRuntime hook.

## Phase 5 — commands and legacy hook migration

- [x] Migrate commands through Vega contracts instead of `Commands.ChatCommands` compatibility surfaces.
- [x] Replace TSAPI event groups incrementally with Vega-native TerrariaRuntime adapters.
- [x] Migrate production behavior by capability rather than reproducing TShock class shapes.
- [x] Keep main server semantics in Core services; helper/extensions remain thin convenience layers.
- [x] Promote true server invariants/security primitives into Core/runtime services.
- [x] Keep gameplay/community policy as optional modules.

`serverinfo` was the first native command path. The base surface now also owns `tps`/`tpsstats`, `kick`, `playing`/`online`/`who`, `checkupdates`, `ban`, `plugin`/`plugins`, `help`/`commands` and `aliases`. Discovery reads the live command catalog, including hot-loaded registrations, and applies the same server/permission visibility rules as dispatch instead of maintaining a second command list. Server-console input is intercepted through TerrariaRuntime command processing; player input is recognized from typed `LoadNetModule`/`NetTextModule` payloads. Effective player permissions live in Vega session state behind `IPlayerAuthorizationService`, and update discovery uses the Vega GitHub release endpoint.

Lifecycle, world decisions, console commands, player commands and packet security have Vega-owned TerrariaRuntime entrypoints. Core owns command behavior and the player packet ownership invariant; optional gameplay policy remains in modules. Unknown commands and non-command chat remain available to Terraria's normal paths.

## Phase 6 — Operations API and Terminal UI

The operations surface is an optional consumer of stable read models and application commands. It must not become a privileged path around Core/runtime rules.

- [x] Define operations/read-model contracts independent of Terminal UI types.
- [x] Introduce structured logging with independent file/stdout/TUI sinks.
- [x] Implement a server dashboard backed by immutable/bounded snapshots.
- [x] Implement a players view using `PlayerSnapshot` / player read models.
- [x] Implement a modules view exposing load state, version, dependencies, startup timing and failures where available.
- [x] Implement a bounded logs viewer with level/source/text filtering and follow/pause behavior.
- [x] Provide a high-contrast, low-flicker windowed desktop with bounded TPS/CPU and memory graphs, configurable visibility, movable/resizable panels and persisted safe layout.
- [x] Expose runtime, network, process, GC and memory metrics through cheap sampled read models.
- [x] Add a command palette over the normal command/application boundary.
- [x] Add safe administrative actions only through normal authorization and service contracts.
- [x] Preserve plain console mode for scripts, redirected output and troubleshooting.
- [x] Support fully headless mode for services, containers and CI.
- [x] Keep TUI initialization optional; failure to initialize an interactive terminal must never block server startup.
- [x] Never read or mutate Terraria globals directly from the UI thread.

`Vega.Operations` now implements the toolkit-independent operations API: immutable server/player/module snapshots, bounded structured logs, cheap sampled runtime/process/GC/network metrics and command actions routed through `ICommandDispatcher`. `Vega.TerminalUI` is the only project that references Terminal.Gui. It renders separate movable/resizable high-contrast windows, bounded TPS/CPU and memory graphs, player/module views and filtered logs from those snapshots. Window visibility and geometry are safely loaded from and persisted to `config/tui.json`. Terraria-owned world/TPS state is sampled on the game thread by `Vega.Terraria`; the UI never traverses Terraria globals.

The runtime supports `auto`, `tui`, `plain` and `headless` modes through `--vega-ui`. Structured events fan out independently to JSON-lines file, stdout when enabled, and a bounded TUI store. The TUI runs as an optional background consumer; initialization and rendering failures are logged and cannot prevent Terraria startup. See ADR 0010 and [`docs/operations.md`](operations.md) for the boundary and operator usage.

## Phase 7 — typed extension state, only when demanded

- [x] Stabilize player/session/entity lifecycle first.
- [x] Add strongly typed scoped keys only for a migrated feature that genuinely needs extension state.
- [x] Support type-safe `GetOrCreate`, `Set`, `Remove` and deterministic cleanup.
- [x] Do not make extension state a universal Core dependency or a substitute for modeled domain state.

`TerrariaPlayerBoundary` now assigns a unique `PlayerSessionId`, publishes explicit open/close lifecycle notifications and includes the session identity in immutable player snapshots. The first concrete consumer is the dedicated `Vega.Modules.Pvp` slice: it keeps projectile-to-weapon attribution, death windows and spectator state in typed per-session scopes. A disconnect removes and disposes the complete scope, so reusing the same player slot cannot inherit state. The store is explicitly composed for this module; it is not embedded in `PlayerSnapshot`, required by Core services or used for modeled authorization/domain state. See [`docs/extension-state.md`](extension-state.md).

## Phase 8 — optional cluster/inter-server messaging

A standalone Vega server must remain fully functional with no cluster transport configured.

- [x] Add strongly typed `ServerId` and cluster message metadata (`MessageId`, correlation, sender, timestamp, schema/version).
- [x] Keep Event, Command and Request/Response semantics distinct.
- [x] Add narrow publishing/sending contracts without a service-locator API.
- [x] Keep Redis/NATS/RabbitMQ/HTTP/WebSocket or any concrete transport outside Core/Contracts.
- [x] Route incoming messages through DI-registered typed handlers.
- [x] Design for duplicate delivery/idempotency; do not promise exactly-once delivery.
- [x] Ensure remote-unavailable behavior does not break unrelated local server operation.
- [x] Prevent republish loops: a local operation publishes once; a received replicated event applies local consequences without becoming a new originating event.

### First end-to-end cluster scenarios

Implement these before more complex distributed workflows:

1. [x] **Server presence / online state**: establish transport, `ServerId`, expiry/staleness, observability and duplicate handling.
2. [x] **Global ban propagation**: typed create/revoke events after successful local ban changes.
3. [x] **Immediate global-ban enforcement**: receiving servers apply the ban locally and kick matching currently connected players through `IPlayerDirectory` + `IPlayerOperations`.
4. [x] **Group / permission synchronization**: typed account/group/permission changes with local authorization cache/state updates.
5. [x] **Inter-server chat**: typed chat event with explicit sender/server identity and local delivery policy.
6. [x] Administrative commands, centralized audit/monitoring and aggregated multi-server status.
7. [x] Keep player transfer / Dimensions disabled behind an explicit readiness gate until the simpler messaging semantics are proven in production.

A durable outbox is optional and should be introduced only if a real scenario requires publication durability across process failure.

`Vega.Cluster` provides these verticals with an in-memory reference transport for deterministic integration tests and local composition. Production transport selection remains deliberately open. The runtime uses `UnavailableClusterClient` by default, so local mutations remain authoritative and report publication unavailability separately. Incoming handlers never receive a publisher dependency for the event they apply, preventing echo loops by construction. Player transfer has not been claimed as production-ready: its gate remains closed until presence, ban, authorization and chat have actual production validation. See [`docs/cluster.md`](cluster.md) and ADR 0019.

## Phase 9 — production module migration

- [x] Port the first two behavior references (`PvPController-1.3` and `CustomWeaponAPI`) to prove the Vega module boundary.
- [x] Restore a pinned pre-cleanup TShock/TSAPI reference snapshot and lock current TerraZ source revisions outside the production solution.
- [x] Complete every capability-level `Keep`, `Port`, `Replace` or `Drop` decision in the historical migration ledger before removing that ledger.
- [x] Port accepted TerraZ behavior to the Vega module API by capability, not by reproducing TShock class shapes.
- [x] Use each migrated module as the forcing function for its required contract instead of building an abstract framework ahead of a consumer.
- [x] Add incident-backed regression tests before retiring the corresponding TShock/TerraZ behavior or workaround.

The first reopened migration slice replaces TerraZ `NoCoinDupe` with `NpcCoinPickupAuthorityPolicy`: inbound Multiplicity `SyncExtraValue (92)` packets are rejected before Terraria can accept client-authored NPC coin state, while server-originated synchronization remains untouched. Its regression suite uses the real 14-byte typed payload and records the confirmed production incident in the migration ledger.

The next StateGuard slice extends the existing player-slot ownership rule to typed `SpectatePlayer (150)` payloads. It deliberately does not claim the whole plugin: configurable spectate/ghost/difficulty policy, physical-world borders, permission bypasses and corrective state synchronization stay open until their required Phase 13/19 capabilities exist.

The first full-TShock network slice ports the TerraZ Bouncer fix for `PlayerDeathV2 (118)`. Vega compares the signed wire field as an unsigned 16-bit value and rejects damage above `42000` before Terraria receives it; the same typed payload must also claim the sending connection's player slot. This is one closed invariant, not a claim that Bouncer or `GetDataHandlers` are fully migrated.

The retired ledger was a capability and repository disposition record. It did not enumerate every branch of every TShock packet handler, command or client/server connection state. Phase 25 reopens that work at source-method and executable-behavior granularity without reviving TShock compatibility architecture.

The post-1.4.5 registration audit also routes client `TeamChangeFromUI (157)` through the native security hook and applies the same sender-slot ownership rule as legacy `PlayerTeam (45)`. Protocol 325 marks historical item packets `145/148` as deprecated, so Vega rejects those packet identities instead of pretending they remain writable item-sync formats. Supported client-authored item state (`21/90/151/160`) is validated for fixed slot bounds, finite positions/velocities, stack, item type and prefix before Terraria sees it.

### Historical implementations to mine for behavior, not architecture

- `popstarfreas/PvPController-1.3`
  - useful scenarios: packet policy context, ordered PvP damage/projectile decisions, authoritative player mutations, temporary session state;
  - its Redis dynamic JSON/action-string synchronization is explicitly a negative example for Vega cluster contracts.
- `popstarfreas/CustomWeaponAPI`
  - useful scenarios: strongly typed partial item overrides, targeted packet delivery and custom item behavior;
  - its bespoke `PacketWriter` must not be carried forward because Vega uses the shared typed `TZ.Multiplicity` protocol layer.

`VegaModuleHost` now starts explicitly composed modules and exposes their load state and startup duration. PVP, CustomItems, AutoWorldCleaner, Chests, Mentions, Wipe and Scenes are capability ports; typed packet security, connection metadata, moderation, role leases, inspection, world clock, TPS and lifecycle behavior are native replacements. The final repository matrix records every remaining optional product as an explicit 1.0 drop rather than leaving placeholder contracts. Accepted invariants have focused incident or lifecycle regressions and no TShock class shape entered the public boundary.

## Phase 10 — remove migration scaffolding

- [x] Remove TShockAPI and TSAPI from the production solution after the native runtime no longer depends on them.
- [x] Remove legacy hook/command/plugin compatibility surfaces from the shipping runtime.
- [x] Rename active physical `TShock*` project paths/namespaces to Vega names.
- [x] Resolve every row in the capability-level legacy/TerraZ repository ledger, then remove that closed historical ledger from the active documentation set.
- [x] Preserve the pinned source references until all accepted behavior, edge cases and incident regressions have executable Vega coverage.
- [x] Review deleted configuration, command, permission, persistence, localization, REST and operations documentation against the running Vega product; restore or rewrite every still-relevant operator contract.
- [x] Close the scaffolding-removal gate only when that historical capability ledger has no unresolved `Partial`, `Missing` or `Unclassified` rows—physical deletion alone is not completion.

The runtime boots Terraria directly through the TerrariaRuntime-backed `Vega.Terraria` host, and architecture/release tests keep TShockAPI and TSAPI outside the source, solution and deployable graph. The exact parent snapshot (`72bff778`), pinned TSAPI revision (`024e480e`) and current TerraZ revisions remain documentary audit evidence. Configuration, command/permission, persistence, localization, REST-drop and Operations decisions are reflected in the running bilingual guides. No unresolved row remained in the retired capability ledger and no legacy assembly was reintroduced; this statement does not close the exhaustive behavioral audit introduced by Phase 25.

## Phase 11 — World image and runtime optimization

Canonical persistence remains Terraria `.wld`. Vega `.vworld` is a disposable, versioned runtime representation and must always be rebuildable or bypassable.

### Baseline and profiling

- [x] Profile stock Terraria `.wld` loading and post-load initialization separately.
- [x] Record baseline file-read time, tile decoding, allocations, GC, chests/signs/entities creation, post-load work and runtime-index construction.
- [x] Record baseline world-load CPU, peak memory and `WorldReady` / `NetworkReady` timing on small, medium and large production-like worlds.

### Level 2: derived Vega World Image

- [x] Define the internal sectioned/chunked versioned Vega World Image format (`.vworld`).
- [x] Keep `.wld` as source of truth for compatibility, import/export, recovery and initial persistence.
- [x] Add source `.wld` content fingerprint plus Terraria world-format, Vega schema and compiler-parameter validation.
- [x] Do not use timestamps as the only cache-validity criterion.
- [x] Implement atomic world-image generation: write temporary file, flush, validate checksums, then atomically replace the previous cache.
- [x] Ensure interrupted cache generation cannot damage `.wld` or the previous valid `.vworld`.
- [x] Implement fast full-load from the derived runtime image with sequential reads, minimal parsing/branching and bounded temporary allocations.
- [x] Cache only measured expensive decoding/precomputation work.
- [x] Keep the binary layout and serializer entirely inside `Vega.Terraria` / world infrastructure; Core sees only world contracts/snapshots.
- [x] Add precomputed runtime indexes only with real consumers and benchmarks.
- [x] Candidate indexes include chest lookup, entities/signs by section, wiring/liquid regions, spatial lookups, initial player-sync data and world map/operations consumers.
- [x] Fall back automatically to `.wld` when `.vworld` is absent, stale, incompatible or corrupt.
- [x] Accept direct `-world path/to/world.vworld` selection while retaining the companion `.wld` as the canonical save and recovery path.
- [x] Refresh `.vworld` after manual, automatic and shutdown saves through a coalesced atomic build queue, and flush the pending image during orderly shutdown.
- [x] Capture an immutable runtime tile snapshot after a successful canonical save and bind it to that exact `.wld` SHA-256 before asynchronous publication.
- [x] Persist and restore `Liquid` / `LiquidBuffer` queues and counters so a validated runtime snapshot can bypass repeated `QuickWater`, `WaterCheck` and startup liquid settling without losing pending liquid work.
- [x] Guard the liquid fast path with schema flags, checksums, queue bounds and the expected two-`WaterCheck` sequence; fall back to `.wld` if any invariant changes.
- [x] Log cache hit/miss, invalidation reason, source/cache size, validation/corruption errors and fallback behavior.
- [x] Integrate `WldLoadDuration`, `WorldImageBuildDuration`, `WorldImageLoadDuration`, `PostLoadInitializationDuration` and `WorldReadyDuration` with startup telemetry.
- [x] Benchmark cold/warm `.wld` against cold/warm `.vworld`, including peak memory, allocations, `WorldReady` and `NetworkReady`.
- [x] Decide from measurements whether `.vworld` should be enabled by default. Do not promise or assume a speedup before benchmarks.

### Level 3: chunked / memory-mapped backend, research only

- [x] Investigate chunked/memory-mapped world storage separately from the Level 2 full-load implementation.
- [x] Investigate lazy section loading for very large or multiple/sandboxed world contexts.
- [x] Validate Terraria runtime assumptions before changing in-memory world ownership/storage semantics.
- [x] Do not make lazy/mmap storage part of Vega 1.0 unless benchmarks and compatibility evidence justify it.

World-image invariant:

```text
.wld    = canonical compatibility/persistence format
.vworld = disposable optimized runtime representation
```

See ADR 0020 for format ownership, validation, fallback and persistence constraints.

Tile-only schema 1 and its reproducible benchmark harness showed no repeatable startup benefit. Schema 2 therefore captures prepared runtime tiles plus liquid queues after save. Live small/medium smoke runs reduced full load time by about 40%/26% and temporary allocations substantially, but `.vworld` remains disabled by default until repeated cold/warm and large-world results validate the new path. No speculative chest/sign/entity indexes or Level 3 lazy/memory-mapped backend were added. See [`docs/world-images.md`](world-images.md) and ADR 0020.

## Phase 12 — distribution and bilingual documentation

- [x] Polish the Russian and English repository README files around the current Vega 1.0 runtime rather than historical migration state.
- [x] Publish matching English and Russian project guides covering architecture, project layout, build, test, publish, startup, modules, operations and known limitations.
- [x] Document the release archive layout with runtime libraries under `libs/` and first-party modules under `ServerPlugins/`.
- [x] Document the automatic tag-triggered NuGet/GitHub release and the separate manual latest-tag server publication workflow.
- [x] Clarify the proprietary license scope, project ownership, third-party boundaries, redistribution restrictions and warranty terms without changing Vega into an open-source project.
- [x] Keep both language versions synchronized whenever commands, packaging, supported runtime modes or public contracts change.

## Phase 13 — TerraZ-derived security and connection invariants

Move server-wide invariants into Core/runtime, not the historical plugin implementations. Every rule lands as one typed vertical with a production regression test and structured observability; do not build a generic security rules engine first.

### Moderation identity from ExtendedBans

- [x] Replace the string-only `IBanService` boundary with typed ban subjects for account, UUID, exact IP and network/CIDR identities.
- [x] Normalize and validate IP/network subjects once as IPv4-only identities, with expiration and reason metadata; reject IPv6 and IPv4-mapped IPv6 instead of maintaining a second address family.
- [x] Provide one authoritative matching service reused by connection admission, administrative commands and cluster replication.
- [x] Keep persistence behind a narrow adapter and keep ban UI/command presentation outside the matching engine.
- [x] Add regression tests for overlapping IPv4 CIDRs, IPv4 normalization, IPv6 rejection, expired bans, account/UUID matching and duplicate replicated revisions.

### Packet and state validation from PacketGuard, StateGuard, LeashedEntityGuard and NoCoinDupe

- [x] Close the universal packet-safety slice: bounded/malformed payload rejection, supported item-sync indices/transforms/stack/type/prefix, projectile create/destroy ownership/index/type/finite transforms, player movement/world/inventory and portal bounds, tile/object/item-frame/chest bounds, chest resize/quick-stack indices, liquids, tile entities, gem locks, mass wiring, food/display placement, tile picking, leashed-entity anchors and the known coin-duplication path.
- [x] Keep configurable gameplay enforcement separate: repeated-death abuse policy, ghost/spectator restrictions, permission bypasses, proximity checks and corrective state synchronization require actual configuration/authority consumers rather than more universal packet guards.
- [x] Reject the known overflowed high-damage `PlayerDeathV2` exploit and forged death player identities through the typed Multiplicity boundary.
- [x] Route post-1.4.5 `TeamChangeFromUI` through native validation and reject a forged player identity.
- [x] Reject protocol-325 deprecated item packets `145/148`; validate supported item packets `21/90/151/160` before Terraria indexes or applies their state.
- [x] Decode through `TZ.Multiplicity`; no old raw-reader/packet-writer implementation enters Core.
- [x] Use typed `Allow`, `Reject` and `Violation` outcomes end to end; add `Correct` only with the first real Terraria-side correction consumer instead of shipping an unused outcome.
- [x] Add narrowly scoped rate limits/deduplication only where a measured protocol abuse requires them.
- [x] Expose bounded per-reason security counters plus rejected/violation totals through the existing network metrics path; anomaly counts remain observability, not proof of cheating.

### Connection metadata from ClientPlatformDetector, SSCHealthFix and UuidGuardExtension

- [x] Model transport endpoint, effective forwarded endpoint and forwarding provenance separately before adding Dimensions support.
- [x] Parse `PlayerPlatformInfo` through the typed packet pipeline, enforce sender-slot ownership and represent missing data as `Unknown`, never as assumed PC.
- [x] Expose platform and connection provenance through immutable player/operations snapshots with explicit authorization for sensitive fields.
- [x] Define connection/login phases before SSC exists so authoritative server character state can reject stale queued client state later.
- [x] Expose transport-neutral structured security signals for external firewall automation; shell, nftables and provider-specific enforcement remain outside Core.
- [x] Keep Dimensions disabled until ADR 0011 trust configuration and admission ordering are implemented and tested end to end.

Protocol 325 reports `PlayerPlatformInfo` as packet 164 with a sender slot and platform byte. Vega records the reported value without the legacy PC fallback, accepts `ClientUUID` only once in the protocol-accepted phase, and rejects forged slots or phase changes before vanilla dispatch. Direct connections still retain separate transport/effective endpoint fields so a future trusted-forwarding adapter cannot overwrite provenance. Operations snapshots are redacted by default; the local administrative TUI requests sensitive connection fields explicitly. Rejections also enter a bounded structured signal stream whose endpoint fields require the same explicit sensitive exposure.

The rate-limit slice intentionally carries only the observed TerraZ limits for expensive or state-transition packets already owned by Vega: chest open, chest placement, spectate, player death, liquid mutation and mass wiring. Counters are per connection and packet, use a one-second window, reset on disconnect and report ordinary rejection rather than declaring a client a cheater. The broad historical every-packet table is not copied into Core.

Optional gameplay enforcement is isolated in `config/security.json` and remains disabled by default for vanilla compatibility. When enabled it covers living-ghost and spectate policy, repeated-death windows, bounded tile/chest/liquid/wiring proximity and the `vega.security.gameplay.bypass` authority check. Rejected client state never reaches Terraria; server-owned state therefore remains the corrective source instead of a second client-authored mutation path.

## Phase 14 — TerraZ-derived runtime and module operations

These capabilities are shared runtime infrastructure, but their policies remain configurable or modular.

- [x] Complete TPS telemetry derived from TPSChecker with rolling windows, min/max timestamps, tick-duration p50/p95/p99 and structured low-TPS events.
- [x] Add an optional per-`WorldId` monotonic world-clock service derived from TimeSync; corrections run on the game thread and explicit time changes re-anchor the clock.
- [x] Add module-directory change detection and a visible `restart required` state derived from PluginLoader.
- [x] Add an opt-in administrative hot-reload host for modules that directly implement `IHotReloadableVegaModule`, with collectible load contexts and mandatory tracked cleanup.
- [x] Make hot reload disableable through `config/modules.json` and disabled by default; built-in `IVegaModule` revisions remain restart-only.
- [x] Remove dynamic command registrations before module disposal and verify that the collectible context is actually released; require restart after incomplete cleanup.
- [x] Surface world-clock, TPS and module-restart state through Operations/TUI snapshots without reading Terraria state from the UI thread.

The TPSChecker migration is complete as a source disposition: one game-thread sampler now publishes current, rolling-minute, lifetime min/max with UTC timestamps and bounded tick-duration p50/p95/p99. `tps`/`tpsstats`, low-TPS Operations warnings and the TUI consume that single immutable source. Phase 14 remains open for structured transition events, per-world attribution, TimeSync and module restart detection.

The PluginLoader migration is resolved as a deliberately unsafe, opt-in administrative capability rather than an implicit compatibility loader. With `config/modules.json` disabled, the bounded watcher only marks `restart required`. When enabled, only assemblies that directly export one `IHotReloadableVegaModule` enter an isolated collectible context. `plugin reload` stages the replacement before stopping the active revision; `plugin unload` detaches tracked hooks and commands in reverse order, stops background work, disposes the module and verifies collection of the old context. Any cleanup or collection failure degrades the module and requires restart. Explicitly composed first-party `IVegaModule` instances are never presented as hot-unloadable.

The TimeSync migration replaces its background reads and TShock mutation with a monotonic per-`WorldId` Core clock. The Terraria adapter observes and corrects only from the game tick, broadcasts corrected world data, treats a drift above the configured re-anchor threshold as an explicit time change, and exposes drift/correction/re-anchor counts through Operations/TUI. `config/world-clock.json` keeps the feature disabled by default.

## Phase 15 — administration primitives with first module consumers

Each Core capability must land with the named first consumer in the same vertical slice. The user-facing behavior remains a `ServerPlugin` or Operations UI feature.

1. [x] **TempGroup / ControlGroupOption**: implement expiring role leases, stale-expiry protection, delegated grant limits and audit records; keep temporary-group commands modular.
2. [x] **InvSee / TSExt_UICommand**: implement immutable inventory snapshots, game-thread mutation operations and centralized redaction/authorization for IP, UUID and other sensitive fields; keep inspection commands/UI optional.
3. [x] **Wipe**: implement validated world-transition/reset orchestration with registered persistence participants, cancellation and observable state; keep destructive command policy and confirmation in an admin module/UI.
4. [x] **AutoWorldCleaner**: implement world-scoped cancellable scheduling plus safe item/projectile snapshots and removal operations; keep cleanup thresholds and schedules in a module.
5. [x] **InfChests / ChestRegen**: implement stable chest identity, immutable versioned snapshots, game-thread transactional mutation and ACL/event hooks; keep refill, lock and regeneration policy in modules.
6. [x] **Mentions**: implement an ordered typed chat transformation pipeline separating content transformation, moderation and per-recipient rendering; keep mention behavior in a module.

`Vega.Modules.AutoWorldCleaner` now owns cleanup scheduling and policy behind its own opt-in `config/auto-world-cleaner.json`. Contracts expose immutable world-scoped item/projectile snapshots plus guarded removal handles, while `Vega.Terraria` is only the game-thread adapter that reads and mutates Terraria arrays. Each run performs snapshot, selection and removal in one dispatched game-thread action; item age and projectile identity are revalidated before mutation to reject stale handles. The module supports cancellable scheduling, online-player gating, non-overlapping runs, `worldclean`/`cleanworld` administration and bounded Operations status/warnings. The current Terraria adapter owns `WorldId.Primary`; future world backends can implement the same contract independently.

Temporary group assignment is now an account-scoped persistent role lease shared by SQLite, PostgreSQL and MySQL. Granting a lease atomically revokes the previous assignment, increments its revision and writes the administration audit before publishing the replacement to the in-memory authorization cache. Effective permissions resolve the active absolute expiry on demand, so no stale timer callback can revoke a newer lease. The built-in account administration adapter exposes bounded `temp`, `temp-clear` and `temp-list` operations and applies the same delegated-authority check as permanent group assignment.

The remaining Phase 15 slices now share the same ownership rule. Inventory inspection uses immutable revisioned snapshots and dispatches edits to the game thread; sensitive identity fields pass through one redaction policy. Chest policies use stable world/coordinate identity, optimistic transactions and typed before/after events. Wipe stages an immutable replacement and promotes it atomically only on the next process start after explicit confirmation, so the loaded canonical world is never replaced underneath Terraria. Ordinary chat passes through ordered transform, moderation and per-recipient render stages; Mentions is a renderer and mute enforcement is a moderation rule. These policies live in bundled modules, not in Core command/configuration or legacy TShock shapes. Other audited TerraZ products remain optional ServerPlugin ports and do not expand Core until their narrow capability is implemented by a real consumer.

### Explicitly outside Core

- [x] Port `Homes`, `HouseRegions`, `TpaRequest`, `Clans`, `SealedDressers`, `Mentions`, `ChatAboveHeads`, `ChatGuard`, `InvSee`, `Wipe`, `Region-Vision`, `InfChests` and `ChestRegen` only as `ServerPlugins` when their required Core capability exists.
- [x] Do not move plugin commands, configuration formats, database schemas, timers, static state or TShock class shapes into Core merely to speed up a port.

## Phase 16 — provider and observer-scoped scene runtime

FakeProvider is already a proven shared capability, not a single-plugin abstraction. The audited consumers include WorldEdit clipboard previews, FakeProviderClustering visibility layers, LobbyVisuals/TUI personal providers, hidden portals and animations, plus lobby/server compositions carrying the provider implementation. Vega should therefore own a stable provider/scene boundary while porting behavior rather than the Terraria/TSAPI-coupled API shapes.

### Contracts and ownership

- [x] Audit the current `TZ.FakeProvider.Abstractions` consumers and freeze the required use cases: ordered global/personal tile layers, observer sets, provider movement/resizing, copying, drawing, lookup and deterministic removal.
- [x] Define Vega-native `WorldId`/scene/overlay/observer identities and immutable tile/area data without exposing `Terraria.ITile`, `MessageBuffer`, TSAPI event args or mutable player objects from Contracts/Core.
- [x] Keep provider composition and observer policy behind narrow Core contracts; keep tile storage, Terraria hooks, section serialization and game-thread mutation in `Vega.Terraria`.
- [x] Give every provider/overlay an explicit owner and lifetime token so module stop, player disconnect, world unload and failed startup clean up deterministically.
- [x] Preserve stable ordering/layer semantics and make conflicting overlays observable instead of relying on plugin registration order.

### First consumer ports

1. [x] **WorldEdit**: port clipboard preview/mask providers, move/resize reuse and clear-on-session-end as the first complete consumer.
2. [x] **Region-Vision**: port personal region-border overlays and block interaction with phantom tiles through the shared observer scope.
3. [x] **LobbyVisuals/TUI**: port personal UI providers, hidden portals and animations without `dynamic` provider access.
4. [x] **FakeProviderClustering**: port player/NPC/projectile visibility layers and cluster transitions after the tile-overlay path is stable.
5. [x] **MiniLobby / SurvivalLobby-style compositions**: provide the equivalent Vega-owned lobby frame/portal/entity-cluster composition and regression tests without embedding or depending on the legacy TShock plugin repositories.

### Network consistency and performance

- [x] Apply the same observer/layer decision to outbound tile/entity visibility and inbound interaction validation; a client must not interact with state it cannot observe.
- [x] Parse/filter through the typed Multiplicity packet boundary and keep rate limits plus malformed-packet handling shared with Phase 13.
- [x] Support bounded dirty-region invalidation and section updates instead of redrawing complete providers for every small change.
- [x] Benchmark provider lookup, composition/section generation, managed allocation per personal overlay and mass observer changes with representative WorldEdit preview/mask plus lobby observer/entity churn workloads; run the harness in CI as a regression gate.
- [x] Expose active scenes/providers, owners, observer counts, dirty regions and degraded/failure state through Operations snapshots.
- [x] Do not replace `Main.tile` globally in the first slice. Evaluate a world-provider backend only after overlay consumers are migrated and Terraria/TerrariaRuntime ownership assumptions are measured.
- [x] Keep single-process multi-world virtualization and arbitrary lazy world storage separate from the provider API; they require their own evidence even though they may later reuse scene contracts.

Phase 16 is complete without the legacy global `Main.tile` replacement. `Vega.Contracts` owns immutable scene, overlay, observer and visibility identities; Core owns deterministic composition and leases; `Vega.Terraria` owns packet serialization and hooks. `Vega.Modules.Scenes` contains the WorldEdit preview/mask, Region-Vision border, lobby visual, hidden-portal and FakeProviderClustering consumers plus a Vega-owned lobby composition. Legacy `MiniLobby`/`LobbyServer` repositories remain external and are not embedded or referenced by Vega. Both outbound delivery and inbound interaction use the same decision. The renderer bounds its dirty queue and packet chunks, Operations exposes owners/conflicts/dirty history/visibility failures, and the representative scene regression harness runs in CI.

## Phase 17 — isolated multi-process worlds and sandbox orchestration

Deliver the first production sandbox implementation as supervised Terraria/Vega backend processes. Each active world owns its process, game thread and mutable Terraria state; the control plane coordinates lifecycle, routing and immutable status without proxying ticks, tiles or other hot-path operations. This is the preferred isolation model from ADR 0012 and does not depend on solving single-process virtualization.

### World definitions and lifecycle

- [x] Define persistent sandbox descriptors with stable `WorldId`, display name, template/source world, persistence mode, player capacity, access policy, expiry/TTL and enabled module set.
- [x] Implement create, start, stop, restart, reset and destroy operations with explicit state transitions, cancellation, timeouts and idempotent retries.
- [x] Materialize writable sandbox instances from immutable templates without modifying the source; use atomic promotion and recovery so failed creation/reset preserves the previous valid instance.
- [x] Distinguish temporary, persistent and externally managed worlds and make retention/cleanup policy explicit for each kind.
- [x] Ensure module stop, process exit and control-plane shutdown release leases and resources deterministically without deleting persistent world data.

### Process supervision and isolation

- [x] Add a backend process supervisor with readiness handshake, heartbeat, graceful shutdown, forced-termination timeout and bounded restart/backoff policy.
- [x] Keep world-local player, entity, region, scheduler and Terraria state inside the backend process; cross-world operations exchange typed commands and immutable snapshots only.
- [x] Define per-world CPU, memory, disk and concurrent-start limits plus a global admission budget so sandbox creation cannot starve the primary server.
- [x] Contain backend crashes and unhealthy worlds, preserve diagnostic logs and expose a typed degraded/failed state instead of silently rerouting players.
- [x] Make control-plane restart reconciliation discover already-running backends and orphaned instance directories without duplicating or corrupting worlds.

### Connection routing and player state

- [x] Complete the Phase 13 forwarding-provenance, trust and admission-order requirements before enabling sandbox routing.
- [x] Implement Dimensions-compatible routing as a Vega-owned boundary with authenticated backend identity, replay protection and explicit target `WorldId`; do not trust client-supplied forwarding metadata.
- [x] Provide explicit enter, leave and transfer operations with capacity/access checks, readiness gating, actionable failure results and safe fallback to the originating or primary world.
- [x] Define and implement per-sandbox SSC policy as inherit, copy-on-enter, isolated or disabled, including revision conflicts, transfer leases, shared/isolated scopes, Terraria character capture/apply and bounded checkpoint persistence on disconnect/transfer/backend loss.
- [x] Keep accounts, bans and baseline authorization server-wide while allowing deliberate typed world-local overrides for regions, warps, rules and module policy.

### Operations and administration

- [x] Expose all managed worlds through Operations/TUI with lifecycle state, endpoint, player count, uptime, TPS, CPU, memory, storage, restart count, template revision and TTL.
- [x] Route create/start/stop/reset/destroy/transfer actions through normal authorization, command and audit boundaries; the UI must not manipulate processes or world files directly.
- [x] Add structured events and bounded diagnostics for lifecycle transitions, rejected admission, resource pressure, failed transfers, crashes, restart suppression and cleanup.
- [x] Support controlled rolling backend updates with explicit version compatibility and no claim of zero-downtime player migration until proven by an end-to-end consumer.

### Performance and production validation

- [x] Establish budgets for idle and populated memory per backend, tick p50/p95/p99, cold/warm start time, template materialization, player transfer latency, control-plane overhead and shutdown time.
- [x] Benchmark increasing active/idle sandbox process counts with bounded template materialization, readiness, sampled resource snapshots and shutdown, and keep a real published-layout Terraria backend smoke as the implementation baseline. Sustained deployment-host NPC/player soak is a release-qualification gate, not an implementation blocker.
- [x] Prove that monitoring and cluster snapshots are sampled/bounded and do not introduce per-tick cross-process RPC or full-world serialization.
- [x] Add overload tests for simultaneous starts, crash loops, full capacity, low disk space, control-plane restart and backend loss during player transfer.
- [x] Default to bounded on-demand activation and idle TTL shutdown; do not keep every configured sandbox process resident without measured operational need.
- [x] Publish deployment guidance, conservative admission budgets, control-plane benchmark evidence and real-backend smoke evidence before marking the multi-process implementation ready for deployment qualification.

### Deferred single-process backend

- [x] Treat multiple live Terraria worlds inside one process as a separate research project after the multi-process API and consumers are stable.
- [x] Require evidence that Terraria/TerrariaRuntime globals, hooks, networking, entity ownership, world save/load and game-thread affinity can be isolated without broad static-state emulation.
- [x] Do not make single-process virtualization, lazy world storage or replacement of `Main.tile` a compatibility promise or prerequisite for the Phase 17 sandbox API.

Phase 17 is complete as the first isolated multi-process sandbox implementation. It has a persistent atomic catalog and immutable template materialization, signed readiness/heartbeat reconciliation, bounded crash recovery, shared read-only backend discovery, target-bound single-use transfer admission, and server-trusted account restoration. SSC supports `Inherit`, `CopyOnEnter`, `Isolated` and `Disabled` through a cross-process revisioned store, transfer leases and a Terraria character capture/apply adapter covering inventory, equipment, banks, loadouts, life/mana, appearance and permanent character flags. Operations/TUI exposes lifecycle and sampled resources; CI exercises representative process/scene benchmarks and the overload matrix. Sustained real-player/NPC soak on a deployment host remains a release-qualification activity and does not reopen the implementation phase.

## Phase 18 — migrate Vega to .NET 11

Active development moved to .NET 11 Preview 7 early under ADR 0024, with Runtime Async enabled repository-wide. The last verified .NET 10 revision and artifacts remain the production and rollback baseline until the preview branch passes the compatibility, startup, performance and soak gates below. Checked items record the completed development cutover; unchecked items remain production-promotion work.

### Dependency and compatibility gate

- [x] Audit .NET 11 support for TerrariaRuntime, ModFramework, MonoMod, `Multiplicity`, Terminal.Gui, MaxMind, test adapters and every runtime dependency before changing Vega target frameworks.
- [ ] Build and run a disposable compatibility branch on .NET 11 with zero suppressed loader, JIT, trimming, native-asset or hook failures.
- [x] Record required dependency upgrades and their behavioral/API changes; do not carry local forks or binding workarounds without an explicit owner and removal condition.
- [x] Decide the public-package compatibility policy: publish one authoritative `Vega` package on `net11.0`; require a separate consumer-backed decision before introducing a narrow package split or .NET 10 multi-target.
- [x] Confirm that every bundled `ServerPlugins` module targets a framework loadable by the .NET 11 host and rejects incompatible modules with an actionable startup diagnostic.

### Project and toolchain migration

- [x] Change the repository target framework and documented SDK baseline from `net10.0` to `net11.0` in one coordinated change, with Runtime Async enabled centrally for all first-party projects.
- [x] Update CI, tagged release, manual publication and local build instructions to install and use the pinned .NET 11 SDK feature band.
- [x] Upgrade analyzers, test infrastructure and build tooling, then resolve new warnings intentionally instead of globally suppressing them.
- [x] Preserve deterministic package metadata and assembly/file versions; publish the unpublished component assemblies together under the single authoritative `Vega` package ID.
- [x] Update the deployable server layout and assembly resolver only where .NET 11 requires it; retain `libs/` and `ServerPlugins/` ownership boundaries.

### Runtime validation

- [x] Run the complete unit, integration and architecture suite on .NET 11, including packet ownership, game-thread dispatch, module lifecycle, cluster scenarios and `.vworld` fallback/corruption paths.
- [ ] Smoke-test clean extraction and startup on every supported operating system/runtime identifier, including interactive world selection, plain console, TUI and headless modes.
- [ ] Validate TerrariaRuntime hooks, Terraria world load/save, networking, player join/leave, commands, shutdown and crash diagnostics against representative production worlds.
- [x] Pack and inspect the single unified `Vega` NuGet package, install it into a clean consumer project and verify that dependency assets resolve without relying on the Vega repository cache.
- [ ] Run upgrade tests for existing Vega configuration, logs, worlds, `.vworld` images and module deployments; document every incompatible operational change.

### Performance and rollout

- [ ] Compare .NET 11 with the recorded .NET 10 baseline for process startup, `WorldReady`/`NetworkReady`, tick p50/p95/p99, allocations, GC pauses, working set, packet throughput and shutdown time.
- [ ] Investigate material regressions before release and require measured evidence for runtime or GC tuning changes.
- [ ] Publish prerelease packages and a deployable release candidate for production-like soak testing before replacing the stable .NET 10 channel.
- [ ] Update English and Russian documentation, support policy, diagnostics and release notes with the new runtime prerequisite and rollback procedure.
- [ ] Mark the migration complete only after CI, packaging, clean-machine deployment, representative world startup and sustained server operation pass on .NET 11.

## Phase 19 — persistent administration, configuration and localization

Vega 1.0 currently has the modern command/authorization boundaries and several read-only contracts, but it does not yet provide a complete TShock-class administration surface. This phase turns those boundaries into a configurable, persistent server product. It must reuse the typed ban work from Phase 13 and the role-lease primitives from Phase 15 instead of creating parallel systems.

### Operator configuration

- [x] Define a documented deploy-time layout for server, storage, security and module settings under `config/`, with generated example files included in the release archive.
- [x] Establish deterministic precedence for command-line arguments, environment variables, Vega configuration, Terraria configuration and built-in defaults; show the source of each effective value in diagnostics without exposing secrets.
- [x] Validate configuration before starting authoritative services and report the exact file, property and rejected value; malformed optional module configuration must degrade that module without silently changing policy.
- [x] Support atomic, section-scoped reload only for settings proven safe to reload; mark port, world, storage-provider and other restart-only changes explicitly.
- [x] Provide `config show`, `config validate` and authorized `config reload` operations through the normal command/audit boundary.
- [x] Document world selection/generation, listener/password, slots, default culture, storage, logs, TUI, `.vworld`, modules and cluster configuration in both project-guide languages.

`config/server.json` composes launch settings before any authoritative service starts. CLI values win over `VEGA_*`, then JSON, then stock defaults; diagnostics retain the source and redact the password. A null world deliberately preserves native interactive selection. `reload`/`config reload` now atomically replace the validated effective configuration snapshot and publish cancellable `ServerReloading` plus post-apply `ServerReloaded` through the shared typed event hub so owned consumers can refresh safe state. Server/listener/world/storage settings remain restart-only and the command reports that boundary instead of pretending those process-owned resources were rebound live.

### Accounts, groups and permissions

- [x] Implement persistent account, group, inheritance and permission stores with stable identifiers, optimistic revisions and baseline schema initialization; the current in-memory session authorization cache is not the source of truth.
- [x] Resolve effective permissions deterministically from account assignment, group inheritance, explicit grants/denials and active temporary role leases, with cycle detection and bounded caching.
- [x] Add authorized commands for account lifecycle, group create/delete/list/show, parent assignment, permission grant/revoke, player group assignment and temporary group assignment.
- [x] Enforce delegated-administration limits from ADR 0014 so an operator cannot grant a role or permission above their own authority.
- [x] Persist every account/group/permission mutation and emit a structured audit record containing actor, target, before/after revision, reason and outcome.

The Vega 1.0 baseline provides the same account/group/role-lease tables through SQLite, PostgreSQL and MySQL, including case-normalized stable account identifiers, PBKDF2-SHA256 password verification, optimistic revisions, explicit grant/deny rows, cycle-bounded inheritance and an atomic administration audit row for every applied mutation. `account` and `group` commands use these services; the console can bootstrap the first administrator, while non-console delegation is bounded by the permissions the caller actually holds. Player account sessions project live base-group policy and the current account-scoped temporary-group override into command and region authorization, record last login, reject account bans and clear on logout/disconnect. Lease expiry is evaluated against absolute UTC time without player timers, and replacement revisions make stale expiry/revoke attempts harmless.

### Regions and world authorization

- [x] Implement the currently contract-only region service with persistent world-scoped region identity, bounds, priority, owner, members, groups and permission/flag ACLs.
- [x] Add game-thread-safe region admission/build/use checks through typed world/player contexts rather than module-specific tile hooks.
- [x] Add authorized `region` create/delete/list/show/resize/priority/owner/member/group/flag commands with overlap and world validation.
- [x] Expose immutable region snapshots and authorized mutations to Operations/TUI so `HouseRegions`, `Region-Vision` and sandbox worlds share one source of truth.
- [x] Add lifecycle and regression tests for overlapping priorities, renamed/deleted worlds, offline owners, permission inheritance and concurrent edits.

The Vega 1.0 baseline stores region identity by explicit `WorldId`, validates non-empty bounded rectangles, resolves overlaps by priority then smallest area and retains owners as offline-capable account names. Members, groups and boolean flags are versioned with the region; mutations use optimistic revisions and the shared administration audit. `region` commands always use `CommandInvocation.WorldId`, so identical names in different worlds do not collide. Runtime build, paint, door, sign, chest and tile-entity packets resolve protected regions from immutable per-world snapshots, use authenticated account/group identity, allow `vega.regions.bypass`, and invalidate the cache after committed mutations. Chest and tile-entity identifiers are resolved against Terraria-owned state before ACL evaluation, including active-chest ownership for item mutation. Operations publishes a bounded deep-immutable projection for all active world contexts; plain output and the configurable TUI Regions window consume it, while mutations still pass through the authorized command boundary. Lifecycle validation for renamed/deleted worlds remains open above.

### Bans and moderation commands

- [x] Complete the Phase 13 typed account/UUID/IP/CIDR ban matcher, persistence and connection-admission enforcement before presenting bans as production-ready.
- [x] Add authorized `ban`, `unban`, `ban list`, `ban show` and expiration/reason workflows over the same authoritative service used by login and cluster replication.
- [x] Add account/player mute primitives only with explicit identity, duration, reason, issuer and audit semantics; do not hide moderation state in command-local tables.
- [x] Expose bounded, redacted moderation views in Operations/TUI and verify that sensitive IP/UUID subjects require separate permissions.

The Vega 1.0 baseline stores revisioned account/player-name mutes with explicit issuer, reason and finite absolute UTC expiry across SQLite, PostgreSQL and MySQL. `mute`/`unmute` own creation, bounded listing, inspection and revocation. Active account identity wins over a display-name match; ordinary chat is cancelled before Terraria while Vega commands remain usable, and no per-player timer or command-local table owns moderation state. Operations limits active ban/mute projections to 200 each, redacts IP/UUID/CIDR ban subjects unless sensitive exposure is requested, and feeds both plain output and the configurable TUI Moderation window.

### Localization and command completeness

- [x] Implement ADR 0009 localization catalogs, module namespaces, deterministic fallback and explicit per-recipient culture without process-global culture mutation.
- [x] Add `/lang`, `/lang list`, `/lang <culture>` and `/lang reset`; persist registered-account preference and keep guest preference session-scoped.
- [x] Add a generated command catalog and `help`/command-discovery output that shows only commands the caller may use, including aliases, syntax and permission requirements.
- [x] Keep Vega commands and Terraria native commands reachable from plain console and TUI without competing stdin readers; capture native command output into bounded operations logs/results.
- [x] Audit the minimum built-in operator set beyond `serverinfo`, `checkupdates`, `kick` and `playing`, and cover save/shutdown, moderation, identity, groups, regions, configuration and language with end-to-end tests.

The native `lang` command and session-scoped guest preference are live for `en-US` and `ru-RU`; authenticated account selection is persisted and restored at login. The command switches only the recipient catalog, and stale module-catalog unload cannot remove a newer registration. Every built-in command now declares canonical syntax in the same descriptor that owns its name, aliases, permissions and console availability. `help` reads a live dispatcher snapshot, filters it with the dispatch authorization rule and includes syntax/aliases/permissions without maintaining a second catalog; dynamically loaded commands appear and disappear with their registration lease. Terraria retains stdin through world selection. Once TUI ownership begins, the native input thread reads the palette queue instead of `Console.ReadLine`, unknown Vega commands continue through Terraria, and native stdout/stderr is captured as bounded `terraria-console` Operations records rather than disappearing behind the dashboard. Authorized `save` joins the autosave barrier and awaits the same worker save; `shutdown`/`stop`/`off` requests Terraria's normal `SaveOnServerExit` server-loop termination rather than killing the process.

### Persistence, baseline schema and release validation

- [x] Ship the domain-oriented `Vega.Persistence` layer with deploy-ready SQLite and selectable PostgreSQL/MySQL providers; initialize it before world loading and fail startup when the selected store is unavailable.
- [x] Persist the first revision-aware domain consumer (global-ban creation/revocation tombstones) through the same contract used by cluster replication.
- [x] Freeze one clean Vega 1.0 baseline schema for every database provider and test creation/reopen from an empty store; do not preserve migration steps between unreleased development schemas.
- [x] Keep numbered, forward-only migrations and upgrade/rollback fixtures absent until the first public Vega schema has shipped.
- [x] Add clean-archive tests proving that an operator can extract the release, edit documented configuration, create the first administrator, start a world and manage groups/regions/bans without repository files.
- [x] Update English/Russian guides and the deployable archive only after command examples, generated configuration and persistence paths match the running server.

ADR 0022 establishes the persistence boundary without reproducing TShock's generic database helpers. Domain contracts never expose SQL or `DbConnection`. Before the first public release all current tables belong to one Vega 1.0 baseline; the temporary development sequence is not a compatibility contract. SQLite tests create, reopen and online-backup the complete baseline locally. The CI persistence job starts disposable PostgreSQL 17 and MySQL 8.4 services, initializes each empty store, reopens it, exercises a domain mutation and validates the provider's logical dump tool. Numbered migrations remain intentionally absent until a public schema exists.

The release workflows also copy the publish output to a repository-independent temporary deployment, parse every shipped JSON file, create a fresh SQLite baseline, generate a new Terraria world and require `NetworkReady`; the smoke copy is then discarded. The same dispatcher used by the live server has persistence-backed coverage for first-account/identity, group permission, region and typed-ban workflows, alongside save/shutdown and language/configuration tests. These are separate because Terraria deliberately disables its interactive reader for redirected CI stdin. Both guides and `artifacts/current-server` match the shipped Backups, Regions and soft-restart configuration paths.

### Scheduled deployment backups

- [x] Add `config/backups.json` with exactly three operator settings: `enabled`, `interval` and `retainedBackups`; write completed archives beneath the fixed deploy-local `backups/` directory.
- [x] Coordinate backup with the world-save barrier so the archive contains one consistent saved world and its matching `.vworld` cache when enabled, without stopping the server process or racing autosave/manual save.
- [x] Include a consistent database snapshot: SQLite online backup, PostgreSQL logical dump and MySQL/MariaDB logical dump through provider-owned implementations; never copy a live SQLite database/WAL pair opportunistically.
- [x] Stage every backup outside the visible archive set, then atomically publish a timestamped archive containing the world, database dump, effective non-secret configuration, version/schema manifest and checksums.
- [x] Reject overlapping scheduled/manual runs, expose progress and the last result through Operations/TUI/logs, and provide an authorized `backup` command without adding more persistent configuration knobs.
- [x] After successful publication retain only the newest `retainedBackups` archives; validate bounds and never delete files outside the resolved `backups/` directory.
- [x] Add cancellation, disk-full, dump-tool failure, world-save failure, archive-integrity, retention and restore-drill tests plus English/Russian recovery documentation.

`Vega.Backups` owns the deployment archive workflow outside Core. The save barrier waits for the canonical `.wld` and optional `.vworld`, database providers own online/logical dump semantics, staging is invisible until ZIP validation succeeds, and retention is path-confined. Operations/TUI and `backup status` expose one bounded state. Focused tests cover overlap, provider/save failures, cleanup, integrity, secret redaction, SQLite online restore and retention; `docs/backups.md` and `docs/ru/backups.md` define the operator restore drill.

## Phase 20 — in-process soft restart

Provide an authorized operational restart of the Vega application/module graph while the Terraria process, loaded world, listener and canonical persistence remain alive. This is a bounded soft restart, not a promise that native runtime updates can be applied without replacing the process.

### Restart boundary and lifecycle

- [x] Classify every runtime component as `PreservedInfrastructure`, `RestartableModule` or `ProcessRestartOnly`; keep Terraria/TerrariaRuntime, the loaded world, network listener, database connections/schema management, lifecycle coordinator and Operations control plane outside the restartable graph.
- [x] Replace one-shot static-module construction with explicit factories and owned registration scopes so every restartable module can quiesce, stop, remove hooks/commands/jobs and be recreated without retaining the previous generation.
- [x] Add a generation-aware module router and an exclusive restart barrier: reject concurrent restart requests, stop new module callbacks, drain bounded in-flight work, atomically swap the packet/event/command generation and then dispose the old graph.
- [x] Stop modules in reverse dependency order and start them in validated topological order with cancellation, per-module timeout and a total restart deadline.
- [x] Define session-state behavior explicitly: authenticated accounts, bans, regions and the world remain; module-owned temporary state is reset unless a module registers a versioned restart handoff contract.

### Failure handling and operator surface

- [x] Validate and construct the replacement graph before quiescing the active one where ownership permits; on startup failure attempt one bounded rollback to a freshly recreated previous configuration and otherwise enter an observable degraded state requiring process restart.
- [x] Treat collectible `ServerPlugins` through their existing cleanup/unload verification path; an uncollected context, native hook leak or restart-only configuration change must return `ProcessRestartRequired` rather than claim success.
- [x] Add `softrestart` (aliases `restartmodules` and `modules restart`) behind `vega.server.soft-restart`, with optional module targeting, explicit confirmation for restarting all modules, structured audit and progress visible in plain console, TUI and logs.
- [x] Keep native Terraria `restart`/`exit` semantics distinct and document exactly which configuration changes require a process restart or a world reload.
- [x] Add integration tests for connected players during restart, packet/event traffic at the generation boundary, scheduler cancellation, command removal, dependency ordering, failed replacement, rollback failure and repeated restarts without handler/thread/ALC growth.
- [x] Benchmark restart latency, dropped/rejected work, allocations and retained objects over at least 100 restart cycles; do not enable the command by default until the lifetime tests are stable.

`config/soft-restart.json` keeps the capability disabled by default. The runtime now owns one explicit bundled-module factory and routes packet, chat, chest, command and cleanup-status callbacks through a drainable generation boundary; preserved Terraria/player/world/persistence services are never captured by the replaceable graph. Module startup is topologically validated and individually bounded, shutdown is reversed, compatible handoff payloads are keyed/versioned/bounded, and one fresh rollback is attempted after replacement failure. Stable command proxies make the handler swap atomic without a dispatcher gap. Collectible external plugins remain on their stricter unload-verification path, and any dirty plugin state blocks soft restart. Lifecycle, rollback, concurrency, dependency-order and 100-cycle retention tests accompany a standalone allocation/latency benchmark; `docs/soft-restart.md` and its Russian counterpart define the exact process-restart boundary.

## Phase 21 — Core cohesion and extraction audit

Review whether `Vega.Core` still represents a cohesive application kernel or has accumulated independent product domains that should become separately owned modules. The goal is clearer ownership and smaller dependency surfaces, not a project-per-folder rewrite.

### Evidence and decisions

- [x] Inventory every `Vega.Core` namespace by responsibility, dependencies, public contracts, runtime registrations, test ownership, change frequency and real consumers; identify cycles and unrelated reasons to change.
- [x] Evaluate regions first: compare keeping region command/authorization policy in Core with a `Vega.Regions` domain module that owns commands, ACL decisions and Operations projections while `Vega.Persistence` keeps relational storage and `Vega.Terraria` keeps game-state adapters.
- [x] Evaluate administration/identity commands, moderation, packet-security policies, GeoIP/update services and module lifecycle as separate candidates; record `Keep`, `Extract` or `Merge` with concrete dependency and consumer evidence.
- [x] Require an ADR for every extraction, including API owner, persistence owner, composition root, migration path, failure behavior and why the split improves cohesion without introducing a generic service locator or cross-module chatter.
- [x] Establish architecture tests for the accepted dependency direction: domain modules may depend on Contracts and narrow infrastructure abstractions, while Core must not become a transit dependency or a container for unrelated implementations.

### Controlled extraction

- [x] Extract one complete vertical slice at a time with its tests and runtime registration; do not move only DTOs or create pass-through interfaces solely to reduce a project file count.
- [x] Preserve public Vega assembly/API compatibility where justified inside the single `Vega` package, or document and version intentional moves; avoid exposing Terraria, SQL, UI or plugin-host types through domain contracts.
- [x] Compare solution build time, server startup, package graph, packet hot-path cost and allocation/working-set baselines before and after each split; revert extractions that add indirection without measurable ownership benefit.
- [x] Update English/Russian architecture documentation, diagrams, module-author guidance and the migration ledger after each accepted ownership change.
- [x] Close the audit only when every Core namespace has one recorded owner/reason to remain and no extracted domain requires Core as a hidden mediator.

The audit is recorded in `docs/core-cohesion-audit.md`. Regions were the one accepted extraction: `Vega.Regions` now owns the complete command and packet-ACL application slice while Persistence keeps SQL and Terraria keeps authoritative object-position adapters. ADR 0023 records compatibility, failure and composition ownership. The implementation adds no packet adapter or Core mediation; Runtime constructs the unchanged hot policy directly. Administration/identity, moderation, generic packet security, GeoIP/updates and module lifecycle remain in Core for concrete recorded reasons rather than being split into project-per-folder assemblies. Architecture tests enforce the Contracts-only domain direction, and both guides, diagrams, release manifests and the migration ledger include the new owner.

## Phase 22 — parallel packet pipeline and game-thread budgeting

Reduce game-thread packet cost and tail latency by moving only pure, order-safe work off the authoritative Terraria thread. This is an internal runtime optimization: ordinary plugin authors must continue to see a simple typed, game-thread-safe API rather than worker lanes, buffer ownership or scheduling primitives.

### Baseline and packet classification

- [x] Add `Vega.PacketPipeline.Benchmarks` and record the current synchronous zero-copy baseline for packets/sec, ns/packet, allocations, CPU, GC, context switches and packet/tick p50/p95/p99/max.
- [x] Classify every inbound packet family for execution placement as `GameThreadOnly`, `PartiallyOffloadable` or `PureBeforeApply`, and separately record whether it is order-sensitive, world-state-sensitive, latency-sensitive and safely coalescable. This performance classification is not a semantic parity audit of every vanilla/TShock handler branch; that belongs to Phase 25.
- [ ] Start with player movement/state, projectile spawn/update/destroy and tile/world interaction; do not redesign the complete protocol before one measured vertical proves the model.
- [x] Keep `Multiplicity` as the protocol representation layer and preserve the existing zero-copy synchronous ingress for framing and typed views.

### Owned intents and worker lanes

- [ ] Never enqueue `IncomingPacket`, `Span<T>`, `ReadOnlySpan<T>` or references to a reusable receive buffer. Convert offloaded work into small owned immutable requests/intents before the receive-buffer lifetime ends; use explicit pooled-memory ownership only where a payload cannot be normalized immediately.
- [ ] Use a fixed, bounded packet-worker pool instead of `Task.Run` per packet, with bounded global and per-session queues and explicit overload/backpressure behavior.
- [ ] Preserve logical ordering within a player/session lane while allowing independent sessions to execute in parallel. Queue identity must include a stable session/generation token, not only a reusable Terraria player slot.
- [ ] Allow worker stages to perform only pure structural/protocol validation, normalization and CPU-heavy calculations that do not read or mutate Terraria-owned mutable state.
- [ ] Cancel or discard stale queued work on disconnect/reconnect, world transition, shutdown, soft restart or relevant module-generation replacement.

### Authoritative game-thread apply

- [ ] Convert successful worker output into typed domain intents/game commands and return only those bounded intents to the authoritative game thread.
- [ ] Perform all validation that depends on current player/NPC/projectile/item/chest/tile/world state immediately before mutation on the game thread; a background result is never proof that world state is still current.
- [ ] Keep ordinary gameplay/plugin hooks on the authoritative game thread at a documented before/after-mutation point so normal plugin code does not need synchronization knowledge.
- [ ] Add a configurable per-tick packet-apply budget so backlog cannot monopolize a tick; expose queue age/depth, worker utilization, apply time, drops, coalesces and overload actions through Operations telemetry.
- [ ] Permit packet-type-aware coalescing only when intermediate states are semantically disposable. Never coalesce independent actions such as tile mutations, item use or chest operations merely to improve throughput.

### Plugin API boundary

- [x] Keep the standard module/plugin surface synchronous-looking, typed and game-thread-safe by default: commands, gameplay hooks, player operations and ordinary domain events must not expose `Channel`, worker lanes, buffer lifetime, generation routing or manual thread switching.
- [x] If a low-level ingress validator API is required, make it a separate opt-in advanced surface with explicit `PURE / NO TERRARIA MUTABLE STATE / NO BLOCKING I/O / MAY RUN CONCURRENTLY` semantics.
- [x] Provide one narrow supported mechanism for genuinely asynchronous plugin I/O and return-to-game-thread application rather than requiring every plugin to invent its own synchronization scheme.

### Validation and rollout

- [ ] Compare at least synchronous zero-copy, queued single-worker, ordered multi-worker and ordered multi-worker plus safe coalescing variants under representative concurrent-player and packet-flood loads.
- [ ] Add regression/stress coverage for per-session ordering, stale intents, reconnect slot reuse, entity/world changes between validation and apply, queue overflow, pooled-memory lifetime, cancellation, shutdown, soft restart and malicious bursts.
- [ ] Roll out packet families incrementally with shadow/diagnostic instrumentation first where useful; retain the synchronous path for any family whose queueing, cache or synchronization overhead exceeds the measured gain.
- [ ] Mark the phase successful only when production-like measurements reduce game-thread CPU and/or p95/p99 tick latency without increasing correctness failures or unacceptable packet latency.

Packet-pipeline invariant:

```text
network/worker threads = pure preparation of owned packet intents
game thread             = current-state authority, plugin gameplay hooks and mutation
```

The first Phase 22 slice is recorded in `docs/packet-pipeline.md`. Every Terraria packet type now has a conservative internal profile; the current security families are only partially offloadable and remain synchronous. The reproducible `Vega.PacketPipeline.Benchmarks` harness measures the `PlayerUpdate` zero-copy view plus ownership-policy path and emits a Linux context-switch counter in CI. The first local Windows baseline exposed 232 bytes allocated per packet, so production worker queues and coalescing remain intentionally unimplemented until that cost and the ordered-intent variants are measured.

## Phase 23 — selective NativeAOT and runtime process specialization

Use NativeAOT where a component is naturally a small isolated executable with a fixed dependency graph. Do not force the main Vega server or its dynamic plugin ecosystem into NativeAOT merely for startup or footprint gains.

### CoreCLR server boundary

- [x] Keep `Vega.Server` on CoreCLR with tiered JIT/dynamic PGO and optional ReadyToRun unless measurements and a future architecture explicitly justify changing the plugin model.
- [x] Keep the Phase 22 packet hot path in-process on CoreCLR; do not add IPC, serialization and extra context switches solely to place packet workers in a NativeAOT process.
- [x] Preserve managed `ServerPlugins`, collectible `AssemblyLoadContext`, hot reload and soft-restart generation boundaries as first-class server capabilities.

### NativeAOT candidates

- [x] Audit isolated executables in this order: launcher/bootstrapper, updater, crash reporter, standalone admin/world tools, sandbox supervisor, backup/world workers and cluster relay/gateway.
- [x] NativeAOT-enable a component only if it has no requirement for arbitrary managed plugin loading/runtime code generation and its trimming/AOT dependency graph is clean.
- [ ] Compare CoreCLR self-contained, ReadyToRun and NativeAOT variants for startup, working set/private memory, publish size, CPU, throughput/latency and operational/build complexity before selecting a deployment mode.
- [x] Keep an ordinary CoreCLR variant when NativeAOT creates dependency, diagnostics, platform or maintenance costs that outweigh measured startup/footprint benefits.

### AOT-compatible library boundaries

- [x] Enable AOT/trimming compatibility analyzers for pure libraries where useful, starting with contracts and protocol/domain utility layers, even when those libraries still execute inside the CoreCLR server.
- [x] Remove accidental reflection/dynamic/runtime-code-generation patterns from those layers only when doing so improves their architecture or enables a real NativeAOT consumer; do not contort dynamic server/plugin boundaries for analyzer purity.
- [x] Add architecture and packaging tests so AOT-oriented helpers cannot pull Terraria mutable state, plugin-host implementation or UI concerns into otherwise isolated executables.

### Rollout

- [ ] Stabilize and benchmark Phase 22 independently before attributing server performance changes to NativeAOT work; do not combine both optimizations into one unmeasurable migration.
- [ ] Ship the first NativeAOT executable as a narrow operational component, validate clean-machine deployment and crash/diagnostic behavior, then expand only from measured evidence.
- [ ] Document per-component runtime mode and fallback behavior in both English and Russian deployment guides.

Runtime invariant:

```text
Vega.Server                         = CoreCLR + JIT/PGO + dynamic plugins
small isolated fixed-function tools = NativeAOT when benchmarked and compatible
```

The Phase 23 audit is recorded in `docs/native-aot.md`. `Vega.Server` is currently the only product executable and is intentionally fixed to CoreCLR; benchmark and test executables are not shipping candidates. The server project rejects NativeAOT, emits tiered-compilation/dynamic-PGO runtime settings and keeps ReadyToRun optional. `Vega.Contracts` is the first analyzer-enforced AOT-compatible boundary. No standalone AOT component was invented: comparison and rollout remain open until a real fixed-function executable exists and the independent Phase 22 performance gate is complete.

## Phase 24 — plugin API ergonomics and anti-framework audit

Make Vega's external plugin-development experience no harder than TShock for ordinary server extensions while preserving Vega's internal ownership, threading and capability boundaries. Architectural complexity is allowed inside the runtime; it is not allowed to become mandatory knowledge for authors who only need commands, gameplay hooks, player operations, configuration or ordinary persistence.

Ergonomic parity with TShock is a product requirement, not API compatibility: Vega does not reproduce `TShockAPI`, Terraria globals or legacy class shapes merely to make examples look familiar.

### Golden-path plugin surface

- [x] Define one obvious public entry path for an ordinary plugin and keep startup/lifecycle boilerplate minimal. Evaluate a small `VegaPlugin`-style facade or equivalent only if it measurably reduces author complexity without becoming a service locator.
- [x] Make the common surfaces immediately discoverable: command registration, typed gameplay hooks, player lookup/messaging/operations, permissions, logging, configuration, scheduling and plugin-scoped persistence where a real consumer requires it.
- [x] Hide registration leases, generation routing, collectible `AssemblyLoadContext`, worker lanes, packet-buffer ownership, game-thread queues and cleanup bookkeeping behind the host for ordinary plugins.
- [x] Track commands, hooks, scheduled work and other registrations automatically within the plugin lifetime so normal unload/reload does not require hand-written reverse-registration code.
- [x] Keep ordinary hooks game-thread-safe and synchronous-looking by default. A plugin handling join/chat/build/projectile/gameplay events must not manually marshal back to the Terraria thread.
- [x] Provide one documented async-I/O pattern for database/HTTP/file work with a narrow supported way to apply the result on the game thread; do not require plugin authors to understand `SynchronizationContext` or internal scheduler topology.
- [x] Keep low-level packet ingress, concurrent validators and other dangerous/high-performance surfaces in an explicitly separate advanced API rather than making their constraints infect normal hooks.

### Representative plugin usability suite

Create small real plugins that compile and run against the public SDK and use them as API tests, not merely documentation snippets:

1. [x] one command with permission, aliases and reply;
2. [x] player join/leave hook with messaging;
3. [ ] cancellable build/gameplay hook;
4. [x] player lookup plus kick/teleport/message operation;
5. [x] JSON configuration with reload/validation appropriate to plugin scope;
6. [x] small persistent plugin state without direct access to Vega's internal database connection;
7. [x] scheduled/repeating gameplay task with deterministic cancellation on unload;
8. [x] asynchronous external I/O followed by a safe game-state action;
9. [x] hot reload/unload with no leaked commands, hooks, timers, tasks or load context;
10. [x] one advanced typed-packet validator demonstrating the deliberately higher-complexity opt-in path.

- [ ] Compare each ordinary sample against an equivalent TShock plugin for boilerplate lines, number of framework concepts, required lifetime-management calls and documentation steps. Vega may use different APIs, but a common plugin should not require materially more ceremony.
- [x] Add compile-time CI fixtures for the golden-path samples so public API refactors cannot silently turn simple plugin scenarios into architecture exercises.
- [x] Add at least one out-of-repository clean consumer build against published/local NuGet packages to prove plugin development does not depend on Vega source-tree internals.

### SDK, templates and documentation

- [x] Define the minimal public package set needed to write plugins; a simple plugin should not reference runtime implementation, Terraria, TerrariaRuntime, persistence providers or UI assemblies.
- [x] Provide a maintained `dotnet new` plugin template or equally direct starter project that builds immediately and contains one command plus one typed hook.
- [x] Publish a short "first plugin" guide before the deep architecture guide. The first successful command should not require reading ADRs, runtime composition documentation or packet internals.
- [x] Document advanced concepts separately: background work, low-level packet validation, custom lifecycle behavior, hot-reload restrictions and performance-sensitive APIs.
- [x] Keep XML documentation and IDE completion useful enough that common command/hook/player operations are discoverable without opening Vega source.
- [x] Provide migration examples from representative TShock plugin patterns while explicitly mapping behavior rather than promising source or binary compatibility.

### Anti-framework audit

- [x] Audit the public plugin-facing contracts for speculative abstractions, pass-through interfaces, giant context objects and concepts exposed only because the runtime needs them internally; remove or internalize them when no external consumer needs them.
- [x] Do not expose a universal `IServer`/service-locator object that hands every subsystem to every plugin. Prefer small curated convenience surfaces backed by narrow capabilities.
- [x] Do not introduce Ninject/Orion-style global service replacement where one plugin can silently replace a core implementation for unrelated consumers. Extensibility must use explicit owned extension points.
- [x] Avoid reflection-heavy mandatory registration. Attributes or source generation may be offered as convenience only when explicit registration remains understandable, testable and cheap.
- [x] Keep snapshots and capability separation internally meaningful, but add ergonomic facade methods where repeated ordinary plugin code otherwise has to compose several low-level services for one obvious action.
- [x] Treat "technically pure but miserable to use" as an API defect. Do not preserve an abstraction solely because it makes the dependency diagram prettier.

### Performance and compatibility guardrails

- [x] Benchmark the convenience facade and standard hook/command path against the underlying native contracts. Ergonomics must not add material packet/tick allocations, reflection scanning or hidden blocking work.
- [x] Keep the simple API as a thin facade over the same authoritative services used by Core/runtime; do not create a second behavioral implementation for plugins.
- [x] Preserve explicit world/session identity where correctness requires it, but provide sensible primary-world/current-session conveniences only where their semantics are unambiguous.
- [ ] Version the public plugin SDK deliberately and add API compatibility checks once the first supported external SDK surface is declared stable.

### Exit criteria

- [ ] A developer unfamiliar with Vega internals can create, build, install and run the template plugin using the public documentation without reading the source tree.
- [x] The ordinary sample plugins require no knowledge of `Channel`, packet lanes, buffer lifetimes, module generations, `AssemblyLoadContext`, Terraria globals or manual thread affinity.
- [x] Commands and ordinary gameplay hooks are approximately TShock-class in authoring complexity while remaining typed and safe by default.
- [x] Advanced APIs remain available for packet/security/runtime specialists without contaminating the golden path.
- [ ] Complete the phase only after a public-surface review removes accidental framework complexity revealed by the sample plugins.

Plugin API invariant:

```text
Vega runtime internals may be complex.
Ordinary Vega plugins must not be.
```

## Phase 25 — vanilla/TerraZ behavioral audit and client compatibility

Recover server behavior that the capability-level migration audit did not enumerate. The goal is not source/API compatibility with TShock and not a line-for-line port. Vanilla Terraria defines protocol and world semantics; TerraZ TShock is a source of security, administration and operator behavior. Each behavior must receive an explicit Vega-owned `Keep`, `Replace`, `Module`, `Native passthrough` or `Drop` decision backed by executable evidence.

### Pinned evidence and durable ledger

- [x] Pin the current TerraZ reference to `TerraZ-Team/TShock` `general-devel@81175b934497f9ef0ee5b5200334e4260b0cc06c`; its current registries contain 71 `GetDataHandlers` packet entries and 95 built-in `Command` registrations.
- [x] Pin the inspected Terraria 1.4.5.8 Windows server executable (`26,028,032` bytes, SHA-256 `D87E3FAF08637F6BE8882C63E7F11FB7E792B0230006309618473ECE0F863E1E`) and protocol `326` as the current binary baseline.
- [x] Record the user-verified decompiled Terraria 1.4.5.7 source at [`JonataOliveiraa/Terraria1.4.5`](https://github.com/JonataOliveiraa/Terraria1.4.5) `main@0459d7b61e35f15354a40fb9d1422d84e9bb4f2c` as the readable comparison baseline. Resolve any 1.4.5.7/1.4.5.8 difference against the pinned 1.4.5.8 binary and captured protocol behavior.
- [x] Create and maintain `docs/server-behavior-audit.md`. Every row must record source revision, file/type/method or packet/command registration, branch/invariant, Vega decision, owner, implementation status and executable evidence. Do not delete or collapse unresolved rows into broad capability labels.
- [ ] Distinguish vanilla protocol/world behavior, TShock hardening/operator behavior, TerraZ-specific deltas and optional gameplay policy. A TShock choice is not automatically a server invariant.
- [x] Add a CI structure check for the ledger and require an explicit refresh when the Terraria protocol, TerrariaRuntime input binary or pinned TerraZ revision changes.

### Client connection and UUID regression

- [ ] Document the exact vanilla client/server state machine for both password and passwordless joins: `ConnectRequest (1)`, slot assignment/password challenge, player info and inventory burst, `ClientUUID (68)`, platform/host-token variants, world request/data, section request/transfer, spawn, `FinishedConnectingToServer (129)`, disconnect, reconnect and slot reuse.
- [x] Verify TerrariaRuntime `MessageBuffer.GetData` hook timing and the meaning/bounds of `start`, `length` and `readOffset` against the actual 1.4.5.8 IL before changing admission policy.
- [x] Fix the known UUID compatibility defect: vanilla loads `ClientUUID` from configuration and transports it as an opaque string, while current Vega requires exactly a canonical 36-character GUID and a 37-byte payload. Accept a non-empty, bounded, completely consumed protocol token compatible with vanilla/TerraZ behavior; preserve exact stable comparison semantics and never treat this client-controlled value as cryptographic authentication.
- [x] Replace GUID-only tests with serialized packet regressions for a fresh vanilla GUID, a persisted non-GUID legitimate token, empty/whitespace input, UTF-8 and length boundaries, truncated/trailing payloads, duplicate/late packets, disconnect cleanup and reconnect slot reuse.
- [ ] Capture a real vanilla-client packet transcript and replay it through Vega for password/no-password, new/existing client configuration, SSC/non-SSC and supported platform variants. Assert state transitions, outbound responses and disconnect reasons, not merely individual packet acceptance.
- [ ] Test Dimensions and sandbox admission end to end: forwarded endpoint provenance must be established before ticket lookup, ordinary primary-world joins must not require a transfer ticket, sandbox tickets must bind the bounded opaque UUID token plus endpoint/session, and stale metadata must not survive slot reuse.

Current evidence for the UUID defect is concrete: Terraria 1.4.5.8 initializes a new value with `Guid.NewGuid().ToString()`, later loads `ClientUUID` from preferences without format normalization, and packet 68 writes the resulting string. The vanilla server reads that string without GUID parsing. Current TerraZ TShock rejects an empty UUID when configured but persists UUID values in a 128-character database column. Vega's stricter `Guid.TryParseExact("D")`, 36-character and 37-byte checks therefore reject protocol-valid persisted client values.

### Packet-handler semantic ledger

- [ ] Enumerate all 71 packet registrations in the pinned TerraZ `GetDataHandlers` table and every client-to-server packet/branch in the pinned vanilla protocol, including protocol 326 additions and packets TShock does not intercept.
- [ ] For each handler branch, record connection-state prerequisite, sender-slot/entity ownership, field/range/finite-value checks, permission/configuration gates, rate/spam limits, world-state reads, mutation, correction/resend, relay suppression, kick/disable behavior and fail-open/fail-closed semantics.
- [ ] Compare each behavior against the current Vega ingress path and Terraria's native handling. Choose `Keep`, `Replace`, `Module`, `Native passthrough` or `Drop`; do not count a coarse packet-family or Phase 22 execution-placement classification as semantic coverage.
- [ ] Add serialized regressions for every accepted/replaced security invariant and an explicit rationale for every drop. Include malformed, boundary, wrong-phase, duplicate, stale-session and hostile-burst cases plus the authoritative outbound correction/relay behavior.
- [ ] Audit TShock's per-tick/bouncer counters, temporary state, cleanup and reset paths so retained checks cannot leak across reconnects, worlds or runtime generations.
- [ ] Complete this semantic baseline before moving a packet family into Phase 22 worker lanes; parallelizing an incompletely understood handler is not an optimization.

### Commands and operator behavior

- [ ] Enumerate all 95 built-in TerraZ command registrations with canonical names, aliases, permissions, console availability, parsing/selectors, side effects, audit behavior and configuration/database dependencies.
- [ ] Assign each command `Vega built-in`, `Terraria native passthrough`, `Optional module` or `Drop`. Verify native dedicated-server commands against 1.4.5.8 rather than assuming that a command name exists, and record operator impact for every omission.
- [ ] Review each retained/replaced command method branch for target ambiguity, delegated authority, offline-account behavior, confirmation, save/game-thread affinity, failure response and audit logging. Port semantics through Vega services, never through TShock class shapes.
- [ ] Add end-to-end command tests for player and console callers, aliases, permissions, selectors, persistence, game-thread mutation and localized success/failure output. Make intentionally absent commands visible in the migration/operator guide rather than silently missing.
- [ ] Audit the current TerraZ REST registrations separately from chat/console commands. Only introduce remote administration routes when Vega has an owned authentication, authorization, rate-limit, audit and lifecycle model; otherwise record an explicit drop.

### Server lifecycle and non-command subsystems

- [ ] Audit join admission beyond UUID: protocol/version, server password, bans, whitelist, duplicate names, character difficulty/world mode, SSC/login/account binding, reserved slots, GeoIP/proxy identity and kick/disconnect messaging.
- [ ] Audit authoritative save, shutdown/no-save, crash recovery, configuration reload, world/event mutation, groups/accounts/permissions, mute/chat, regions/protections, item/projectile/tile bans, console/log routing, plugin lifecycle and background cleanup.
- [ ] Compare vanilla, TerraZ and Vega exception/resynchronization behavior around malformed packets and failed mutations. Retain native behavior where it is correct; add Vega policy only for a documented invariant or operator requirement.
- [ ] Run real-client, clean-world, existing-world, reconnect, sustained-operation and malicious-input smoke suites against the resulting decisions. Feed measured hot spots such as NPC AI into profiling/optimization phases only after behavior is pinned.

### Exit criteria

- [ ] Every pinned vanilla inbound connection/packet branch and every TerraZ packet/command registration has a resolved ledger row with executable evidence or an explicit, reviewed drop rationale.
- [ ] A stock legitimate 1.4.5.8 client completes passwordless and password joins without Vega-only handshake rejection, while malformed and unauthorized variants fail with stable reasons and no stale session state.
- [ ] Operator documentation describes the actual Vega command/administration surface and every intentional TShock difference.
- [ ] No TShock/TSAPI compatibility assembly, namespace or class shape re-enters the production graph as a consequence of the audit.

Behavioral-audit invariant:

```text
vanilla binary/protocol = semantic authority
TerraZ TShock           = security and operator behavior reference
Vega                    = independent implementation with explicit evidence
```

## Phase 26 — structured logging, durable indexing and observability foundation

Phase 6 introduced the minimum structured event feed required by Operations/TUI. That completed slice is not the end-state logging architecture. This phase replaces the TShock-style "formatted string is the log" model with a Vega-owned infrastructure subsystem for durable structured events, searchable local indexing, live consumption and future observability backends. It is infrastructure for diagnostics, administration, MCP/debug APIs and later observability work, not a cosmetic logging refactor.

### Stable structured logging API and event model

- [ ] Define one Vega logging API with `Microsoft.Extensions.Logging` semantics, either directly through compatible `ILogger` abstractions or through a narrow Vega-owned compatibility layer. Backend selection such as Serilog must remain composition/infrastructure detail.
- [ ] Do not expose a concrete logging backend to plugins. The public Plugin SDK must provide a stable plugin-scoped logger/context whose source/plugin identity is supplied by the host rather than repeated manually at every call site.
- [ ] Define a durable structured event schema containing at least UTC timestamp, level, category, event type / event id, source/subsystem, plugin identity, player id, player name, IPv4 address where authorized, connection id, command, packet id/type, world, exception and arbitrary additional structured properties.
- [ ] Define stable Vega event ids/types for important runtime, security, command, persistence, world, plugin and administration events instead of relying only on rendered message text as machine identity.
- [ ] Preserve message templates and typed properties independently from the human-rendered console message. A structured property must not need to be reparsed from a formatted string later.
- [ ] Support nested scopes/context so common fields propagate automatically through player/session handling, a connection lifetime, command execution, packet processing, plugin invocation and other related operations.
- [ ] Keep context lifetime explicit and bounded. Disconnect, slot reuse, plugin unload, soft restart and sandbox/world transition must not leak stale scope fields into later unrelated events.

### Asynchronous pipeline, authoritative storage and rotation

- [ ] Make an append-only JSONL/NDJSON event log the default authoritative long-term local log storage. The log file is the source of truth; secondary indexes and views are disposable derived state.
- [ ] Route producers through a bounded asynchronous pipeline/channel so file I/O, compression and secondary indexing never block the Terraria game/server tick path.
- [ ] Define measured queue capacity and backpressure behavior instead of an unbounded buffer. Record queue depth, queue age, dropped/deferred event counts and sink failures through bounded diagnostics that cannot recursively flood the same failed pipeline.
- [ ] Define severity-aware overflow behavior explicitly. Critical/error and audit/security-significant events must not disappear silently; if durability cannot be maintained, surface a deterministic degraded/failure signal and use a bounded emergency fallback rather than blocking the game thread indefinitely.
- [ ] Preserve event ordering metadata sufficient to diagnose concurrent producers without pretending that unrelated concurrent operations have one magical global execution order.
- [ ] Implement log rotation by date and maximum file size, with deterministic file naming and safe continuation across process restart.
- [ ] Support optional post-rotation compression of closed files. Compression must run outside the game thread and failure must leave the original closed log intact.
- [ ] Define flush/shutdown semantics so orderly shutdown drains a bounded amount of pending logging work without hanging the server forever; crash recovery must tolerate a truncated final JSONL record.
- [ ] Keep human-readable console output as a presentation sink derived from the structured event. Plain console, TUI and headless modes must not require changing the stored event shape.

### Secret and sensitive-data policy

- [ ] Introduce centralized redaction/suppression for passwords, authentication tokens, API keys, session secrets and other explicitly classified secret values before events reach durable or live sinks.
- [ ] Do not depend on authors remembering to redact every interpolation manually. Provide safe helpers/types and tests for known secret-bearing configuration, authentication and command paths.
- [ ] Keep authorization for sensitive identity fields such as IP separate from secret redaction: an address may be queryable by an authorized administrator without making passwords or tokens loggable.
- [ ] Add structured tests proving that server passwords, auth tokens, API keys and session secrets cannot appear in JSONL, SQLite FTS content, console rendering or the live event stream through the supported APIs.

### Rebuildable SQLite search index

- [ ] Build a local SQLite index as a performance layer over JSONL, never as the only storage of log events. Deleting or corrupting the index must not destroy the authoritative log history.
- [ ] Index at minimum timestamp, level, category, event type/id, player id, normalized player name, plugin, command, packet id/type, IPv4 address and connection id, plus stable file/record identity needed to locate the authoritative event.
- [ ] Evaluate and use SQLite FTS5 where it is operationally justified for full-text search across rendered/template message text, exception text and selected textual properties.
- [ ] Keep indexing asynchronous from gameplay producers and define bounded catch-up behavior when the indexer falls behind or is temporarily unavailable.
- [ ] Add `vega logs reindex` plus the equivalent internal administration API. Reindex must rebuild a fresh index entirely from JSONL, validate it and promote it atomically rather than mutating a damaged index in place.
- [ ] Make startup tolerant of a missing/stale/corrupt index: durable logging continues, index state is reported as degraded/rebuilding, and operators can recover without losing the source log.
- [ ] Version the JSON event schema and SQLite index schema independently. An index schema change must be resolvable by reindexing rather than rewriting authoritative historical log files.

### Query service and live event consumption

- [ ] Introduce one Core/Operations-facing Log Query API so TUI, REST API, MCP/debug tooling and future administration clients never parse JSONL files or query the SQLite schema directly.
- [ ] Support structured filters, inclusive/exclusive time ranges, deterministic sorting, limit, cursor-based pagination and full-text search without exposing storage-specific SQL through public contracts.
- [ ] Support combining multiple filters in one request, including player identity, plugin, command, packet, connection, source/category, level/event type and time window.
- [ ] Optimize and cover the common operator queries: all events for one player; errors in the last N minutes; commands executed by a player; events from one plugin; rejected/malformed packets; events for one connection id; exception-text search; and mixed multi-field filters.
- [ ] Provide a bounded live stream/subscription of newly accepted structured events for TUI, REST streaming and MCP/debug consumers without repeatedly rereading log files.
- [ ] Define live-stream overflow and slow-consumer behavior separately from durable storage. A slow UI subscriber may lose live presentation events, but it must not be able to stall durable logging or the game thread.
- [ ] Enforce sensitive-field authorization and redaction consistently in historical queries and live streams instead of allowing each frontend to invent its own security policy.

### Logging levels and sink extensibility

- [ ] Support independent configured minimum levels for Vega Core, named subsystems/categories and each plugin, with safe runtime reload where practical.
- [ ] Preserve the stable plugin logging API if the default backend changes. Plugin assemblies must not take a source dependency on Serilog sinks, SQLite, JSONL serializers or future remote collectors.
- [ ] Define sink/export extension points that can later add PostgreSQL, ClickHouse, Loki, OpenTelemetry or a remote collector without changing plugin code or the authoritative event model.
- [ ] Keep every external observability system optional. A standalone Vega installation must retain complete local structured logging, indexing and querying with no network service or external database.
- [ ] Treat remote sinks as additional consumers, not as the local source of truth unless a future separately reviewed architecture deliberately changes that invariant.

### Migration of existing logging call sites

- [ ] Inventory existing Core/runtime/module/plugin logging and Operations structured-event calls before replacing them. Classify which strings already contain player, command, packet, connection, world, plugin, result or exception semantics that belong in fields.
- [ ] Migrate by semantic event, not by mechanical text substitution. A call such as `Log.Info($"{player.Name} executed command {command}")` must become a message template with `PlayerName`, `Command` and an appropriate stable event id/type rather than one preformatted opaque string.
- [ ] Migrate security, command audit, lifecycle, persistence, world/sandbox, plugin lifecycle and packet rejection paths first because they are the primary consumers for MCP/debug and incident analysis.
- [ ] Preserve intentional human wording independently from machine properties so localization/presentation changes do not break search or automation.
- [ ] Remove obsolete parallel logging/event stores only after TUI/Operations and all runtime consumers use the new pipeline; do not keep two authoritative log histories indefinitely.

Example target shape:

```csharp
_logger.LogInformation(
    VegaEvents.CommandExecuted,
    "Player {PlayerName} executed command {Command}",
    player.Name,
    command);
```

Target architecture:

```text
Core / Plugins
      │
      ▼
Structured Logging API
      │
      ├── Console sink
      ├── JSONL append-only authoritative storage
      └── Live event stream
                │
                ▼
        SQLite rebuildable index
                │
                ▼
          Log Query Service
          ├── TUI
          ├── REST API
          └── MCP / debug tooling
```

### Validation and exit criteria

- [ ] Add tests for structured property preservation, scopes, plugin identity, disconnect/slot-reuse cleanup, exception capture, secret redaction and independent per-category/plugin log levels.
- [ ] Add rotation/restart/truncated-record/compression tests plus complete reindex tests from multiple rotated JSONL files, including missing/corrupt/stale SQLite state and FTS queries.
- [ ] Add overload tests for bounded producer queues, slow disk/indexer/live consumers and sink failures. Prove that gameplay/server tick execution is never synchronously coupled to normal log file writes.
- [ ] Benchmark producer overhead and allocations on representative command, packet and security hot paths; define a regression budget before completing migration of high-frequency events.
- [ ] Validate the common query shapes against production-like log volumes and record latency/index-size/reindex-time evidence rather than assuming SQLite is fast enough at any scale.
- [ ] Update Operations/TUI and future REST/MCP integrations to consume only the Log Query/Live APIs, then document storage, rotation, retention, reindex, redaction and troubleshooting in English and Russian operator guides.
- [ ] Close the phase only when JSONL is the tested authoritative source, SQLite is demonstrably rebuildable from it, existing semantic log calls are migrated, secrets are covered by regression tests and logging backpressure cannot silently destroy critical diagnostics or stall the game thread.

Logging invariant:

```text
JSONL/NDJSON = authoritative append-only history
SQLite       = disposable searchable index
console/TUI  = presentation consumers
plugins      = stable Vega logging API, never backend-specific sinks
```

## Phase 27 — Vega Admin Client and remote administration protocol

Turn the existing administrative Terminal UI into a reusable client of a stable administration boundary. Remote administration is not a remote shell: no ANSI/PTY stream, no SSH surrogate and no server-rendered terminal frames. The UI renders locally and talks to Vega through typed commands, queries and events.

### Contracts and client boundary

- [ ] Create `Vega.Admin.Contracts` for protocol-neutral administration DTOs, command/query request and result models, event envelopes, permission descriptors, command catalog/help descriptors, configuration descriptors and version/capability negotiation.
- [ ] Define one `IVegaAdminClient` surface used by `Vega.Admin.Tui` regardless of transport. The TUI must not reference Vega Server implementation classes, Terraria globals, persistence providers or concrete network code.
- [ ] Provide a local `IVegaAdminClient` implementation over the existing Operations/application services so the embedded/server-local TUI remains available without loopback networking.
- [ ] Provide a remote `IVegaAdminClient` implementation over the Admin Protocol with the same observable semantics, authorization results and DTOs as the local implementation.
- [ ] Keep the Admin Protocol independent of Terminal.Gui so the same contracts can serve a future Web Admin UI, CLI, MCP, Telegram/Discord bots, inter-server administration and other clients.
- [ ] Keep authoritative mutations on the server. A client may request an operation but may not bypass command/service authorization, game-thread ownership, confirmation or audit rules by choosing a lower-level protocol message.

Recommended project split:

```text
Vega.Admin.Contracts
Vega.Admin.Client
Vega.Admin.Tui
Vega.Server
```

Target client model:

```text
Vega.Admin.Tui
       │
IVegaAdminClient
   ┌───┴───────────┐
   │               │
Local client   Remote client
   │               │
Vega Core      Admin Protocol
                   │
               Vega Server
```

### Admin protocol and realtime event model

- [ ] Support typed request/response commands and queries plus server push events over one secured administration connection. Evaluate WebSocket, SignalR and a narrow Vega-owned duplex protocol; choose from measured complexity, NativeAOT compatibility, framing/versioning and reconnect behavior rather than fashion.
- [ ] Support server/status changes, player connect/disconnect and state projections, chat, command results, plugin/module lifecycle events, metrics, structured logs/live log subscriptions and other Operations snapshots without polling whole state continuously.
- [ ] Define monotonic sequence/cursor or equivalent replay/resync metadata for pushed streams so reconnect can detect gaps and request a bounded fresh snapshot instead of silently presenting stale state.
- [ ] Version protocol contracts and negotiate capabilities. A newer client must fail gracefully against an older server and vice versa; no brittle assumption that client and server binaries were built from the same commit.
- [ ] Bound all server-to-client streams and per-session queues. A slow or disconnected admin UI must not backpressure the game thread, logging durability or unrelated admin sessions.
- [ ] Add keepalive, reconnect and explicit disconnected/degraded UI state; do not make transient network loss look like authoritative server state.

### Authentication, authorization and transport security

- [ ] Require TLS for remote administration and document certificate/trust configuration. Security must not depend on the Admin Protocol being obscure or the NativeAOT client being difficult to reverse engineer.
- [ ] Reuse Vega's server-owned identity/authorization model for administration sessions and define explicit admin permissions, including at least `server.view`, `server.command`, `server.restart`, `players.view`, `players.kick`, `players.ban`, `plugins.view`, `plugins.manage` and `logs.view`; add narrower permissions for sensitive/debug/config operations where needed.
- [ ] Do not embed a master key, permanent API key or server secret in `Vega.Admin.Client`/`Vega.Admin.Tui` binaries or configuration.
- [ ] Use short-lived authenticated sessions/access tokens with explicit expiry, server-side revocation and bounded refresh/re-authentication semantics. Long-lived credentials, where required for automation, must be a separate deliberately scoped server credential model rather than the interactive client's hidden secret.
- [ ] Support multiple simultaneous independent admin sessions with separate identity, permissions, token/session id, subscriptions and audit context. Revoking one administrator/session must not disconnect unrelated administrators.
- [ ] Record structured audit events for authentication, session creation/revocation, permission denial and privileged mutations without logging credentials/tokens.
- [ ] Apply separate authorization to sensitive player metadata, configuration secrets, debug data and log fields; a broad `server.view` must not accidentally become `view everything secret`.

### Module-oriented command catalog and help

- [ ] Promote the live command catalog into an Admin API read model grouped by owning module/subsystem. Every descriptor must include command name, aliases, syntax, localized title/description/help, permission requirements, console/player availability and owner/module identity.
- [ ] Make module ownership first-class: selecting a module in the TUI shows its commands and descriptions; selecting a command shows full help, syntax, aliases, required permissions and relevant examples where provided by the module.
- [ ] Add global command/help search by name, alias, module, description/help text and permission, with filtering limited to commands visible to the current administrator.
- [ ] Keep help metadata registered with the command itself so hot-loaded plugins appear/disappear automatically and the server, local TUI, remote TUI, CLI and Web UI do not maintain competing help databases.
- [ ] Allow command execution from the catalog only through normal `IVegaAdminClient` command execution and server authorization; the help/catalog UI is discovery, not a privileged dispatch path.

### Multilingual admin UI and shared localization

- [ ] Reuse and harden the Phase 19 / ADR 0009 localization engine rather than inventing a TUI-only translation layer. Admin contracts carry stable localization keys plus fallback text where appropriate, not server-formatted language-specific blobs as the only representation.
- [ ] Support localized module names, command descriptions/help, validation errors, permission-denied reasons and standard TUI chrome with deterministic fallback.
- [ ] Allow the administrator's UI culture to differ from the server default and from player cultures. Changing client language must not mutate process-global culture on the server.
- [ ] Expose plugin/module localization catalogs through the stable SDK registration model so a third-party module can contribute command/help/config text without a dependency on Terminal.Gui.

### Plugin configuration editor

- [ ] Add a configuration browser/editor to `Vega.Admin.Tui`: module/plugin catalog on the left, selected registered configuration document/schema on the right, with search/filtering when many plugins are installed.
- [ ] Do not expose an arbitrary remote filesystem text editor. Plugins register administrable configuration descriptors/schemas through a stable API, while the server owns file paths, validation, redaction and apply/reload behavior.
- [ ] Support version/revision-aware reads and optimistic updates so two administrators cannot silently overwrite each other's changes.
- [ ] Show validation diagnostics before apply, identify restart-only versus reloadable settings, and route reload/restart actions through the same authorized server services used by commands.
- [ ] Redact secrets on read and prevent secret placeholders from being written back as literal values. Sensitive configuration editing requires a dedicated permission and audit trail.
- [ ] Provide a raw text mode only for registered safe configuration documents where the plugin opts in; prefer structured editors when schema metadata exists.

### Debug and metrics workspace

- [ ] Add a dedicated TUI debug/diagnostics workspace over Admin/Operations contracts, with permission-gated access to all useful existing metrics and bounded diagnostics rather than direct process/runtime object access.
- [ ] Include at least TPS/tick distributions, CPU/memory/working set, GC, thread-pool/runtime counters, packet rates/rejections/queues, connections, plugin/module lifecycle and timings, world/sandbox state, `.vworld` load/build metrics, logging-pipeline/index status, persistence/backup state and Admin Protocol/session health where available.
- [ ] Reuse sampled/bounded telemetry. Opening a debug screen must not enable expensive unbounded per-packet/per-tick tracing by default.
- [ ] Allow drill-down from metrics to related structured log queries/event streams where Phase 26 can provide a stable correlation field.

### Terminal.Gui v2 client and NativeAOT distribution

- [ ] Build `Vega.Admin.Tui` on C# / .NET 11 / Terminal.Gui v2 and pin the current stable Terminal.Gui v2 release that passes the project's trimming/NativeAOT tests at implementation time. Do not depend on preview-only APIs once an equivalent stable release exists.
- [ ] Target Windows x64/ARM64 and Linux x64/ARM64; support macOS x64/ARM64 when Terminal.Gui and all required native/runtime dependencies pass the same architecture and AOT tests without platform-specific architectural hacks.
- [ ] Keep the client AOT-compatible and self-contained. The client project baseline includes:

```xml
<TargetFramework>net11.0</TargetFramework>
<PublishAot>true</PublishAot>
<IsAotCompatible>true</IsAotCompatible>
```

- [ ] Add dedicated CI NativeAOT publish checks for at least `win-x64` and `linux-x64`; add ARM64/macOS jobs as the supported matrix is proven.
- [ ] Treat trimming/AOT warnings as defects requiring source/dependency analysis. Do not globally suppress them merely to obtain a green publish.
- [ ] Verify clean-machine startup of the produced native client without an installed .NET Runtime and validate connect/authenticate/reconnect, command execution, live events and shutdown.
- [ ] Do not claim NativeAOT as a security boundary. It may remove ordinary IL and make casual decompilation less convenient, but protocol/authentication security remains fully server-side.
- [ ] Exclude `.pdb`, `.dbg` and `.dSYM` from public client releases. Preserve symbols separately as private CI artifacts where diagnostics policy permits.

### Exit criteria

- [ ] The same TUI binary can manage a local server through the local client and a remote server through the remote client without conditional UI business logic or direct server implementation references.
- [ ] Remote administrators can authenticate through TLS, receive only permitted projections/events, execute permitted operations and have access revoked without server restart.
- [ ] Module command catalog/help/search, logs, chat, players, plugins, configuration editing and debug/metrics all run through `IVegaAdminClient`/Admin Contracts rather than private shortcuts.
- [ ] At least `win-x64` and `linux-x64` NativeAOT publishes are CI-gated and clean-machine tested, and public artifacts contain no debug symbols.

Admin-client invariant:

```text
Terminal.Gui renders on the administrator machine.
Admin Protocol transports typed state, commands and events, never a remote terminal.
Authorization and secrets remain server-owned.
```

## Phase 28 — platform modularization, shared SDK tools and legacy feature convergence

This phase collects follow-up work that has emerged from real plugin/server consumers after the earlier Core-cohesion and plugin-ergonomics audits. It is not permission to create a new monolith named `Utils` or to copy legacy plugin APIs wholesale. Every extraction or shared helper must have a concrete consumer and a stable owner.

### Localization engine as reusable infrastructure

- [ ] Audit the Phase 19 / ADR 0009 localization implementation as a reusable engine, not merely `/lang` command behavior. Separate stable localization contracts/catalog registration from Core command implementation where that reduces coupling.
- [ ] Support module/plugin namespaces, typed/stable localization keys, deterministic culture fallback, plural/format argument validation and per-recipient culture without process-global mutation.
- [ ] Make localization available through the Plugin SDK and Admin Contracts so commands, plugin help, configuration descriptions, TUI/Web UI and other clients consume one language model instead of duplicating translation logic.
- [ ] Add catalog collision/unload/hot-reload tests and diagnostics for missing keys/fallbacks without turning missing localization into a game-thread failure.

### Shared plugin tools without a grab-bag API

- [ ] Introduce a small AOT-friendly shared utility surface/package for plugin authors only where multiple real plugins need the same pure operation. Keep utilities stateless, allocation-conscious and independent of Terraria mutable globals.
- [ ] Add a supported Terraria/chat tag stripping utility with recursive/nested tag handling and explicit policy options, including a mode that strips formatting/control tags while preserving item tags.
- [ ] Define behavior for malformed/unclosed/nested tags and add fuzz/regression tests so plugins do not each carry subtly different parsers.
- [ ] Prefer focused namespaces such as text/chat parsing, identifiers and bounded parsing helpers over one universal `Utils` class; do not expose internal runtime helpers merely because they already exist.
- [ ] Keep shared tools usable by third-party plugins through the public SDK/package and covered by API compatibility/AOT tests.

### `.vworld` loading follow-up optimization

- [ ] Reopen `.vworld` performance work with current .NET 11 measurements and production-like large worlds rather than treating the earlier Phase 11 results as the final ceiling.
- [ ] Profile cold/warm load at section granularity, decompression/copy/validation cost, allocations, page faults, post-load liquid/runtime reconstruction and readiness barriers; identify the dominant remaining cost before changing format again.
- [ ] Evaluate safe parallel decode/preparation only for independent immutable sections and keep Terraria mutable-state publication on the authoritative game thread where required.
- [ ] Evaluate lower-copy reads, pooled buffers, memory-mapped/sequential I/O and selectively precomputed indexes only when benchmarks show a real `WorldReady`/memory gain.
- [ ] Preserve `.wld` as canonical source and `.vworld` as rebuildable cache; no optimization may turn a cache corruption into loss of the authoritative world.
- [ ] Add repeatable small/medium/large cold/warm benchmarks to CI or a controlled benchmark lane and retain before/after artifacts so optimizations cannot be justified by one lucky run.

### Core/module extraction and naming audit

- [ ] Revisit the Phase 21 cohesion decision now that Admin Protocol, richer command metadata, logging and additional plugin consumers exist. Re-evaluate command infrastructure as a candidate `Vega.Commands` domain/module instead of allowing command dispatch, help, parsing and administration adapters to make `Vega.Core` a permanent transit dependency.
- [ ] Apply the same evidence-based review to localization, administration, moderation, logging/query and other Core areas that now have independent consumers. Extract complete vertical ownership, not DTO-only projects or pass-through interfaces.
- [ ] Decompose `src/Vega.Runtime/Program.cs` into explicit bootstrap/composition responsibilities: argument/config bootstrap, persistence, runtime/Terraria host wiring, module graph, Operations/Admin services, UI mode selection and lifecycle/shutdown. `Program.cs` should remain a thin composition entry point rather than an accumulating service script.
- [ ] Run a repository-wide naming audit for overloaded/ambiguous terms, duplicate concepts and names whose meaning changes between Contracts/Core/Terraria/Operations/Admin/Plugin SDK. Rename only with a recorded semantic reason; avoid churn for aesthetics alone.
- [ ] Add architecture tests for the accepted dependency directions after extraction and keep composition in Runtime rather than replacing Core coupling with a service locator.

### Homes behavior port

- [ ] Use the current project-owned `Homes` plugin at pinned revision `TerraZ-Team/Homes@4e7d33fe3dba7c94a498b04553df10c92f056007` as the behavior reference for the Vega Homes implementation; audit its `myhome`, `sethome` and `delhome` commands, permissions, limits, persistence, world identity, teleport validation, cooldowns and failure messages before porting. Keep this named-home feature distinct from TShock's built-in `/home` personal-spawn recall.
- [ ] Keep Homes as an optional first-party module unless a demonstrated primitive belongs lower. Reuse Vega player teleport, authorization, localization, persistence and `WorldId` contracts rather than importing TShock/plugin class shapes.
- [ ] Preserve relevant production behavior and edge cases from the reference plugin with end-to-end tests, including offline persistence, renamed/deleted worlds, invalid destinations and sandbox/world boundaries.
- [ ] Expose Homes command/help/config descriptors through the common Plugin SDK/Admin catalog so the feature requires no bespoke TUI integration.

### RegionExt.Core integration and Plugin SDK region surface

- [ ] Audit the current project-owned `RegionExt.Core` behavior/data model against `Vega.Regions`; pin its source revision and produce a capability matrix before choosing direct adapter, port or replacement for each feature.
- [ ] Integrate accepted RegionExt capabilities into the Vega region domain without exposing RegionExt/TShock implementation types through Core or public contracts.
- [ ] Provide a stable Plugin SDK region API for querying region membership/bounds/flags/ownership and for authorized supported mutations/events so plugins do not access persistence tables or Terraria hooks directly.
- [ ] Reconcile overlap priority, custom flags/properties, membership/group semantics, world identity and lifecycle behavior with the existing `Vega.Regions` source of truth rather than maintaining two competing region databases.
- [ ] Add compatibility/import tooling only if real existing RegionExt data requires it; do not make a legacy schema a permanent Vega persistence contract.

### InvSee integration and inventory administration

- [ ] Use the current project-owned `InvSee` plugin as the behavior reference for the Vega inventory-inspection integration; pin the exact source revision when implementation begins and audit commands, permissions, online/offline behavior, SSC interaction, inventory sections, mutation/copy/save behavior and failure cases before deciding parity.
- [ ] Build the retained InvSee behavior as an optional first-party administration module/UI over the Phase 15 immutable revisioned inventory snapshot and game-thread mutation contracts. Do not expose `Terraria.Player` inventory arrays or legacy plugin/TShock types through the module, Admin Protocol or Plugin SDK.
- [ ] Expose a stable Plugin SDK inventory-inspection surface for authorized read-only snapshots and only the narrowly justified mutation operations required by real plugin consumers. Preserve player/session/world identity and revision information so stale snapshots cannot be applied silently.
- [ ] Add an inventory inspector/editor to `Vega.Admin.Tui` over `IVegaAdminClient`: player selection plus structured inventory sections, item details and refresh/revision state. Editing, where enabled, must use explicit server operations rather than a client-side copy of Terraria state.
- [ ] Carry inventory DTOs through `Vega.Admin.Contracts` so local and remote administration use the same model. Define dedicated permissions such as `players.inventory.view` and `players.inventory.edit`, with a separate permission if offline/SSC inventory access exposes a broader authority boundary.
- [ ] Apply every online inventory mutation on the authoritative game thread with optimistic revision checks, validation, synchronization to the client and structured audit containing actor, target, operation and outcome. Never let a remote or plugin caller write raw slot arrays.
- [ ] Treat offline/SSC inventory as a separate authoritative source when supported: distinguish it explicitly from the live connected-player snapshot, define conflict/revision behavior and do not read or edit persistence tables directly from TUI/plugins.
- [ ] Register retained InvSee commands/help/configuration through the common module-oriented catalog so local TUI, remote TUI, CLI/Web/MCP and ordinary command discovery all describe the same behavior and permissions.
- [ ] Add parity and security tests for ambiguous/offline targets, permission denial, inventory sections, stale revision conflicts, disconnect/slot reuse, full/invalid mutations, SSC/live source selection, remote Admin round trips and audit output.

### TSExt capability integration and documentation

- [ ] Audit the relevant project-owned `TSExt*` plugins at pinned revisions and map each capability to `Core`, first-party module, Plugin SDK primitive, Admin UI, native passthrough or explicit drop. Do not port a plugin family wholesale merely because its name starts with TSExt.
- [ ] Revisit `TSExt_UICommand` `/ui` and `/ai` behavior against the existing sensitive-field authorization work. Document the exact output/permission behavior and expose equivalent information through stable player/account inspection contracts rather than legacy command internals.
- [ ] Specifically audit and document the `/ai` multiboxing/multi-client behavior before implementation: define what identity/session relationship is being detected or displayed, which evidence is authoritative versus heuristic, privacy/permission requirements and how false positives are represented.
- [ ] Add the retained `/ui` and `/ai` behavior to the module-oriented command catalog/help so operators can discover what each command means, what data it exposes and which permissions gate sensitive fields.
- [ ] Integrate other useful TSExt capabilities only through existing or newly justified Vega services, with executable parity tests and no TShock API/class-shape dependency.

### Validation and exit criteria

- [ ] The localization engine, shared plugin tools and region/inventory interfaces are usable from an out-of-repository plugin SDK consumer without referencing Runtime/Terraria implementation assemblies.
- [ ] `.vworld` follow-up changes demonstrate measured startup/readiness or memory improvement on representative worlds and retain safe `.wld` fallback.
- [ ] `Program.cs` is reduced to composition/orchestration, command/domain extraction decisions are recorded, and naming changes remove actual ambiguity rather than merely rename files.
- [ ] Homes, InvSee, accepted RegionExt.Core capabilities and accepted TSExt capabilities have pinned behavior references, tests and clear ownership in the module/Admin/SDK architecture.

Platform-convergence invariant:

```text
Reuse behavior and proven capabilities.
Do not reuse legacy architectural coupling.
Shared SDK surfaces exist because multiple real consumers need them, not because a Utils folder feels convenient.
```

## Architectural guardrails

These apply across all phases:

- Core must not know `Terraria.Main`, `Terraria.Player`, TerrariaRuntime, TSAPI, `TerrariaApi.Server`, `TShockAPI.TShock`, `TSPlayer` or `Commands.ChatCommands`.
- Do not build interfaces for every Terraria type in advance.
- Do not introduce a custom DI container, dynamic service rebinding layer or reflection-heavy Core registration.
- Do not move business logic into helper/extension APIs.
- Do not add async merely because the language supports it; use it only across genuine asynchronous boundaries.
- Local business logic stays local. Cluster networking extends capabilities instead of becoming a hidden dependency.
- Operations/TUI consumes safe read models and application commands; it never owns authoritative game state.
- Packet workers may prepare owned immutable intents, but only the authoritative game thread may validate current Terraria mutable state and apply gameplay mutations.
- Packet-pipeline concurrency is an internal runtime detail; ordinary plugin/module APIs remain typed and game-thread-safe without exposing queues, lanes, buffer ownership or manual synchronization.
- Keep the main dynamic server on CoreCLR; use NativeAOT selectively for isolated fixed-function executables only when compatibility and benchmarks justify it.
- Plugin developer ergonomics is an architectural release criterion: ordinary commands/hooks/gameplay plugins must not require knowledge of runtime scheduling, buffer ownership, generation routing or internal composition.
- Do not expose a universal plugin service locator or global replaceable-service model merely to reduce constructor parameters; prefer curated convenience surfaces over hidden runtime magic.
- Structured logs are durable structured events, not formatted strings with optional metadata; JSONL remains authoritative and every search index is rebuildable derived state.
- Logging producers on gameplay/server tick paths must never perform normal durable sink I/O synchronously; queue/backpressure and critical-event failure behavior must be explicit and observable.
- Plugin logging depends only on the stable Vega API and structured event model, never on a concrete sink/backend such as Serilog, SQLite, Loki or OpenTelemetry.
- Remote administration transports typed commands/state/events, never an ANSI/PTY terminal stream; authorization remains server-owned and TLS-protected.
- The Admin Protocol is a reusable server boundary for TUI, Web UI, CLI, MCP and other clients; no frontend receives a privileged private path around application services.
- NativeAOT may harden distribution against casual IL inspection but is never treated as authentication, authorization or protocol security.
- Shared plugin utilities remain narrow, pure and consumer-driven; never grow a universal `Utils` surface that becomes hidden framework coupling.
- Localization is one reusable per-recipient/module-aware engine across commands, plugins and admin clients; frontends must not invent incompatible language catalogs.
- Inventory inspection/editing uses revisioned Vega contracts and server-owned authorization; plugins and admin clients never read/write Terraria inventory arrays or persistence tables directly.
- `.vworld` is a cache, not a hidden second source of truth or mandatory persistence path.
- Prefer one working vertical slice over ten unused abstractions.
