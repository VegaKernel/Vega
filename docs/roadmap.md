# Vega roadmap

## Phase 0 — freeze the target architecture

- [x] Establish .NET 10 baseline.
- [x] Record production-derived requirements and world/threading constraints.
- [x] Rename the product/repository identity to Vega.
- [x] Adopt Vega as an independent runtime rather than a TShock compatibility modernization (ADR 0016).
- [x] Define startup lifecycle, readiness barriers and performance budgets (ADR 0017).
- [x] Define event/player/packet migration architecture and anti-framework constraints (ADR 0018).
- [x] Define optional typed cluster messaging and first production scenarios (ADR 0019).
- [x] Add architecture tests that prevent Terraria/OTAPI/TSAPI/TShockAPI references from entering Vega.Core.

## Phase 1 — native runtime boundary

- [x] Establish `Vega.Runtime` / `Vega.Terraria` as the Terraria/OTAPI integration boundary.
- [x] Move process bootstrap/readiness telemetry into the Vega runtime host.
- [x] Add Vega-owned OTAPI lifecycle/readiness hooks.
- [x] Add a bounded Vega-native game-thread dispatcher contract and OTAPI implementation.
- [x] Measure Vega overhead separately from Terraria world load time.
- [x] Replace the remaining compatibility hook dispatch with Vega-native OTAPI adapters.
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

The first end-to-end packet slice validates inbound `PlayerHp` ownership. `Vega.Terraria` parses the OTAPI payload window through `TZ.Multiplicity`, the typed context flows into a Core anti-spoof policy, and a rejected decision cancels the native OTAPI hook.

## Phase 5 — commands and legacy hook migration

- [x] Migrate commands through Vega contracts instead of `Commands.ChatCommands` compatibility surfaces.
- [x] Replace TSAPI event groups incrementally with Vega-native OTAPI adapters.
- [x] Migrate production behavior by capability rather than reproducing TShock class shapes.
- [x] Keep main server semantics in Core services; helper/extensions remain thin convenience layers.
- [x] Promote true server invariants/security primitives into Core/runtime services.
- [x] Keep gameplay/community policy as optional modules.

`serverinfo` was the first native command path. The base surface now also owns `tps`/`tpsstats`, `kick`, `playing`/`online`/`who`, `checkupdates`, `ban`, `plugin`/`plugins`, `help`/`commands` and `aliases`. Discovery reads the live command catalog, including hot-loaded registrations, and applies the same server/permission visibility rules as dispatch instead of maintaining a second command list. Server-console input is intercepted through OTAPI command processing; player input is recognized from typed `LoadNetModule`/`NetTextModule` payloads. Effective player permissions live in Vega session state behind `IPlayerAuthorizationService`, and update discovery uses the Vega GitHub release endpoint.

Lifecycle, world decisions, console commands, player commands and packet security have Vega-owned OTAPI entrypoints. Core owns command behavior and the player packet ownership invariant; optional gameplay policy remains in modules. Unknown commands and non-command chat remain available to Terraria's normal paths.

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
- [ ] Complete every `Keep`, `Port`, `Replace` or `Drop` decision in the [TShock and TerraZ migration audit](migration-audit.md); an unclassified subsystem or plugin keeps this phase open.
- [ ] Port accepted TerraZ behavior to the Vega module API by capability, not by reproducing TShock class shapes.
- [ ] Use each migrated module as the forcing function for its required contract instead of building an abstract framework ahead of a consumer.
- [ ] Add incident-backed regression tests before retiring the corresponding TShock/TerraZ behavior or workaround.

The first reopened migration slice replaces TerraZ `NoCoinDupe` with `NpcCoinPickupAuthorityPolicy`: inbound Multiplicity `SyncExtraValue (92)` packets are rejected before Terraria can accept client-authored NPC coin state, while server-originated synchronization remains untouched. Its regression suite uses the real 14-byte typed payload and records the confirmed production incident in the migration ledger.

