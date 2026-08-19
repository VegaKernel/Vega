# 0001: Preserve TShockAPI binary compatibility during migration

## Status

Historical. Superseded by ADR 0015; ADR 0015 is itself superseded by ADR 0016.

## Context

A large installed plugin ecosystem references `TShockAPI.dll` public metadata directly. Requiring every plugin to rebuild at the start of the original TShockNG migration would have made architectural cleanup unnecessarily disruptive.

## Decision

The original TShockNG migration would retain existing public API shapes where practical and expose modern APIs alongside legacy APIs during a compatibility period.

## Consequences

This compatibility policy no longer governs Vega. ADR 0016 establishes Vega as an independent runtime and removes TShock binary/source compatibility from the target architecture. This ADR remains as migration history.
