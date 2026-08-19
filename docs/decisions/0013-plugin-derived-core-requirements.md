# 0013: Plugin-derived core requirements

## Status

Accepted

## Context

TerraZ production servers use a number of small TShock plugins and Dimensions extensions that either compensate for limitations in the current server architecture or implement optional gameplay features.

During the TShockNG modernization these projects are useful as production evidence: if a plugin exists only to restore a server invariant, close a protocol hole, repair connection sequencing, provide missing runtime semantics, or work around a structural limitation of the legacy API, the long-term solution should usually live in TShockNG Core instead of requiring the workaround plugin forever.

This ADR records requirements discovered from a read-only audit of TerraZ-Team repositories. It does not import those repositories wholesale and does not make TShockNG depend on TerraZ-Team assemblies.

## Decision criteria

A plugin-derived behavior is a candidate for Core when it does one or more of the following:

- fixes incorrect or unsafe base-server behavior;
- enforces a protocol or game-state invariant that all servers should be able to rely on;
- provides security validation that belongs in the packet/state validation pipeline;
- repairs SSC, login, proxy or Dimensions sequencing;
- defines fundamental account, ban, world, runtime or module-host semantics;
- exists mainly because the legacy TShock API exposes only global/static state.

A behavior should normally remain an optional module when it is a gameplay policy, convenience command, community feature or server-specific rule.

## Core/runtime candidates

### World timing

`TerraZ-Team/TimeSync` demonstrates that Terraria world time can drift with server TPS. TShockNG should provide an optional world-clock service whose progression is based on elapsed real/monotonic time rather than assuming exactly 60 successful game ticks per second.

Requirements:

- clock state belongs to a `WorldId` / world context;
- correction of Terraria state is scheduled on the game thread;
- background timing code must not mutate `Main.*` directly;
- large discontinuities or explicit administrator/game changes re-anchor the clock instead of being overwritten;
- behavior is configurable per world context.

### Runtime TPS and tick telemetry

`TerraZ-Team/TPSChecker` demonstrates the need for first-class tick-rate telemetry. This belongs to Core Operations rather than a standalone plugin.

Requirements:

- measure completed game updates against monotonic elapsed time;
- expose current TPS and rolling windows through immutable operational snapshots;
- expose minimum/maximum values with timestamps where useful;
- record tick-duration percentiles such as p50/p95/p99 in addition to average TPS;
- metrics are associated with `WorldId` / server instance and must be usable by the Terminal.Gui operations UI;
- low-TPS alerts are emitted as structured operational events, with notification policy outside the measurement component;
- no static global metric state in the modern implementation.

### Ban and moderation identity

`TerraZ-Team/ExtendedBans` extends TShock bans/mutes with account, UUID, IP and CIDR matching. These are fundamental moderation primitives and should be represented by modern Core contracts rather than command-specific database queries.

Target concepts include:

- typed ban subjects (account, UUID, exact IP, network/CIDR);
- normalized address/network matching;
- explicit expiration and reason metadata;
- one authoritative matching service used during login and administrative commands;
- adapters preserving legacy TShock ban APIs while behavior moves into Core.

Moderation policy and UI remain separable from the identity/matching engine.

### Packet and state validation

The following TerraZ plugins contain validation rules that should be reviewed as candidates for the TShockNG Core + ProtocolGuard pipeline rather than permanently duplicated as external guards:

- `PacketGuard`;
- `StateGuard`;
- `LeashedEntityGuard`;
- `NoCoinDupe`.

The target architecture is not to merge their source files into TShockAPI. Rules should be expressed as typed packet/state validation over immutable context and return structured decisions such as Allow, Reject, Correct or Violation.

Candidate invariants discovered during the audit include:

- malformed packet rejection;
- packet rate limiting and narrowly-scoped deduplication;
- item spawn distance/sanity;
- projectile ownership, type, velocity and active-count checks;
- repeated KillMe abuse protection;
- invalid chest resize protection;
- ghost/spectate/state restrictions where configured;
- world-edge placement restrictions;
- safe validation/rebuild of leashed entities and anchors;
- prevention of the coin-duplication packet path currently blocked by `NoCoinDupe`.

Telemetry/anomaly scoring is operational evidence, not proof of cheating, and must remain separate from deterministic validation rules.

### Fake world providers, scenes and visibility scopes

`TerraZ-Team/FakeProvider` is production evidence for the sandbox/multi-world architecture described by ADR 0012. It replaces direct `Main.tile` access with layered tile providers, supports private providers and isolates players, NPCs and projectiles by visibility layer.

TShockNG should not embed the existing plugin as-is. Its behavior should inform first-class abstractions such as:

- tile/world provider interfaces below higher-level world services;
- ordered provider/layer composition;
- private or observer-scoped scene providers;
- visibility scopes for players, NPCs, projectiles and other networked entities;
- inbound interaction validation that respects the same visibility/sandbox scope used for outbound filtering;
- clear ownership by `WorldId` / scene identity;
- game-thread-safe mutation and snapshot-based observation.

