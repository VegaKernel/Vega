# Migration from TShock to TShockNG

## Rule: migrate by seam, not by rewrite

TShockNG is not a clean-room rewrite. Each subsystem should gain a new internal seam, migrate behavior behind that seam, and retain compatibility adapters until the new path is proven.

## Packet processing

1. Keep current TSAPI hook semantics.
2. Expose raw buffer + offset + exact payload length internally.
3. Parse with Multiplicity.
4. Migrate one handler group at a time.
5. Raise legacy events from the new path.
6. Remove duplicated parsing only after compatibility tests pass.

## Security

1. Identify pure checks inside `Bouncer`/handlers.
2. Define the minimum immutable context each check needs.
3. Move the check to ProtocolGuard.
4. Return a validation result instead of performing side effects.
5. Keep kick/correction/logging decisions in TShockNG.

## REST

REST extraction happens after packet/security foundations are stable.

The old public REST facade may remain in `TShockAPI` while the HTTP implementation moves to a modern host. Compatibility types are adapters, not a reason to keep `HttpServer.dll` forever.

## Database code

Generic provider/query infrastructure may later move out of the core. Domain managers such as bans, users, groups, regions, and SSC remain TShockNG domain code unless a stronger boundary emerges.
