# Compatibility policy

## Objective

TShockNG should modernize internals aggressively while keeping the existing TShock plugin ecosystem usable during migration.

The target architecture is not a rewritten legacy API. New code should use a modern TShockNG API, while the historical `TShockAPI` surface becomes a progressively thinner compatibility facade over the new implementation.

## Binary compatibility

Existing compiled plugins reference public metadata from `TShockAPI.dll` and `TerrariaServer.dll`. Preserving only type names in source is not enough.

During the compatibility period:

- keep assembly names expected by existing plugins;
- keep existing public types where practical;
- keep public member shapes and signatures where binary compatibility matters;
- do not change a public field into a property merely because the new design would prefer one;
- do not remove constructors used by existing plugins without an adapter path.

The NuGet package identity is separate from assembly identity. The working modern package ID is `TShockNG`, while compatibility assemblies such as `TShockAPI.dll` retain historical identities as long as existing plugin binaries require them.

## Modern API

New plugins and new internal code should target the modern API rather than adding more functionality to the compatibility facade.

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

## Source compatibility

New typed APIs may be added alongside old APIs.

Old APIs may be marked:

```csharp
[Obsolete("Use the modern TShockNG API instead.", false)]
```

`false` is intentional: migration warnings are acceptable; forced immediate breakage is not. Deprecation begins only after a viable modern replacement exists.

## Legacy packet hooks

The canonical internal path is:

```text
TSAPI raw packet
    -> Multiplicity typed packet
    -> modern TShockNG handler/service
    -> ProtocolGuard where applicable
    -> legacy event adapter
    -> existing plugin hook
```

The legacy API must not remain the architectural source of truth and must not duplicate packet parsing or validation logic.

## Deprecation stages

1. Supported and recommended.
2. Modern replacement introduced; legacy API delegates to it.
3. Supported but obsolete with a warning.
4. Frozen legacy API: bug fixes only.
5. Removal only in a documented incompatible major API transition.

## Compatibility testing

TShockNG should eventually maintain a small compatibility test suite containing representative prebuilt plugins to verify that existing binaries still load and their key hooks remain callable.
