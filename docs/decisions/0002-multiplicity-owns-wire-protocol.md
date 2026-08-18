# 0002: Multiplicity owns the Terraria wire protocol

## Status

Accepted

## Context

Packet layouts are currently duplicated across stream-based handlers and plugins. Duplicate wire-format knowledge increases drift risk whenever Terraria changes its protocol.

## Decision

Multiplicity is the canonical implementation of Terraria packet IDs, layouts, serialization, deserialization, and typed zero-copy views.

TShockNG and ProtocolGuard consume typed packet data rather than reimplementing packet binary layouts.

## Consequences

- `GetDataHandlers` gradually loses manual binary parsing.
- Protocol changes are centralized and testable.
- Multiplicity must remain independent of TShock domain state.
