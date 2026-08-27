# ADR 0025: Admin client boundary and Terminal.Gui v2 workspaces

## Status

Accepted for the initial Phase 27 vertical slice.

## Context

The original embedded `Vega.TerminalUI` renders a useful local Operations dashboard, but it was constructed directly from `IOperationsSnapshotProvider`, `IOperationsCommandService` and `ICommandCatalog`. That coupling cannot be reused by a future remote administration client without either exposing runtime implementation types or turning the protocol into a remote console/terminal stream.

Phase 27 requires one administration surface for local and remote clients, transport-neutral contracts, capability negotiation, client-side rendering and eventual NativeAOT distribution.

Terminal.Gui v2 is a complete rewrite relative to v1. Vega must use APIs verified against the pinned v2 package rather than pre-v2 examples. For Terminal.Gui 2.4.17 the tabbed container is `Tabs`; selection is controlled through `Tabs.Value`, keyboard handling is available through the instance `IApplication.Keyboard`, and normal layout remains `View` plus `Pos`/`Dim`. The plugin configuration editor uses `Terminal.Gui.Editor` 2.5.7 rather than the obsolete `TextView` surface.

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

`IVegaAdminClient` is the observable client boundary. The implemented Phase 27 query/action surface includes:

- protocol/capability information;
- server status;
- player snapshots;
- command catalog and typed command execution;
- module snapshots;
- bounded log queries;
- bounded chat snapshots;
- administration group/account projections;
- bounded diagnostics snapshots;
- explicitly exposed managed plugin configuration list/read/replace/reload operations.

The current capability names are:

```text
server.status
players.snapshot
commands.catalog
commands.execute
modules.snapshot
logs.query
chat.snapshot
administration.snapshot
diagnostics.snapshot
configuration.list
configuration.read
configuration.replace
configuration.reload
```

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

Workspace 1 temporarily hosts the existing local tiled Operations dashboard. Workspaces 2-9 use administration projections/actions rather than directly interpreting Terraria or persistence objects. The selected workspace is the only dedicated projection polled at its configured cadence; unrelated workspace state is not continuously transferred.

Workspace 3 provides master/detail command browsing and search by command name, alias, owner/module, syntax, permission and help/description text.

Workspace 7 edits only managed plugin configurations that are already registered by the owning plugin through `IPluginConfiguration.Use<T>` and are explicitly allowlisted by the server operator. It never accepts arbitrary filesystem paths. The UI uses the Admin configuration DTOs and does not consume the DEBUG configuration API directly.

### Managed configuration administration

The production administration facade reuses the existing plugin-owned managed configuration lifecycle instead of introducing a second configuration format. Therefore plugin defaults, typed JSON deserialization, plugin validation, reload behavior and atomic save remain authoritative.

Exposure is controlled by `config/admin-configuration.json`. The fail-closed default is:

```json
{
  "documents": []
}
```

A deliberate exposure can be configured as:

```json
{
  "documents": [
    {
      "plugin": "Example.Plugin",
      "fileName": "settings.json",
      "allowEdit": true,
      "redactedJsonPointers": [
        "/apiKey",
        "/database/password"
      ],
      "displayName": "Example settings",
      "description": "Operator-safe managed settings"
    }
  ]
}
```

A matching rule does nothing unless that plugin has actually registered the named managed configuration. The policy cannot expose an arbitrary server file.

Each visible configuration has a SHA-256 revision derived from its authoritative JSON. Replace and reload requests carry the revision that the client opened. A concurrent change returns `configuration.revision_conflict` instead of silently overwriting newer state.

`redactedJsonPointers` use JSON Pointer paths. Redacted values are replaced in the client document and the raw values never cross the Admin boundary. On replace, the server restores those values from the current authoritative document before invoking the plugin's typed validator and atomic save. This means saving a redacted document cannot replace a secret with the redaction marker. A pointer to an optional value that is currently absent is harmless; an existing redacted value cannot be removed by deleting its containing property/object from the submitted document.

Malformed or unreadable exposure policy fails closed and exposes no managed configuration documents. Physical configuration paths and DEBUG capability state are never part of the Admin configuration DTOs.

The Terminal.Gui configuration workspace provides an allowlisted document list, `Editor`, Refresh, Reload and Save controls. Save is revision-aware. Reload is blocked while the editor has unsaved changes, and periodic catalog refresh never silently replaces dirty editor contents.

### Keyboard navigation

A terminal does not portably encode `Tab` as a modifier comparable to Ctrl/Alt/Shift. Therefore the requested `Tab+digit` navigation is implemented as a portable two-key chord:

```text
Tab, then 1..9
```

The first `Tab` is not consumed, so ordinary Terminal.Gui focus traversal continues. The digit selects the workspace within a bounded chord window. When the command palette has focus, its Tab autocomplete takes precedence and does not arm the workspace chord.

Clickable native `Tabs` headers remain available in addition to the chord.

### Polling and future events

Snapshot queries are an initial bootstrap/read model, not the final realtime transport. The selected workspace is refreshed at a bounded workspace-specific cadence. Late results from a previously selected workspace are discarded instead of delaying or overwriting the newly selected workspace. Future remote protocol work will add push events, sequence/cursor metadata, reconnect/resync and bounded queues without changing the UI business model.

### Security

No decision in this ADR makes NativeAOT, protocol obscurity or terminal rendering a security boundary. Remote authentication, TLS, session expiry/revocation and permission-filtered projections remain server responsibilities.

Administration configuration cannot expose arbitrary filesystem paths or files. Configuration exposure is an explicit server-side allowlist, redactions are enforced server-side and writes remain subject to the owning plugin's typed validation.

## Consequences

- The existing Dashboard can keep working while Phase 27 migrates its remaining projection separately.
- Dedicated workspaces no longer depend on Terraria/persistence implementation objects.
- Plugin configuration workspace 7 is now a real revisioned Admin surface rather than a placeholder.
- A future remote client can implement `IVegaAdminClient` without Terminal.Gui references.
- The Admin wire model stays AOT/serialization friendly.
- The current embedded bridge remains temporary and must be removed when Dashboard workspace 1 is migrated to Admin Contracts.
- Managed plugin configuration keeps one owner and one validation/save path; Admin is a controlled projection over it rather than a competing configuration subsystem.

## Rejected alternatives

### Remote terminal / PTY / ANSI streaming

Rejected. It couples UI rendering to the server, makes automation and alternative clients brittle and turns authorization into shell semantics.

### One giant continuously polled administration snapshot

Rejected. It transfers and rebuilds unrelated state when only one workspace is visible and becomes especially wasteful over a remote connection.

### Terminal.Gui v1 examples or `TabView` APIs from unrelated versions

Rejected. Vega uses patterns verified against the pinned Terminal.Gui v2 package. API names are not guessed from stale examples.

### DEBUG configuration diagnostics as the remote config API

Rejected. Debug diagnostics are privileged implementation tooling and expose implementation details such as physical paths. The production Admin facade uses the same managed configuration registrations internally but projects only allowlisted logical identity, redacted JSON and revisioned actions.

## Next Phase 27 slices

- migrate Dashboard workspace 1 readiness/status/metrics away from continuous legacy `OperationsSnapshot` capture;
- add server-owned remote authentication, session lifetime/revocation and encrypted transport;
- add cursor/sequence-based log and chat streaming with bounded reconnect/resync;
- introduce permission-filtered projections before remote exposure;
- build the standalone NativeAOT `Vega.Admin.Tui` only after the transport boundary is stable.
