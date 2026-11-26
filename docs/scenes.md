# Observer-scoped scenes

Vega's scene runtime is the replacement boundary for the useful parts of TerraZ
FakeProvider. It composes immutable tile overlays for a `WorldId` and observer without
replacing `Terraria.Main.tile`. Terraria remains the owner of canonical world state.

Every scene is created through an `ISceneLease` with an explicit owner. Disposing the
lease removes all of its overlays and schedules restoration of the affected regions.
Overlays have stable creation order, an explicit numeric order and either a global or
personal observer scope. A later overlay wins when orders are equal; equal-order
overlaps are reported as conflicts in Operations instead of depending on hook order.

The bundled `Vega.Modules.Scenes` consumers cover the audited legacy use cases:

- WorldEdit clipboard and mask previews can be moved or resized by updating existing
  overlay identities and are cleared with the player/session-owned lease;
- Region-Vision uses a personal border overlay, while inbound interaction with its
  phantom tiles is rejected by the same observer decision;
- lobby visuals support global and personal immutable UI/animation frames;
- FakeProviderClustering owns player, NPC and projectile visibility leases.

Outbound tile squares are composed per recipient. Native section delivery is followed
by bounded overlay redraws. Entity packets use the same visibility layer for targeted
and broadcast delivery, and incoming NPC/projectile interactions with hidden entities
are rejected through typed Multiplicity views. The redraw queue is bounded to 256
regions and packets to at most 46x46 tiles (worst-case encoded size fits the signed packet length). Adapter failures, dirty regions, owners,
conflicts and visibility layers are visible in Operations/TUI.

## Performance baseline

Run `dotnet run --project benchmarks/Vega.Scene.Benchmarks -c Release`. On the x64
.NET 10.0.11 development host, the 64-overlay synthetic baseline measured:

| Workload | Result | Managed allocation |
| --- | ---: | ---: |
| lookup among 64 overlays, 250,000 operations | 1.18 us/op | 56 B total |
| compose a 64x64 region | 2.14 ms total | 56 B total |
| replace 200 observers, 1,000 times | 27.1 us/op | 13.0 MB total |
| create 1,000 personal 32x32 overlays sharing immutable tile data | 2.69 us/op | 1.36 MB total |

These are regression baselines, not hardware-independent guarantees. The harness now
also executes WorldEdit preview movement with a mask and a 200-observer/300-entity lobby
composition churn workload; CI runs it as a regression gate and reports elapsed time plus
managed allocation for every workload. Socket throughput under real clients remains a
deployment-qualification measurement rather than a scene-runtime implementation gate.
The hot tile lookup performs no per-call LINQ materialization. Monitoring takes sampled
immutable snapshots and never performs per-tick cross-process or full-world work.

Single-process multi-world virtualization remains deliberately outside this API. Phase
17 gives each live world a process; a future in-process experiment must prove isolation
of Terraria globals before it can change that decision.

## Built-in FakeProvider facade

`Vega.Modules.Scenes.FakeProviderService` provides named, world-local global and
personal tile providers using the same scene leases and visibility rules. This is a
native Vega port, **not** a binary reference to the legacy `TZ.TShockAPI` plugin.
Provider names are unique within the owning world (case-insensitive). The module owns
leases and clears personal observers on native session closing/disconnect, including
slot reuse; disposing the module restores canonical tiles. The commands are available
when the Scenes module is enabled and require `vega.scenes.manage`:

```text
fake create <name> <x> <y> <width> <height> [air|stone]
pfake create <name> <online-player> <x> <y> <width> <height> [air|stone]
fake|pfake list [page]
fake|pfake info <name>
fake|pfake move <name> <dx> <dy>
fake|pfake layer <name> <layer>
pfake air <name> on|off
fake|pfake enable|disable|remove <name>
```

