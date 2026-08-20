# ADR 0010: Isolated operations API and Terminal UI

Status: Accepted

Implemented by the Phase 6 `Vega.Operations` and `Vega.TerminalUI` projects. The initial implementation intentionally exposes only data already available through stable snapshots and actions already owned by the normal command boundary; later modules may enrich the same contracts without giving the UI direct runtime ownership.

## Context

A continuously scrolling console log is useful as a transport and diagnostic stream, but it is a poor primary operations interface once Vega has players, modules, background work, cluster state, protocol counters, runtime telemetry and administrative actions worth inspecting.

The operations surface must improve observability without becoming a privileged subsystem that reads or mutates Terraria globals directly. It must reuse the same player snapshots, application commands, authorization rules, structured logging and runtime contracts used by other administration surfaces.

Terminal.Gui v2 remains the preferred first interactive TUI toolkit, but the architecture is not defined around that library. The reusable boundary is the Vega operations/read-model API.

## Decision

Introduce an optional operations layer and Terminal UI after the player/packet/command boundaries are sufficiently stable.

Conceptually:

```text
Terraria / OTAPI
       |
       v
 Vega.Terraria
       |
       v
 Core services / runtime services
       |
       v
 operations read models + safe actions
       |             |              |
       v             v              v
 Terminal UI    plain console    headless/API consumers
```

The Core must not reference Terminal.Gui or any terminal/UI toolkit. UI types must not appear in Core or general application contracts.

## Operations/read-model contracts

The operations layer exposes bounded, stable projections instead of raw server objects.

Initial read models should cover:

- server identity, version, uptime and lifecycle stage;
- world/readiness state;
- players and connection/session state;
- loaded modules and their health/startup status;
- recent structured log records;
- runtime/process health;
- network counters where inexpensive;
- GC, managed memory and process memory metrics;
- startup/world-load telemetry;
- active warnings/degraded capabilities.

Read models should be immutable records/value objects or otherwise read-only projections. They must not leak `Terraria.Player`, `Main`, TSAPI/TShock types, sockets, database connections or mutable module internals.

The same operations contracts should be reusable by a future web/admin interface without depending on Terminal.Gui.

## Structured logging

Do not build the TUI by globally replacing `Console.Out` as the primary architecture.

New logging should flow through structured records and independent sinks:

```text
structured log event
    |-- file sink
    |-- stdout/plain-console sink
    `-- bounded operations/TUI sink