The next StateGuard slice extends the existing player-slot ownership rule to typed `SpectatePlayer (150)` payloads. It deliberately does not claim the whole plugin: configurable spectate/ghost/difficulty policy, physical-world borders, permission bypasses and corrective state synchronization stay open until their required Phase 13/19 capabilities exist.

The first full-TShock network slice ports the TerraZ Bouncer fix for `PlayerDeathV2 (118)`. Vega compares the signed wire field as an unsigned 16-bit value and rejects damage above `42000` before Terraria receives it; the same typed payload must also claim the sending connection's player slot. This is one closed invariant, not a claim that Bouncer or `GetDataHandlers` are fully migrated.

The post-1.4.5 registration audit also routes client `TeamChangeFromUI (157)` through the native security hook and applies the same sender-slot ownership rule as legacy `PlayerTeam (45)`. Protocol 325 marks historical item packets `145/148` as deprecated, so Vega rejects those packet identities instead of pretending they remain writable item-sync formats. Supported client-authored item state (`21/90/151/160`) is validated for fixed slot bounds, finite positions/velocities, stack, item type and prefix before Terraria sees it.

### Historical implementations to mine for behavior, not architecture

- `popstarfreas/PvPController-1.3`
  - useful scenarios: packet policy context, ordered PvP damage/projectile decisions, authoritative player mutations, temporary session state;
  - its Redis dynamic JSON/action-string synchronization is explicitly a negative example for Vega cluster contracts.
- `popstarfreas/CustomWeaponAPI`
  - useful scenarios: strongly typed partial item overrides, targeted packet delivery and custom item behavior;
  - its bespoke `PacketWriter` must not be carried forward because Vega uses the shared typed `TZ.Multiplicity` protocol layer.

`VegaModuleHost` now starts explicitly composed modules and exposes their load state and startup duration. `Vega.Modules.Pvp` ports hostile-health authority, ordered projectile/damage attribution, death windows and session cleanup. `Vega.Modules.CustomItems` ports strongly typed partial overrides and requests targeted delivery through a capability; the `Vega.Terraria` implementation serializes `UpdateItemDrop`, `TweakItem` and `ItemOwner` with `TZ.Multiplicity`. These are the first two ports, not evidence that the TerraZ module estate is complete. Phases 13–16 and 19 contain the remaining security, operations, administration, provider and persistent-server work discovered by the audit.

## Phase 10 — remove migration scaffolding

- [x] Remove TShockAPI and TSAPI from the production solution after the native runtime no longer depends on them.
- [x] Remove legacy hook/command/plugin compatibility surfaces from the shipping runtime.
- [x] Rename active physical `TShock*` project paths/namespaces to Vega names.
- [ ] Resolve every legacy subsystem and TerraZ repository in the [migration audit](migration-audit.md), with source evidence, target owner, disposition and validation recorded.
- [ ] Preserve the pinned source references until all accepted behavior, edge cases and incident regressions have executable Vega coverage.
- [ ] Review deleted configuration, command, permission, persistence, localization, REST and operations documentation against the running Vega product; restore or rewrite every still-relevant operator contract.
- [ ] Close the scaffolding-removal gate only when there are no unresolved `Partial`, `Missing` or `Unclassified` rows—physical deletion alone is not completion.

The runtime now boots Terraria directly through the OTAPI-backed `Vega.Terraria` host, so TShockAPI and TSAPI correctly remain outside the shipping dependency graph. Their removal in `f24b8036` happened before the behavior audit was complete, however. The exact parent snapshot (`72bff778`) and its pinned TSAPI revision (`024e480e`) are therefore retained as external audit inputs while active namespaces remain `Vega.Contracts` and `Vega.Core`. This phase is intentionally reopened; no legacy assembly is reintroduced into the product build.

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
- [ ] Keep both language versions synchronized whenever commands, packaging, supported runtime modes or public contracts change.

## Phase 13 — TerraZ-derived security and connection invariants

