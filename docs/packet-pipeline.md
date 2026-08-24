# Packet pipeline

Phase 22 reduces authoritative game-thread packet cost only when preparation is pure, ordered and based on owned data. The production ingress remains synchronous in this first slice; classification and measurement precede any worker queue.

```text
network/worker threads = pure preparation of owned packet intents
game thread             = current-state authority, plugin gameplay hooks and mutation
```

## Current boundary

`NativePlayerPacketSecurityHooks` receives the bounded Terraria buffer window. `MultiplicityPacketProcessing` creates a stack-only `PacketView` over that window and synchronously invokes the current policy pipeline before the receive-buffer lifetime ends. No `IncomingPacket`, span, packet view or raw receive-buffer reference is retained.

`PacketPipelineClassification` is internal to `Vega.Terraria`. Ordinary contracts and plugin APIs do not expose lanes, queues, channels, buffer ownership or thread switching. Every `PacketTypes` value receives a conservative profile: packet types not explicitly owned by the Vega security path are `GameThreadOnly`, order-sensitive, world-state-sensitive and not coalescable.

## Inbound family audit

All currently intercepted security families are `PartiallyOffloadable`: bounded structural parsing and normalization can eventually run away from the game thread, but current-state policy and mutation must remain authoritative. No current family is classified `PureBeforeApply`, and no family is yet approved for coalescing.

| Family | Packet types | Order-sensitive | World-state-sensitive | Latency-sensitive | Coalescable now |
| --- | --- | --- | --- | --- | --- |
| Player state | `PlayerUpdate`, `PlayerHp`, `PlayerMana`, `PlayerTeam`, `TeamChangeFromUI`, `TogglePvp`, `SpectatePlayer`, `PlayerTeleportPortal`, `PlayerHurtV2`, `PlayerDeathV2` | yes | yes | yes | no |
| Projectile | `ProjectileNew`, `ProjectileDestroy`, `SyncProjectileTrackers` | yes | yes | yes | no |
| Item | `ItemDrop`, `UpdateItemDrop`, shimmer/cannot-take/despawn/position sync, nearest-chest and food-platter placement | yes | yes | no | no |
| World interaction | tile mutation, doors, tile/wall paint, signs, object/item-frame placement, liquids, gem locks and mass wiring | yes | yes | no | no |
| Chest | contents, item, open/place/size/name/unlock, player chest index | yes | yes | no | no |
| Tile entity | placement, display jar, display-doll/hat-rack sync, interaction, tile picking and leashed-anchor placement | yes | yes | no | no |
| Other owned security | `SyncExtraValue` | yes | yes | no | no |
| All other inbound packet types | conservative default | yes | yes | packet-specific/unknown | no |

The first measured vertical is `PlayerUpdate`; projectile and tile/world families remain the next candidates. A future owned intent must include a stable connection/session generation rather than only the reusable Terraria slot. Classification does not authorize background access to player, projectile, tile, chest or world state.

## Synchronous baseline

Run the reproducible harness with:

```powershell
dotnet run --project benchmarks/Vega.PacketPipeline.Benchmarks/Vega.PacketPipeline.Benchmarks.csproj `
  --configuration Release
```

The harness reuses serialized `PlayerUpdate` payloads for 200 sessions, processes 256 packets per synthetic tick through `PacketViewParser` and `PlayerPacketOwnershipPolicy`, and records process CPU, total allocations, GC collections and packet/tick latency percentiles. Linux also records voluntary plus involuntary context switches from `/proc/self/status`; the current Windows host does not expose that counter through the harness.

The initial .NET 11 Preview 7 x64 Windows run on 2026-08-24 produced:

| Metric | Result |
| --- | ---: |
| Packets | 1,024,000 |
| Throughput | 2,735,024 packets/s |
| Wall time | 365.6 ns/packet |
| CPU time | 427.2 ns/packet |
| Allocation | 232.0 bytes/packet |
| GC | 37 Gen0, 0 Gen1, 0 Gen2 |
| Synthetic tick p50/p95/p99/max | 37.1 / 283.2 / 323.6 / 774.7 microseconds |
| Context switches | unavailable on this Windows host; captured by the Linux CI artifact |

This is a diagnostic baseline, not an acceptance threshold. In particular, the observed per-packet allocation must be attributed before claiming a zero-allocation worker design. CI builds and runs the same harness on Linux and uploads its raw JSON result.

## Next implementation gate

The next vertical must compare the synchronous baseline with bounded single-worker and session-ordered multi-worker preparation. It must copy only normalized immutable values into an owned intent, reject stale session generations, retain current-state validation on the game thread and demonstrate a latency/CPU win before the production hook changes.
