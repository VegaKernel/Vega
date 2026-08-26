# ADR 0011: Built-in Dimensions protocol compatibility

Status: Accepted and implemented

Vega decodes packet 67 through a bounded Multiplicity `PacketView`/`PacketReader`,
accepts type 0 only from exact IPv4 addresses in `config/security.json` when explicitly enabled,
and preserves transport and effective endpoints with trusted-forwarder provenance. The
Phase 17 routing service adds target-bound, expiring single-use HMAC tickets and sends
Dimensions type 3 switches only to connections carrying that trusted provenance.

## Context

Some TShock deployments sit behind the Dimensions Terraria proxy and currently require the separate `Dimensions` TShock plugin only to consume Dimensions-specific connection metadata.

The reference implementation is:

- `popstarfreas/Dimensions-TerrariaServer/Dimensions`;
- it registers a `NetGetData` hook;
- it consumes Terraria `PacketTypes.Placeholder`;
- in the current TSAPI packet enum `PacketTypes.Placeholder` is packet ID `67`;
- the packet payload is a signed 16-bit `joinType` followed by a Terraria binary string;
- `joinType = 0` carries the client's real remote IP address;
- `joinType = 1` was reserved/consumed by the historical GameModes integration.

The historical plugin handles `joinType = 0` by reflecting into the private `TSPlayer.CacheIP` field and then re-running part of GeoIP/proxy policy because the normal TShock checks have already happened by the time the plugin changes the address.

That is useful compatibility behavior, but it is the wrong architectural boundary for Vega and is unsafe if packet 67 is blindly trusted from arbitrary public clients.

Terraria's server transport is treated by Vega as IPv4-only. Vega therefore does not carry an independent IPv6 identity path for bans, forwarding, sandbox admission, GeoIP or server access policy. IPv6 and IPv4-mapped IPv6 values are rejected instead of being normalized into a second address family.

## Decision

Implement first-party, optional Dimensions protocol compatibility in Vega so a dedicated Dimensions plugin is not required for this protocol shim.

The feature is disabled by default and controlled by the Vega security configuration.

Initial configuration shape:

```json
{
  "EnableDimensionsPacket": false,
  "DimensionsTrustedProxies": [
    "127.0.0.1"
  ]
}
```

The semantics are explicit:

- `EnableDimensionsPacket` enables handling of the Dimensions protocol carried in packet 67;
- `DimensionsTrustedProxies` contains exact IPv4 addresses of immediate TCP peers allowed to supply forwarded client metadata;
- IPv6 trusted peers and forwarded IPv6 addresses are invalid configuration/protocol input.

Do not interpret packet 67 as trusted forwarding metadata merely because the feature is enabled.

## Protocol

The compatibility decoder supports the historical Dimensions payload:

```text
Terraria packet id: 67 (PacketTypes.Placeholder)

payload:
  int16  joinType
  string joinInfo
```

Known `joinType` values:

```text
0 = forwarded/real client IPv4 address
1 = reserved historical GameModes message
```

### Type 0: forwarded client IPv4

For `joinType = 0`:

1. verify that Dimensions compatibility is enabled;
2. verify that the immediate IPv4 socket peer matches `DimensionsTrustedProxies`;
3. parse `joinInfo` as an IPv4 `IPAddress`;
4. reject malformed, empty, IPv6, IPv4-mapped IPv6, unspecified, broadcast, or otherwise unusable addresses;
5. store the forwarded IPv4 address through the connection metadata/admission abstraction;
6. make that effective client address available to Vega policy and observability code without reflection;
7. record both the immediate proxy endpoint and effective client endpoint for diagnostics.

Do not mutate private fields with reflection.

### Type 1 and unknown types

Do not guess the semantics of historical `joinType = 1`.

Initially:

- preserve the decoder/dispatch shape so another first-party module can consume it later;
- ignore or report unsupported Dimensions message types according to normal protocol logging policy;
- do not treat unknown `joinType` values as valid forwarded addresses.

## Connection admission ordering

