# 0014: Module-facing gameplay and administration primitives

## Status

Accepted

## Context

The TerraZ plugin audit in ADR 0013 identifies behaviors that belong directly in TShockNG Core because they repair server invariants, runtime behavior, connection sequencing or protocol security.

A second group of production plugins is different. Their user-facing behavior should usually remain optional, but their implementations expose missing reusable primitives in the current TShock API. If those primitives are not designed into the modern Contracts/Core layers, future modules will continue to hook global state and duplicate unsafe implementation details.

This ADR records requirements derived from a read-only review of the following TerraZ-Team plugins:

- `Mentions`;
- `InvSee`;
- `Wipe`;
- `TempGroup`;
- `Region-Vision`;
- `AutoWorldCleaner`;
- `TSExt_UICommand`;
- `ControlGroupOption`;
- `InfChests`;
- `ChestRegen`.

`LeashedEntityGuard` remains classified by ADR 0013 as a Core/ProtocolGuard validation candidate rather than an optional module primitive.

## Decision

TShockNG will keep convenience/gameplay behavior modular, while providing explicit world-aware and account-aware services that allow those modules to be implemented without direct dependence on `Main.*`, mutable `TSPlayer` internals or monolithic command handlers.

### World lifecycle and reset orchestration

`TerraZ-Team/Wipe` performs a complete world reset, clears configured database state and supplies world-generation parameters including size, difficulty, evil, seed and Terraria 1.4.5 special/secret seed combinations.

The `/wipe` command itself does not need to be mandatory Core functionality, but the underlying lifecycle does.

The modern architecture should provide a world lifecycle/orchestration service capable of representing operations such as:

- prepare a replacement world configuration;
- validate world-generation parameters before shutdown;
- request a controlled world/server transition;
- identify world-scoped persistent stores that participate in a reset;
- execute reset/cleanup work through explicit registered participants rather than hard-coded table names;
- expose pending, running, failed and completed lifecycle state to operations tooling;
- associate lifecycle operations with a `WorldId`;
- support future sandbox creation/reset/destruction using the same primitives defined by ADR 0012.

Destructive operations must support explicit confirmation/cancellation at the command/UI layer and should be structured so that validation occurs before irreversible steps begin.

### Temporary groups, role leases and delegated administration

`TerraZ-Team/TempGroup` demonstrates that temporary group assignment is already effectively part of TShock authorization semantics: an active temporary group overrides the normal group, has expiration state and requires careful timer replacement so an older timer cannot revoke a newer assignment.

`TerraZ-Team/ControlGroupOption` additionally demonstrates the need to control who may create/delete/modify accounts and who may assign temporary groups.

These concepts should become modern identity/authorization primitives rather than fields and timers owned directly by `TSPlayer`.

Target concepts include:

- account/group assignment services independent of the connected player object;
- expiring role/group leases with an absolute expiration time;
- cancellation/replacement semantics that prevent stale expiry callbacks from revoking newer assignments;
- persistence where configured so a role lease is not inherently tied to one connection;
- structured authorization policy for account administration and role delegation;
- ability to constrain which roles/groups an administrator is allowed to grant;
- audit events for account, group and temporary-role changes;
- compatibility adapters for legacy `TSPlayer.tempGroup`, `tempGroupTimer` and existing permissions while migration is incomplete.

Expiration should be driven by a scheduler/service and monotonic/absolute time calculations, not one long mutable `System.Timers.Timer` stored on a player object.

### Player and inventory inspection

`TerraZ-Team/InvSee` allows an administrator to inspect/copy another player's inventory and optionally save it.

The user-facing command remains optional, but Core should expose safe inventory observation/mutation primitives:

- immutable player inventory snapshots;
- explicit inventory slot/item DTOs rather than exposing Terraria arrays directly;
- world/player identity attached to a snapshot;
- game-thread scheduled mutation for operations that change an online player's inventory;
- an explicit authoritative source for SSC/offline account inventory where available;
- permission/audit hooks for privileged reads and writes.

This also gives Terminal.Gui and future REST APIs a safe way to inspect player state without teaching those interfaces about Terraria internals.

### Sensitive administrative information

`TerraZ-Team/TSExt_UICommand` replaces `/ai` and `/ui` partly to separate permission to view IP addresses and UUID information from permission to view general player/account information.

Modern player/account inspection must therefore classify sensitive fields instead of returning one oversized DTO to every caller.

Requirements:

- IP address, forwarded/origin address, UUID and similar identifiers are explicit sensitive metadata;
- authorization can distinguish general player information from network identity and persistent identifiers;
- Terminal.Gui, REST and command adapters consume the same authorization policy rather than duplicating redaction logic;
- logs and operational snapshots must avoid accidentally exposing sensitive fields where they are unnecessary;
- legacy `/ai` and `/ui` output can be preserved through adapters while the data source and authorization policy move into Core.

### Private visual overlays and virtual scenes

`TerraZ-Team/Region-Vision` draws region boundaries using tiles that exist only for the requesting player. It also prevents interactions with phantom boundary tiles and updates the visualization when regions change.

This is further evidence, alongside FakeProvider in ADR 0013, for observer-scoped scene/overlay primitives.

