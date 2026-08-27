# Vega Admin Client

This document records the implementation state of Phase 27 while the existing embedded
Terminal.Gui dashboard is migrated behind a transport-neutral administration boundary.

## Invariant

```text
Terminal.Gui renders on the administrator machine.
Admin Protocol transports typed state, commands and events.
The server owns authorization, secrets and authoritative mutations.
```

The Admin Protocol is not a remote terminal, PTY, ANSI stream or hidden server console.
NativeAOT may make casual inspection less convenient, but it is not an authentication or
transport-security boundary.

## Initial vertical slice

The first Phase 27 slice introduces two dependency-light projects:

```text
Vega.Admin.Contracts
        ^
        |
Vega.Admin.Client
        ^
        |
   IVegaAdminClient
```

`Vega.Admin.Contracts` currently owns protocol-neutral models for:

- protocol version and advertised capabilities;
- server status;
- command catalog descriptors;
- command requests/results;
- structured/localizable command messages.

`IVegaAdminClient` currently exposes:

```text
GetProtocolInfoAsync
GetServerStatusAsync
GetCommandCatalogAsync
ExecuteCommandAsync
```

Protocol version `1.0` advertises only the capabilities actually represented by this slice:

```text
server.status
commands.catalog
commands.execute
```

Capabilities are additive/versioned. A future transport must negotiate them instead of assuming
that client and server binaries came from the same commit.

## Local client path

`LocalVegaAdminClient` maps existing server Operations/command services into Admin DTOs. Its
command path uses the normal Vega command dispatcher and preserves `MessageDescriptor` key,
arguments, severity, fallback text and source instead of flattening the result into one
server-language string.

The existing `Vega.TerminalUI` remains the embedded dashboard during migration. Its command
execution and autocomplete paths now cross `IVegaAdminClient` through compatibility adapters.
The rest of its dashboard still reads `OperationsSnapshot` directly and is intentionally marked
as migration debt rather than pretending the whole UI is already transport-neutral.

Unknown command text is no longer treated by the migrated command path as an implicit raw
Terraria console escape hatch. Remote administration must never gain a second privileged dispatch
path merely because the local dashboard historically had one.

## Not implemented yet

The first slice does **not** claim any of the following:

- remote transport;
- TLS/authentication/session tokens;
- server push events, sequence cursors or reconnect/resync;
- permission-filtered remote command catalogs;
- first-class module ownership/help metadata for every command;
- player/chat/plugin/log/config/debug projections;
- extraction/rename of `Vega.TerminalUI` into the final `Vega.Admin.Tui` executable;
- NativeAOT publish validation.

`AdminCommandDescriptor.Owner` therefore uses `unassigned` where the current command registration
model does not yet expose module ownership. Inventing ownership from permission strings would make
the UI look finished while quietly lying about the source of commands.

## Next slices

1. Promote command owner/help/localization metadata into the command registration model and Admin
   catalog without maintaining a second help database.
2. Move server status and then player/module/log projections off direct `OperationsSnapshot`
   access into Admin Contracts.
3. Evaluate WebSocket, SignalR and a narrow Vega-owned duplex protocol against framing,
   capability negotiation, reconnect/resync, bounded queues and NativeAOT behavior before choosing
   the remote transport.
4. Add authenticated administration sessions with server-owned authorization and explicit
   permissions before exposing any remote mutation endpoint.
5. Extract the transport-neutral Terminal.Gui client as `Vega.Admin.Tui`, then enable its .NET 11
   NativeAOT/self-contained publish baseline and platform matrix.