Move server-wide invariants into Core/runtime, not the historical plugin implementations. Every rule lands as one typed vertical with a production regression test and structured observability; do not build a generic security rules engine first.

### Moderation identity from ExtendedBans

- [x] Replace the string-only `IBanService` boundary with typed ban subjects for account, UUID, exact IP and network/CIDR identities.
- [x] Normalize and validate IP/network subjects once, with explicit IPv4/IPv6 matching, expiration and reason metadata.
- [x] Provide one authoritative matching service reused by connection admission, administrative commands and cluster replication.
- [x] Keep persistence behind a narrow adapter and keep ban UI/command presentation outside the matching engine.
- [x] Add regression tests for overlapping CIDRs, IPv4/IPv6 normalization, expired bans, account/UUID matching and duplicate replicated revisions.

### Packet and state validation from PacketGuard, StateGuard, LeashedEntityGuard and NoCoinDupe

- [x] Close the universal packet-safety slice: bounded/malformed payload rejection, supported item-sync indices/transforms/stack/type/prefix, projectile create/destroy ownership/index/type/finite transforms, player movement/world/inventory and portal bounds, tile/object/item-frame/chest bounds, chest resize/quick-stack indices, liquids, tile entities, gem locks, mass wiring, food/display placement, tile picking, leashed-entity anchors and the known coin-duplication path.
- [ ] Keep configurable gameplay enforcement separate: repeated-death abuse policy, ghost/spectator restrictions, permission bypasses, proximity checks and corrective state synchronization require actual configuration/authority consumers rather than more universal packet guards.
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

1. [ ] **TempGroup / ControlGroupOption**: implement expiring role leases, stale-expiry protection, delegated grant limits and audit records; keep temporary-group commands modular.
2. [ ] **InvSee / TSExt_UICommand**: implement immutable inventory snapshots, game-thread mutation operations and centralized redaction/authorization for IP, UUID and other sensitive fields; keep inspection commands/UI optional.
3. [ ] **Wipe**: implement validated world-transition/reset orchestration with registered persistence participants, cancellation and observable state; keep destructive command policy and confirmation in an admin module/UI.
4. [x] **AutoWorldCleaner**: implement world-scoped cancellable scheduling plus safe item/projectile snapshots and removal operations; keep cleanup thresholds and schedules in a module.
5. [ ] **InfChests / ChestRegen**: implement stable chest identity, immutable versioned snapshots, game-thread transactional mutation and ACL/event hooks; keep refill, lock and regeneration policy in modules.
6. [ ] **Mentions**: implement an ordered typed chat transformation pipeline separating content transformation, moderation and per-recipient rendering; keep mention behavior in a module.

`Vega.Modules.AutoWorldCleaner` now owns cleanup scheduling and policy behind its own opt-in `config/auto-world-cleaner.json`. Contracts expose immutable world-scoped item/projectile snapshots plus guarded removal handles, while `Vega.Terraria` is only the game-thread adapter that reads and mutates Terraria arrays. Each run performs snapshot, selection and removal in one dispatched game-thread action; item age and projectile identity are revalidated before mutation to reject stale handles. The module supports cancellable scheduling, online-player gating, non-overlapping runs, `worldclean`/`cleanworld` administration and bounded Operations status/warnings. The current Terraria adapter owns `WorldId.Primary`; future world backends can implement the same contract independently.

### Explicitly outside Core

- [ ] Port `Homes`, `HouseRegions`, `TpaRequest`, `Clans`, `SealedDressers`, `Mentions`, `ChatAboveHeads`, `ChatGuard`, `InvSee`, `Wipe`, `Region-Vision`, `InfChests` and `ChestRegen` only as `ServerPlugins` when their required Core capability exists.
- [ ] Do not move plugin commands, configuration formats, database schemas, timers, static state or TShock class shapes into Core merely to speed up a port.

## Phase 16 — provider and observer-scoped scene runtime

