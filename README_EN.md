# Vega

[Русский](README.md) | [English](README_EN.md)

Vega is an independent modern server framework/runtime for Terraria. Historical TShock code is retained as production evidence and migration material, not as Vega's architectural foundation.

## Current boundaries

```text
Terraria / OTAPI
       ↓
Vega.Terraria
       ↓
Vega.Runtime
      / \
Vega.Contracts  Vega.Core
       ↑
Vega modules
```

- `Vega.Contracts` contains implementation-independent contracts and DTOs.
- `Vega.Core` owns server behavior and must not reference Terraria, OTAPI, TSAPI or TShockAPI.
- `Vega.Terraria` is the target home for Terraria/OTAPI-specific integration.
- `Vega.Runtime` is the process host and composition root.
- `TShockAPI` and `external/TSAPI` remain temporary migration scaffolding only.

The Core dependency boundary is enforced by architecture tests.

## Build

```bash
dotnet restore Vega.sln
dotnet build Vega.sln
dotnet test Vega.sln
```

The main executable project is `src/Vega.Runtime/Vega.Runtime.csproj`, producing `Vega.Server`.

See [`ADR 0016`](docs/decisions/0016-vega-independent-runtime-architecture.md) and [`ADR 0017`](docs/decisions/0017-startup-lifecycle-readiness-and-performance-budgets.md).

## License

GPL-3.0-or-later. See [`COPYING`](COPYING).
