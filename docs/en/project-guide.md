# Vega project guide

[Русская версия](../ru/project-guide.md) · [Repository README](../../README_EN.md) · [Roadmap](../roadmap.md) · [ADRs](../decisions/)

## 1. Project overview

Vega is an independent Terraria server runtime and framework built for .NET 10. The process host uses OTAPI for low-level Terraria integration while Vega owns the server-facing contracts, policies, module lifecycle, observability, and application boundaries.

The project is designed around three rules:

1. Mutable Terraria state stays on the game thread.
2. Core behavior depends on typed contracts and immutable snapshots, not Terraria globals.
3. New abstractions arrive with a working consumer instead of as speculative framework surface.

Vega does not load TShock or TSAPI plugins and does not preserve their API shapes. Historical projects remain useful only as behavioral evidence and regression input.

## 2. Repository map

| Path | Responsibility |
|---|---|
| `src/Vega.Contracts` | Public identifiers, snapshots, events, command, player, world, networking, operations, and cluster contracts |
| `src/Vega.Core` | Server/application behavior independent of Terraria and OTAPI |
| `src/Vega.Terraria` | Terraria/OTAPI hooks, packet translation, player operations, game-thread scheduling, and world integration |
| `src/Vega.Runtime` | Executable host, composition root, module startup, runtime options, and assembly resolution |
| `src/Vega.Operations` | Structured logging, metrics, read models, and safe command actions |
| `src/Vega.TerminalUI` | Optional Terminal.Gui dashboard and command palette |
| `src/Vega.Cluster` | Optional typed inter-server scenarios and the in-memory reference transport |
| `src/Vega.Modules.Pvp` | PvP authority, projectile attribution, death windows, and per-session state |
| `src/Vega.Modules.CustomItems` | Typed custom-item definitions and targeted Terraria delivery |
| `tests/Vega.Architecture.Tests` | Dependency and ownership guardrails |
| `tests/Vega.Runtime.Tests` | Unit and integration tests for runtime behavior |
| `benchmarks/Vega.WorldImage.Benchmarks` | Reproducible `.wld` versus `.vworld` measurements |
| `docs/decisions` | Architecture Decision Records and their status |

## 3. Runtime architecture

```text
Terraria
   ↓
OTAPI
   ↓
Vega.Runtime ───────→ Vega.TerminalUI
   │                       ↓
   ├──→ Vega.Terraria → Vega.Operations
   ├──→ Vega.Core          ↓
   ├──→ Vega.Cluster   Vega.Contracts
   └──→ Server modules ────↑
```

`Vega.Contracts` is the shared semantic boundary. `Vega.Core` cannot reference Terraria, OTAPI, TSAPI, or TShockAPI. `Vega.Terraria` owns all conversions between mutable game objects and Vega snapshots or operations. Architecture tests fail when forbidden dependencies cross that boundary.

The default runtime composes modules explicitly; it does not scan arbitrary assemblies. `ServerPlugins/` is the distribution location for bundled server modules, not yet a promise of dynamic hot-loading. Adding a DLL to that directory alone does not register it.

## 4. Prerequisites

- .NET 10 SDK;
- network access to configured NuGet feeds for the first restore;
- Git when building from a clone;
- a platform supported by the selected Terraria/OTAPI runtime packages.

The repository pins its important package versions in project files. Do not add the old MonoMod development feed or restore removed TSAPI submodules.

## 5. Build and test

Run from the repository root:

```powershell
dotnet restore Vega.sln
dotnet build Vega.sln --configuration Release --no-restore
dotnet test Vega.sln --configuration Release --no-build --no-restore
```

Build the world-image benchmark harness separately:

```powershell
dotnet build benchmarks/Vega.WorldImage.Benchmarks/Vega.WorldImage.Benchmarks.csproj `
  --configuration Release `
  --no-restore
```

MonoMod emits an expected warning because Vega explicitly opts into the target runtime. The build must still complete with zero errors.

## 6. Publish and distribution layout

```powershell
dotnet publish src/Vega.Runtime/Vega.Runtime.csproj `
  --configuration Release `
  --output artifacts/server
```

The resulting directory is arranged as follows:

```text
artifacts/server/
├── Vega.Server.exe
├── Vega.Server.dll
├── Vega.Server.deps.json
├── Vega.Server.runtimeconfig.json
├── libs/                  shared Vega and third-party runtime libraries
├── ServerPlugins/         bundled Vega.Modules.* assemblies
├── runtimes/              platform-specific package assets when present
└── <culture>/             satellite resources when present
```

The assembly resolver checks `libs/` first and then `ServerPlugins/`. The old `bin/` and `Plugins/` distribution names are not used.

## 7. Starting the server

```powershell
artifacts/server/Vega.Server.exe --vega-ui=plain
```

Vega-specific options are removed before the remaining arguments are passed to Terraria.

### Operations modes

