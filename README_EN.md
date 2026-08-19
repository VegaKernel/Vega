# Vega

[Русский](README.md) | [English](README_EN.md)

Vega is an independent modern server framework/runtime for Terraria.

It grew from production experience with TShock, but it is **not TShock with a modernized internal architecture**. Historical TShock code is a source of proven behavior, requirements, edge cases and regression scenarios; its API shapes are not long-term Vega constraints.

## Target architecture

```text
Terraria
   ↓
OTAPI
   ↓
Vega.Runtime / Vega.Terraria
   ↓
Vega.Contracts
   ↓
Vega.Core
   ↓
Vega.Modules
```

- OTAPI remains the low-level instrumentation/patching layer.
- TSAPI is not part of Vega's target architecture.
- TShockAPI is not a target compatibility layer.
- Binary compatibility with TShock/TSAPI plugins is not required.
- Source-level legacy compatibility is not a long-term goal.
- Existing TerraZ plugins will be ported to the Vega module API.
- `Vega.Core` does not depend on Terraria, OTAPI, TSAPI or TShockAPI.

Existing `TShock*` projects and the TSAPI submodule may temporarily remain as migration scaffolding while a native Vega runtime path is built. New architecture must not deepen those dependencies.

## Fast startup

Startup speed is an architectural invariant. `NetworkReady` is the primary readiness point; optional warmup continues after the server can accept players.

Terraria world loading is measured separately from Vega startup overhead.

See [`ADR 0017`](docs/decisions/0017-startup-lifecycle-readiness-and-performance-budgets.md).

## Documentation

- [`docs/architecture.md`](docs/architecture.md)
- [`docs/roadmap.md`](docs/roadmap.md)
- [`docs/decisions`](docs/decisions)
- [`ADR 0016`](docs/decisions/0016-vega-independent-runtime-architecture.md)
- [`ADR 0017`](docs/decisions/0017-startup-lifecycle-readiness-and-performance-budgets.md)

Physical solution/project names may still reflect the migration era. Mechanical renaming and removal of TSAPI/TShockAPI are separate implementation changes.

## License

GPL-3.0-or-later. See [`COPYING`](COPYING).