FakeProvider is already a proven shared capability, not a single-plugin abstraction. The audited consumers include WorldEdit clipboard previews, FakeProviderClustering visibility layers, LobbyVisuals/TUI personal providers, hidden portals and animations, plus lobby/server compositions carrying the provider implementation. Vega should therefore own a stable provider/scene boundary while porting behavior rather than the Terraria/TSAPI-coupled API shapes.

### Contracts and ownership

- [ ] Audit the current `TZ.FakeProvider.Abstractions` consumers and freeze the required use cases: ordered global/personal tile layers, observer sets, provider movement/resizing, copying, drawing, lookup and deterministic removal.
- [ ] Define Vega-native `WorldId`/scene/overlay/observer identities and immutable tile/area data without exposing `Terraria.ITile`, `MessageBuffer`, TSAPI event args or mutable player objects from Contracts/Core.
- [ ] Keep provider composition and observer policy behind narrow Core contracts; keep tile storage, Terraria hooks, section serialization and game-thread mutation in `Vega.Terraria`.
- [ ] Give every provider/overlay an explicit owner and lifetime token so module stop, player disconnect, world unload and failed startup clean up deterministically.
- [ ] Preserve stable ordering/layer semantics and make conflicting overlays observable instead of relying on plugin registration order.

### First consumer ports

1. [ ] **WorldEdit**: port clipboard preview/mask providers, move/resize reuse and clear-on-session-end as the first complete consumer.
2. [ ] **Region-Vision**: port personal region-border overlays and block interaction with phantom tiles through the shared observer scope.
3. [ ] **LobbyVisuals/TUI**: port personal UI providers, hidden portals and animations without `dynamic` provider access.
4. [ ] **FakeProviderClustering**: port player/NPC/projectile visibility layers and cluster transitions after the tile-overlay path is stable.
5. [ ] **MiniLobby / SurvivalLobby compositions**: remove vendored FakeProvider copies after equivalent Vega capabilities and regression tests exist.

### Network consistency and performance

- [ ] Apply the same observer/layer decision to outbound tile/entity visibility and inbound interaction validation; a client must not interact with state it cannot observe.
- [ ] Parse/filter through the typed Multiplicity packet boundary and keep rate limits plus malformed-packet handling shared with Phase 13.
- [ ] Support bounded dirty-region invalidation and section updates instead of redrawing complete providers for every small change.
- [ ] Benchmark provider lookup, composition, tile-section generation, memory per personal overlay and mass observer changes with representative WorldEdit/lobby workloads.
- [ ] Expose active scenes/providers, owners, observer counts, dirty regions and degraded/failure state through Operations snapshots.
- [ ] Do not replace `Main.tile` globally in the first slice. Evaluate a world-provider backend only after overlay consumers are migrated and Terraria/OTAPI ownership assumptions are measured.
- [ ] Keep single-process multi-world virtualization and arbitrary lazy world storage separate from the provider API; they require their own evidence even though they may later reuse scene contracts.

## Phase 17 — isolated multi-process worlds and sandbox orchestration

Deliver the first production sandbox implementation as supervised Terraria/Vega backend processes. Each active world owns its process, game thread and mutable Terraria state; the control plane coordinates lifecycle, routing and immutable status without proxying ticks, tiles or other hot-path operations. This is the preferred isolation model from ADR 0012 and does not depend on solving single-process virtualization.

### World definitions and lifecycle

- [ ] Define persistent sandbox descriptors with stable `WorldId`, display name, template/source world, persistence mode, player capacity, access policy, expiry/TTL and enabled module set.
- [ ] Implement create, start, stop, restart, reset and destroy operations with explicit state transitions, cancellation, timeouts and idempotent retries.
- [ ] Materialize writable sandbox instances from immutable templates without modifying the source; use atomic promotion and recovery so failed creation/reset preserves the previous valid instance.
- [ ] Distinguish temporary, persistent and externally managed worlds and make retention/cleanup policy explicit for each kind.
- [ ] Ensure module stop, process exit and control-plane shutdown release leases and resources deterministically without deleting persistent world data.

