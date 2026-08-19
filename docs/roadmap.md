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

- [ ] Implement explicit event priorities: `Earliest`, `Early`, `Normal`, `Late`, `Latest`.
- [ ] Implement structured monotonic cancellation with machine-readable reason and source.
- [ ] Allow handlers to opt into execution after cancellation.
- [ ] Implement a read-only `Monitor` stage that observes the final outcome without mutating it.
- [ ] Keep final side-effect application owned by the runtime/integration component instead of exposing `Apply` as a generic handler priority.
- [ ] Separate synchronous handlers from genuinely asynchronous handlers; never block the game thread on async work.
- [ ] Register handlers explicitly through standard .NET DI/composition root; no reflection-heavy Core scanning.
- [ ] Migrate one real TShock hook flow end-to-end through the new event pipeline before broadening the framework.

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

## Phase 6 — typed extension state, only when demanded

- [ ] Stabilize player/session/entity lifecycle first.
- [ ] Add strongly typed scoped keys only for a migrated feature that genuinely needs extension state.
- [ ] Support type-safe `GetOrCreate`, `Set`, `Remove` and deterministic cleanup.
- [ ] Do not make extension state a universal Core dependency or a substitute for modeled domain state.

Concrete future pressure already exists in historical PvP behavior: projectile-to-weapon attribution, death/spectator state and timing windows. These are test cases, not justification for implementing the state system early.

## Phase 7 — optional cluster/inter-server messaging

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

## Phase 8 — production module migration

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

## Phase 9 — remove migration scaffolding

- [ ] Remove the TShockAPI project after no required production path depends on it.
- [ ] Remove TSAPI from the solution/submodules after native Vega hosting/hook dispatch is complete.
- [ ] Remove legacy hook/command/plugin compatibility surfaces.
- [ ] Rename remaining physical `TShock*` project paths/namespaces to final Vega names as they cease being migration sources.
- [ ] Delete migration-only adapters and documentation that no longer describe an active path.

## Phase 10 — world/runtime optimization

- [ ] Profile Terraria `.wld` loading independently.
- [ ] Optimize world metadata/index initialization without changing Vega.Core contracts.
- [ ] Evaluate lazy indexes and time-sliced world operations.
- [ ] Keep `NetworkReady` independent from optional background warmup.

## Architectural guardrails

These apply across all phases:

- Core must not know `Terraria.Main`, `Terraria.Player`, OTAPI, TSAPI, `TerrariaApi.Server`, `TShockAPI.TShock`, `TSPlayer` or `Commands.ChatCommands`.
- Do not build interfaces for every Terraria type in advance.
- Do not introduce a custom DI container, dynamic service rebinding layer or reflection-heavy Core registration.
- Do not move business logic into helper/extension APIs.
- Do not add async merely because the language supports it; use it only across genuine asynchronous boundaries.
- Local business logic stays local. Cluster networking extends capabilities instead of becoming a hidden dependency.
- Prefer one working vertical slice over ten unused abstractions.
