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
- [ ] Replace remaining `LegacyTsapiBridge` hook dispatch with Vega-native hook adapters.
- [ ] Keep TSAPI only as temporary scaffolding while legacy TShockAPI still consumes historical hooks.

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

- [ ] Introduce demand-driven read-only `PlayerSnapshot`.
- [ ] Introduce `IPlayerDirectory` for lookup/enumeration.
- [ ] Introduce `IPlayerOperations` for kick/teleport/authoritative state changes.
- [ ] Introduce `IPlayerMessenger` separately from mutation capabilities.
- [ ] Ensure Terraria player mutation is implemented only in `Vega.Terraria` and scheduled on the game thread.
- [ ] Migrate a real player/security/command scenario before adding more player abstraction surface.

## Phase 4 — packet processing context

- [ ] Use typed packets from `TZ.Multiplicity`; do not introduce a competing packet codec.
- [ ] Add packet direction, connection identity, player identity and required processing metadata.
- [ ] Pass one typed context through protocol validation, security and Core policy stages.
- [ ] Keep raw byte parsing/encoding in the Terraria/protocol integration boundary.
- [ ] Migrate a real packet-sensitive behavior such as join/security or PvP validation through the context.

## Phase 5 — commands and legacy hook migration

- [ ] Migrate commands through Vega contracts instead of `Commands.ChatCommands` compatibility surfaces.
- [ ] Replace TSAPI event groups incrementally with Vega-native OTAPI adapters.
- [ ] Migrate production behavior by capability rather than reproducing TShock class shapes.
- [ ] Keep main server semantics in Core services; helper/extensions remain thin convenience layers.
- [ ] Promote true server invariants/security primitives into Core/runtime services.
- [ ] Keep gameplay/community policy as optional modules.

## Phase 6 — Operations API and Terminal UI

The operations surface is an optional consumer of stable read models and application commands. It must not become a privileged path around Core/runtime rules.

- [ ] Define operations/read-model contracts independent of Terminal UI types.
- [ ] Introduce structured logging with independent file/stdout/TUI sinks.
- [ ] Implement a server dashboard backed by immutable/bounded snapshots.
- [ ] Implement a players view using `PlayerSnapshot` / player read models.
- [ ] Implement a modules view exposing load state, version, dependencies, startup timing and failures where available.
- [ ] Implement a bounded logs viewer with level/source/text filtering and follow/pause behavior.
- [ ] Expose runtime, network, process, GC and memory metrics through cheap sampled read models.
- [ ] Add a command palette over the normal command/application boundary.
- [ ] Add safe administrative actions only through normal authorization and service contracts.
- [ ] Preserve plain console mode for scripts, redirected output and troubleshooting.
- [ ] Support fully headless mode for services, containers and CI.
- [ ] Keep TUI initialization optional; failure to initialize an interactive terminal must never block server startup.
- [ ] Never read or mutate Terraria globals directly from the UI thread.

See ADR 0010 for the operations/TUI boundary and runtime-mode requirements.

## Phase 7 — typed extension state, only when demanded

- [ ] Stabilize player/session/entity lifecycle first.
- [ ] Add strongly typed scoped keys only for a migrated feature that genuinely needs extension state.
- [ ] Support type-safe `GetOrCreate`, `Set`, `Remove` and deterministic cleanup.
- [ ] Do not make extension state a universal Core dependency or a substitute for modeled domain state.

Concrete future pressure already exists in historical PvP behavior: projectile-to-weapon attribution, death/spectator state and timing windows. These are test cases, not justification for implementing the state system early.

## Phase 8 — optional cluster/inter-server messaging

A standalone Vega server must remain fully functional with no cluster transport configured.

- [ ] Add strongly typed `ServerId` and cluster message metadata (`MessageId`, correlation, sender, timestamp, schema/version).
- [ ] Keep Event, Command and Request/Response semantics distinct.
- [ ] Add narrow publishing/sending contracts without a service-locator API.
- [ ] Keep Redis/NATS/RabbitMQ/HTTP/WebSocket or any concrete transport outside Core/Contracts.
- [ ] Route incoming messages through DI-registered typed handlers.
- [ ] Design for duplicate delivery/idempotency; do not promise exactly-once delivery.
- [ ] Ensure remote-unavailable behavior does not break unrelated local server operation.
- [ ] Prevent republish loops: a local operation publishes once; a received replicated event applies local consequences without becoming a new originating event.

### First end-to-end cluster scenarios

Implement these before more complex distributed workflows:

1. [ ] **Server presence / online state**: establish transport, `ServerId`, expiry/staleness, observability and duplicate handling.
2. [ ] **Global ban propagation**: typed create/revoke events after successful local ban changes.
3. [ ] **Immediate global-ban enforcement**: receiving servers apply the ban locally and kick matching currently connected players through `IPlayerDirectory` + `IPlayerOperations`.
4. [ ] **Group / permission synchronization**: typed account/group/permission changes with local authorization cache/state updates.
5. [ ] **Inter-server chat**: typed chat event with explicit sender/server identity and local delivery policy.
6. [ ] Administrative commands, centralized audit/monitoring and aggregated multi-server status.
7. [ ] Player transfer / Dimensions only after the simpler messaging semantics are proven in production.