### Process supervision and isolation

- [ ] Add a backend process supervisor with readiness handshake, heartbeat, graceful shutdown, forced-termination timeout and bounded restart/backoff policy.
- [ ] Keep world-local player, entity, region, scheduler and Terraria state inside the backend process; cross-world operations exchange typed commands and immutable snapshots only.
- [ ] Define per-world CPU, memory, disk and concurrent-start limits plus a global admission budget so sandbox creation cannot starve the primary server.
- [ ] Contain backend crashes and unhealthy worlds, preserve diagnostic logs and expose a typed degraded/failed state instead of silently rerouting players.
- [ ] Make control-plane restart reconciliation discover already-running backends and orphaned instance directories without duplicating or corrupting worlds.

### Connection routing and player state

- [ ] Complete the Phase 13 forwarding-provenance, trust and admission-order requirements before enabling sandbox routing.
- [ ] Implement Dimensions-compatible routing as a Vega-owned boundary with authenticated backend identity, replay protection and explicit target `WorldId`; do not trust client-supplied forwarding metadata.
- [ ] Provide explicit enter, leave and transfer operations with capacity/access checks, readiness gating, actionable failure results and safe fallback to the originating or primary world.
- [ ] Define per-sandbox SSC policy as inherit, copy-on-enter, isolated or disabled, including conflict handling and persistence on disconnect, transfer and backend failure.
- [ ] Keep accounts, bans and baseline authorization server-wide while allowing deliberate typed world-local overrides for regions, warps, rules and module policy.

### Operations and administration

- [ ] Expose all managed worlds through Operations/TUI with lifecycle state, endpoint, player count, uptime, TPS, CPU, memory, storage, restart count, template revision and TTL.
- [ ] Route create/start/stop/reset/destroy/transfer actions through normal authorization, command and audit boundaries; the UI must not manipulate processes or world files directly.
- [ ] Add structured events and bounded diagnostics for lifecycle transitions, rejected admission, resource pressure, failed transfers, crashes, restart suppression and cleanup.
- [ ] Support controlled rolling backend updates with explicit version compatibility and no claim of zero-downtime player migration until proven by an end-to-end consumer.

### Performance and production validation

- [ ] Establish budgets for idle and populated memory per backend, tick p50/p95/p99, cold/warm start time, template materialization, player transfer latency, control-plane overhead and shutdown time.
- [ ] Benchmark one primary world plus increasing active/idle sandbox counts under representative world sizes, NPC/entity load and player concurrency; record CPU, working set, GC pauses, disk I/O and network overhead.
- [ ] Prove that monitoring and cluster snapshots are sampled/bounded and do not introduce per-tick cross-process RPC or full-world serialization.
- [ ] Add overload tests for simultaneous starts, crash loops, full capacity, low disk space, control-plane restart and backend loss during player transfer.
- [ ] Default to bounded on-demand activation and idle TTL shutdown; do not keep every configured sandbox process resident without measured operational need.
- [ ] Publish deployment guidance and capacity-planning evidence before marking multi-process sandboxes production-ready.

### Deferred single-process backend

- [ ] Treat multiple live Terraria worlds inside one process as a separate research project after the multi-process API and consumers are stable.
- [ ] Require evidence that Terraria/OTAPI globals, hooks, networking, entity ownership, world save/load and game-thread affinity can be isolated without broad static-state emulation.
- [ ] Do not make single-process virtualization, lazy world storage or replacement of `Main.tile` a compatibility promise or prerequisite for the Phase 17 sandbox API.

## Phase 18 — migrate Vega to .NET 11

Move the complete runtime, public packages and release toolchain to .NET 11 only after a stable SDK/runtime and the critical Terraria integration dependencies are production-usable. Keep the last verified .NET 10 revision and artifacts available as the rollback baseline until the migration passes compatibility, startup and performance validation.

### Dependency and compatibility gate