```

A log record should carry, when available:

- timestamp;
- severity;
- category/source;
- message;
- exception metadata;
- relevant correlation/context identifiers.

Legacy console writes may be bridged during migration, but the TUI is never the only log destination.

## Server dashboard

The first dashboard should show useful operational state without becoming a profiler:

- server/version/build identity;
- uptime;
- lifecycle/readiness stage;
- current world;
- player count and slots;
- world-load/startup timing;
- listener/network state;
- process memory and GC summary;
- current warnings/degraded capabilities;
- latest save/cache status when available.

Expensive diagnostics remain opt-in and sampled appropriately.

## Players view

The players view consumes `PlayerSnapshot`/player read models, not Terraria objects.

Useful fields include:

- stable player/session identity;
- name;
- account/login state where available;
- group/authorization summary where appropriate;
- slot/index;
- connection/join duration;
- ping/network metadata where available;
- language/culture where available.

Selecting a player may expose safe administrative actions, but all mutations must flow through `IPlayerOperations`, command/application services or equivalent authorized contracts. The UI thread must never mutate Terraria state directly.

## Modules view

Expose module/runtime state through a common projection:

- module name and version;
- enabled/loaded state;
- dependencies/capabilities where exposed;
- registration/startup duration;
- current lifecycle/startup state;
- load/startup errors;
- degraded capability information.

Do not add bespoke direct UI calls into each module implementation.

## Logs viewer

Provide a bounded in-memory recent-log view with:

- scrolling independent of server execution;
- follow-tail/pause behavior;
- severity filtering;
- category/source filtering;
- text search/filtering;
- clear warning/error presentation;
- bounded retention so the UI cannot become an accidental memory leak.

## Runtime, network and GC metrics

Expose cheap sampled metrics through read models, including where practical:

- managed heap size;
- collection counts;
- process working set;
- thread-pool/runtime health;
- packet/network counters;
- pending game-thread work;
- startup/world timing;
- cluster transport health when configured;
- world-image cache hit/miss/load/build timing once implemented.

The dashboard must not continuously perform expensive reflection, heap inspection or full-world scans.

## Command palette and safe admin actions

Provide keyboard-first command/action access over normal application boundaries.

The command palette may invoke existing Vega commands or explicit operations commands. It must not duplicate authorization logic in the view layer.

Safe actions may include, when the corresponding application capability exists:

- kick/disconnect player;
- teleport or other authorized player operations;
- save world;
- reload/reconfigure a module where explicitly supported;
- graceful shutdown/restart-oriented actions;
- administrative commands.

Every action must use the same domain/security checks as equivalent non-UI operations.

## Runtime modes

Vega must support all of the following independently:

1. **Interactive Terminal UI**
   - optional Terminal.Gui host;
   - keyboard-first navigation;
   - mouse support may be added but is not required for operation.

2. **Plain console mode**
   - traditional stdout/stderr logging;
   - command input suitable for local terminals and SSH;
   - compatible with redirected logs and operational scripts.

3. **Headless mode**
   - no interactive terminal requirement;
   - suitable for services, containers, CI and orchestrated deployments.

Failure to initialize an interactive terminal must not prevent the server from starting. TUI availability is not a readiness barrier.

## Threading and ownership

Terminal/UI code has its own event loop and must never read or mutate authoritative Terraria state directly.

Rules:

- Terraria state remains game-thread-only;
- UI consumes immutable/bounded snapshots;
- snapshot production must avoid blocking the game loop;
- state mutations are marshalled through the normal player/world/application operation boundary;
- no `Main.player[]`, NPC arrays, world collections or mutable Terraria objects are traversed from the UI thread;
- cancellation/shutdown is explicit so UI tasks cannot keep the server process alive;
- prefer event-driven invalidation plus a modest refresh cadence over high-frequency polling.

## Project boundary

Target structure may evolve, but responsibilities should resemble:

```text
src/Vega.Operations/
    ReadModels/
    Queries/
    Actions/
    Logging/

src/Vega.TerminalUI/
    Views/
    ViewModels/
    Navigation/
    Rendering/
```

`Vega.Operations` may depend on stable Vega contracts/application services. `Vega.TerminalUI` depends on operations contracts and the chosen TUI toolkit. Core must not depend on either UI project.

## Sequencing

Recommended order:

1. stabilize lifecycle, event and player read-model boundaries;
2. establish structured logging and operations contracts;
3. implement read-only server/player/module projections;
4. add plain/headless operations hosting;
5. add the optional Terminal.Gui dashboard;
6. add the bounded logs viewer;
7. add runtime/network/GC metrics;
8. add command palette and safe administrative actions through existing application/security boundaries.

The first implementation should favor inspection and observability. Mutation/control features come after the read path and threading rules are proven.

## Testing

Test the operations layer independently of terminal rendering:

- server/player/module projections;
- bounded log retention/filtering;
- authorization and command/action routing;
- safe operation with TUI disabled/unavailable;
- plain/headless mode startup;
- shutdown/cancellation;
- concurrent snapshot production/consumption without direct Terraria access.

Terminal.Gui integration tests should be limited to behavior that actually depends on the toolkit.

## Consequences

Vega gains a usable operational dashboard and a reusable administration read-model surface without making the UI part of server correctness. The server remains fully functional in plain-console and headless environments, while future administration front ends can reuse the same contracts.
