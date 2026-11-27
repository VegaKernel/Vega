# Optional cluster messaging

Vega cluster messaging adds typed inter-server capabilities without making networking a prerequisite for a standalone server. In an unconfigured runtime, local operations continue independently. The optional [authenticated peer transport](plugin-cluster.md) enables plugin notifications and bounded host presence with advisory routing.

## Contract model

Every envelope contains a typed `ServerId`, `MessageId`, optional correlation ID, sender, timestamp and matching schema/version metadata. Events, targeted commands and bounded request/response exchanges have separate interfaces and handler families.

The public capabilities are intentionally narrow:

- `IClusterPublisher` broadcasts events that already happened locally;
- `IClusterCommandSender` targets an action at one server;
- `IClusterRequestSender` requests a typed response with a timeout;
- typed handler interfaces receive only their corresponding envelope.

There is no service locator, dynamic payload dictionary or string action protocol.

## Delivery and failure semantics

The in-memory reference transport belongs to `Vega.Cluster`, outside Core and Contracts. It exists for local composition and deterministic integration testing. The optional authenticated HTTP transport has a narrower explicit event allowlist: plugin notifications, host presence announcements, and withdrawals. Experimental command/request handlers are not exposed over this transport.

Dispatch uses bounded message-ID retention for duplicate detection. Duplicate event/command delivery is successful and observable as `Duplicate`; duplicate requests reuse a bounded cached typed response instead of repeating the handler side effect. Failed or cancelled delivery releases the message ID so a transport can retry it. Exactly-once delivery is not promised. Audit records capture family, message type, metadata, status and failure detail in a bounded log.

Originating coordinators change local state first and publish once. Replicated handlers apply only the local consequence and do not depend on `IClusterPublisher`, which prevents message echo loops. When a target or transport is unavailable, the result is `Unavailable` rather than an exception that damages unrelated local operation.

## Implemented verticals

- Presence announcements and withdrawal with expiry/staleness and snapshots.
- Global ban create/revoke replication with revision convergence and online-player enforcement through `IPlayerDirectory` and `IPlayerOperations`.
- Account group and permission replication with stale-revision rejection.
- Inter-server chat with explicit local delivery policy and `IPlayerMessenger`.
- Targeted player disconnect commands.
- Administrative command requests through `IOperationsCommandService`.
- Per-server and aggregated status requests.
- Bounded centralized cluster audit records.

Player transfer is intentionally not implemented. `PlayerTransferReadinessGate` keeps it disabled until presence, ban, authorization and chat behavior have all been validated in production. This records the Phase 8 guardrail without pretending that local integration tests are production evidence.

A durable outbox remains optional. It should be added only when a production scenario requires publication durability across process failure.