- [ ] Audit .NET 11 support for OTAPI, ModFramework, MonoMod, `TZ.Multiplicity`, Terminal.Gui, MaxMind, test adapters and every runtime dependency before changing Vega target frameworks.
- [ ] Build and run a disposable compatibility branch on .NET 11 with zero suppressed loader, JIT, trimming, native-asset or hook failures.
- [ ] Record required dependency upgrades and their behavioral/API changes; do not carry local forks or binding workarounds without an explicit owner and removal condition.
- [ ] Decide the public-package compatibility policy from real consumers: multi-target only narrow libraries that still require active .NET 10 consumers, while keeping the server runtime on one authoritative target.
- [ ] Confirm that every bundled `ServerPlugins` module targets a framework loadable by the .NET 11 host and rejects incompatible modules with an actionable startup diagnostic.

### Project and toolchain migration

- [ ] Change the repository target framework and documented SDK baseline from `net10.0` to `net11.0` in one coordinated change.
- [ ] Update CI, tagged release, manual publication and local build instructions to install and use the pinned .NET 11 SDK feature band.
- [ ] Upgrade analyzers, test infrastructure and build tooling, then resolve new warnings intentionally instead of globally suppressing them.
- [ ] Preserve deterministic package metadata, symbols, assembly/file versions and the `VegaKernel.*` package IDs across the framework transition.
- [ ] Update the deployable server layout and assembly resolver only where .NET 11 requires it; retain `libs/` and `ServerPlugins/` ownership boundaries.

### Runtime validation

- [ ] Run the complete unit, integration and architecture suite on .NET 11, including packet ownership, game-thread dispatch, module lifecycle, cluster scenarios and `.vworld` fallback/corruption paths.
- [ ] Smoke-test clean extraction and startup on every supported operating system/runtime identifier, including interactive world selection, plain console, TUI and headless modes.
- [ ] Validate OTAPI hooks, Terraria world load/save, networking, player join/leave, commands, shutdown and crash diagnostics against representative production worlds.
- [ ] Pack and inspect every NuGet and symbol package, install them into clean consumer projects and verify that dependency assets resolve without relying on the Vega repository cache.
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

- [ ] Define a documented deploy-time layout for server, storage, security and module settings under `config/`, with generated example files included in the release archive.
- [ ] Establish deterministic precedence for command-line arguments, environment variables, Vega configuration, Terraria configuration and built-in defaults; show the source of each effective value in diagnostics without exposing secrets.
- [ ] Validate configuration before starting authoritative services and report the exact file, property and rejected value; malformed optional module configuration must degrade that module without silently changing policy.
- [ ] Support atomic, section-scoped reload only for settings proven safe to reload; mark port, world, storage-provider and other restart-only changes explicitly.
- [ ] Provide `config show`, `config validate` and authorized `config reload` operations through the normal command/audit boundary.
- [ ] Document world selection/generation, listener/password, slots, default culture, storage, logs, TUI, `.vworld`, modules and cluster configuration in both project-guide languages.

### Accounts, groups and permissions

- [ ] Implement persistent account, group, inheritance and permission stores with stable identifiers, optimistic revisions and schema migrations; the current in-memory session authorization cache is not the source of truth.
- [ ] Resolve effective permissions deterministically from account assignment, group inheritance, explicit grants/denials and active temporary role leases, with cycle detection and bounded caching.
- [ ] Add authorized commands for account lifecycle, group create/delete/list/show, parent assignment, permission grant/revoke, player group assignment and temporary group assignment.
- [ ] Enforce delegated-administration limits from ADR 0014 so an operator cannot grant a role or permission above their own authority.
- [ ] Persist every account/group/permission mutation and emit a structured audit record containing actor, target, before/after revision, reason and outcome.

### Regions and world authorization