The command fill defaults to air; width/height are bounded to 255 for operator creation.
The module API additionally accepts immutable `SceneTileArea` data, updates/replaces
contents and bounds (`ReplaceTiles`), changes observers and layer, and removes providers.
Plugins can continue to create their own scoped providers with `context.World.Scenes`
(scene leases are tied to plugin ownership). These are **visual, observer-scoped layers**;
server-side world interaction on phantom tiles is still denied. They are not extra
physics worlds, native virtual chest inventories/tile entities, nor binary compatibility with the
legacy FakeProvider API. Independent simulation remains process-isolated.

### Logical virtual chests for plugins

`IPluginTileProvider.CreateVirtualChest(localX, localY, name, items)` attaches a
server-owned logical container to a 2x2 footprint inside an existing provider.
`IPluginFakeProvider.SupportsVirtualChests` advertises the capability. The caller
supplies the visual chest tiles; the container moves with its provider and uses
the same world, layer order, enabled state, air-mask rules and observer membership.
All four footprint tiles must be active and visible from the owning provider.
A higher overlay covering even one corner suppresses the interaction.

The handle exposes an immutable `Definition` (40 logical slots), revision-checked
`TryReplace(expectedRevision, name, items)`, `TryTakeOpenRequest`, and `Dispose`.
Content changes do not enqueue tile redraws. The immutable scene lookup is lazily
republished after mutations; the first implementation scans scene owners only on
container interaction, not for every resolved tile. Moving/removing/disabling the
provider, disconnecting or replacing a session prevents stale opens from reaching
another connection. The runtime retains only the latest open intent per active
player, expires it after five seconds, and revalidates visibility and session when
consumed. No plugin callback is executed directly in the native packet path.
An open intent is recorded only for a living, exact-session observer within
eight tiles of the container; this is a conservative logical interaction range.

The ordinary chest-open interaction is **observed, not forwarded to a canonical
chest**. This API does not assign native chest indices, open the vanilla inventory
UI, transfer items, or persist data in `.wld`; logical item counts are not native
item validation. Plugins can implement their own read-only display/actions and
persist data using their storage API. A native inventory interface will require a
separate authoritative transaction design and real-client qualification, particularly
with SSC. See `samples/Vega.PluginSamples/VirtualChestPreviewCommand.cs` (`samplechest`).

### Logical virtual signs

`IPluginFakeProvider.SupportsVirtualSigns` advertises support for
`IPluginTileProvider.CreateVirtualSign(localX, localY, text)`. A sign owns a
2x2 local footprint and immutable text (up to 1024 characters) with a revision.
`IPluginVirtualSign.TrySetText(expectedRevision, text)` replaces that text;
`TryTakeReadRequest` returns the latest, exact-session read intent. The same
visibility, active-footprint, world, reach, expiry and unload rules apply as for
logical chests. A provider supports up to 256 combined virtual objects; their
footprints cannot overlap. Chests and signs share one optional metadata reference
on the overlay, so ordinary tile-only providers do not gain a field for every kind.

The ordinary sign-read interaction is observed by the plugin, and sign reads/edits
in virtual terrain do not reach canonical sign state. Existing signs outside an
overlay remain native. Text is owned by the plugin, not written to the `.wld` or
assigned a native sign index. This is not the vanilla sign editor or a native
tile-entity implementation. See `samples/Vega.PluginSamples/VirtualSignPreviewCommand.cs`
(`samplesign`), which displays text through chat from a tracked game-thread task.
Both virtual-object samples use `plugin.Players.Message(playerSnapshot, text)`;
the snapshot overload checks the observed session/world and preserves the exact
native target when dispatching the tracked message operation. Hosts without an
explicit session-bound messenger capability return unavailable rather than
falling back to a reusable numeric slot.

Personal providers can enable `FillOutsideWithAir` with `pfake air <name> on`,
`IPluginFakeProvider.CreatePersonal(..., fillOutsideWithAir: true)`, or
`IPluginTileProvider.SetFillOutsideWithAir(true)`. With at least one enabled mask
for a player, only their personal overlay layers are composed against air; global
overlays and canonical tiles remain visible to other players and unchanged in world
storage. Activation, removal, disable and observer changes repaint only that
connection's already loaded native sections; newly transmitted sections are repainted
after the native send. Moving an active provider retains rectangle-only redraws.
This is an asynchronous visual background, not a confidentiality boundary: canonical
section data is still transmitted and the native world remains the physics authority.
With any personal air background active, broadcast tile-square updates may need
per-recipient composition even outside provider rectangles. Loaded-section repaint
latency and this broadcast workload still require measurement with real clients.

