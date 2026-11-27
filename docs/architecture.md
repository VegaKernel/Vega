# Vega architecture

Vega is an independent modern server framework/runtime for Terraria. It is not a compatibility-preserving TShock rewrite.

TShock remains an important source of production behavior, requirements, security lessons and regression scenarios. Its public shapes are not architectural constraints.

## Dependency direction

```text
Terraria -> TerrariaRuntime -> Vega.Terraria -> Vega.Contracts
                         ^                  ^
                         |                  |
Vega.Runtime ------------+------------------+
     |                    |                  ^
     +-> Vega.TerminalUI -> Vega.Operations-+
     +-> Vega.Cluster -----------------------+
     +-> Vega.Sandboxes ---------------------+
     +-> Vega.Backups -----------------------+
     +-> Vega.Core --------------------------+
     +-> Vega.Regions -----------------------+
     `-> Vega.Modules -----------------------+
```

Project names may evolve, but these boundaries are normative.

### Vega.Terraria

Owns Terraria and TerrariaRuntime integration:

- instrumentation/patch entrypoints;
- translation between Terraria objects and Vega contracts;
- game-thread scheduling;
- network/runtime integration that necessarily knows Terraria;
- world loading integration.

Terraria and TerrariaRuntime types stop here.

### Vega.Runtime

Owns process-level hosting:

- bootstrap and shutdown;
- composition root;
- module discovery/registration/lifecycle;
- readiness state machine;
- startup telemetry and performance budgets;
- operational hosting concerns that are not Core behavior.

`Program.cs` is the bootstrap only: it installs the runtime assembly resolver before entering `RuntimeHost.Run`. The explicit composition root has typed stages, in this order: launch/world preparation, process foundation, admission and sandboxes, world services and restartable modules, identity, character persistence and packet policies, administration, operations, plugins, dashboard, and native host execution. Stage outputs are private typed records, not a general service locator. Each successful disposable registration is retained by the startup resource scope and released in reverse construction order on an early return, failure or normal shutdown. Native tile storage still encloses the entire host lifetime. See [runtime decomposition audit](code-complexity-metrics-runtime-2026-11-27.md).

### Vega.Contracts

Contains implementation-independent contracts, identifiers, DTOs, snapshots, events and capability interfaces.

It must not reference Terraria or TerrariaRuntime.

### Vega.PluginSdk

Contains the thin external-plugin entry point and five curated capability facades over Contracts. It must not reference Core, Runtime, Terraria, TerrariaRuntime, UI, packet-worker internals or a universal service locator. Core implements the host side; plugin behavior still uses the same authoritative Contracts services.

### Vega.Core

Owns server/application behavior independent of the Terraria implementation boundary.

Core must not know about:

```text
Terraria.Main
Terraria.Player
TerrariaRuntime
reflection-based compatibility bridges
mutable integration globals
```

World/player state enters Core through explicit contracts, immutable snapshots and scheduled capabilities.

### Vega.Regions

Owns the complete region application slice above persistence: the authorized command surface and hot-path packet ACL decisions. It depends on `Vega.Contracts` and typed Multiplicity packet views, not Core, SQL, Terraria, Operations or UI. `Vega.Persistence` retains relational storage and baseline-schema ownership; `Vega.Terraria` retains chest/tile-entity position adapters; Operations consumes immutable region contracts directly.

### Vega.Operations

Owns toolkit-independent operational projections, bounded structured logging, cheap sampled metrics and safe action routing. It consumes stable Contracts/application services and never owns authoritative Terraria state.

### Vega.TerminalUI

Owns the optional Terminal.Gui host, rendering and view state. Terminal.Gui types are contained to this project. The UI consumes immutable operations snapshots and routes commands through the normal application boundary; it does not reference Terraria or TerrariaRuntime.

### Vega.Cluster

Owns optional cluster dispatch, duplicate detection, observability, reference transport infrastructure and scenario adapters. Typed message contracts live in `Vega.Contracts`; concrete transports do not. A standalone runtime composes an unavailable client and keeps local operation independent from remote availability. See ADR 0019 and [`docs/cluster.md`](cluster.md).

### Vega.Sandboxes

Owns the primary-process catalog, immutable template materialization, lifecycle supervisor, signed backend heartbeat and transfer-ticket routing for isolated worlds. Each active sandbox runs a complete `Vega.Server` backend with its own Terraria state and game thread; only typed commands and sampled immutable status cross the process boundary. A backend never creates another control plane. See [`docs/sandboxes.md`](sandboxes.md).

### Vega.Backups

Owns scheduled/manual deployment archive orchestration, atomic publication, retention and redaction. It depends only on backup/world-save/database snapshot contracts; `Vega.Terraria` implements the world consistency barrier and each `Vega.Persistence` provider owns its database capture. Backup policy does not belong to Core. See [`docs/backups.md`](backups.md).

### Vega modules

Modules consume Vega-native contracts and lifecycle APIs. Existing TerraZ plugins are ported deliberately; neither binary nor source-level TShock compatibility is a target requirement.

`Vega.Modules.Pvp` consumes packet, player-combat and session-state capabilities for ordered PvP policy. Custom item recipes are activation-owned in the Plugin SDK; `Vega.Terraria` performs exact-session native item delivery, including bounded transient world-item stats. Temporary PvP state follows `PlayerSessionId` lifecycle and is disposed on disconnect; it is not attached as a universal bag to player/domain models. See [`docs/extension-state.md`](extension-state.md).

TShockAPI, TSAPI and their hook/command/plugin compatibility surfaces are not present in the executable path, solution or active source tree. The completed migration ledger and superseded compatibility scaffolding have been removed; architecture tests prevent those runtime dependencies from returning.

## TerrariaRuntime

TerrariaRuntime remains the low-level instrumentation/patching layer directly above Terraria. It is intentionally isolated from Core and Contracts.

## World model

World-specific APIs use explicit `IWorldContext`/`WorldId`-style boundaries. Core does not assume one global `Terraria.Main` world.

This preserves the option to change world loading, metadata, indexes, sandbox hosting or runtime integration without redesigning Core.

## Threading

Mutable Terraria state is game-thread-only.

SQL, HTTP, file I/O, serialization and pure computation may run away from the game thread. Results that change Terraria state are scheduled back to the relevant game thread. Large mutations should use time slicing where practical.

Concurrency is used only where work is genuinely independent and safe.

## Startup and readiness

Startup performance is an architectural invariant defined by ADR 0017.

Canonical stages:

```text
ProcessStart
  -> Bootstrap
  -> RuntimeReady
  -> ModulesRegistered
  -> WorldLoading
  -> WorldReady
  -> NetworkReady
  -> BackgroundWarmup
  -> FullyWarmed
```

`NetworkReady` is the user-visible readiness point. `FullyWarmed` must not normally block the server from appearing online.

Terraria world load time is measured separately from Vega startup overhead.

## Module lifecycle

Registration is synchronous and cheap. It declares services, commands, handlers and startup work; it does not perform blocking I/O or warmup.

Startup work declares the readiness barrier it blocks:

- `RuntimeBarrier`;
- `WorldBarrier`;
- `NetworkBarrier`;
- `Background`;
- `Lazy`.

The runtime times each stage/module and warns when configured budgets are exceeded.

## Protocol and validation

`TZ.Multiplicity` remains the typed Terraria wire-protocol dependency where applicable. Protocol parsing and pure validation should remain separable from enforcement and mutable game state.

## Architectural history

ADRs before 0016 record the TShockNG migration era. They remain useful history, but ADR 0016 supersedes those assumptions and phases 9–10 completed their physical removal.