The effective remote IPv4 address must be established before policies that depend on client identity are finalized.

This includes, where applicable:

- IP bans;
- server lock / whitelist admission;
- GeoIP lookup;
- proxy/VPN policy;
- connection logging;
- operations diagnostics related to the player connection;
- plugins or first-party services consuming the canonical client endpoint.

The historical plugin had to redo GeoIP/proxy checks because it changed `TSPlayer.CacheIP` after those checks. Vega instead models the connection stages explicitly so forwarded metadata can be applied at the correct point.

Conceptually:

```text
socket accept
    |
    v
immediate IPv4 peer endpoint
    |
    v
optional trusted Dimensions metadata (packet 67/type 0)
    |
    v
canonical/effective IPv4 client endpoint
    |
    +--> admission / whitelist
    +--> ban policy
    +--> GeoIP
    +--> proxy policy
    +--> player/session projection
    `--> logs / operations UI
```

## Security model

Forwarded IP data is an assertion made by a proxy, not by the Terraria client.

Therefore:

- packet 67/type 0 is authoritative only when received over a connection whose immediate IPv4 peer is trusted;
- public clients cannot spoof their IP by sending the same packet directly;
- retain the original immediate peer address even after a forwarded address is accepted;
- IPv6 is rejected rather than accepted into a partially supported identity path;
- malformed Dimensions packets fail closed for metadata handling without crashing the game/network thread;
- the compatibility parser is bounded and uses the same typed packet validation path as other networking code;
- rejected forwarding attempts should be logged at an appropriate rate-limited diagnostic level.

This design is analogous to trusted reverse-proxy forwarding on HTTP servers: forwarded identity is accepted only from configured trusted hops.

## Architecture

Dimensions support belongs in the networking/connection-admission boundary, not in the Terminal.Gui UI, command system, or a reflection helper.

```text
DimensionsPacketDecoder
    -> DimensionsMessage
       -> ForwardedClientAddress

ConnectionMetadata
    - TransportRemoteEndPoint
    - EffectiveRemoteEndPoint
    - ForwardingSource

ConnectionAdmissionService
    -> validates trusted IPv4 proxy
    -> applies forwarded IPv4 identity
    -> runs connection policies
```

The important properties are:

- typed packet parsing;
- immutable connection metadata snapshots where practical;
- explicit trust policy;
- one address family for Terraria connection identity;
- no reflection;
- no direct dependency from core connection logic on a plugin assembly.

## Player compatibility

Existing APIs that expose a player's IP see the effective client IPv4 address when trusted Dimensions forwarding is active.

Internally preserve both addresses:

- transport/proxy IPv4 address;
- effective/forwarded client IPv4 address.

New APIs make this distinction explicit rather than overloading one mutable string field.

## Operations UI

The operations UI may expose connection provenance in the player detail view:

```text
Client IP:       203.0.113.10
Connected via:   127.0.0.1:7778 (Dimensions)
Forwarding:      trusted
```

The UI consumes the connection metadata read model and never parses forwarding packets itself.

## Testing

Tests cover at least:

- packet ID 67 recognized only when compatibility is enabled;
- type 0 parses a valid IPv4 address;
- IPv6 trusted-peer configuration is rejected;
- a trusted peer cannot forward an IPv6 address;
- malformed IP is rejected;
- unknown `joinType` is not treated as an IP update;
- packet from an untrusted peer cannot replace the effective address;
- trusted peer can supply the effective IPv4 address;
- transport peer remains available after forwarding;
- ban/GeoIP/admission policies consume only IPv4 identity;
- malformed/truncated payload cannot crash packet processing;
- normal Terraria operation is unchanged when the feature is disabled.

## Consequences

Vega deployments behind Dimensions no longer need a privileged compatibility plugin solely to replace player IP state. The compatibility feature is explicit, testable and secure-by-default. Vega intentionally does not advertise or retain IPv6 support for Terraria server connection identity; introducing it later would require a separate transport-level decision rather than incidental `IPAddress` acceptance.
