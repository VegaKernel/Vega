# Vega roadmap

## Phase 0 — freeze the target architecture

- [x] Establish .NET 10 baseline.
- [x] Record production-derived requirements and world/threading constraints.
- [x] Rename the product/repository identity to Vega.
- [x] Adopt Vega as an independent runtime rather than a TShock compatibility modernization (ADR 0016).
- [x] Define startup lifecycle, readiness barriers and performance budgets (ADR 0017).
- [ ] Add architecture tests that prevent Terraria/OTAPI/TSAPI/TShockAPI references from entering Vega.Core.

## Phase 1 — native runtime boundary

- [ ] Establish `Vega.Runtime` / `Vega.Terraria` (names may be refined) as the only Terraria/OTAPI integration boundary.
- [ ] Move process bootstrap/readiness telemetry into the Vega runtime host.
- [ ] Keep TSAPI only as temporary scaffolding while the native host is incomplete.
- [ ] Add a game-thread scheduler contract owned by the runtime boundary.
- [ ] Measure Vega overhead separately from Terraria world load time.

## Phase 2 — Vega module API

- [ ] Implement cheap synchronous module discovery/registration.
- [ ] Implement explicit startup barriers: Runtime, World, Network, Background and Lazy.
- [ ] Add per-module startup timing and budget warnings.
- [ ] Add deterministic module shutdown/cancellation semantics.
- [ ] Define typed command/event/capability registration without TShock surfaces.

## Phase 3 — Core isolation

- [ ] Remove direct Terraria/TShock dependencies from Core-owned behavior.
- [ ] Move world/player mutation behind world contexts, snapshots and scheduled capabilities.
- [ ] Port accounts, authorization, bans, SSC, regions, persistence and administration behavior from reference implementations into Vega-native services.
- [ ] Keep protocol parsing/validation side-effect-free where possible.

## Phase 4 — production module migration

- [ ] Port TerraZ modules to the Vega module API by capability, not by reproducing TShock class shapes.
- [ ] Promote true server invariants/security primitives into Core/runtime services.
- [ ] Keep gameplay/community policy as optional modules.
- [ ] Add regression tests from known production incidents before retiring legacy workarounds.

## Phase 5 — remove migration scaffolding

- [ ] Remove the TShockAPI project after no required production path depends on it.
- [ ] Remove TSAPI from the solution/submodules after native Vega hosting is complete.
- [ ] Remove legacy hook/command/plugin compatibility surfaces.
- [ ] Rename remaining physical `TShock*` solution/project paths and namespaces to their final Vega names.
- [ ] Delete migration-only adapters and documentation that no longer describe an active path.

## Phase 6 — world/runtime optimization

- [ ] Profile Terraria `.wld` loading independently.
- [ ] Optimize world metadata/index initialization without changing Vega.Core contracts.
- [ ] Evaluate lazy indexes, time-sliced world operations and sandbox backends.
- [ ] Keep `NetworkReady` independent from optional background warmup.
