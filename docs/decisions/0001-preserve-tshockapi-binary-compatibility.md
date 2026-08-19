# 0001: Preserve TShockAPI binary compatibility during migration

## Status

Superseded by ADR 0015

## Context

A large installed plugin ecosystem references `TShockAPI.dll` public metadata directly. Requiring every plugin to rebuild at the start of TShockNG migration would make architectural cleanup unnecessarily disruptive.

## Decision

TShockNG will retain existing public API shapes where practical and expose modern APIs alongside legacy APIs. Legacy members may be marked obsolete with warnings but remain functional during the compatibility period.

## Consequences

- Internal architecture can change independently of plugin-facing APIs.
- Compatibility adapters are expected temporary complexity.
- Public metadata changes require explicit compatibility review.
