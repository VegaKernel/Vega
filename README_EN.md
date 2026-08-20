# Vega

<p align="center">
  <a href="README.md">Русский</a>
  &nbsp;·&nbsp;
  <a href="README_EN.md"><strong>English</strong></a>
  &nbsp;·&nbsp;
  <a href="docs/architecture.md">Architecture</a>
  &nbsp;·&nbsp;
  <a href="docs/roadmap.md">Roadmap</a>
  &nbsp;·&nbsp;
  <a href="docs/decisions/">ADRs</a>
</p>

Vega is an independent modern server framework/runtime for Terraria. Historical TShock code is retained as production evidence and migration material, not as Vega's architectural foundation.

## Architecture

```mermaid
flowchart TB
    Terraria["Terraria / OTAPI"] --> TerrariaAdapter["Vega.Terraria<br/>Terraria integration boundary"]

    Runtime["Vega.Runtime<br/>process host + composition root"] --> TerrariaAdapter
    Runtime --> Core["Vega.Core<br/>server behavior"]
    Runtime --> Contracts["Vega.Contracts<br/>contracts + DTOs"]

    TerrariaAdapter --> Contracts
    Core --> Contracts

    Modules["Vega modules"] --> Contracts
    Runtime -. "explicit registration" .-> Modules

    Legacy["TShockAPI + TSAPI<br/>temporary migration scaffolding"] -. "migration only" .-> TerrariaAdapter
```

- `Vega.Contracts` contains implementation-independent contracts and DTOs.
- `Vega.Core` owns server behavior and must not reference Terraria, OTAPI, TSAPI or TShockAPI.
- `Vega.Terraria` is the target home for Terraria/OTAPI-specific integration.
- `Vega.Runtime` is the process host and composition root.
- `TShockAPI` and `external/TSAPI` remain temporary migration scaffolding only and are not part of the target architecture.

The Core dependency boundary is enforced by architecture tests.

## Build

```bash
dotnet restore Vega.sln
dotnet build Vega.sln
dotnet test Vega.sln
```

The main executable project is `src/Vega.Runtime/Vega.Runtime.csproj`, producing `Vega.Server`.

## Server readiness

`NetworkReady` is the primary user-facing readiness point. Optional warmup work runs only after the server is open. Terraria world loading is measured separately from Vega startup overhead.

See [`ADR 0017`](docs/decisions/0017-startup-lifecycle-readiness-and-performance-budgets.md).

## Documentation

- [`docs/architecture.md`](docs/architecture.md)
- [`docs/roadmap.md`](docs/roadmap.md)
- [`ADR 0016`](docs/decisions/0016-vega-independent-runtime-architecture.md)
- [`ADR 0017`](docs/decisions/0017-startup-lifecycle-readiness-and-performance-budgets.md)

## License

Original Vega code is proprietary and governed by [`LICENSE`](LICENSE). All rights not expressly granted by the project owner are reserved.

This repository still contains legacy/migration TShock code and third-party components governed by their own licenses. Those licenses are not overridden by Vega's proprietary license. A copy of GPL-3.0 applicable to inherited TShock files is kept at [`licenses/TShock-GPL-3.0.txt`](licenses/TShock-GPL-3.0.txt).
