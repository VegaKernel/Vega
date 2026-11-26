# Packet pipeline

Phase 22 reduces authoritative game-thread packet cost only when preparation is pure, ordered and based on owned data. The production ingress remains synchronous in this first slice; classification and measurement precede any worker queue.

```text
network/worker threads = pure preparation of owned packet intents
game thread             = current-state authority, plugin gameplay hooks and mutation
```

## Current boundary

`NativePlayerPacketSecurityHooks` receives the bounded Terraria buffer window. `MultiplicityPacketProcessing` creates a stack-only `PacketView` over that window and synchronously invokes the current policy pipeline before the receive-buffer lifetime ends. No `IncomingPacket`, span, packet view or raw receive-buffer reference is retained.

`PacketPipelineClassification` is internal to `Vega.Terraria`. Ordinary contracts and plugin APIs do not expose lanes, queues, channels, buffer ownership or thread switching. Every `PacketTypes` value receives a conservative profile: packet types not explicitly owned by the Vega security path are `GameThreadOnly`, order-sensitive, world-state-sensitive and not coalescable.

TerrariaRuntime 1.1.2 provides a dedicated TCP listener factory, full packet-pass override and narrower frame/admission budget hooks. Vega currently installs only the narrow budgets (`config/network.json`) and leaves the complete native packet pass intact. The limits bound pending connections and frames per slot, not total packet CPU time or outbound bytes. The game-thread dispatcher runs cooperative work at most once per tick before yielding. This is a bounded scheduling baseline, not evidence of a throughput improvement.

For the experimental opt-in native socket adapter, see [native transport design](native-transport-design.md). Queue ownership is at the accepted socket because some native/Vega sends bypass `SendBytes`; receive completion on close and generation-bound send callbacks are compatibility-critical. Vanilla TCP remains the default.

The ordinary metadata observation path reuses the immutable allow decision and does not format a socket endpoint on every packet. Endpoint formatting remains on the forwarding metadata, admission and rejection paths. Player session snapshots retain the transport endpoint only for that connection generation; effective forwarded identity and login phase still come from the metadata registry. Unchanged metadata records are reused until a field changes, without mutating already published snapshots.

### Hook validation baseline (Terraria 1.4.6.1)

| Hook boundary | Current verification |
| --- | --- |
| TCP listener factory, complete packet pass, connection admission and frame budget | TerrariaRuntime launcher smoke exercises overrides, fallback and deferred frames. |
| Connection activation/close | Launcher exercises real `RemoteClient.Activate` and `Reset` calls and verifies notifications. |
| Dedicated world ready, native game thread, console commands | Vega deployment smoke loads/generates `.wld`, waits for `NetworkReady`, executes `time` and `wind`, then saves and shuts down. |
| Packet receive/send, scene, chest, world lifecycle and save metrics | Vega compiles against the generated hook ABI; existing typed policy tests cover owned paths. Full real-client exercise remains pending. |
| NPC/projectile authority, tile block override, replacement socket and packet pass | Hook API available; not installed as Vega replacements yet. |

Use the actual release package and stock 1.4.6.1 clients for future compatibility/throughput checks. No full gameplay or protocol parity is implied by a successful hook assembly build.

### Next runtime interception points, in dependency order

