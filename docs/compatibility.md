# Plugin migration policy

## Objective

TShockNG modernizes the server API without treating historical `TShockAPI.dll` metadata as a permanent compatibility boundary.

Existing plugins may be rebuilt for TShockNG. The migration goal is to keep the common source-level plugin entry points straightforward while the internal architecture moves to `TShockNG.Contracts` and `TShockNG.Core`.

ADR 0015 is the authoritative compatibility decision.

## Binary compatibility

TShockNG does not guarantee binary compatibility with existing TShock or earlier TShockNG plugin assemblies.

Public metadata may change when architectural cleanup requires it. No facade, adapter, assembly identity, field layout, constructor, obsolete global, or other public shape is retained solely so that an already-compiled plugin continues to load unchanged.

Plugins should be rebuilt against the TShockNG version they target.

## Supported source-level transition surface

The deliberately narrow legacy-facing registration surface is:

- `IHookRegistry<TEventArgs>` for registering and unregistering handlers for legacy-shaped events;
- `ILegacyCommandRegistry` for plugins that still register the current legacy `Command` type.

Legacy event arguments and `Command` remain transitional source models. Their concrete binary metadata is not frozen.

Plugins should register commands through `Commands.Registry` instead of mutating a global command list.

## Modern API direction

New internal code and new APIs should target `TShockNG.Contracts` and `TShockNG.Core` rather than adding new behavior to legacy-shaped types.

The modern API should prefer:

- explicit services and constructor injection instead of static global access;
- strongly typed packet views and event data;
- explicit operation and validation result types;
- immutable or read-only contexts and snapshots where appropriate;
- nullable annotations on new public surfaces;
- `async` plus `CancellationToken` for genuine asynchronous I/O;
- explicit game-thread affinity for Terraria state access;
- small focused interfaces instead of service-locator access to the entire server.

`async` is not a decoration. CPU-bound packet parsing and game-thread mutation remain synchronous unless asynchronous execution has a concrete semantic or performance benefit.

## Packet and hook migration

The intended internal packet path is:

```text
TSAPI raw packet
    -> Multiplicity typed packet
    -> TShockNG.Core handler/service
    -> ProtocolGuard where applicable
    -> legacy-shaped event bridge where a source-level plugin hook is still needed
```

Legacy hooks are migration boundaries, not the architectural source of truth. Packet parsing, validation, and game rules must not be duplicated merely to preserve an old API implementation.

## Removal policy

Legacy-shaped APIs may be changed or removed once their current source-level migration purpose is gone. There is no staged binary-compatibility freeze.

When a legacy surface remains, it should have one of these reasons:

1. a current plugin source still needs the registration or event concept;
2. it is an active bridge to a modern TShockNG contract;
3. removing it would block an unfinished migration that is already represented in the roadmap.

Historical compatibility by itself is not sufficient justification.

## Testing

Compatibility tests should validate the supported source-level migration contracts, including hook registration and legacy command registration. Prebuilt-plugin binary loading is not a release requirement.
