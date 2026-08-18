# TShockNG architecture

## Goals

TShockNG modernizes TShock without forcing an immediate rewrite of the existing plugin ecosystem.

Primary goals:

1. Preserve existing `TShockAPI` binary compatibility where practical.
2. Make packet parsing typed and zero-copy where possible.
3. Separate protocol parsing, validation, server domain logic, and infrastructure.
4. Keep mutable Terraria state on the game thread.
5. Move blocking I/O and expensive pure work away from the game thread.
6. Make core services injectable and testable instead of relying on global static state everywhere.
7. Keep upstream-oriented changes narrow enough that future TShock changes can still be reviewed and ported.

## Repository responsibilities

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
- compatibility API for existing TShock plugins

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

TSAPI should not own TShock domain logic.

### TShockNG/Multiplicity

Target repository for the canonical Terraria wire-protocol implementation:

- packet identifiers
- packet layouts
- serialization/deserialization
- zero-copy packet views
- protocol-version tests

Multiplicity must remain independent of TShock and TSAPI.

### TShockNG/ProtocolGuard

Owns reusable semantic packet validation:

- packet sanity checks
- range and ownership checks
- player/projectile/item/tile/chest validation
- validation results and violations

ProtocolGuard must not kick players, send packets, mutate Terraria state, or depend on global TShock state.

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
Multiplicity
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

Constructor injection is preferred for new code. Existing global APIs remain available through compatibility adapters while old code is migrated.

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

Legacy TShock hooks remain supported through adapters.

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
