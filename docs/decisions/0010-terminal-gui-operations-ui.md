# ADR 0010: Build an isolated Terminal.Gui operations UI

Status: Planned

## Context

TShock is primarily operated through a continuously scrolling console log. That is useful as a transport and diagnostic stream, but it is a poor primary operations interface once the server has many plugins, players, background services, protocol components, and runtime state worth inspecting.

TShockNG already plans to move toward explicit services, prioritized events, game-thread ownership for Terraria state, and a modern API/legacy facade split. An operations UI must sit on top of those abstractions rather than become another privileged subsystem that reads and mutates Terraria globals directly.

Terminal.Gui v2 is the selected TUI toolkit. It provides a cross-platform terminal UI model suitable for Windows, Linux, macOS, local terminals, and SSH sessions.

## Decision

After the .NET 10 baseline and repository-layout normalization, introduce an optional interactive operations UI built on Terminal.Gui v2.

The UI must be architecturally isolated from the server core.

Conceptually:

```text
Terraria / TSAPI / TShock core
            |
            v
  application services
  status/read models
  command/query contracts
            |
            v
   TShock operations API
       /            \
      v              v
Terminal.Gui TUI   plain/headless console
```

The core must not reference Terminal.Gui. The Terminal.Gui project may reference stable TShockNG contracts/read models, but UI types must not leak into core APIs.

## Project boundary

Create a dedicated first-party project after the planned repository relocation, for example:

```text
src/TShock.Operations/
src/TShock.TerminalUI/
```

The exact names may change during implementation, but responsibilities must remain separated:

- `Operations` exposes read models, queries, commands, and event streams needed by administration front ends;
- `TerminalUI` owns Terminal.Gui views, layout, key bindings, navigation, rendering, and UI-specific state;
- the game/server core owns authoritative state and behavior;
- the UI never becomes the source of truth.

This boundary should allow a future web/admin front end to reuse the same operations contracts without depending on Terminal.Gui.

## Runtime modes

Support at least these modes:

- interactive TUI;
- traditional/plain console;
- fully headless/non-interactive operation suitable for services, containers, redirected output, and CI.

Terminal.Gui must therefore be optional at runtime. Failure to initialize an interactive terminal must not prevent the Terraria server from starting; the host must be able to fall back to plain/headless operation.

Do not make server startup, packet processing, saving, shutdown, or plugin loading depend on a functioning TUI.

## Initial UI areas

The first useful UI should provide a compact dashboard rather than reproduce every command as a button.

### Overview

Show at least:

- server name/version;
- Terraria/TShockNG/TSAPI versions;
- uptime;
- current world;
- player count and slot usage;
- basic process/runtime health;
- active warnings/errors;
- last save / save status where available;
- network/listener status where available.

### Players

Provide a live player table with useful operational data such as:

- name;
- account/login state;
- group;
- slot/index;
- ping/connection information where available;
- IP metadata where policy permits;
- selected language after the planned i18n work;
- join duration/state.

Selection should open a detail view rather than cramming every field into one enormous row.

Administrative actions may be added later, but mutations must execute through the normal application command/service boundary, not by directly editing player/game objects from the UI thread.

### Modules and plugins

Provide a module/plugin view showing at least:

- loaded plugin/module name;
- version;
- author/source where available;
- enabled/loaded state;
- initialization/load errors;
- relevant dependencies;
- registration/runtime status where the architecture exposes it.

As TShockNG introduces first-party modules, TSAPI components, Multiplicity, ProtocolGuard, localization, GeoIP, and other services, expose their health through a common status model instead of adding bespoke UI calls into each implementation.

### Logs

Replace the endless log-only experience with a dedicated log pane backed by a bounded in-memory stream/ring buffer.

Provide:

- scrolling without stopping server execution;
- level filtering;
- text filtering/search;
- source/category filtering where supported;
- pause/follow-tail behavior;
- clear visual separation for errors and warnings;
- access to recent historical lines after switching between screens.

Logging must remain independently writable to normal files/stdout sinks. The TUI is an additional sink/view, not the only place logs exist.

