# Vega architecture and code complexity review ? 27 November 2026

## Scope and method

The [reproducible Roslyn report](code-complexity-metrics-2026-11-27.md) covers the C# source under `src`, excluding build output, and direct source `ProjectReference` dependencies. Run locally with the repository's .NET 11 SDK:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/audit-code-complexity.ps1
```

The decision score is a syntax screening metric, not McCabe's exact cyclomatic complexity or a semantic defect prediction. In particular a table-like switch can have a large score despite simple, exhaustively tested alternatives. Lambda bodies are measured separately. Physical lines include formatting and comments. Direct project references are not runtime/DI dependency measurements. The before snapshot was taken at the beginning of this implementation round while independent feature work was underway, not at a clean historical revision.

## Changes completed

| Entry point | Before score / lines | After score / lines | Largest extracted method score | Rationale |
|---|---:|---:|---:|---|
| `RestAdministration.WriteAsync` | 86 / 131 | 15 / 23 | 34 | Dispatch by domain; preserve common body and error handling; keep domain validation local. |
| `WorldEventCommand.ExecuteAsync` | 84 / 56 | 14 / 15 | 29 | Static alias/event maps and separate parsing, current-state lookup and native patch construction. |
| `OfflineMigrationCommands.TryRun` | 63 / 197 | 8 / 11 | 17 | Separate homes, whitelist, regions, SSC, accounts, bans and warps; preserve switch order and offline startup boundary. |
| `GroupCommand.ExecuteAsync` | 58 / 124 | 25 / 37 | 11 | Route separately to group lifecycle, inheritance, permission and presentation operations; preserve aliases, argument guards and existing authorization order. |

The improvements reduce branch concentration, not necessarily the sum of decisions across the entire application. No new migration schema, general dispatcher abstraction, reflection-based command discovery, or additional module dependency was introduced for these refactors.

## Findings and priorities

- **P0/P1 architectural hotspot: Runtime composition.** `Program.Start` is still 806 lines and score 95. It is the intended dependency composition root (19 direct references); however startup, optional services, hook ordering, and shutdown are intertwined. Extract cohesive lifetime-owned subsystem assemblies of registrations, with tests for ordering and disposal, rather than hiding the existing method in a large class or using service-location.
- **P1 native world operations.** `TerrariaWorldEnvironmentService.Patch` (71), `TerrariaWorldNativeActions.Grow` (67), and `TerrariaSchematicObjects.Prepare` (59) require operation-specific validation and apply stages. Keep all native mutations on the owning thread and preserve transactional rollback. Do not parallelize or generalize mutable Terraria calls solely to reduce a complexity metric.
- **P1 administration.** `LoopbackRestService.ServeAsync` (62), ban tools (61), warps (53) and scene command (52) are the next command/request decomposition candidates; separate parsing and access checks from domain operations, preserving existing response texts, permission ordering and behavior. Measured route tables and game event maps need not be split artificially.
- **P1 API boundaries.** 467 SDK bodies, maximum score 17, two at least 15, none at least 30. Dependency graph: no source project cycles; Contracts and Schematics have no source project references, ordinary modules typically have one or two. The SDK remains typed and detached, including scoped event registrations, durable plugin-owned data, service contracts, background work, inventory actions and world-local APIs. Prefer new small typed capabilities over plugin access to native objects.
- **Performance.** These are maintainability metrics, not TPS measurements. The hot tile observation path does not allocate a frame, snapshot or scan tiles when unsubscribed; subscribed accepted observations are bounded. Existing owner-thread and background lane boundaries remain unchanged. New gameplay subscribers should batch durable writes off-thread rather than block the simulation.

## Plugin migration

See the current [TerraZ readiness matrix](terraz-plugin-port-readiness.md). This round adds bounded committed cell observations, composable FakeProvider tile UI controls, durable virtual custom item instances and authenticated role/capacity cluster discovery. The framework supports developing new plugins against one typed SDK without TShock dependencies. Full legacy TUI widgets, physical arbitrary item stats across stock inventory/SSC, independently hosted player transfer, definitive client-sourced PvP attribution, actual stock-client SSC and stable 60 TPS at target load remain distinct open items. Do not interpret a successful unit/architecture suite as stock-client qualification or production equivalence.


## Runtime decomposition follow-up

The [after snapshot](code-complexity-metrics-runtime-2026-11-27.md) records the next round against the original audit. `Program.Start` has been replaced by a short lifecycle sequence in `RuntimeHost.Run`, with private explicitly typed stage outputs. The stages preserve the original hook/policy registration order, primary/sandbox distinctions, configuration-read order and exit-code handling. Construction ownership is shared through a small LIFO resource scope: a failed stage does not strand previously constructed resources, and disposal continues even if a resource fails. The separate tile-storage scope still encloses the host. There is no new dependency, automatic service discovery, public SDK change or database migration.

The native world-environment mutation now validates all regular fields before applying clock/weather, location/rule and seasonal/Angler changes on the game thread. Special native events and hardmode retain their exclusive operation path. Growth keeps separate cactus, herb and tree operations and a ground-type table. Loopback REST separates listener lifetime, request access/dispatch, snapshot projections, administrator routes and world maintenance without changing its sequential lane or authorization requirements.

| Entry point | Before score / lines | After score / lines |
|---|---:|---:|
| Startup | 95 / 806 | 13 / 27 |
| Native world environment patch | 71 / 124 | 8 / 32 |
| Native growth | 67 / 80 | 18 / 28 |
| REST listener loop | 62 / 137 | 12 / 24 |

Measured maximum decision score in Vega.Runtime is now 25 (previously 95); no Runtime callable body is 100 lines or longer. This does not imply lower total complexity across every domain: extracted route dispatch, validation and mapping are measured separately. The largest remaining native hotspots are the packet classification and schematic preparation tables, and Core command handlers such as ban tools. Existing performance/TPS and actual-client qualification gates are unchanged.

Verification: Release solution tests passed **937 runtime + 33 architecture**; eight external MySQL/PostgreSQL cases were skipped without local connection settings. The native deployment smoke passed REST world reads and mutations, schematic export, Ordered TCP with producer batching, sandbox startup and shutdown, shared admission storage, and TShock-compatible group/region/restriction/world-event commands. The new tests verify LIFO cleanup despite a disposal failure, validation before compound native mutation, and unchanged incompatible cactus/herb/gem-tree ground.


## Further domain decomposition

The [follow-up audit](code-complexity-metrics-followup-2026-11-27.md), compared with the Runtime after-snapshot above, shows **35 ? 18** bodies with decision score ? 30 and **19 ? 15** bodies spanning at least 100 lines. Syntax errors and project dependency cycles remain zero. Additional helper methods explain the increase in physical line count; the goal is smaller independently reviewable responsibilities, not a lower sum of syntax nodes.

| Entry point | Before score | After score |
|---|---:|---:|
| ExtendedBans dispatch | 61 | 13 |
| Native schematic preparation | 59 | 4 |
| Warp dispatch | 53 | 28 |
| FakeProvider command | 52 | 26 |
| Virtual chest transfer | 49 | 23 |
| Native item delivery callback | 48 | 24 |
| Mute dispatch | 46 | 10 |
| Native player-state sampling | 45 | 16 |
| Sandbox creation | 43 | 20 |
| Homes dispatch | 40 | 22 |
| Region membership tick | 37 | 9 |
| Native schematic placement | 37 | 26 |
| Schematic document validation | 36 | 1 |
| Item command | 35 | 17 |
| REST sandbox administration | 34 | 19 |
| World-image validation | 33 | 16 |
| Native schematic object application | 32 | 2 |

The refactoring preserves command aliases, permission checks, response text, exact session/world checks, revision validation, native game-thread ownership, and original operation ordering. Preparation and application of schematic registrations are separated while keeping one shared footprint tracker and rollback list. Virtual chest transfer builds both replacements before the existing atomic authority callback; a committed transfer remains applied even if synchronization fails. The world-image reader retains file format, validation order and failure reason codes. Region and player tick observation retain existing caches, scans and buffer reuse. Sandbox creation's operation gate and template rollback remain in the orchestration method.

The remaining 18 bodies are not 18 defects. The highest score (60) is the exhaustive packet-family classification table, not a deeply nested algorithm; splitting it would obscure the mapping. Native scene send interception, outbound queue lifetime, cluster authentication, tile authorization and binary image decoding are also sensitive to correctness and performance. Further changes should be driven by a specific maintenance or measurement need, with targeted tests; no new workers, dispatch queues or abstraction layers were introduced by this pass.

Verification: final Release solution run passed **938 runtime + 33 architecture tests**, eight external MySQL/PostgreSQL cases skipped without live local endpoints. New ExtendedBans coverage checks the online subject set, exact world/session kick, ticket lookup and invalid-duration non-mutation. Native deployment smoke passed REST, schema export, Ordered TCP, sandbox readiness/isolation/shared admission and parity commands. No new 255-player TPS qualification was attempted for this maintainability round.
