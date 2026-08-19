# ADR 0011: Built-in Dimensions protocol compatibility

Status: Planned

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

That is useful compatibility behavior, but it is the wrong architectural boundary for TShockNG and is unsafe if packet 67 is blindly trusted from arbitrary public clients.

## Decision

Implement first-party, optional Dimensions protocol compatibility in TShockNG so a dedicated Dimensions plugin is not required for this protocol shim.

The feature is disabled by default and controlled by the main TShockNG configuration.

Initial configuration shape:

```json
{
  "EnableDimensionsPacket": false,
  "DimensionsTrustedProxies": [
    "127.0.0.1",
    "::1"
  ]
}
```

The exact serialization names may be normalized when the configuration model is modernized, but the semantics must remain explicit:

- `EnableDimensionsPacket` enables handling of the Dimensions protocol carried in packet 67;
- `DimensionsTrustedProxies` defines which immediate TCP peers are allowed to supply forwarded client metadata.

Do not interpret packet 67 as trusted forwarding metadata merely because the feature is enabled.

## Protocol

The compatibility decoder initially supports the historical Dimensions payload:

```text
Terraria packet id: 67 (PacketTypes.Placeholder)

payload:
  int16  joinType
  string joinInfo
```

Known `joinType` values:

```text
0 = forwarded/real client IP address
1 = reserved historical GameModes message
```

### Type 0: forwarded client IP

For `joinType = 0`:

1. verify that Dimensions compatibility is enabled;
2. verify that the immediate socket peer matches `DimensionsTrustedProxies`;
3. parse `joinInfo` with `IPAddress.TryParse`;
4. reject malformed, empty, multicast, unspecified, or otherwise unusable addresses;
5. store the forwarded address through the connection metadata/admission abstraction;
6. make that effective client address available to TShock policy and observability code without reflection;
7. record both the immediate proxy endpoint and effective client endpoint for diagnostics.

Do not mutate private fields with reflection.

### Type 1 and unknown types

Do not guess the semantics of historical `joinType = 1`.

Initially:

- preserve the decoder/dispatch shape so another first-party module can consume it later;
- ignore or report unsupported Dimensions message types according to normal protocol logging policy;
- do not treat unknown `joinType` values as valid forwarded addresses.

## Connection admission ordering

The effective remote address must be established before policies that depend on client identity are finalized.

This includes, where applicable:

- IP bans;
- GeoIP lookup;
- proxy/VPN policy;
- connection logging;
- REST/operations diagnostics related to the player connection;
- plugins or first-party services consuming the canonical client endpoint.

The historical plugin had to redo GeoIP/proxy checks because it changed `TSPlayer.CacheIP` after those checks. TShockNG should instead model the connection stages explicitly so forwarded metadata can be applied at the correct point.

Conceptually:

```text
socket accept
    |
    v
immediate peer endpoint
    |
    v
optional trusted Dimensions metadata (packet 67/type 0)
    |
    v
canonical/effective client endpoint
    |
    +--> ban policy
    +--> GeoIP
    +--> proxy policy
    +--> player/session projection
    `--> logs / operations UI
```

## Security model

Forwarded IP data is an assertion made by a proxy, not by the Terraria client.

Therefore:

- packet 67/type 0 must only be authoritative when received over a connection whose immediate peer is trusted;
- public clients must not be able to spoof their IP by sending the same packet directly;
- retain the original immediate peer address even after a forwarded address is accepted;
- malformed Dimensions packets must fail closed for metadata handling without crashing the game/network thread;
- the compatibility parser must be bounded and use the same typed packet validation path planned for the modern networking layer;
- log rejected forwarding attempts at an appropriate rate-limited diagnostic level.

This design is intentionally analogous to trusted reverse-proxy forwarding on HTTP servers: forwarded identity is accepted only from configured trusted hops.

## Architecture

Dimensions support belongs in the networking/connection-admission boundary, not in the Terminal.Gui UI, command system, or a `TSPlayer` reflection helper.

Recommended contracts after the networking refactor:

```text
DimensionsPacketDecoder
    -> DimensionsMessage
       -> ForwardedClientAddress

ConnectionMetadata
    - TransportRemoteEndPoint
    - EffectiveRemoteEndPoint
    - ForwardingSource

ConnectionAdmissionService
    -> validates trusted proxy
    -> applies forwarded identity
    -> runs connection policies
```

The concrete names may change. The important properties are:

- typed packet parsing;
- immutable connection metadata snapshots where practical;
- explicit trust policy;
- no reflection;
- no direct dependency from core connection logic on a plugin assembly.

ProtocolGuard should eventually validate the semantic Dimensions message through the same typed validation pipeline as other packets rather than special-case raw byte offsets in unrelated code.

## Player compatibility

Existing APIs that expose a player's IP should continue to see the effective client IP when trusted Dimensions forwarding is active, because that is the behavior administrators rely on today.

Internally, however, preserve both addresses:

- transport/proxy address;
- effective/forwarded client address.

New APIs should make this distinction explicit rather than overloading one mutable string field forever.

## Operations UI

The planned Terminal.Gui operations UI should expose connection provenance in the player detail view when useful:

```text
Client IP:       203.0.113.10
Connected via:   127.0.0.1:7778 (Dimensions)
Forwarding:      trusted
```

Do not expose this by making the UI parse packets itself. It consumes the connection metadata read model.

## Testing

Add tests covering at least:

- packet ID 67 recognized only when compatibility is enabled;
- type 0 parses a valid IPv4 address;
- type 0 parses a valid IPv6 address;
- malformed IP is rejected;
- unknown `joinType` is not treated as an IP update;
- packet from an untrusted peer cannot replace the effective address;
- trusted peer can supply the effective address;
- transport peer remains available after forwarding;
- ban/GeoIP/proxy policies receive the effective address in the correct order;
- malformed/truncated payload cannot crash packet processing;
- normal Terraria operation is unchanged when the feature is disabled.

## Sequencing

Implement this after the .NET 10 baseline is merged and while the networking/connection pipeline is being modernized.

Preferred order:

1. establish explicit connection metadata and effective endpoint semantics;
2. add trusted-proxy configuration;
3. add the typed packet-67 Dimensions decoder;
4. integrate it before identity-dependent connection policies;
5. add compatibility projection for existing `TSPlayer` IP APIs;
6. expose connection provenance to diagnostics/Terminal.Gui read models;
7. integrate semantic validation with ProtocolGuard;
8. remove the need for the external Dimensions compatibility plugin in deployments that only use this functionality.

## Consequences

TShockNG deployments behind Dimensions no longer need to carry a small privileged plugin solely to replace player IP state. The compatibility feature becomes explicit, testable, secure-by-default, and integrated at the point where connection identity actually belongs.
