# Vega architecture

Vega is an independent modern server framework/runtime for Terraria. It is not a compatibility-preserving TShock rewrite.

TShock remains an important source of production behavior, requirements, security lessons and regression scenarios. Its public shapes are not architectural constraints.

## Dependency direction

```text
Terraria -> OTAPI -> Vega.Terraria -> Vega.Contracts
                         ^                  ^
                         |                  |
Vega.Runtime ------------+------------------+
     |                    |                  ^
     +-> Vega.TerminalUI -> Vega.Operations-+
     +-> Vega.Core --------------------------+
     `-> Vega.Modules -----------------------+
```

Project names may evolve, but these boundaries are normative.

### Vega.Terraria

Owns Terraria and OTAPI integration:

- instrumentation/patch entrypoints;
- translation between Terraria objects and Vega contracts;
- game-thread scheduling;
- network/runtime integration that necessarily knows Terraria;
- world loading integration.

Terraria and OTAPI types stop here.

### Vega.Runtime

Owns process-level hosting:

- bootstrap and shutdown;
- composition root;
- module discovery/registration/lifecycle;
- readiness state machine;
- startup telemetry and performance budgets;
- operational hosting concerns that are not Core behavior.

### Vega.Contracts

Contains implementation-independent contracts, identifiers, DTOs, snapshots, events and capability interfaces.

It must not reference Terraria, OTAPI, TSAPI or TShockAPI.

### Vega.Core

Owns server/application behavior independent of the Terraria implementation boundary.

Core must not know about:

```text
Terraria.Main
Terraria.Player
OTAPI
TSAPI
TerrariaApi.Server
ServerApi.Hooks
TShockAPI.TShock
TSPlayer
Commands.ChatCommands
```

World/player state enters Core through explicit contracts, immutable snapshots and scheduled capabilities.

### Vega.Operations

Owns toolkit-independent operational projections, bounded structured logging, cheap sampled metrics and safe action routing. It consumes stable Contracts/application services and never owns authoritative Terraria state.

### Vega.TerminalUI

Owns the optional Terminal.Gui host, rendering and view state. Terminal.Gui types are contained to this project. The UI consumes immutable operations snapshots and routes commands through the normal application boundary; it does not reference Terraria, OTAPI, TSAPI or TShockAPI.

### Vega modules

Modules consume Vega-native contracts and lifecycle APIs. Existing TerraZ plugins are ported deliberately; neither binary nor source-level TShock compatibility is a target requirement.

## TSAPI and TShockAPI

TSAPI and TShockAPI may temporarily remain in the repository while migration keeps the existing executable path alive. They are migration scaffolding, not target layers.

New architecture must not add dependencies on them. Their removal is an explicit implementation milestone after native Vega runtime/module paths cover required behavior.

## OTAPI

OTAPI remains the low-level instrumentation/patching layer directly above Terraria. It is intentionally isolated from Core and Contracts.

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

ADRs before 0016 record the TShockNG migration era. They remain useful history, but ADR 0016 supersedes the long-term assumptions that TShockAPI, TSAPI or legacy plugin surfaces belong in Vega's target architecture.
