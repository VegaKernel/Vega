# 0015: Keep source-level plugin registration contracts, not binary compatibility

## Status

Accepted

## Context

The original migration plan treated the existing `TShockAPI.dll` metadata as a compatibility boundary. That would force new architecture to retain a broad facade, old static globals, historical type layouts and adapters whose only purpose is loading already-compiled plugins.

TShockNG does not require that constraint. Plugins may be rebuilt for TShockNG. What must remain straightforward is the source-level ability to subscribe to hooks and register commands while those plugin surfaces are migrated.

## Decision

TShockNG does not guarantee binary compatibility with existing TShock or TShockNG plugin assemblies.

The supported transition surface is deliberately narrow:

1. `IHookRegistry<TEventArgs>` registers and unregisters handlers for legacy-shaped events.
2. `ILegacyCommandRegistry` registers current `Command` objects and supports lookup/unregistration during the command migration.

Existing hook argument and `Command` types may be retained temporarily because plugins still need those concepts, but their concrete binary metadata is not frozen. Plugins are expected to rebuild when those types change.

New core code continues to use `TShockNG.Contracts` and must not depend on `TShockAPI` types. The registration interfaces are plugin-entry boundaries, not a second implementation of command or event behavior.

No adapter, facade, assembly name, member layout or obsolete global is retained solely to load an old compiled plugin. Compatibility work must have a current source-level consumer or a concrete migration purpose.

ADR 0001 is superseded by this decision. ADR 0005 is amended only where it previously required binary compatibility, assembly identity, or a broad legacy facade; its Contracts/Core/composition-root architecture remains active.

## Consequences

- Existing compiled plugins may fail to load and must be rebuilt.
- Plugin authors get explicit registration interfaces instead of mutating the `Commands.ChatCommands` global list.
- Hook subscription can be passed and tested as an interface instead of requiring direct access to a concrete `HandlerList<TEventArgs>`.
- TShockNG can change public metadata when architectural cleanup requires it.
- Legacy-shaped events and commands remain transitional source models, not permanent binary contracts.
- The modern command dispatcher and typed event pipeline remain the target architecture.
