# 0003: Mutable Terraria state is game-thread-only

## Status

Accepted

## Context

Terraria global state was not designed for arbitrary concurrent mutation. Moving world/player/NPC mutation to worker threads would introduce races that are difficult to reproduce and diagnose.

## Decision

Mutable Terraria state is accessed for mutation only from the game thread. Background workers operate on immutable or copied snapshots. Results that change Terraria state are posted back to the game thread.

Large game-thread-only operations should use time slicing when possible.

## Consequences

- SQL, HTTP, file I/O, serialization, and pure computation can run in workers.
- ProtocolGuard must not retain mutable Terraria references.
- Thread-affinity assertions should be added to dangerous execution boundaries.
