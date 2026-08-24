# 0016: Vega as an independent Terraria server runtime

## Status

Accepted

Supersedes the removed migration-era compatibility and TSAPI-target decisions. It amends ADR 0012, ADR 0013 and ADR 0014 where those decisions refer to TShockNG, TSAPI, TShockAPI or legacy plugin surfaces as target architecture.

## Context

The project began as a modernization effort around TShock and inherited assumptions that were useful for incremental migration: `TShockAPI` as an integration surface, TSAPI as the server host, and source-level compatibility for existing hooks and commands.

Those assumptions are no longer the product direction. Vega is not TShock with a cleaner interior. Vega is an independent modern server framework/runtime for Terraria. Existing TShock code remains valuable as a record of production behavior, operational requirements, security lessons and edge cases, but it is not the architectural foundation that new code must preserve.

The transition must therefore stop treating historical plugin surfaces as constraints. Existing TerraZ plugins will be ported to the Vega module API. Binary compatibility with TShock/TSAPI plugins is not required. Source-level compatibility with TShock hooks, `Command`, `TSPlayer`, globals or plugin lifecycle types is also not a long-term requirement.

TerrariaRuntime remains useful because it provides low-level instrumentation and patching over Terraria. That is a deliberately narrow role and does not make TerrariaRuntime part of Core.

## Decision

Vega is an independent Terraria server runtime with a strict separation between game integration and implementation-independent server behavior.

The target dependency direction is conceptually:

```text
Terraria
   |
   v
TerrariaRuntime
   |
   v
Vega.Runtime / Vega.Terraria
   |
   v
Vega.Contracts
   |
   v
Vega.Core
   |
   v
Vega.Modules
```

The exact project names may be refined during implementation. The dependency constraints are normative even if names change.

A more precise interpretation is:

- `Vega.Terraria` owns Terraria/TerrariaRuntime integration, adapters, patch entrypoints, game-thread scheduling and translation between Terraria objects and Vega contracts.
- `Vega.Runtime` owns process/bootstrap/module-host/readiness concerns that are not domain behavior. It may compose `Vega.Terraria` and `Vega.Core`.
- `Vega.Contracts` contains implementation-independent contracts, identifiers, DTOs, events, snapshots and capability interfaces.
- `Vega.Core` contains server/application behavior that can be reasoned about without direct references to Terraria, TerrariaRuntime, TSAPI or TShockAPI.
- `Vega.Modules` denotes modules built on the public Vega module API. It is a conceptual consumer boundary, not necessarily one assembly.

`Vega.Core` must not reference or expose:

```text
Terraria.Main
Terraria.Player
TerrariaRuntime
TSAPI
TerrariaApi.Server
ServerApi.Hooks
TShockAPI.TShock
TSPlayer
Commands.ChatCommands
```

Terraria-specific mutable state is represented to Core through contracts, immutable snapshots and explicit scheduled operations.

## TShock and TSAPI status

TShock is a behavior/reference source, not a target dependency model.

Historical code may be used to answer questions such as:

- which production invariants exist;
- how account, permission, SSC, region and moderation behavior has worked;
- which malformed packet and lifecycle cases have already been encountered;
- which operator workflows must be preserved functionally;
- which regressions require focused tests before replacement.

This does not imply preservation of TShock class layouts, namespaces, static globals, command types, hook types or plugin lifecycle semantics.

TSAPI is not part of Vega's target architecture. During migration it may remain in the repository and solution only as temporary scaffolding needed to keep the existing server executable while a native Vega runtime host is built. New target architecture must not be designed around TSAPI APIs.

Removal of TSAPI from the solution/submodules is a later implementation step and is intentionally not performed by this ADR.

## TerrariaRuntime boundary

TerrariaRuntime remains the low-level instrumentation/patching layer directly above Terraria.

Vega may consume TerrariaRuntime-specific hooks and patched runtime capabilities only inside `Vega.Terraria` or an equivalently named runtime-integration layer. TerrariaRuntime types must not leak into `Vega.Contracts` or `Vega.Core`.

If a capability can later be implemented without TerrariaRuntime, changing that integration must not require changes to Core contracts unless the underlying semantic capability itself changes.

## Module direction

Vega modules are first-class consumers of Vega contracts and module-host services. They do not inherit TShock plugin compatibility as a design requirement.

Existing TerraZ plugins are migration inputs:

1. identify the behavior or capability;
2. decide whether it is a Core invariant/service or an optional module policy;
3. define a Vega-native contract if needed;
4. port the plugin/module to the new API;
5. retire the legacy implementation after equivalent tests and production observability exist.

No compatibility adapter is justified solely because an old plugin already uses `TSPlayer`, `ServerApi.Hooks`, `Commands.ChatCommands` or another TShock surface.

## World boundary

ADR 0012 remains directionally valid: world-specific behavior must use explicit world context rather than an implicit singleton. The naming and legacy bridge text are amended by this ADR.

`IWorldContext` and related contracts must allow future runtime implementations to optimize world metadata, loading, indexes, sandboxes and multi-process or single-process world hosting without placing Terraria globals into Core.

## Threading

ADR 0003 remains active.

- Mutable Terraria state is changed only on the appropriate game thread.
- SQL, HTTP, file I/O, serialization and pure computation may execute away from the game thread.
- Background results that mutate Terraria state are scheduled back to the game thread.
- Large game-thread operations should support time slicing where practical.
- Asynchrony and concurrency are tools, not architectural goals. Independent work may run concurrently only when ownership and ordering are explicit.

## Migration policy

This decision deliberately allows temporary architectural debt while Vega remains executable.

Temporary code may still contain TShockAPI or TSAPI dependencies, but each such dependency is migration debt, not a supported public boundary. New code must not deepen those dependencies.

The preferred migration sequence is:

1. establish Vega contracts and runtime boundaries;
2. make startup/readiness and module lifecycle explicit;
3. move Terraria/TerrariaRuntime integration behind `Vega.Terraria`;
4. port server behavior into `Vega.Core` without Terraria/TShock types;
5. introduce the Vega module host/API;
6. port TerraZ modules;
7. remove TShockAPI and TSAPI scaffolding once no required production path depends on them.

## ADR audit

The current ADR set is interpreted as follows after this decision:

| ADR | Result |
|---|---|
| 0002 | Remains valid where protocol ownership is independent of TShock/TSAPI naming |
| 0003 | Remains valid |
| 0004 | Remains directionally valid; event/module surfaces become Vega-native |
| 0008 | Remains valid; implementation must live behind Vega contracts/boundaries |
| 0009 | Remains directionally valid; names and legacy adapters are transitional |
| 0010 | Remains directionally valid; UI consumes Vega operations contracts |
| 0011 | Remains valid as protocol/routing requirements, not plugin compatibility |
| 0012 | Remains valid with Vega naming and without a long-term TShock bridge |
| 0013 | Remains valid as production-requirements evidence; legacy loader/adapter goals are removed |
| 0014 | Remains valid as module/Core capability guidance; compatibility adapters are not a goal |

## Consequences

- Vega can design its public API for its own lifecycle and semantics rather than for TShock compatibility.
- Existing plugins require deliberate ports to Vega modules.
- Removing TSAPI and TShockAPI becomes an explicit migration objective.
- Core remains portable and testable without Terraria, TerrariaRuntime or legacy server assemblies.
- TShock remains extremely valuable as production evidence, but future architecture is free to reject its shapes.
- Some short-term migration code will become disposable. This is intentional and should be labelled as such rather than promoted to permanent API.
