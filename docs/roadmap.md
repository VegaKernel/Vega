# TShockNG roadmap

## Phase 0 — establish project boundaries

- [ ] Point the `TerrariaServerAPI` submodule at `TShockNG/TSAPI` instead of `TerraZ-Team/TSAPI`.
- [ ] Add architecture and compatibility documentation.
- [ ] Add ADRs for protocol ownership, binary compatibility, and threading.
- [ ] Establish clean CI on the TShockNG organization repositories.

## Phase 1 — typed packet pipeline

- [ ] Add Multiplicity as the canonical packet parser.
- [ ] Introduce an internal raw packet context containing packet type, player, buffer, offset, and exact payload length.
- [ ] Use Multiplicity `ParsePayload`/typed packet views for migrated handlers.
- [ ] Keep legacy `MemoryStream`-based public hooks working.
- [ ] Migrate hot/simple packets first.
- [ ] Add packet-path benchmarks and allocation measurements.

Suggested first packet groups:

- player movement/update
- inventory slot updates
- HP/mana/team/PvP/zones
- projectile creation/update
- simple tile interaction packets

## Phase 2 — services and events

- [ ] Add explicit service contracts for high-coupling TShock subsystems.
- [ ] Add prioritized event dispatch.
- [ ] Add cancel reasons.
- [ ] Add `Monitor` event priority for audit/logging consumers.
- [ ] Keep legacy hooks as adapters.

## Phase 3 — ProtocolGuard extraction

- [ ] Define immutable validation contexts/snapshots.
- [ ] Move pure checks out of `Bouncer` and packet handlers.
- [ ] Start with projectile, item, tile, and player sanity validation.
- [ ] Keep enforcement decisions in TShockNG.
- [ ] Add regression tests for malformed and abusive packets.

## Phase 4 — execution and freeze reduction

- [ ] Route blocking SQL/HTTP/file work away from the game thread where semantics allow it.
- [ ] Add thread-affinity assertions for game-thread-only APIs.
- [ ] Add time-sliced scheduler for large Terraria-state mutations.
- [ ] Add slow-tick and slow-hook watchdog output.
- [ ] Measure P50/P95/P99/max tick duration before and after changes.

## Phase 5 — remove obsolete infrastructure

- [ ] Extract/replace old REST implementation.
- [ ] Remove `HttpServer.dll` from the core.
- [ ] Replace TShock Newtonsoft.Json usage with `System.Text.Json` while preserving configuration compatibility.
- [ ] Split database provider infrastructure if it materially simplifies the core.

## Phase 6 — internal cleanup

- [ ] Break down `GetDataHandlers.cs` after packet migration makes the split natural.
- [ ] Reduce `Bouncer.cs` to orchestration/adapters after ProtocolGuard extraction.
- [ ] Split giant command implementation by domain without breaking public command behavior.
- [ ] Remove obsolete compatibility surfaces only in a future explicitly incompatible API line.