| Boundary | Intended improvement | Ownership / rollout gate |
| --- | --- | --- |
| `ServerConnectionActivated`, `ServerConnectionClosed` | Native socket-generation cleanup instead of waiting for player snapshot polling | Installed; registry metadata is removed only for matching socket generation. |
| `DedicatedWorldStopping` | Stop admitting/routes to a world while it is saving and shutting down | Installed; readiness is withdrawn and scheduled cleanup stops. |
| `ServerConnectionAdmission` | Cheap pre-slot transport policies before queueing; separate raw peer admission from forwarded identity | Exported, not enabled as a replacement; validate direct/proxy clients and admission accounting. |
| `ServerTcpListenerFactory`, `SendBytes` | Bounded async socket queues, backpressure, per-connection ordered completions, slow-peer isolation | Port transport mechanics from TerraRuntime; benchmark against native TCP before enabling. Socket workers do not mutate gameplay state. |
| `ServerPacketPass` | Fair per-tick scheduling across connection slots, phase telemetry | Preserve vanilla connection updates, input and packet order. Replace only after differential testing; frame budget remains active today. |
| `WriteServerTileBlock` | Immutable section snapshots for measured compression/cache experiments | World thread captures owned section data; workers only compress immutable data. Invalidate on tile/world revision change. |
| NPC and projectile lifecycle hooks | Native entity-generation tracking and measured gameplay-administration operations | Keep NPC simulation and all mutable entity state on the world thread. Do not parallelize mutable AI or replace vanilla gameplay. |
| Dedicated save lifecycle | Coalesced saves, durable per-world readiness and backup handoff | Keep the canonical `.wld` serialization native; snapshot/backup publication is separate. |

World isolation is process isolation, not a set of per-world static fields in one assembly load context. Each worker has its own Terraria globals, port, save directory and plugin state. Only account/ban storage, routing and signed transfer admission are deliberately shared. The durable world-file UUID is distinct from the routing `WorldId` and keys world-local warp persistence; an instance clone has its own runtime data root even if the source world UUID is identical.

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

### Receive-window allocation regression (1.4.6.1 integration)

The variable-length Multiplicity parser's span overload materializes a copy of its source before deserializing. Vega now calls the `ReadOnlyMemory<byte>` overload with only the bounded receive window in the common ingress, chat and chest hooks. This removes a copy proportional to the backing receive-buffer size without changing the ordering of packet policies. Multiplicity still creates a reader and decoded packet model for variable-length packets, so the path is not allocation-free.

Run the realistic backing-buffer variant using `dotnet run --project benchmarks/Vega.PacketPipeline.Benchmarks -c Release -- --receive-buffer`. On .NET 11.0.0 Windows x64, one diagnostic run processed 1,024,000 ordinary `PlayerUpdate` payloads (14-byte payload windows in 128 KiB backing buffers) for 200 sessions at about 2.08 million packets/s, 192.03 bytes allocated per packet, and synthetic tick p95/p99 of 482/508 microseconds. The preceding run of the small-buffer legacy path allocated 232.03 bytes per packet. The runs were not isolated latency benchmarks and do not represent complete server throughput; the important result is that allocation is no longer proportional to the backing buffer size.

## Next implementation gate

The next vertical must compare the synchronous baseline with bounded single-worker and session-ordered multi-worker preparation. It must copy only normalized immutable values into an owned intent, reject stale session generations, retain current-state validation on the game thread and demonstrate a latency/CPU win before the production hook changes.

## Protocol 329 migration

The pinned native TerrariaRuntime 1.1.5 assembly contains `Terraria.Main.curRelease = 329`. Multiplicity 2.6.2 was the older protocol 325 codec; the current 4.0.x codec targets 329 and has a native net11 asset. Vega now uses it throughout Core, Terraria, Regions, Restrictions, PvP, package and advanced packet sample. Regression checks compare the protocol constant directly with the native runtime. Native PlayerPlatformInfo is parsed with the current typed slot/platform/user-id/optional Nintendo-name/host schema without trusting the reported identity. QuickStackChests is direction-dependent and checked as an inventory-slot list; width/height custom item overrides are no longer sent or applied because the protocol removed these fields. All existing packet authority checks stay ordered on the game thread.
Multiplicity 4.0.1 removes reader/stream/model allocations for the canonical PlayerUpdate deserializer while preserving the custom-deserializer fallback. A/B current Vega ownership + metadata path: 475–492 → 335–349 ns/packet, 192.032 → 0.032 B/packet, Gen0 11 → 0. This substantially weakens the case for offloading this inexpensive packet alone; compare an immutable-intent worker design against the new synchronous baseline before changing ownership/order. See [performance measurements](performance-2026-11-26.md).