- [ ] Implement the currently contract-only region service with persistent world-scoped region identity, bounds, priority, owner, members, groups and permission/flag ACLs.
- [ ] Add game-thread-safe region admission/build/use checks through typed world/player contexts rather than module-specific tile hooks.
- [ ] Add authorized `region` create/delete/list/show/resize/priority/owner/member/group/flag commands with overlap and world validation.
- [ ] Expose immutable region snapshots and authorized mutations to Operations/TUI so `HouseRegions`, `Region-Vision` and sandbox worlds share one source of truth.
- [ ] Add migrations and regression tests for overlapping priorities, renamed/deleted worlds, offline owners, permission inheritance and concurrent edits.

### Bans and moderation commands

- [ ] Complete the Phase 13 typed account/UUID/IP/CIDR ban matcher, persistence and connection-admission enforcement before presenting bans as production-ready.
- [ ] Add authorized `ban`, `unban`, `ban list`, `ban show` and expiration/reason workflows over the same authoritative service used by login and cluster replication.
- [ ] Add account/player mute primitives only with explicit identity, duration, reason, issuer and audit semantics; do not hide moderation state in command-local tables.
- [ ] Expose bounded, redacted moderation views in Operations/TUI and verify that sensitive IP/UUID subjects require separate permissions.

### Localization and command completeness

- [ ] Implement ADR 0009 localization catalogs, module namespaces, deterministic fallback and explicit per-recipient culture without process-global culture mutation.
- [ ] Add `/lang`, `/lang list`, `/lang <culture>` and `/lang reset`; persist registered-account preference and keep guest preference session-scoped.
- [ ] Add a generated command catalog and `help`/command-discovery output that shows only commands the caller may use, including aliases, syntax and permission requirements.
- [ ] Keep Vega commands and Terraria native commands reachable from plain console and TUI without competing stdin readers; capture native command output into bounded operations logs/results.
- [ ] Audit the minimum built-in operator set beyond `serverinfo`, `checkupdates`, `kick` and `playing`, and cover save/shutdown, moderation, identity, groups, regions, configuration and language with end-to-end tests.

### Persistence, migration and release validation

- [x] Ship the domain-oriented `Vega.Persistence` layer with deploy-ready SQLite and selectable PostgreSQL/MySQL providers; initialize it before world loading and fail startup when the selected store is unavailable.
- [x] Persist the first revision-aware domain consumer (global-ban creation/revocation tombstones) through the same contract used by cluster replication.
- [ ] Make schema upgrades transactional with backup/recovery guidance and idempotent migration tests from every released Vega schema.
- [ ] Add clean-archive tests proving that an operator can extract the release, edit documented configuration, create the first administrator, start a world and manage groups/regions/bans without repository files.
- [ ] Update English/Russian guides and the deployable archive only after command examples, generated configuration and persistence paths match the running server.

ADR 0022 establishes the persistence boundary without reproducing TShock's generic database helpers. Domain contracts never expose SQL or `DbConnection`. SQLite and PostgreSQL use transactional schema upgrades; MySQL uses a database-scoped migration lock plus idempotent retryable DDL because its engine implicitly commits DDL. Schema 1 and its clean/reopen/future-version/revision/tombstone/expiry tests are complete. The phase remains open for account, group, region, SSC and moderation product stores and for real disposable PostgreSQL/MySQL contract tests in CI.

## Architectural guardrails

These apply across all phases:

- Core must not know `Terraria.Main`, `Terraria.Player`, OTAPI, TSAPI, `TerrariaApi.Server`, `TShockAPI.TShock`, `TSPlayer` or `Commands.ChatCommands`.
- Do not build interfaces for every Terraria type in advance.
- Do not introduce a custom DI container, dynamic service rebinding layer or reflection-heavy Core registration.
- Do not move business logic into helper/extension APIs.
- Do not add async merely because the language supports it; use it only across genuine asynchronous boundaries.
- Local business logic stays local. Cluster networking extends capabilities instead of becoming a hidden dependency.
- Operations/TUI consumes safe read models and application commands; it never owns authoritative game state.
- `.vworld` is a cache, not a hidden second source of truth or mandatory persistence path.
- Prefer one working vertical slice over ten unused abstractions.