The tile resolver now lazily publishes a world-keyed immutable, priority-sorted
projection after mutations. Steady-state tile resolution does not acquire the scene
mutation lock and stops on the first applicable layer. Observer sets are frozen when
stored. This avoids FakeProvider's full dimensions-sized provider index and global
lock in the common read path, while batched updates only rebuild on the next read.

The native tile-square renderer uses a batch-local, one-chunk cache. Each recipient's
tiles and frame-importance decisions are independently resolved; only an exact match
with the preceding composition reuses its encoded packet. This is intentionally not
a cache keyed by a "global" scope, since concurrent personal-layer changes can change
what a particular player observes. The same per-recipient chunk order is maintained.
The renderer rechecks the socket reference after resolving, never shares scratch
buffers with network I/O, and passes immutable exact-length packets to native TCP
and Ordered TCP. Other socket implementations receive private copies. Composition
and serialization scratch rentals are returned after each batch.

Large redraws now yield through the cooperative game-thread dispatcher: one batch
covers up to 46x46 tiles for at most four recipients, and one dispatcher step handles
at most four batches with a soft 1 ms elapsed-time budget. Only the next batch is
enumerated; the queue's 256-region limit includes the active request. The renderer
captures a recipient's native socket when starting the request and checks it again
before and after composition, so a resumed request cannot target a replacement
connection. This bounds work per dispatcher step, not end-to-end redraw latency or
an absolute tick duration. Cache reuse is limited to a batch rather than all
recipients, deliberately trading some repeated encoding for bounded work and fresh
composition across ticks. Ordinary native tile squares outside applicable overlays
are not intercepted; section refreshes redraw only their intersection with an enabled
overlay, for recipients within its observer scope. Unfinished redraws return to the
tail after each batch, allowing smaller updates to progress before a large refresh
finishes. Each batch composes current state and retains its original socket identities.
Real-client redraw latency under load remains to be measured.
Small region probes use the sparse chunk index; broad rectangles use the immutable
flat projection. The section hook enumerates enabled intersecting definitions from
that projection without allocating diagnostic scene snapshots or calculating conflict
counts. Direct recipients reuse the immutable one-slot observer scope.
If the dirty-region queue or game-thread dispatcher refuses admission, the original
native tile-square send is retained rather than canceled. A rejected reservation
does not remain in an unscheduled queue; the visual layer may temporarily lag the
vanilla state and its next successful redraw restores the composed tiles.

A local .NET 11 Release A/B composed and encoded 24 repeated 64x64 regions using the
previously built scene runtime, the original encoder, and the new cache. Payloads were
byte-compared against the original encoder for every observer. At 32 recipients with
identical global composition, encoding/composition took 6.91–6.92 ms per region before
and 4.03–4.06 ms afterward, with measured managed allocations falling from 2,889,472 to
24,784 bytes per region. With a distinct personal tile for each of 32 recipients,
allocations fell to 791,104 bytes; time was 11.89–11.90 ms versus 12.71–14.67 ms before.
One-recipient time was similar (about 1.3 ms) with fewer allocations. Eight-recipient
global time was unstable across first and last runs, so no timing improvement is
claimed for that workload. Raw output: `../artifacts/performance-2026-11-26/scene-render-results.txt`.
These are composition/packet-encoding timings, not live-client network or gameplay TPS.
For at least eight overlays, a bounded sparse 64x64 chunk index narrows the candidates, with the priority-ordered list as fallback when coverage is oversized. The final A/B on 64 overlays measured 1406–1432 → 294–322 ns/lookup, 624–634 → 350–372 ns/tile for a 64x64 composition. Large observer-set mutation is slower; see [performance report](performance-2026-11-26.md).
