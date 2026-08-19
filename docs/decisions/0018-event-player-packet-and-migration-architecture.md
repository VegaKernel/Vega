# 0018: Event, player and packet processing architecture

## Status

Accepted

## Context

Vega is no longer a compatibility modernization of TShock. The next contracts must support migration of real production behavior without rebuilding TShockAPI shapes under new names and without creating a large abstract framework that has no working consumers.

Several recurring legacy problems must be prevented explicitly:

- event ordering is often implicit or encoded by registration timing;
- cancellation is usually a boolean without a structured reason or final outcome;
- monitoring handlers can accidentally modify an event they intended only to observe;
- asynchronous APIs are easily introduced into game-thread code even when there is no real asynchronous boundary;
- player wrappers often expose mutable `Terraria.Player` / `TSPlayer` state directly to business logic;
- packet handlers frequently parse raw streams ad hoc and combine decoding, validation, policy and Terraria mutation in one method;
- helper classes tend to accumulate business semantics until the helper layer becomes the actual architecture;
- generic extension/state systems are tempting to design before any entity lifecycle is stable.

The architecture must therefore grow together with migration of concrete TShock behavior. No subsystem in this ADR is justification for building a generic framework ahead of a real consumer.

## Decision

### 1. Event model

Vega will use a typed event pipeline with explicit handler ordering and explicit outcome semantics.

Event payloads are strongly typed and should be immutable records or equivalent read-only values where practical. Mutable Terraria objects are not event payloads in Core.

Handler registration is explicit through the composition root / standard .NET dependency injection. Core must not perform assembly scanning or reflection-heavy registration.

A conceptual registration shape is:

```csharp
builder.Events.Handle<PlayerJoining>(
    handler: HandlePlayerJoining,
    priority: EventPriority.Normal,
    invokeWhenCancelled: false);
```

Exact syntax may change, but the semantics below are normative.

#### Handler priority

Handlers have an explicit deterministic priority. The initial semantic bands are:

```text
Earliest
Early
Normal
Late
Latest
```

A stable secondary registration order is used within the same priority. Numeric custom priorities should not be exposed until a real scenario requires finer ordering.

#### Cancellation

Cancelable events carry structured cancellation state rather than a bare boolean.

A cancellation includes at least:

- machine-readable reason/code;
- optional human-readable diagnostic/message;
- source/handler identity where useful.

Cancellation is monotonic by default: a later ordinary handler does not silently un-cancel a decision. If a future scenario genuinely needs replacement/override semantics, that is modeled explicitly for that event type rather than by mutating a shared `Handled` flag.

Handlers declare whether they should run after cancellation. The default is to skip ordinary decision handlers once cancellation is final for their stage.

#### Monitor stage

`Monitor` is a separate final observation stage, not merely the numerically lowest/highest priority.

Monitor handlers:

- see the final event payload and decision/outcome;
- run after decision handlers;
- cannot modify cancellation or the final decision through the event API;
- are suitable for audit, metrics, diagnostics and observational integrations;
- should not be used to implement required business behavior.

Where a Terraria action must occur only after all handlers have produced a decision, the owner/integration layer performs a separate final **Apply** stage.

The model is therefore conceptually:

```text
Typed event input
    -> ordered decision handlers
    -> final immutable outcome
    -> Monitor observers
    -> Apply by owning runtime/integration component when required
```

`Apply` is not a general public handler priority. It is owned by the component that controls the underlying side effect, preventing arbitrary modules from racing to mutate Terraria state.

#### Synchronous vs asynchronous handlers

Synchronous and asynchronous handlers are distinct contracts.

Synchronous handlers are the default for game-thread events and bounded in-memory decisions.

Async handlers exist only for event flows whose origin can safely await or schedule asynchronous work without blocking the game thread. A synchronous Terraria hook must never be converted into `Task.Wait`, `.Result`, `GetAwaiter().GetResult()` or equivalent blocking simply to support an async handler.

I/O triggered by a synchronous event should normally capture the required immutable data, schedule background work, and later post any Terraria mutation back through `IGameThreadDispatcher`.

### 2. Player model

Core code consumes read-only player state through `PlayerSnapshot` rather than arbitrary mutable Terraria objects.

The initial player boundary consists of separate capabilities:

```text
PlayerSnapshot          read-only state required by business logic
IPlayerDirectory        find/list currently known players
IPlayerOperations       authoritative mutations such as kick/teleport/state changes
IPlayerMessenger        player/broadcast message delivery
```

These interfaces are deliberately separate. A service that only needs to send a message should not automatically receive teleport/kick/mutation capability.

`PlayerSnapshot` must remain demand-driven. It should not become a mirror of every field on `Terraria.Player`. New fields are added only when migrated behavior requires them.

