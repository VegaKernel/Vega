# 0005: Modern core API with a legacy compatibility facade

## Status

Accepted

## Context

TShockNG must preserve compatibility with the existing TShock plugin ecosystem while replacing old implementation patterns with a modern architecture suitable for new code.

Keeping the legacy public surface as the primary architecture would preserve technical debt indefinitely. Replacing it outright would break existing compiled plugins.

The migration is therefore incremental: the existing `TShockAPI.dll` remains available for compatibility while implementation is moved behind explicit contracts into a new core.

## Decision

TShockNG will use three architectural layers during the compatibility period:

1. `TShockNG.Contracts` - modern implementation-independent interfaces, DTOs, result types, snapshots and event contracts.
2. `TShockNG.Core` - the source of truth for server behavior and application services.
3. `TShockAPI.dll` - a thin legacy compatibility facade that preserves old public names and delegates to the modern contracts.

The dependency direction is deliberately one-way:

```text
                 TShockNG.Contracts
                    ^          ^
                    |          |
             TShockNG.Core   TShockAPI.dll
                    ^          ^
                    |          |
                    +---- composition root
```

`TShockNG.Core` must not reference `TShockAPI.dll`.

`TShockAPI.dll` must not become a second implementation of server behavior. Its purpose is compatibility, public adapters and legacy-facing interfaces only where preserving an existing contract requires them.

The launcher/composition root is responsible for constructing the modern services and connecting the legacy facade to those services.

## Modern API design

New APIs should prefer:

- explicit service contracts and constructor injection over static global access;
- typed packet views from `TZ.Multiplicity` instead of ad-hoc `BinaryReader` parsing;
- explicit result types for validation and operations instead of `bool` plus hidden side effects;
- immutable or read-only request/context/snapshot types where mutation is not required;
- `async` APIs for genuinely asynchronous I/O, with `CancellationToken` support;
- nullable reference type annotations on all new API surfaces;
- clear thread-affinity contracts for operations that must run on the Terraria game thread;
- event data that is strongly typed and can carry structured cancellation/decision information;
- small focused interfaces rather than service-locator style access to the entire server.

Async must not be introduced merely to make signatures look modern. CPU-bound packet parsing and game-thread state mutation remain synchronous unless there is a concrete reason otherwise.

## Core ownership

The new core becomes the owner of behavior as code is migrated. This includes, progressively:

- command execution and command services;
- account, group and permission behavior;
- database repositories and migrations;
- server/player state projections;
- network packet processing and validation orchestration;
- localization selection and per-player culture;
- GeoIP metadata services;
- Dimensions forwarding metadata;
- operational snapshots consumed by the Terminal.Gui UI;
- modern HTTP/REST hosting after the legacy HTTP server is replaced.

Terraria-owned mutable game state remains game-thread-only as defined by ADR 0003. Core services expose snapshots or scheduled operations rather than allowing arbitrary background access to Terraria globals.

## TShockAPI compatibility facade

Existing public types such as `TShockAPI.TShock`, `TSPlayer`, `Commands`, `GetDataHandlers`, permissions and legacy hooks remain available while compatibility is required.

As each subsystem is migrated:

1. behavior is implemented in `TShockNG.Core`;
2. the required public contract is represented in `TShockNG.Contracts`;
3. the legacy TShockAPI entry point delegates to that contract;
4. duplicate implementation is removed from the compatibility facade.

Consequently, files such as the current monolithic `Commands.cs` are transitional code, not the target architecture. Command implementations should be split by responsibility and migrated into focused core services/handlers instead of merely dividing one legacy monolith into several equally coupled files.

The facade may contain small compatibility transformations such as legacy argument conversion, return-value mapping and hook bridging. It must not contain database policy, packet validation, business rules or duplicated state machines.

Example direction:

```text
TSAPI raw packet
    -> Multiplicity typed view
    -> TShockNG.Core handler/service
    -> ProtocolGuard where applicable
    -> TShockAPI compatibility adapter
    -> existing TShockAPI hook/plugin surface
```

Legacy APIs may later be marked with `[Obsolete(..., false)]` after a supported modern replacement exists. Deprecation warnings must not precede a viable migration path.

## Commands migration

`Commands.cs` will be dismantled incrementally rather than maintained as a permanent monolith.

The target form is approximately:

```text
TShockNG.Contracts/
  Commands/
    ICommandDispatcher.cs
    ICommandContext.cs
    CommandResult.cs

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
  Commands.cs                 # compatibility registration/facade only
  Compatibility/Commands/     # legacy argument/result adapters
```

The legacy `Command`, `CommandArgs`, aliases, permissions and plugin-visible registration behavior are preserved while old plugins depend on them. New core handlers do not depend on the legacy static `Commands` class.

## Packaging and binary identity

NuGet package identity is independent from assembly identity.

The working package name for the modern distribution is `TShockNG`. Existing compatibility assemblies such as `TShockAPI.dll` retain their historical assembly names while being shipped by the modern package.

`TShockNG.Contracts.dll` and `TShockNG.Core.dll` are new implementation assemblies and therefore do not inherit the compatibility constraints of `TShockAPI.dll`.

## Consequences

- New functionality is implemented in the modern layer first.
- Legacy APIs become progressively thinner as migration proceeds.
- `TShockNG.Core` stays free of dependencies on the legacy `TShockAPI.dll`.
- Protocol parsing and security rules are not duplicated between old and new APIs.
- Binary compatibility can be retained without forcing new plugins to use the historical API style.
- The existing TShockAPI directory becomes a compatibility boundary rather than the place where new server architecture accumulates.
- Large legacy files are dismantled while behavior is moved, instead of receiving purely cosmetic file splits.
- Removal of legacy compatibility surfaces is reserved for an explicitly incompatible major transition.
