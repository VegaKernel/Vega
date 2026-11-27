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
