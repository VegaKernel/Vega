# 0019: Optional typed cluster messaging

## Status

Accepted

## Context

Vega servers need to cooperate across multiple Terraria instances for selected production scenarios, but a single server must remain fully functional without any cluster transport.

The cluster layer must not turn Core into a distributed application framework, must not require Redis/RabbitMQ/NATS/HTTP/WebSocket, and must not make ordinary local business operations depend on remote availability.

The first practical scenarios are:

1. propagation of global bans;
2. immediate enforcement of new global bans against players already connected to other servers;
3. synchronization of account group and permission changes;
4. inter-server chat;
5. server presence / online state.

More complex features such as player transfer / Dimensions integration are deferred until these vertical scenarios establish the contracts and operational behavior.

A historical example such as `PvPController-1.3` demonstrates the need for cross-server updates, but its Redis channel with dynamic JSON, string `objectType` / `changeType` actions and weak payloads is explicitly not the target design.

## Decision

Cluster messaging is an optional infrastructure capability built on strongly typed contracts.

### Architectural boundary

The target ownership is:

```text
Vega.Contracts/
  Cluster/
    ServerId
    Messages/
    Events/
    Commands/
    Requests/
    IClusterPublisher
    IClusterCommandSender

Vega.Core/
  Cluster/
    Handlers/            # only real local business handlers as scenarios migrate

Vega.Runtime or Infrastructure/
  Cluster/
    Transport/
```

Exact project names may evolve, but the dependency rule is normative:

```text
Core/Contracts -> no concrete cluster transport dependency
Runtime/Infrastructure -> transport implementation
```

A Vega server without a configured transport must still start, reach `NetworkReady`, accept players and perform all local operations that do not explicitly require a cluster capability.

### Server identity

`ServerId` is a strongly typed value, not a free-form routing dictionary field.

It identifies the logical Vega server instance participating in a cluster. Human display names, endpoints and runtime health belong to separate metadata and presence messages.

### Message families

Cluster messages are explicitly categorized.

#### Event

An Event states that something has already happened on the sender.

Examples:

```text
GlobalBanCreated
GlobalBanRevoked
AccountGroupChanged
PermissionAssignmentChanged
PlayerSessionOpened
PlayerSessionClosed
ServerPresenceChanged
InterServerChatPublished
```

Receiving an event does not mean the remote server should repeat the original domain operation as a new originating operation. It applies the corresponding local consequence.

For example:

```text
Server A creates global ban locally
    -> publishes GlobalBanCreated once
    -> Server B receives it
    -> updates local ban view/cache
    -> kicks matching currently connected players
    -> does NOT publish GlobalBanCreated again
```

This prevents message echo loops and keeps origin semantics clear.

#### Command

A Command asks a specific server or explicit target set to perform an action.

Examples may later include:

```text
DisconnectPlayer
ExecuteAdministrativeAction
PreparePlayerTransfer
```

Commands are not used to describe state that has already changed.

#### Request / Response

Request/Response is used only when the caller genuinely requires a logical answer within a bounded timeout.

It must not become the default replacement for local queries or a hidden synchronous RPC layer between Core services.

Potential later examples include querying a specific authoritative server for transfer preparation or a narrowly scoped aggregated status request.

### Strong typing and schema evolution

Cluster payloads must be strongly typed .NET contracts.

The following are prohibited as the primary message model:

- action strings;
- `Dictionary<string, object>`;
- `dynamic` JSON payloads;
- unversioned generic envelopes whose meaning is determined by arbitrary string fields.

Each message contract has explicit schema/version identity. Versioning must make incompatible changes visible rather than silently changing payload interpretation.

The initial convention should prefer additive evolution of immutable message records. Breaking semantic changes require a new schema version/message version and an explicit compatibility decision.

### Message metadata

Every transported message has structured transport metadata containing at least:

```text
MessageId
CorrelationId?          # required for related request/response or operation chains
SenderServerId
Timestamp
Schema / MessageVersion
```

Transport-specific delivery metadata does not leak into Core domain contracts unless it is genuinely required for processing semantics.

### Delivery semantics

Exactly-once delivery is not required and must not be claimed.

Handlers must be designed for duplicate delivery where the scenario requires durable effects.

The transport/runtime layer must provide or enable:

- message identity;
- duplicate detection/idempotency support;
- cancellation tokens;
- bounded timeout for request/response;
- graceful remote-unavailable behavior;
- correlation where a workflow spans multiple messages;
- observable failures/retries without blocking unrelated local server behavior.

The first implementations may use at-least-once or best-effort delivery depending on scenario, but the chosen semantics must be explicit per transport/scenario.

### Publication rule

