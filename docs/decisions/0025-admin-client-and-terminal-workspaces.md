# ADR 0025: Admin client boundary and Terminal.Gui v2 workspaces

## Status

Accepted for the initial Phase 27 vertical slice.

## Context

The original embedded `Vega.TerminalUI` renders a useful local Operations dashboard, but it was constructed directly from `IOperationsSnapshotProvider`, `IOperationsCommandService` and `ICommandCatalog`. That coupling cannot be reused by a future remote administration client without either exposing runtime implementation types or turning the protocol into a remote console/terminal stream.

Phase 27 requires one administration surface for local and remote clients, transport-neutral contracts, capability negotiation, client-side rendering and eventual NativeAOT distribution.

Terminal.Gui v2 is a complete rewrite relative to v1. Vega must use APIs verified against the pinned v2 package rather than pre-v2 examples. For Terminal.Gui 2.4.17 the tabbed container is `Tabs`; selection is controlled through `Tabs.Value`, keyboard handling is available through the instance `IApplication.Keyboard`, and normal layout remains `View` plus `Pos`/`Dim`.

## Decision

### Administration boundary

Introduce:

```text
Vega.Admin.Contracts
Vega.Admin.Client
        │
 IVegaAdminClient
        │
 ┌──────┴──────────┐
 local adapter   future remote adapter
```

`Vega.Admin.Contracts` contains only transport-safe DTOs. It must not reference Terraria, Terminal.Gui, persistence providers or runtime service implementations.

`IVegaAdminClient` is the observable client boundary. The initial implemented query surface includes:

- protocol/capability information;
- server status;
- player snapshots;
- command catalog and typed command execution;
- module snapshots;
- bounded log queries;
- bounded chat snapshots;
- administration group/account projections;
- bounded diagnostics snapshots.

A client request to execute a command carries command name, arguments and silent mode as typed fields. It does not carry a magic remote shell line on the wire. Local text entry is parsed before crossing the client boundary.

Localized command results preserve localization key, source, fallback text, severity and protocol-safe scalar formatting arguments. Arbitrary CLR `object` values are not protocol contracts.

### Command metadata

Command ownership/help metadata remains registered with the command descriptor. The catalog exposes owner/module identity, aliases, syntax, permissions, server/player availability and optional localization/help keys plus fallback text. The TUI does not maintain a second command-help database.

### Terminal workspaces

The embedded Terminal.Gui v2 shell exposes nine workspaces:

1. Dashboard
2. Players
3. Commands / Help
4. Modules / Plugins
5. Logs
6. Chat
7. Plugin Configuration
8. Debug / Metrics
9. Administration

Workspace 1 temporarily hosts the existing local tiled Operations dashboard. Workspaces 2-6 and 8-9 query their own projection through `IVegaAdminClient` only when selected. This avoids polling the entire administration state continuously and establishes the same behavior required by a future remote client.

Workspace 3 provides master/detail command browsing and search by command name, alias, owner/module, syntax, permission and help/description text.

Workspace 7 intentionally remains capability-gated until a production administrable-configuration registry exists. The current DEBUG configuration diagnostics are not the Admin API: they expose implementation/debug concepts including physical paths and are not an acceptable remote filesystem contract.

### Keyboard navigation

A terminal does not portably encode `Tab` as a modifier comparable to Ctrl/Alt/Shift. Therefore the requested `Tab+digit` navigation is implemented as a portable two-key chord:

```text
Tab, then 1..9
```

The first `Tab` is not consumed, so ordinary Terminal.Gui focus traversal continues. The digit selects the workspace within a bounded chord window. When the command palette has focus, its Tab autocomplete takes precedence and does not arm the workspace chord.

Clickable native `Tabs` headers remain available in addition to the chord.

### Polling and future events

Snapshot queries are an initial bootstrap/read model, not the final realtime transport. The selected workspace is refreshed at a bounded workspace-specific cadence. Future remote protocol work will add push events, sequence/cursor metadata, reconnect/resync and bounded queues without changing the UI business model.

### Security

No decision in this ADR makes NativeAOT, protocol obscurity or terminal rendering a security boundary. Remote authentication, TLS, session expiry/revocation and permission-filtered projections remain server responsibilities.

Administration configuration must not expose arbitrary filesystem paths or files. Sensitive projections require narrower permissions before remote transport is enabled.

## Consequences

- The existing dashboard can keep working while Phase 27 migrates one projection at a time.
- New dedicated workspaces no longer depend on `OperationsSnapshot` contents.
- A future remote client can implement `IVegaAdminClient` without Terminal.Gui references.
- The Admin wire model stays AOT/serialization friendly.
- The current embedded bridge remains temporary and must be removed when Dashboard workspace 1 is migrated to Admin Contracts.
- Plugin configuration editing requires a new production-safe registration/schema/revision layer rather than reuse of DEBUG-only configuration diagnostics.

## Rejected alternatives

### Remote terminal / PTY / ANSI streaming

Rejected. It couples UI rendering to the server, makes automation and alternative clients brittle and turns authorization into shell semantics.

### One giant continuously polled administration snapshot

Rejected. It transfers and rebuilds unrelated state when only one workspace is visible and becomes especially wasteful over a remote connection.

### Terminal.Gui v1 examples or `TabView` APIs from unrelated versions

Rejected. Vega uses patterns verified against the pinned Terminal.Gui v2 package. API names are not guessed from stale examples.

### DEBUG configuration diagnostics as the remote config API

Rejected. Debug diagnostics are privileged implementation tooling, not a stable administrable configuration contract.
