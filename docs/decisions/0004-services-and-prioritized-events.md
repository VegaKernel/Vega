# 0004: Use explicit service contracts and prioritized events

## Status

Accepted

## Context

Large static/global surfaces make TShock code hard to isolate and test. Plugins also need deterministic ordering when validation, gameplay behavior, and audit observers all react to the same event.

## Decision

New core code prefers narrow service interfaces with constructor injection. Event dispatch supports explicit priority ordering and a final `Monitor` priority for observers.

No reflection-heavy DI framework is required for the initial implementation; explicit composition is preferred.

## Consequences

- ProtocolGuard and other extracted components can be tested against interfaces/snapshots.
- Security handlers can run before ordinary gameplay handlers.
- Audit/logging handlers can observe final event state.
