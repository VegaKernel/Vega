# 0012: World contexts and sandboxed worlds

## Status

Accepted

## Context

TShockNG is being split into `TShockNG.Contracts`, `TShockNG.Core` and the legacy-compatible `TShockAPI.dll` facade. This migration is the point where assumptions about a single global Terraria world must stop leaking into new architecture.

The current Terraria/TShock model is strongly process-global: world state, players, entities and many operations are reached through static/global objects such as `Main.*` and `TShock.*`. If the new core repeats those assumptions, adding isolated worlds later would require another broad rewrite.

TShockNG should support isolated temporary or persistent worlds ("sandboxes") while retaining a practical implementation path. The first implementation may use separate backend processes and Dimensions-compatible routing. A later runtime may host multiple isolated world contexts in one process if the Terraria/OTAPI runtime can safely support that model.

## Decision

All new core APIs that operate on world-specific state must be designed around an explicit world/server context rather than an implicit singleton world.

The initial contracts will model this with stable identifiers and context interfaces. The exact Terraria-backed implementation may evolve without changing consumers.

Conceptually:

```text
TShockNG host/control plane
  |
  +-- shared services
  |    +-- accounts
  |    +-- groups / permissions
  |    +-- bans
  |    +-- localization preferences
  |    +-- configuration / operations
  |
  +-- world context: main
  |
  +-- world context: event-arena
  |
  +-- world context: sandbox-42
  |
  +-- world context: build-test
```

A world context owns or exposes world-scoped services such as players, world state, scheduling, regions, entities and world-local configuration. Shared identity/account policy remains outside the world context unless there is a deliberate world-specific override.

## Core constraints

New `TShockNG.Core` code must not assume that:

- there is exactly one world;
- `Main.*` is the only source of world state;
- `TShock.Players` is the only player directory;
- a command always executes in the primary world;
- regions, warps or world rules are globally unique;
- a player connection and a world membership are permanently the same thing.

Terraria-backed adapters may still call legacy/global APIs during migration, but those dependencies stay behind world-context or infrastructure adapters. They must not become part of modern contracts.

## Initial contracts

The direction is approximately:

```csharp
public readonly record struct WorldId(string Value);

public interface IWorldContext
{
    WorldId Id { get; }
    string Name { get; }
    IWorldPlayerDirectory Players { get; }
    IWorldScheduler Scheduler { get; }
}

public interface IWorldContextAccessor
{
    IWorldContext Primary { get; }
    bool TryGet(WorldId id, out IWorldContext? world);
}
```

The contracts remain deliberately small. Terraria-specific mutable types are not exposed from `TShockNG.Contracts`.

Command invocations and future event/request contexts should be capable of carrying a world identity/context. The compatibility facade may infer the primary/current world for legacy callers that know nothing about multi-world operation.

## Sandbox model

A sandbox is a managed world context with explicit lifecycle and isolation policy. It may be persistent or temporary.

Target capabilities include:

- create a sandbox from a world/template;
- start, stop, reset and destroy it;
- assign a stable `WorldId` and display name;
- limit player count and access policy;
- choose whether SSC/player state is inherited, copied or isolated;
- configure world-local regions, warps and rules;
- select modules/plugins that are enabled for the sandbox where feasible;
- expose status and metrics through the operations API and Terminal.Gui UI;
- support automatic expiry/TTL for temporary sandboxes;
- move players between managed worlds through an explicit routing operation.

## Runtime implementation stages

### Stage 1: one real world, multi-world-safe contracts

The first implementation may expose only the primary Terraria world. This is still valuable: new core code becomes context-aware immediately and stops introducing singleton-world coupling.

### Stage 2: multi-process sandboxes

Use separate Terraria/TShockNG backend processes for isolated worlds while the main TShockNG control plane manages lifecycle, status and player routing. The built-in Dimensions protocol compatibility can carry forwarded connection metadata and support routing without requiring the historical plugin.

This stage provides strong crash and memory isolation and avoids immediately virtualizing Terraria's process-global state.

### Stage 3: optional single-process multi-world runtime

Investigate hosting multiple Terraria/OTAPI world instances in one process once the runtime provides sufficient isolation. `IWorldContext` remains the abstraction, so callers should not care whether a world lives in another process or in the same runtime.

Single-process multi-world support is an optimization/advanced backend, not a prerequisite for the sandbox API.

## Threading and safety

Each world context defines its own game-thread affinity. World state may only be mutated through that world's scheduler/game thread.

Cross-world operations must not directly mutate another world's Terraria state. They enqueue/schedule an operation against the target context and exchange immutable snapshots/messages where possible.

A failure in one sandbox should be contained as far as the selected backend permits. Multi-process sandboxes provide the strongest isolation and are therefore the preferred first production implementation.

## Compatibility

`TShockAPI.dll` continues to present the historical single-server surface to existing plugins.

For legacy callers:

- `TShock.Players`, legacy commands and existing hooks map to the player's/current primary world as compatibility requires;
- existing plugins are not required to understand `WorldId`;
- modern plugins/services use contracts from `TShockNG.Contracts` and can be world-aware;
- no existing public type is removed merely to enable sandbox support.

The compatibility facade must not expose arbitrary cross-world mutable Terraria objects.

## Operations UI

Terminal.Gui consumes the operations/read-model layer and should eventually show all managed world contexts, for example:

```text
Worlds
--------------------------------
main          37 players   running
event-arena    8 players   running
build-test     2 players   stopped
sandbox-42     1 player    expires 00:42:18
```

The UI can initiate lifecycle/routing commands through application services but never manipulates Terraria globals directly.

## Consequences

- Sandbox support is designed in before the new core accumulates single-world assumptions.
- The first useful sandbox implementation can be multi-process and does not depend on solving single-process Terraria isolation.
- Dimensions compatibility becomes a routing/forwarding building block rather than a permanent external plugin dependency.
- Commands, localization, operations and future APIs can evolve with explicit world context.
- Legacy TShock plugins retain the historical single-world facade.
- New code that reaches `Main.*` or `TShock.*` directly must be treated as an infrastructure/compatibility boundary, not normal core architecture.
