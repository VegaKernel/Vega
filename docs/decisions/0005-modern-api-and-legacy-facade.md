# 0005: Modern core API and explicit integration boundaries

## Status

Accepted; architecture migration in progress.

ADR 0015 amends only the binary-compatibility and broad legacy-facade requirements from the original revision. The Contracts/Core/composition-root architecture in this ADR remains active.

## Context

TShockNG is replacing tightly coupled historical implementation patterns with an architecture that can be evolved, tested, and composed explicitly.

The useful part of the original migration plan is the separation between implementation-independent contracts, modern server behavior, and the TShock-facing integration surface. ADR 0015 later removed a different constraint: TShockNG does not need to preserve binary compatibility with already compiled plugins or retain a broad facade solely for that purpose.

## Decision

TShockNG uses three architectural roles:

1. `TShockNG.Contracts` contains implementation-independent interfaces, DTOs, result types, snapshots and event contracts.
2. `TShockNG.Core` owns server behavior and infrastructure implementations that do not depend on `TShockAPI`.
3. `TShockAPI` is the Terraria/TShock integration layer and the temporary source-level plugin surface while legacy hooks and commands are migrated.

`TShockLauncher` is the composition root. It constructs concrete Core services and supplies them to the TShock integration layer through Contracts interfaces.

The dependency direction is:

```text
                    TShockNG.Contracts
                     ^            ^
                     |            |
              TShockNG.Core    TShockAPI
                     ^            ^
                     |            |
                     +--- TShockLauncher
                         composition root
```

`TShockNG.Core` must not reference `TShockAPI`.

`TShockAPI` must not instantiate concrete `TShockNG.Core` implementations. It consumes Contracts and receives concrete services from the composition root.

No assembly name, member layout, obsolete global, adapter, or facade is retained solely so an already compiled plugin can load. ADR 0015 governs the supported source-level plugin transition surface.

## Modern API design

New APIs should prefer:

- explicit service contracts and constructor injection over static global access;
- typed packet views from `TZ.Multiplicity` instead of ad-hoc `BinaryReader` parsing;
- explicit result types instead of `bool` plus hidden side effects;
- immutable or read-only request/context/snapshot types where mutation is not required;
- `async` APIs only for genuinely asynchronous I/O, with `CancellationToken` support;
- nullable reference type annotations on new API surfaces;
- clear game-thread affinity contracts;
- strongly typed event data with structured cancellation/decision information;
- small focused interfaces rather than service-locator access to the whole server.

## Core ownership

Core is the source of truth for migrated behavior. Its ownership includes:

- command dispatch and command handlers;
- account, group and permission behavior;
- bans and access policy;
- database repositories, query building and migrations;
- character persistence and server/player projections;
- regions, warps and world-domain behavior;
- network packet processing and validation orchestration;
- localization selection and per-player culture;
- GeoIP metadata;
- Dimensions forwarding metadata;
- operational snapshots and update services;
- non-Terraria process infrastructure.

Terraria-owned mutable game state remains game-thread-only as defined by ADR 0003. Integration code may translate between Terraria/TShock objects and Core contracts, but Core must not use TShock globals as its architecture.

## TShockAPI integration surface

`TShockAPI` retains only code that is still genuinely an integration or source-level plugin boundary:

- TSAPI/Terraria plugin entrypoints and hook wiring;
- transitional `TSPlayer` and command/hook-shaped plugin models while they still have source-level consumers;
- `IHookRegistry<TEventArgs>` and `ILegacyCommandRegistry`;
- integration adapters that translate between Terraria/TShock objects and Contracts/Core;
- REST code until its separate migration is explicitly undertaken.

Legacy behavior must not remain in `TShockAPI` merely because it historically lived there.

## Commands migration

`Commands.cs` is transitional code, not the target architecture. Command behavior belongs under `TShockNG.Core/Commands`, grouped by responsibility. `TShockAPI` should eventually contain only command registration/integration required by ADR 0015.

Target direction:

```text
TShockNG.Contracts/
  Commands/

TShockNG.Core/
  Commands/
    Accounts/
    Administration/
    Players/
    World/
    Teleportation/
    Items/

TShockAPI/
  Compatibility/Commands/
  ILegacyCommandRegistry.cs
```

## Composition root

`TShockLauncher` owns process composition:

- construction of concrete Core services;
- process-level paths/options;
- host networking/socket selection;
- supplying services to TShock integration code.

It must not become a service locator used from arbitrary domain code.

## REST scope

REST is intentionally excluded from the structural migration in the ADR 0005 architecture PR. Existing REST files remain in `TShockAPI/Rest` until the REST/HTTP migration is handled separately.

## Validation

This architecture-only pass is allowed to be temporarily non-buildable while ownership is moved. Compilation repair, dependency cleanup and behavioral verification are a separate follow-up step after the structural review.

The final implementation of ADR 0005 is not complete until the dependency direction is enforced by code and tests.

## Consequences

- `TShockAPI` becomes an integration boundary rather than the home of server behavior.
- `TShockNG.Core` becomes the owner of migrated domain behavior.
- `TShockNG.Contracts` remains free of Terraria/TShock implementation details.
- `TShockLauncher` becomes the explicit composition root.
- binary compatibility is not a constraint;
- source-level hook and command registration remains available under ADR 0015;
- REST can migrate independently without blocking the rest of the architecture.