A durable outbox is optional and should be introduced only if a real scenario requires publication durability across process failure.

## Phase 9 — production module migration

- [ ] Port TerraZ modules to the Vega module API by capability, not by reproducing TShock class shapes.
- [ ] Use migrated modules as the forcing function for new contracts instead of building an abstract framework ahead of consumers.
- [ ] Add regression tests from known production incidents before retiring legacy workarounds.

### Historical implementations to mine for behavior, not architecture

- `popstarfreas/PvPController-1.3`
  - useful scenarios: packet policy context, ordered PvP damage/projectile decisions, authoritative player mutations, temporary session state;
  - its Redis dynamic JSON/action-string synchronization is explicitly a negative example for Vega cluster contracts.
- `popstarfreas/CustomWeaponAPI`
  - useful scenarios: strongly typed partial item overrides, targeted packet delivery and custom item behavior;
  - its bespoke `PacketWriter` must not be carried forward because Vega uses the shared typed `TZ.Multiplicity` protocol layer.

## Phase 10 — remove migration scaffolding

- [ ] Remove the TShockAPI project after no required production path depends on it.
- [ ] Remove TSAPI from the solution/submodules after native Vega hosting/hook dispatch is complete.
- [ ] Remove legacy hook/command/plugin compatibility surfaces.
- [ ] Rename remaining physical `TShock*` project paths/namespaces to final Vega names as they cease being migration sources.
- [ ] Delete migration-only adapters and documentation that no longer describe an active path.

## Phase 11 — World image and runtime optimization

Canonical persistence remains Terraria `.wld`. Vega `.vworld` is a disposable, versioned runtime representation and must always be rebuildable or bypassable.

### Baseline and profiling

- [ ] Profile stock Terraria `.wld` loading and post-load initialization separately.
- [ ] Record baseline file-read time, tile decoding, allocations, GC, chests/signs/entities creation, post-load work and runtime-index construction.
- [ ] Record baseline world-load CPU, peak memory and `WorldReady` / `NetworkReady` timing on small, medium and large production-like worlds.

### Level 2: derived Vega World Image

- [ ] Define the internal sectioned/chunked versioned Vega World Image format (`.vworld`).
- [ ] Keep `.wld` as source of truth for compatibility, import/export, recovery and initial persistence.
- [ ] Add source `.wld` content fingerprint plus Terraria world-format, Vega schema and compiler-parameter validation.
- [ ] Do not use timestamps as the only cache-validity criterion.
- [ ] Implement atomic world-image generation: write temporary file, flush, validate checksums, then atomically replace the previous cache.
- [ ] Ensure interrupted cache generation cannot damage `.wld` or the previous valid `.vworld`.
- [ ] Implement fast full-load from the derived runtime image with sequential reads, minimal parsing/branching and bounded temporary allocations.
- [ ] Cache only measured expensive decoding/precomputation work.
- [ ] Keep the binary layout and serializer entirely inside `Vega.Terraria` / world infrastructure; Core sees only world contracts/snapshots.
- [ ] Add precomputed runtime indexes only with real consumers and benchmarks.
- [ ] Candidate indexes include chest lookup, entities/signs by section, wiring/liquid regions, spatial lookups, initial player-sync data and world map/operations consumers.
- [ ] Fall back automatically to `.wld` when `.vworld` is absent, stale, incompatible or corrupt.
- [ ] Log cache hit/miss, invalidation reason, source/cache size, validation/corruption errors and fallback behavior.
- [ ] Integrate `WldLoadDuration`, `WorldImageBuildDuration`, `WorldImageLoadDuration`, `PostLoadInitializationDuration` and `WorldReadyDuration` with startup telemetry.
- [ ] Benchmark cold/warm `.wld` against cold/warm `.vworld`, including peak memory, allocations, `WorldReady` and `NetworkReady`.
- [ ] Decide from measurements whether `.vworld` should be enabled by default. Do not promise or assume a speedup before benchmarks.

### Level 3: chunked / memory-mapped backend, research only

- [ ] Investigate chunked/memory-mapped world storage separately from the Level 2 full-load implementation.
- [ ] Investigate lazy section loading for very large or multiple/sandboxed world contexts.
- [ ] Validate Terraria runtime assumptions before changing in-memory world ownership/storage semantics.
- [ ] Do not make lazy/mmap storage part of Vega 1.0 unless benchmarks and compatibility evidence justify it.

World-image invariant:

```text
.wld    = canonical compatibility/persistence format
.vworld = disposable optimized runtime representation
```

See ADR 0020 for format ownership, validation, fallback and persistence constraints.

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