Core/Contracts should provide enough abstraction for modules to create temporary private visual overlays without mutating the authoritative world:

- observer-scoped tile overlays/providers;
- ownership/lifetime tokens for an overlay so cleanup is deterministic;
- update/invalidation when the underlying region/world object changes;
- inbound interaction filtering consistent with the overlay shown to that player;
- bounded/streamed updates for large overlays rather than sending an entire large region at once;
- world/scene identity so overlays work correctly with sandbox contexts.

`Region-Vision` itself should remain a module built on these primitives.

### World entity maintenance

`TerraZ-Team/AutoWorldCleaner` periodically removes world projectiles and items.

Automatic cleanup policy is server-specific and should remain optional, but the Core should expose world-scoped maintenance primitives:

- safe enumeration/snapshots of world items/projectiles;
- game-thread scheduled removal operations;
- cancellable recurring jobs owned by a module/world context;
- structured metrics showing how many entities were removed and why;
- no background task may mutate Terraria entity arrays directly.

The same scheduler/lifecycle facilities should be reusable by world clocks, sandbox cleanup and other maintenance tasks instead of every plugin starting its own timer/thread.

### Chest state, snapshots and transactional mutation

`TerraZ-Team/InfChests` implements infinite/refilling chests, chest clearing and item-key-based locking. `TerraZ-Team/ChestRegen` snapshots and later regenerates chest contents.

These behaviors should remain optional modules, but their common infrastructure belongs in Core.

Target chest primitives include:

- stable chest identity scoped to `WorldId`;
- immutable chest/content snapshots;
- explicit open/read/write/clear/restore operations;
- game-thread-safe transactional chest mutation;
- validation that a chest still exists and still refers to the expected world object before applying a stored snapshot;
- extensible access-control hooks for region ownership, permissions, item-key policy or other modules;
- change/version information so a module can detect stale snapshots or conflicting edits;
- structured events for chest opened/changed/destroyed/restored where useful.

Core must not hard-code infinite-chest, key-lock or regeneration policy. Those are consumers of the chest service.

### Chat transformation pipeline

`TerraZ-Team/Mentions` intentionally performs a local transformation of the raw player-message portion instead of rebuilding the full formatted chat line. This allows it to coexist with other chat plugins and preserves transformations made by other handlers.

Mentions remain optional, but the modern chat pipeline should support composable transformations:

- typed chat message context containing raw text and structured metadata;
- ordered/named transformation stages rather than several plugins independently rebuilding one formatted string;
- separation between content transformation, moderation policy and final rendering;
- preservation of transformations added by independent modules;
- per-recipient rendering where localization, mentions or visibility require it;
- no requirement for a module to know unrelated group-prefix/chat-style plugins.

This pipeline should align with the per-player localization design in ADR 0009.

## Module classification

The current classification is:

| Capability | Long-term home |
|---|---|
| world lifecycle/reset primitives | Core |
| temporary role/group leases | Core identity/authorization |
| delegated account/group administration policy | Core authorization |
| inventory snapshot/mutation API | Core primitive |
| sensitive player/account field authorization | Core authorization/metadata |
| private visual overlay/provider API | Core world/scene primitive |
| world entity maintenance scheduler/API | Core primitive; cleanup policy remains module |
| chest snapshot/mutation/ACL API | Core primitive |
| mentions | optional module |
| InvSee command/UI | optional admin module/UI |
| Wipe command/policy | optional admin module/UI over Core lifecycle |
| Region-Vision | optional module |
| AutoWorldCleaner policy | optional module |
| InfChests | optional module |
| ChestRegen | optional module |

## Relationship to Terminal.Gui

The Terminal.Gui operations UI from ADR 0010 should be able to consume these primitives without referencing legacy plugin types. Useful future views/actions include:

- world lifecycle/reset state and pending destructive operations;
- account/group/temporary-role details and expiry times subject to authorization;
- player inventory inspection through snapshots;
- sensitive network identity only when the operator has the required permission;
- active private scenes/overlays/providers;
- world item/projectile counts and cleanup history;
- chest state/diagnostics where an installed module exposes them.

UI actions must call application services. The UI must never mutate Terraria or plugin state directly.

## Relationship to sandbox worlds

World lifecycle, private overlays, inventory operations, maintenance jobs and chest state must carry `WorldId` from the beginning. A module written against these contracts should not have to be redesigned when TShockNG moves from one primary world to multiple sandbox/world contexts.

In particular, the FakeProvider/Region-Vision style observer-scoped overlay is a useful intermediate abstraction, while full sandbox worlds use the broader `IWorldContext` model described by ADR 0012.

## Consequences

- TShockNG gains reusable application services without turning every production convenience plugin into built-in server behavior.
- Modules can become substantially smaller because they express policy instead of manipulating Terraria/TShock internals.
- Legacy commands can gradually become adapters over the same services used by Terminal.Gui and future HTTP APIs.
- Account/group permissions, world lifecycle and sensitive data access become explicit policies instead of scattered command checks.
- Sandbox support is reinforced because new world-facing primitives are required to carry `WorldId`.