Implementations that mutate Terraria state live in `Vega.Terraria` and execute the mutation on the game thread through the established dispatcher or an equivalent game-thread-owned path.

Core must not expose or accept `Terraria.Player`, `TSPlayer`, `Terraria.Main` or similar mutable runtime objects.

### 3. Packet processing context

Vega will define a typed packet processing context around packets decoded by `TZ.Multiplicity`.

Vega must not introduce a competing generic packet codec.

The processing context contains only transport/runtime metadata that processing stages genuinely need, including initially:

- typed packet value from `TZ.Multiplicity`;
- packet direction;
- connection identity;
- player identity when established;
- receipt timestamp / processing metadata required for diagnostics or validation;
- cancellation token where the surrounding flow supports cancellation.

The context must be suitable for passing through validation, security and Core policy pipelines without requiring those layers to parse a raw Terraria byte stream again.

Protocol decoding/encoding remains the responsibility of `TZ.Multiplicity` and the Terraria integration boundary.

### 4. Typed extension state

Vega may provide scoped typed extension state for entities/sessions after entity lifecycle is stable and a real migrated feature requires it.

The intended properties are:

- strongly typed keys;
- type-safe `GetOrCreate`, `Set` and `Remove` operations;
- ownership/lifetime tied to a concrete entity/session lifecycle;
- deterministic cleanup on disconnect/despawn/disposal as appropriate.

This state store is not a universal Core dependency and is not a substitute for modeled domain state. It is an escape hatch for genuinely extension-specific state.

Implementation priority for this capability is deliberately later than events, players and packet context.

### 5. Core services and helpers

Primary server semantics live in Core services.

Extension methods and helper APIs are allowed only as thin convenience layers over services/contracts. They must not become hidden stores of authorization, validation, persistence or gameplay/business policy.

### 6. No speculative Terraria abstraction hierarchy

Vega will not create interfaces for every Terraria type in advance.

A new abstraction is added only when:

- a real architectural boundary exists;
- a real feature is being migrated;
- a testable business rule needs to become independent from Terraria;
- or multiple implementations are already required.

### 7. Composition and DI

Vega uses standard .NET DI and an explicit composition root.

Vega will not introduce:

- a custom DI container;
- dynamic service rebinding infrastructure;
- reflection-heavy Core registration;
- service-locator APIs;
- magical assembly-wide handler discovery as a required mechanism.

### 8. Vertical migration rule

Architecture evolves together with real functionality migrated from TShock.

A new contract should normally land in the same implementation sequence as at least one working consumer or the immediately following vertical slice.

The implementation order is:

1. event model;
2. `PlayerSnapshot` + player directory/operations/messaging;
3. typed packet processing context based on `TZ.Multiplicity`;
4. migrate commands and legacy hooks through the new contracts;
5. typed extension state only after entity/session lifecycle has stabilized.

This order is intentionally designed to avoid a large unused framework core.

## Reference implementations and production scenarios

Historical/community modules may be used as sources of required behavior and architectural test cases, but not copied as architecture.

### `popstarfreas/PvPController-1.3`

Useful lessons to preserve as migration scenarios:

- packet policy requires a context combining player identity, packet semantics and server policy;
- PvP damage/projectile processing needs explicit ordered decision points rather than ad-hoc packet handlers;
- temporary per-player state such as projectile-to-weapon attribution, death/spectator state and timing windows is a concrete future consumer for typed session extension state;
- server-authoritative player mutation and client synchronization should be represented as operations, not direct `Terraria.Player` mutation from policy code;
- its Redis synchronizer is a useful negative example for cluster design: string action names and dynamic JSON payloads must be replaced by typed versioned messages.

### `popstarfreas/CustomWeaponAPI`

Useful lessons to preserve as migration scenarios:

- partial item overrides are a concrete example of strongly typed capability data;
- custom item drop/update behavior should use the shared typed packet layer instead of a bespoke `PacketWriter`;
- item/world mutation belongs to the Terraria integration/game-thread side, while desired item semantics can remain typed data consumed by a service;
- the implementation is a useful regression scenario for packet composition and player-targeted delivery, not a model for a new packet codec.

## Consequences

Positive:

- deterministic event ordering and observable final outcomes;
- audit/monitoring does not alter decisions;
- Core business logic no longer needs mutable Terraria players;
- packet validation can share a single typed context;
- game-thread mutation boundaries become enforceable;
- new APIs are introduced by real migration pressure rather than speculative generality.

Costs:

- migration requires explicit adapters and more deliberate capability boundaries;
- some legacy handlers that mix parsing, policy and mutation must be split before moving;
- async event support is intentionally narrower than a generic `Task`-based event bus;
- old plugins cannot be ported by mechanical namespace replacement.
