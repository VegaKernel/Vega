# 0005: Modern core API and explicit integration boundaries

## Status

Accepted, with binary-compatibility policy amended by ADR 0015

## Context

TShockNG is replacing tightly coupled historical implementation patterns with an architecture that can be evolved, tested, and composed explicitly.

The useful part of the original migration plan is the separation between implementation-independent contracts, modern server behavior, and the TShock-facing integration surface. ADR 0015 later removed a different constraint: TShockNG does not need to preserve binary compatibility with already compiled plugins or retain a broad facade solely for that purpose.

The architectural layering in this ADR remains active. The binary-compatibility and assembly-identity requirements from the original revision do not.

## Decision

TShockNG uses three architectural roles:

1. `TShockNG.Contracts` contains implementation-independent interfaces, DTOs, result types, snapshots, hosting contracts, and event contracts.
2. `TShockNG.Core` owns modern server behavior and infrastructure implementations that do not depend on TShockAPI or mutable Terraria globals.
3. `TShockAPI` is the Terraria/TShock integration layer and the temporary source-level plugin surface while legacy hooks and commands are migrated.

The launcher is the composition root. It constructs concrete Core services and supplies them to the TShock integration layer through Contracts interfaces.

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

`TShockNG.Core` must not reference `TShockAPI` or mutable Terraria server types.

`TShockAPI` must not reference concrete `TShockNG.Core` implementations. It consumes Contracts and receives concrete services from the composition root.

No assembly name, member layout, obsolete global, adapter, or facade is retained solely so an already compiled plugin can load. ADR 0015 governs the supported source-level plugin transition surface.

## Modern API design

New APIs should prefer:

- explicit service contracts and constructor injection over static global access;
- typed packet views from `TZ.Multiplicity` instead of ad-hoc `BinaryReader` parsing;
- explicit result types instead of `bool` plus hidden side effects;
- immutable or read-only request/context/snapshot types where mutation is not required;
- `async` APIs for genuinely asynchronous I/O, with `CancellationToken` support;
- nullable reference type annotations on new API surfaces;
- clear thread-affinity contracts for operations that must run on the Terraria game thread;
- strongly typed event data with structured cancellation/decision information;
- small focused interfaces rather than service-locator access to the whole server.

Async is not introduced merely to make signatures look modern. CPU-bound packet parsing and game-thread state mutation remain synchronous unless there is a concrete reason otherwise.

## Core ownership

Core becomes the owner of behavior as each seam is migrated. This includes progressively:

- command dispatch and command handlers;
- account, group and permission behavior;
- database repositories and migrations;
- server/player state projections;
- network packet processing and validation orchestration;
- localization selection and per-player culture;
- GeoIP metadata services;
- Dimensions forwarding metadata;
- operational snapshots consumed by the Terminal UI;
- HTTP hosting and other process infrastructure.

Terraria-owned mutable game state remains game-thread-only as defined by ADR 0003. Core services use snapshots, contracts, or scheduled operations instead of arbitrary background access to Terraria globals.

## Composition root

`TShockLauncher` is responsible for selecting and constructing concrete infrastructure. TShockAPI does not instantiate Core implementations itself.

Initial composed services include:

- the MMDB IP metadata provider factory;
- the HTTP server factory backed by ASP.NET Core/Kestrel.

The same pattern applies as more behavior is migrated. A composition boundary may expose a small set of configured services to the TSAPI-created TShock plugin, but it must not become a general-purpose service locator for domain code.

## HTTP/REST migration

The inherited `HttpServer.dll` transport is removed.

HTTP hosting is represented by small contracts in `TShockNG.Contracts` and implemented by Kestrel in `TShockNG.Core`. The current REST route registry and endpoint callbacks may remain temporarily in `TShockAPI` because they directly manipulate TShock/Terraria state, but the transport itself is no longer owned by TShockAPI.

This creates the seam for later migration of individual REST operations into Core services without keeping a third-party binary HTTP server or coupling Core to legacy REST types.

## GeoIP migration

`IIpMetadataProvider` and its factory are Contracts. The MaxMind MMDB implementation is Core-owned. TShockAPI receives the provider factory from the composition root and only performs connection-flow integration.

The historical database parser is gone. A thin `GeoIPCountry` integration adapter may remain temporarily because the legacy TShock connection path still exposes that type; the adapter depends only on Contracts and obtains the concrete provider from the launcher composition boundary.

## Commands migration

`Commands.cs` is dismantled incrementally rather than maintained as a permanent monolith.

The target structure is:

```text
TShockNG.Contracts/
  Commands/
    ICommandDispatcher.cs
    CommandInvocation.cs
    CommandDescriptor.cs

TShockNG.Core/
  Commands/
    CommandDispatcher.cs
    Accounts/
    Administration/
    Players/
    World/
    Teleportation/
    Items/

TShockAPI/
  Commands.cs                    # transitional registration/integration
  Compatibility/Commands/        # only source-level adapters still needed
```

Legacy `Command`, `CommandArgs`, aliases, permissions, and hook-shaped types may remain while plugins are rebuilt against the source-level transition surface defined by ADR 0015. Their binary metadata is not frozen.

New Core handlers do not depend on the static legacy `Commands` class.

## Packaging

NuGet package identity and internal assembly layout may change when architecture requires it. `TShockNG.Contracts` and `TShockNG.Core` are implementation assemblies with no obligation to preserve historical TShock binary identity.

## Validation

Architecture tests enforce the most important dependency boundary: Core must not reference TShockAPI. Build/CI also verifies the launcher, TShockAPI, Contracts, Core, and TSAPI together after composition changes.

## Consequences

- New functionality is implemented behind Contracts/Core seams first.
- TShockAPI becomes progressively thinner instead of accumulating new behavior.
- Core stays independent from TShockAPI and Terraria mutable state.
- Concrete infrastructure is selected in the launcher composition root.
- Legacy binaries and adapters can be removed as soon as there is no current source-level migration purpose.
- Command, REST, packet, database, localization, and administration migration can proceed subsystem by subsystem without changing the dependency direction.
- ADR 0015 remains authoritative for plugin compatibility: source migration is supported where required, binary compatibility is not.