| Option | Behavior |
|---|---|
| `--vega-ui=auto` | Use the TUI for a compatible interactive terminal, otherwise plain mode |
| `--vega-ui=tui` | Request the Terminal.Gui dashboard |
| `--vega-ui=plain` | Traditional console input and output |
| `--vega-ui=headless` | No interactive UI or operations stdout sink |

TUI initialization is best-effort and never blocks server readiness. Vega leaves the native Terraria console in control during interactive world selection, then starts Terminal.Gui at `WorldLoading` with a startup progress screen and switches to the operations dashboard at `NetworkReady`. In TUI mode the command palette exclusively owns terminal input; unknown Vega commands are queued to Terraria's native parser so commands such as `save` and `exit` remain usable. Routine logs stay in the dashboard/file sink instead of painting over the terminal. Structured file logs are written to `logs/vega-operations.jsonl` when the path is available.

The dashboard is a high-contrast desktop of movable and resizable server, performance, memory, network, player, module and log windows. TPS/CPU and memory histories are rendered as bounded graphs. Visibility and percentage-based geometry are configured in `config/tui.json`; use `:windows`, `:show <name>`, `:hide <name>`, `:layout save` and `:layout reset` in the palette. See [the operations guide](../operations.md) for the complete window and filtering reference.

### World-image mode

`--vega-world-image=on` enables the experimental derived `.vworld` cache; `off` disables it. The default is off. `.wld` remains canonical, and stale, incompatible, or corrupt images automatically fall back to it.

## 8. Lifecycle and readiness

The lifecycle progresses through:

```text
ProcessStart → Bootstrap → RuntimeReady → ModulesRegistered
→ WorldLoading → WorldReady → NetworkReady
→ BackgroundWarmup → FullyWarmed
```

`NetworkReady` is the operational readiness point. Terraria world loading is measured separately from Vega framework overhead. Lifecycle transitions, module state, TPS, network counters, memory, GC, and world-image measurements are available through operations snapshots and structured logs.

## 9. Modules and extension boundaries

Bundled modules implement `IVegaModule` and are created by the runtime composition root.

- `Vega.Modules.Pvp` owns PvP-specific policy and temporary session state.
- `Vega.Modules.CustomItems` owns custom-item definitions while `Vega.Terraria` performs packet delivery.

Modules should depend on the narrowest capability they need. They must not mutate `Terraria.Main`, retain mutable player objects, introduce a competing packet codec, or hide business behavior in global helpers. Temporary extension state is scoped to a concrete player session and removed deterministically on disconnect.

## 10. Operations and cluster behavior

`Vega.Operations` exposes immutable server, player, module, log, and metric projections. The Terminal UI is only a consumer; administrative actions still use the normal command and authorization boundaries.

Cluster support is optional. The current transport is an in-memory reference implementation for deterministic tests and local composition. A standalone server uses `UnavailableClusterClient` and remains functional. No production Redis, NATS, RabbitMQ, HTTP, or WebSocket transport is selected by the current ADR.

## 11. Packages and releases

`Directory.Build.props` is the version source. Library projects pack as `VegaKernel.*`; `Vega.Runtime` is distributed as the server archive instead of a NuGet library.

The automatic `Release Tag` workflow runs when a `vMAJOR.MINOR.PATCH` tag is pushed. It validates the tag against `VersionPrefix`, restores, builds and tests the solution, packs the public libraries and symbol packages, pushes them to NuGet.org, and creates or refreshes the GitHub Release with the deployable server ZIP and checksums. The repository must contain the `NUGET_API_KEY` Actions secret before a release tag is pushed.

The separate `Publish Manual` workflow selects the newest valid tag and only rebuilds its deployable server archive and GitHub Release assets. It deliberately skips tests and NuGet publication and does not require the NuGet secret. See [the release procedure](../releasing.md).

## 12. Current limitations

- per-player language selection and translation catalog persistence are not implemented;
- Dimensions packet forwarding is not implemented;
- sandbox backends and multi-world process management are not implemented;
- `ServerPlugins/` does not provide arbitrary dynamic discovery or hot reload;
- cluster scenarios have no selected production transport;
- `.vworld` is experimental and disabled by default;
- several advanced inventory, chest, overlay, role-lease, and world-reset primitives remain roadmap work.

The [roadmap](../roadmap.md) records delivered phases. The [ADRs](../decisions/) are normative design records, but accepted future-facing requirements are not automatically claims of completed implementation.

## 13. Documentation and change discipline

When a change moves ownership, alters threading, changes readiness semantics, adds a public contract, or changes package/runtime layout:

1. update the matching code and focused tests;
2. update the English and Russian project guides;
3. update the subsystem document or ADR;
4. update the roadmap only when the implementation and validation are complete.

Original Vega code is proprietary. Review [LICENSE](../../LICENSE) before using or distributing source, binaries, containers, or NuGet packages. Third-party materials keep their own terms.
