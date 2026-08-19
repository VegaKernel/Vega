# ADR 0005 ownership map

This file records the intended ownership after the architecture-only split. It is deliberately about **where code belongs**, not whether the repository already compiles after the move.

## TShockNG.Contracts

Implementation-independent boundaries only:

- command invocation/dispatch contracts;
- accounts, groups, permissions and bans contracts;
- player/server/world snapshots and service contracts;
- region and persistence-facing abstractions;
- networking decisions and metadata contracts;
- localization/culture contracts;
- operational/update contracts.

Contracts must not depend on TShockAPI, TSAPI, Terraria mutable types, database drivers or concrete host implementations.

## TShockNG.Core

Source of truth for server behavior:

- `Accounts/`
- `Authorization/`
- `Bans/`
- `Commands/`
- `Localization/`
- `Networking/`
- `Persistence/`
- `Players/`
- `Regions/`
- `Restrictions/`
- `Security/`
- `Updates/`
- `Worlds/`

During the structural pass some moved legacy files may still carry historical namespaces or dependencies. Those are migration debt to resolve in the compile/behavior pass, not a reason to keep ownership in TShockAPI.

## TShockAPI

Integration and source-level plugin boundary:

- TSAPI/Terraria plugin lifecycle;
- hook wiring and hook argument models;
- source-level legacy command registration;
- transitional player-facing plugin models;
- integration adapters;
- configuration entry integration;
- modules that wire TShock into TSAPI;
- REST, which is explicitly out of scope for this pass.

## TShockLauncher

Process composition and host integration:

- composition root;
- CLI/process startup;
- socket/host selection;
- construction of concrete Core services;
- passing Contracts-facing services into TShock integration.

## Deliberately deferred

The architecture PR does not attempt to make the moved code compile or preserve behavior. Follow-up work will:

1. correct namespaces and dependencies;
2. split monolithic legacy files, especially commands and packet handlers;
3. replace direct TShock/Terraria globals with contracts/snapshots/scheduled operations;
4. add boundary tests;
5. restore a green build and tests;
6. migrate REST separately.
