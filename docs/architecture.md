# TShockNG architecture

## Goals

TShockNG modernizes TShock with an explicit source migration path for plugins. Existing plugin binaries are not guaranteed to load unchanged.

Primary goals:

1. Do not promise `TShockAPI` binary compatibility; keep only narrow source-facing contracts for hook and legacy-command registration.
2. Make packet parsing typed and zero-copy where possible.
3. Separate protocol parsing, validation, server domain logic, and infrastructure.
4. Keep mutable Terraria state on the game thread.
5. Move blocking I/O and expensive pure work away from the game thread.
6. Make core services injectable and testable instead of relying on global static state everywhere.
7. Keep upstream-oriented changes narrow enough that future TShock changes can still be reviewed and ported.

## Repository and package responsibilities

### TShockNG/TShockNG

Owns TShock domain behavior:

- players and `TSPlayer`
- groups and permissions
- commands
- SSC
- regions
- bans and accounts
- configuration
- world/player state orchestration
- source-facing plugin registration contracts for hooks and legacy `Command` objects

It does not own Terraria wire-format parsing or reusable packet validation rules.

### TShockNG/TSAPI

Owns integration with Terraria/OTAPI and execution infrastructure:

- plugin lifecycle and loading
- OTAPI hooks
- server lifecycle
- game-thread dispatcher
- background worker execution
- time-sliced game-thread work
- profiling and slow-hook/slow-tick diagnostics

TSAPI should not own TShock domain logic and does not need a Multiplicity dependency.

### TZ.Multiplicity NuGet package

`TZ.Multiplicity` is the canonical Terraria wire-protocol dependency consumed through NuGet.

It owns:

- packet identifiers
- packet layouts
- serialization/deserialization
- zero-copy packet views
- protocol-version tests

TShockNG consumes a pinned package version rather than adding Multiplicity as a source submodule. Multiplicity remains independent of TShock and TSAPI.

### TShockNG/ProtocolGuard

Owns reusable semantic packet validation:

- packet sanity checks
- range and ownership checks
- player/projectile/item/tile/chest validation
- validation results and violations

ProtocolGuard may consume `TZ.Multiplicity` typed packet models/views through NuGet, but must not kick players, send packets, mutate Terraria state, or depend on global TShock state.

## Request pipeline

```text
Terraria / OTAPI
      |
      v
    TSAPI
      |
      v
raw packet buffer
      |
      v
IncomingPacket (stack-only view)
      |
      v
TZ.Multiplicity (NuGet)
      |
      +-------------------+
      | typed packet      |
      v                   v
ProtocolGuard          TShockNG
      |                   |
validation result         | domain decision
      +---------+---------+
                |
                v
            TShockNG
                |
                v
      mutate Terraria state
         on game thread
```

`IncomingPacket` retains the original receive buffer, offset, and exact payload length. Its `PacketView` is produced with `PacketViewParser.ParsePayload`, so the typed path does not need another payload array. The type is a `ref struct` specifically to prevent receive-buffer-backed views from escaping into asynchronous/background work.

During packet migration an internal adapter may still create a `MemoryStream` view when a legacy-shaped handler requires it. That implementation detail is not a binary-compatibility guarantee.

## Service contracts

New internal architecture should prefer narrow service interfaces over direct static access.

Initial contracts should include only boundaries we actually need, for example:

- `IPlayerService`
- `IWorldService`
- `IUserAccountService`
- `IGroupService`
- `IRegionService`
- `ICommandService`
- `IExecutionDispatcher`

Constructor injection is preferred for new code. Legacy globals may be removed as code is migrated. Plugin entry points are limited to explicit registration contracts rather than a broad compatibility facade.

Do not introduce a reflection-heavy dependency-injection framework merely to imitate a framework architecture. Start with explicit composition and plain constructors.

## Event model

The new event layer should support ordered handlers:

1. `Highest`
2. `High`
3. `Normal`
4. `Low`
5. `Lowest`
6. `Monitor`

`Monitor` handlers run last and observe the final event state. They must not be used to change a decision that earlier handlers already made.

Cancelable events should carry an optional reason so diagnostics can explain why an operation was rejected.

Legacy-shaped hook events may remain during migration, but registration is exposed through `IHookRegistry<TEventArgs>` and their concrete binary metadata is not stable.

## Threading model

Mutable Terraria state is game-thread-only.

Examples include:

- `Main.player`
- `Main.npc`
- `Main.projectile`
- `Main.tile`
- `Main.chest`
- tile entities
- `WorldGen`
- `Wiring`

Background workers may perform blocking I/O and pure computation using immutable/copy snapshots. Results that mutate Terraria state must be marshalled back to the game thread.

Large operations that must mutate Terraria state should be time-sliced across ticks rather than blindly moved to worker threads.
