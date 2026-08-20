# Typed extension state

Vega exposes typed extension state only for temporary data owned by a concrete lifecycle. It is not a general property bag on players and it does not replace domain models, persistence or authorization state.

## Player session lifecycle

`TerrariaPlayerBoundary` detects active-player transitions on the game thread. Each connection receives a new `PlayerSessionId`, even when Terraria later reuses the same numeric player slot. The boundary publishes `PlayerSessionOpened` and `PlayerSessionClosed`, and immutable `PlayerSnapshot` values carry the current session ID.

Consumers must key temporary player data by `PlayerSessionId`, not only by `PlayerId`. Closing a session removes the whole scope and disposes it. A stale scope rejects further access.

## State API

`ExtensionStateKey<T>` makes the stored value type part of the key identity. `IExtensionStateScope` supports:

- `GetOrCreate` for lazy, single-scope initialization;
- `TryGet` for typed lookup;
- `Set` for replacement;
- `Remove` for targeted cleanup;
- scope disposal for deterministic bulk cleanup.

The same textual key name with a different `T` is a different key. Scope creation is explicit and duplicate scope IDs are rejected.

## First consumer

`Vega.Modules.Pvp.PvpSessionStateService` is the first demand-driven consumer. It stores:

- projectile identity to weapon attribution;
- bounded death timing windows;
- spectator state.

The service subscribes to the player-session lifecycle, initializes a scope on connect and removes it on disconnect. Tests cover player-slot reuse to ensure the next player receives no stale projectile, death or spectator state.

The runtime composes this store and consumer explicitly. Other Core services do not depend on extension state, and `PlayerSnapshot` does not expose a generic state bag.
