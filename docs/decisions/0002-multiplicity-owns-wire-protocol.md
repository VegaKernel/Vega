# 0002: Multiplicity owns the Terraria wire protocol

## Status

Accepted

## Context

Packet layouts are currently duplicated across stream-based handlers and plugins. Duplicate wire-format knowledge increases drift risk whenever Terraria changes its protocol.

## Decision

`TZ.Multiplicity` is the canonical implementation of Terraria packet IDs, layouts, serialization, deserialization, and typed zero-copy views.

Consumers reference Multiplicity as a pinned NuGet package. TShockNG does not vendor it as source or add it as another submodule.

TShockNG and ProtocolGuard consume typed packet data rather than reimplementing packet binary layouts. TSAPI stays independent from Multiplicity unless a future TSAPI-level requirement genuinely needs it.

## Consequences

- `GetDataHandlers` gradually loses manual binary parsing.
- Protocol changes are centralized and testable.
- Multiplicity remains independently versioned.
- Updating Terraria protocol support becomes an explicit package-version update in consumers.