This creates a path from current one-world fake scenes to future real sandbox worlds without forcing Core callers to depend directly on `Main.tile`.

### Client platform and connection metadata

`TerraZ-Team/ClientPlatformDetector` demonstrates that Terraria already exposes platform information through the normal `PlayerPlatformInfo` packet. This should become first-class connection/player metadata instead of requiring a separate plugin and ad-hoc extension state on `TSPlayer`.

Requirements:

- parse platform information through the typed packet pipeline rather than manually repositioning a shared `BinaryReader`;
- associate the reported platform with the authenticated connection/player identity only when the packet's player slot matches the sending connection;
- represent platform as a typed value with an explicit `Unknown` state;
- absence of `PlayerPlatformInfo` must not be promoted to a proven `PC` platform; heuristics/fallback guesses, if retained for compatibility, must remain distinguishable from confirmed client-reported metadata;
- expose confirmed/unknown platform through immutable player/connection snapshots and the modern player metadata contracts;
- surface platform in administrative player-info commands and Terminal.Gui player details without making authorization or security decisions solely from the client-reported platform;
- keep the model extensible for future client capability/version metadata without adding more fields directly to legacy `TSPlayer`.

The legacy `/platform <player>` behavior may be preserved as a compatibility command while its data source moves into Core.

### SSC and Dimensions connection sequencing

`TerraZ-Team/SSCHealthFix` demonstrates a sequencing bug between Dimensions packet queuing and TShock SSC restore: a queued pre-login `PlayerHealth` packet can be replayed after SSC state has been restored and overwrite authoritative max HP.

This must be treated as a connection-state invariant rather than a permanent external workaround.

Requirements:

- connection/login processing has explicit phases;
- pre-auth/pre-SSC packets can be classified as stale once authoritative SSC state is loaded;
- queued packets are replayed only when valid for the resulting connection phase;
- authoritative server-side character state wins over stale client state;
- Dimensions integration can participate through typed forwarding/connection metadata rather than ad-hoc packet interception.

`TerraZ-Team/UuidGuardExtension` additionally demonstrates the need for a clean integration boundary for trusted proxy/firewall control signals. Firewall enforcement itself remains an external/infrastructure concern; Core should expose structured security events/signals instead of encoding shell/nftables policy into game logic.

## Module / extension candidates

The following audited projects are useful capabilities but are primarily gameplay or server-policy features and should normally remain optional modules built on modern contracts:

- `Homes` — named personal teleport locations;
- `HouseRegions` — player-owned houses built on region primitives;
- `TpaRequest` — player teleport requests;
- `Clans` and similar community features;
- `SealedDressers` — server-specific gameplay restriction;
- chat/community conveniences such as `Mentions`, `ChatAboveHeads`, `ChatGuard` policy.

Core may provide the primitives these modules require, for example world-aware teleport services, region ownership/ACL contracts, account metadata and command registration. It should not make every convenience feature part of the base server.

## Plugin/module hosting

`TerraZ-Team/PluginLoader` is evidence that operators need predictable module lifecycle and visibility when plugin files change. Hot-reloading arbitrary legacy TShock assemblies is unsafe because existing plugins may retain hooks, threads or static state.

TShockNG should therefore distinguish:

- legacy plugin compatibility loading;
- modern managed modules with explicit lifecycle contracts;
- file-system change detection as an operational signal;
- reload/restart-required state rather than pretending every arbitrary assembly is safely unloadable.

Module status and pending-reload state should be exposed to the Terminal.Gui operations UI.

## Relationship to Terminal.Gui operations UI

The operations UI described by ADR 0010 should consume Core snapshots rather than plugin statics. Plugin-derived requirements add at least:

- per-world TPS/current tick latency and rolling statistics;
- low-TPS/lag events;
- loaded module/plugin state and lifecycle status;
- packet validation/rate-limit counters;
- active world/sandbox/scene/provider information;
- player world/visibility scope;
- client platform/capability metadata with confirmed-vs-unknown state;
- security and connection metadata without exposing secret credentials.

## Migration approach

1. Keep existing TerraZ plugins operational while equivalent TShockNG behavior is incomplete.
2. Extract the invariant or service contract, not the old plugin class hierarchy.
3. Add focused tests reproducing the production bug or behavior before replacing a workaround.
4. Implement the modern behavior in Contracts/Core/ProtocolGuard as appropriate.
5. Bridge legacy TShock APIs only where compatibility requires it.
6. Retire a workaround plugin only after the integrated behavior has equivalent tests and production-observable metrics.

## Consequences

- Production workarounds become a source of modernization requirements instead of permanent dependencies.
- TShockNG Core gains missing runtime and security primitives without becoming a bundle of every TerraZ gameplay plugin.
- World/sandbox work can reuse lessons from FakeProvider rather than rediscovering layered visibility and tile-provider problems later.
- TPS/time behavior becomes world-aware and observable through the same operations model used by Terminal.Gui.
- PacketGuard and other guards converge toward one typed validation pipeline rather than accumulating overlapping hooks.
- Client platform information becomes structured connection metadata instead of plugin-local mutable state.
