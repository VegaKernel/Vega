# Vega

<p align="center">
  <a href="README.md">Русский</a>
  · <strong>English</strong>
  · <a href="docs/en/project-guide.md">Documentation</a>
  · <a href="docs/roadmap.md">Roadmap</a>
  · <a href="docs/decisions/">ADRs</a>
</p>

Vega is an independent Terraria server runtime and framework targeting .NET 10. It hosts Terraria through OTAPI, separates game integration from server behavior, and exposes typed contracts for commands, events, players, packets, modules, operations, and cluster scenarios.

Vega is not a TShock fork and does not provide binary or source compatibility for TShock/TSAPI plugins. Historical implementations are used only as evidence for production behavior, requirements, and regression scenarios.

## Architecture

```mermaid
flowchart TB
    Terraria["Terraria"] --> OTAPI["OTAPI"]
    OTAPI --> TerrariaAdapter["Vega.Terraria<br/>game integration"]

    Runtime["Vega.Runtime<br/>host + composition root"] --> TerrariaAdapter
    Runtime --> Core["Vega.Core<br/>server behavior"]
    Runtime --> Operations["Vega.Operations<br/>logs + metrics + read models"]
    Runtime --> Cluster["Vega.Cluster<br/>optional inter-server scenarios"]
    Runtime -. "explicit registration" .-> Modules["ServerPlugins<br/>Vega.Modules.*"]

    TerrariaAdapter --> Contracts["Vega.Contracts<br/>contracts + snapshots"]
    Core --> Contracts
    Operations --> Contracts
    Cluster --> Contracts
    Modules --> Contracts

    TerminalUI["Vega.TerminalUI"] --> Operations
```

`Vega.Core` has no Terraria or OTAPI dependency. Mutable game state remains behind the `Vega.Terraria` boundary, while other components use typed contracts and immutable snapshots.

## Available today

- native `Vega.Server` hosting without TSAPI or TShockAPI;
- strict `Vega.Contracts` / `Vega.Core` / `Vega.Terraria` / `Vega.Runtime` boundaries;
- typed events, commands, and a `TZ.Multiplicity` packet pipeline;
- immutable player snapshots and game-thread-dispatched operations;
- built-in PvP and Custom Items modules;
- Terminal UI, plain-console, and headless modes;
- structured logs plus runtime, GC, network, TPS, and readiness telemetry;
- optional typed cluster contracts with a reference transport;
- an experimental `.vworld` cache with safe `.wld` fallback.

## Requirements

- .NET 10 SDK for builds;
- NuGet access for the initial restore;
- a compatible Terraria/OTAPI server runtime environment.

## Build and test

```powershell
dotnet restore Vega.sln
dotnet build Vega.sln --configuration Release --no-restore
dotnet test Vega.sln --configuration Release --no-build --no-restore
```

Publish a ready-to-run server directory:

```powershell
dotnet publish src/Vega.Runtime/Vega.Runtime.csproj `
  --configuration Release `
  --output artifacts/server
```

The launcher stays at the publish root, shared dependencies live in `libs/`, and bundled server modules live in `ServerPlugins/`.

Hot replacement for external unload-safe `IHotReloadableVegaModule` plugins in `ServerPlugins/` is available but disabled by default in `config/modules.json`. When enabled, the watcher automatically handles DLL creation, change, deletion, and rename; bundled `IVegaModule` revisions still require restart.

## Run

```powershell
artifacts/server/Vega.Server.exe --vega-ui=plain
```

Supported modes are `auto`, `tui`, `plain`, and `headless`. Enable the world-image cache explicitly with `--vega-world-image=on`; it remains off by default because Vega 1.0 measurements showed no repeatable startup benefit.

## Documentation

- [Complete English project guide](docs/en/project-guide.md)
- [Полное руководство на русском](docs/ru/project-guide.md)
- [Architecture](docs/architecture.md)
- [Operations API and Terminal UI](docs/operations.md)
- [Cluster messaging](docs/cluster.md)
- [World images](docs/world-images.md)
- [Release procedure](docs/releasing.md)
- [Roadmap](docs/roadmap.md)
- [Architecture Decision Records](docs/decisions/)

## Status

The roadmap through Phase 12 establishes the Vega 1.0 foundation; it does not mean that every future-facing ADR requirement is complete. Per-player localization, Dimensions, sandbox backends, and several advanced Core primitives remain future work.

## License

Original Vega code is distributed under the proprietary [Vega Proprietary License](LICENSE). Public access to the repository or a NuGet package does not make Vega open source and grants no use, modification, or redistribution rights beyond an express authorization.

Third-party dependencies retain their respective licenses; see [licenses/README.md](licenses/README.md).