Core services perform local business operations locally first.

When a successfully completed local operation has cluster relevance, an integration/application boundary publishes the corresponding event once.

Core services must not directly issue network requests to another Terraria server as part of ordinary local business logic.

Incoming cluster messages enter through typed DI-registered handlers and reuse the same local services/capabilities where appropriate.

Remote handling must distinguish **originating** operations from **replicated/local-application** operations so applying an incoming event does not emit the same event again.

This may be represented by an explicit operation source/context or by a dedicated cluster handler path. Hidden ambient flags are discouraged.

### Interaction with Vega event architecture

Cluster messages are not automatically identical to in-process domain events.

An in-process event may be mapped to a cluster event when crossing a server boundary. This mapping is explicit because cluster contracts have independent versioning, delivery and compatibility concerns.

Incoming cluster messages are dispatched through DI and the existing typed handler/event architecture where that fits the scenario. No reflection-heavy Core registration is required.

### Failure semantics

Cluster unavailability must not make unrelated local server behavior fail.

Scenario-specific rules:

- local global-ban creation succeeds according to the local persistence policy; cluster publication failure is surfaced/queued/retried according to transport policy rather than pretending the local operation never happened;
- incoming duplicate global-ban events must be safe to process repeatedly;
- group/permission replication must converge without creating republish loops;
- inter-server chat may degrade to local-only behavior when transport is unavailable unless operator policy explicitly requires otherwise;
- stale/missing presence is represented as unknown/offline according to timeout policy, not as a fatal server condition.

A durable outbox may be introduced for scenarios where publication after local persistence must survive process failure, but it is not mandated until a real scenario requires that durability guarantee.

### First vertical implementations

The cluster layer is implemented scenario-by-scenario, not as a generic distributed framework.

Implementation order:

1. **Server presence / online state**
   - establishes `ServerId`, message metadata, publication, subscription, expiry/staleness and telemetry with minimal business risk.

2. **Global ban propagation**
   - publish `GlobalBanCreated` / `GlobalBanRevoked` from successful local operations;
   - receiving servers update their local authoritative/read model as defined by the ban subsystem;
   - duplicate delivery is idempotent.

3. **Immediate online-player enforcement**
   - after applying `GlobalBanCreated`, each receiving server checks local `PlayerSnapshot`/directory state and schedules required disconnect through player operations;
   - no remote server mutates another server's Terraria state directly.

4. **Group and permission synchronization**
   - typed account/group/permission events;
   - invalidate/update local authorization state without string action payloads.

5. **Inter-server chat**
   - typed chat publication with sender/server identity and explicit delivery policy;
   - local rendering/delivery remains a local capability.

6. **Later scenarios**
   - administrative commands;
   - centralized audit/monitoring;
   - aggregated multi-server state;
   - player transfer / Dimensions integration after the simpler messaging semantics are proven.

### Transport selection

No transport is selected by this ADR.

Redis, NATS, RabbitMQ, HTTP, WebSocket or another implementation may be evaluated when the first vertical scenario is implemented. Selection is based on actual required delivery semantics, operational footprint and production environment rather than abstract feature count.

Transport packages and SDKs remain outside `Vega.Core` and `Vega.Contracts`.

### Implementation note (Phase 8)

The first vertical implementation lives in `Vega.Cluster`. It provides explicit typed registration/dispatch, bounded duplicate detection and audit records, plus an in-memory reference transport used for deterministic integration tests and local composition. This reference transport does not select or imply a production transport.

The runtime defaults to `UnavailableClusterClient`. Local global-ban changes are applied before publication and expose transport unavailability separately from the local result. Incoming replicated ban and authorization handlers apply revision-aware local consequences and have no publishing capability, so they cannot echo the received event.

Presence with expiry, global ban create/revoke and immediate kick, authorization replication, policy-controlled chat, targeted administrative commands and bounded status requests are implemented as separate scenario adapters. Player transfer remains disabled behind `PlayerTransferReadinessGate` until those simpler semantics are validated in production. No production validation or exactly-once behavior is claimed by the in-memory integration tests.

## Consequences

Positive:

- single-server Vega remains independent and fully operational;
- cross-server contracts are typed and evolvable;
- local business logic does not acquire hidden network latency/failure modes;
- duplicate delivery and correlation are designed in from the beginning;
- cluster behavior can reuse local Core services without making Core distributed;
- simple production scenarios validate the design before player transfer or distributed workflows are attempted.

Costs:

- cluster event contracts require explicit mapping from local events;
- idempotency and replication semantics must be defined per scenario;
- some scenarios may eventually need durable publication/outbox infrastructure;
- no illusion of exactly-once delivery is provided.
