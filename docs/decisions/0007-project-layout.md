# ADR 0007: Normalize repository layout after the .NET 10 baseline

## Status

Superseded as target layout by ADR 0016.

## Context

This ADR normalized the repository during the TShockNG migration and deliberately placed first-party source under `src/`, tests under `tests/`, and external dependencies under `external/`.

That mechanical cleanup remains useful, but its target tree permanently named `TShockAPI`, `TShockLauncher`, `TShockNG.*` and `external/TSAPI`, which no longer reflects Vega's target architecture.

## Historical decision retained

The following layout principles remain useful:

- first-party source belongs under `src/`;
- tests belong under `tests/`;
- external source/submodules belong under `external/`;
- repository metadata, licensing, CI and top-level documentation stay at the root;
- repository-wide mechanical moves should be separated from behavior changes where practical.

## Superseding direction

ADR 0016 defines the target roles as Vega Runtime/Terraria integration, Contracts, Core and Modules. Physical project/directory renaming and removal of TSAPI/TShockAPI are implementation work after the architecture is fixed.

TSAPI must not be treated as a permanent `external/` dependency merely because this historical layout placed it there.
