# 0005: Modern API with a legacy compatibility facade

## Status

Accepted

## Context

TShockNG must preserve compatibility with the existing TShock plugin ecosystem while replacing old implementation patterns with a modern API suitable for new code.

Keeping the legacy public surface as the primary architecture would preserve technical debt indefinitely. Replacing it outright would break existing compiled plugins.

## Decision

TShockNG will maintain two API layers during the compatibility period:

1. A modern API that is the source of truth for new code.
2. A thin legacy `TShockAPI` compatibility facade that adapts old public contracts to the modern implementation.

The legacy facade must not own duplicated domain or protocol logic.

## Modern API design

New APIs should prefer:

- explicit service contracts and constructor injection over static global access;
- typed packet views from `TZ.Multiplicity` instead of ad-hoc `BinaryReader` parsing;
- explicit result types for validation and operations instead of `bool` plus hidden side effects;
- immutable or read-only request/context/snapshot types where mutation is not required;
- `async` APIs for genuinely asynchronous I/O, with `CancellationToken` support;
- nullable reference type annotations on new API surfaces;
- clear thread-affinity contracts for operations that must run on the Terraria game thread;
- event data that is strongly typed and can carry structured cancellation/decision information;
- small focused interfaces rather than service-locator style access to the entire server.

Async must not be introduced merely to make signatures look modern. CPU-bound packet parsing and game-thread state mutation remain synchronous unless there is a concrete reason otherwise.

## Compatibility layer

Existing public types such as `TShockAPI.TShock`, `TSPlayer`, `Commands`, `GetDataHandlers`, and legacy hooks remain available while compatibility is required.

Where a modern equivalent exists, the old API should delegate through an adapter rather than maintain an independent implementation.

Example direction:

```text
TSAPI raw packet
    -> Multiplicity typed view
    -> modern TShockNG handler/service
    -> ProtocolGuard where applicable
    -> legacy adapter
    -> existing TShockAPI hook
```

Legacy APIs may later be marked with `[Obsolete(..., false)]` after a supported modern replacement exists. Deprecation warnings must not precede a viable migration path.

## Packaging and binary identity

NuGet package identity is independent from assembly identity.

The working package name for the modern distribution is `TShockNG`. Existing compatibility assemblies such as `TShockAPI.dll` may retain their historical assembly names while being shipped by the `TShockNG` package.

This separation allows NuGet naming to evolve without breaking compiled plugins that reference existing assembly metadata.

## Consequences

- New functionality should be implemented in the modern layer first.
- Legacy APIs become progressively thinner as migration proceeds.
- Protocol parsing and security rules must not be duplicated between old and new APIs.
- Binary compatibility can be retained without forcing new plugins to use the historical API style.
- Removal of legacy compatibility surfaces is reserved for an explicitly incompatible major transition.