### Internal/runtime status

Expose useful diagnostics through read models, for example:

- registered hooks/event handlers and optionally handler counts/priorities;
- loaded services/modules;
- background jobs/tasks;
- database backend and connection health;
- localization catalog status;
- GeoIP/MMDB status once implemented;
- protocol/Multiplicity/ProtocolGuard counters once implemented;
- GC/process memory and thread-pool/runtime metrics that can be obtained cheaply;
- packet/network counters where useful and inexpensive.

Avoid turning the dashboard into a profiler. Expensive diagnostics must be opt-in and sampled appropriately.

## Threading and state ownership

Terminal.Gui has its own application/event loop. It must not read or mutate Terraria game state directly from that UI loop.

Rules:

- authoritative Terraria state remains game-thread-only;
- the UI consumes immutable snapshots/read models produced through safe application services;
- snapshots should be cheap, bounded, and preferably immutable records/value objects;
- UI refresh must not block the game loop;
- mutations requested by the UI must be marshalled through the same command/application boundary used by other administration surfaces;
- no `Main.player[]`, NPC arrays, world collections, or plugin internals are to be traversed directly from the UI thread;
- cancellation and shutdown must be explicit so the UI cannot keep the process alive after server shutdown.

Prefer event-driven invalidation plus a modest refresh cadence over repeatedly polling large server structures at high frequency.

## Logging architecture

Do not intercept console text by globally replacing `Console.Out` as the primary design.

Instead, modernize logging behind explicit sinks/providers and allow both:

```text
log event
  |-- file sink
  |-- stdout/plain-console sink
  `-- bounded TUI log sink
```

The TUI log view should consume structured log records when possible: timestamp, severity, category/source, message, exception metadata.

Legacy console writes may be bridged during migration, but new code should use the logging abstraction.

## Commands and keyboard operation

The TUI should remain efficient over SSH and without a mouse.

Provide keyboard-first navigation, with discoverable shortcuts and optional mouse support. A command palette/action dialog may invoke existing TShock commands through the normal command dispatcher where that is the most compatible path.

Do not duplicate command authorization in the view layer. Administrative actions must still pass through the same authorization/domain rules as equivalent console/API operations.

## Compatibility

- Preserve the traditional console mode for scripts, redirected logs, service managers, Docker, troubleshooting, and administrators who prefer it.
- Existing console commands and plugin logging behavior must continue to work.
- Plugins must not need to reference Terminal.Gui.
- Do not expose Terminal.Gui types in TShockAPI public contracts.
- The TUI may be disabled entirely without changing gameplay behavior.

## Testing

Add automated tests around the operations layer independently of Terminal.Gui rendering:

- status snapshot generation;
- module/plugin status projection;
- player projection;
- bounded log buffering/filtering;
- command dispatch and authorization boundaries;
- safe behavior when the TUI is disabled/unavailable;
- shutdown/cancellation behavior;
- concurrent snapshot production and UI consumption without direct game-state access.

Keep most UI logic testable as presentation/view-model logic without requiring a real terminal. Use Terminal.Gui integration tests only where toolkit behavior itself matters.

## Sequencing

Recommended order:

1. finish and merge the green .NET 10 baseline;
2. normalize the repository layout;
3. establish the common service/operations contracts and structured logging boundary;
4. add read-only server/player/plugin/module status projections;
5. add the optional Terminal.Gui host and dashboard;
6. add the structured log viewer;
7. add module/player/runtime detail views;
8. add safe administrative actions through application commands;
9. integrate later subsystems such as localization, GeoIP, Multiplicity, and ProtocolGuard through their status contracts rather than direct coupling.

The first implementation should favor observability and inspection. Rich mutation/control features come after the read path and threading boundaries are proven safe.

## Consequences

TShockNG gains a usable operations console instead of requiring administrators to infer server state from a wall of log text. More importantly, the UI becomes a consumer of stable application contracts, which improves architecture even when the TUI is disabled and leaves room for other administration front ends later.
