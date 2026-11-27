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
physics worlds, unrestricted native virtual chest UI/full tile-entity simulation, nor binary compatibility with the
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
chest**. The plugin chooses whether to display, accept or exchange a stack.
`SupportsVirtualChestTransfers` advertises explicit server-owned inventory transfers.
The virtual chest's `Transfer` method takes the open intent, expected native
inventory revision, chest slot, main inventory slot (0–49), amount and direction.
Call it on the game thread and with authoritative native SSC enabled. For example,
after receiving `open` and capturing a native `inventory` snapshot for its exact
session inside the game-thread callback:

```csharp
var result = chest.Transfer(new SceneVirtualChestTransferRequest(open,
    inventory.Revision, ChestSlot: 0, InventorySlot: 5, Amount: 1,
    Direction: VirtualChestTransferDirection.Withdraw));
if (result.Status == VirtualChestTransferStatus.Applied)
    plugin.Log.Write(OperationsLogLevel.Information, "Transferred one item.");
```

The transaction rechecks the current visible chest definition, revision, both
inventories' stack compatibility, native max stack/prefix, live session and reach.
It publishes both state changes together before syncing the native inventory slot.
Deposits of favorited items are rejected. The result's `Applied` status is final,
including when the subsequent inventory notification failed (`Error` is set).
Only standard item type, stack and prefix are represented in logical chests.
The explicit transfer API does not require opening the vanilla inventory UI or
registering a native chest. Plugins own policies and storage; logical contents
are not persisted in `.wld`.

#### Managed native chest GUI (opt-in minigame sessions)

`SupportsManagedVirtualChestGui` / `TryPresentManagedGui(open)` sends the stock
40-slot chest snapshot and opening response to the exact observer, without
registering anything in the native chest table or changing server `player.chest`.
Native chest metadata initializes the client-only entry before slot data; aliases
are coordinate-bound and capped at 32 per connection. Presentation is refused
over a canonical chest position or occupied native index.
It requires authoritative SSC and a complete native 2×2 chest-shaped footprint.
The GUI is deliberately **not** an unrestricted vanilla inventory mode: opting in
makes that exact connection's client-driven inventory and canonical chest/item
operations server-managed until disconnect/reconnect, even after the GUI closes.
This avoids releasing a speculative late local inventory update into ordinary
native character state. Use it only for plugin-owned inventory/minigame sessions.

Only a balanced movement between one chest slot and one compatible main inventory
or mouse slot is observed as a transfer intent. The plugin polls
`TryTakeManagedGuiTransfer`, then calls `AcceptManagedGuiTransfer` (atomic
revision-checked native assignment) or `RejectManagedGuiTransfer` (close and
resynchronize). Unmatched, unsupported, stale, timed-out, hidden or out-of-reach
operations close the GUI and correct authoritative inventory; no implicit
canonical mutation occurs. Multi-slot sorting, quick-stack, swaps and renaming
are intentionally not supported. A second viewer of a stale chest revision will
close rather than commit against outdated contents.

`TryCloseManagedGui(observer)` closes this plugin's GUI but does not return the
session to unrestricted client-written inventory. The sample `samplechest`
defaults to chat-only display; `--managed-gui` explicitly enables the constrained
native mode and accepts balanced transfers for its personal demo chest.
Headless native decoder and domain/native transaction fixtures qualify server
framing, item conservation and exact-session state; stock graphical drag timing
and unrestricted client inventory compatibility remain unqualified.

### Logical virtual signs

`IPluginFakeProvider.SupportsVirtualSigns` advertises support for
`IPluginTileProvider.CreateVirtualSign(localX, localY, text)`. A sign owns a
2x2 local footprint and immutable text (up to 1024 characters) with a revision.
`IPluginVirtualSign.TrySetText(expectedRevision, text)` replaces that text;
`TryTakeReadRequest` returns the latest, exact-session read intent. The same
visibility, active-footprint, world, reach, expiry and unload rules apply as for
logical chests. A provider supports up to 256 combined virtual objects; their
footprints cannot overlap. Chests, signs and display entities share one optional metadata reference
on the overlay, so ordinary tile-only providers do not gain a field for every kind.

The ordinary sign-read interaction is observed by the plugin, and sign reads/edits
in virtual terrain do not reach canonical sign state. Existing signs outside an
overlay remain native. Text is owned by the plugin, not written to the `.wld` or
registered into the native server sign table. `SupportsVirtualSignEdits` advertises the opt-in
`CreateVirtualSign(..., allowEdits: true)` overload. A sign read opens a bounded
two-minute edit observation window for that exact sign and connection; one
matching text edit produces a five-second `TryTakeEditRequest` intent. The plugin
may authorize it by calling `TryAcceptEdit(request)`, which rechecks the live
session, world, reach, visibility and original immutable revision before publishing
text. Read-only signs remain the default; text never mutates in the packet path.
`SupportsVirtualSignUi` and `TryPresentRead(request)` additionally send a stock
native sign-opening response from the game thread for the exact live observer,
without registering a canonical sign. The presentation requires a complete native
sign-shaped footprint. The bounded per-session index binding survives provider
movement/removal so a stale virtual edit cannot become a canonical edit; an actual
canonical read/response may reclaim the index. `TryRefreshEdit(request)` sends the
current logical text without reopening the window (e.g. after rejection).
`TryAcceptEdit` also attempts this refresh after committing the new revision.
The stock sign protocol permits local editing even on read-only presentations;
the server ignores it and a subsequent read restores authoritative text. Native
metadata decoding and the opening-response framing are tested headlessly;
graphical editor interaction still requires stock-client qualification.
`samples/Vega.PluginSamples/VirtualSignPreviewCommand.cs` (`samplesign`) presents
the native response when available with chat fallback; `--edit` explicitly opts
the sample personal sign into accepting its observer's edits.
Both virtual-object samples use `plugin.Players.Message(playerSnapshot, text)`;
the snapshot overload checks the observed session/world and preserves the exact
native target when dispatching the tracked message operation. Hosts without an
explicit session-bound messenger capability return unavailable rather than
falling back to a reusable numeric slot.

### Read-only virtual display furniture

`SupportsVirtualItemFrames` advertises `CreateVirtualItemFrame(localX, localY,
itemType, stack, prefix)` and a revisioned `IPluginVirtualItemFrame.TryReplace`.
The caller supplies the actual 2×2 native item-frame tiles (tile type 395, complete
18-pixel frames). Item updates schedule a redraw; the SDK lease follows provider
movement, observer visibility, and plugin unload. Portable `.trschem` item-frame
records are imported as the same non-canonical metadata by `/fake schematic`
and `/pfake schematic`. `SupportsVirtualDisplays` and `CreateVirtualDisplay(kind,
localX, localY, items, dyes)` additionally support weapon racks, mannequins, hat
racks and food platters, with kind-specific native footprints and capacities.
These kinds also import from `.trschem`, while unsupported tile entities remain
in the source document. A display's `TryReplace` uses its current revision.
Items/dyes are detached slot-ordered lists (one item for racks/platters, up to
nine equip/dye entries for mannequins, up to two each for hat racks). The native
adapter validates supported item IDs, stack limits and prefixes before sending a
display; incompatible metadata is not projected into a native entity.

After redrawing the tiles, the native renderer sends standard tile-entity metadata
to the exact observing connection. It reserves IDs via the native allocator but
does not insert entities into server `TileEntity.ByID` or `ByPosition`. The
presentation is bounded to 256 active display entities (including frames) per observer; hidden/removed displays
receive a removal packet when the affected tiles are redrawn. A canonical tile
entity already at that position suppresses the virtual projection; a canonical
collision detected during redraw removes the virtual ID and restores canonical
metadata. Ordinary placement into phantom displays and interactions with canonical
tile entities hidden by overlays remain blocked. This is read-only presentation,
not native inventory transfer. There is no graphical client UI qualification,
independent simulation, or guarantee of confidentiality of canonical section data.
Pylons, sensors and unrestricted tile-entity inventories remain outside this scope.

#### Managed placement into an empty frame or weapon rack

`SupportsManagedDisplayInteractions` advertises an explicit one-shot interaction
for item frames and weapon racks. On the game thread, call
`frame.TryArmManagedInteraction(playerSnapshot)` (or the weapon rack handle's
same method). This requires authoritative SSC, an exact living session, complete
visible reachable footprint, and no canonical tile-entity registration at the
position. Arming activates the same server-managed inventory scope as the managed
chest GUI, **until disconnect/reconnect**; do not use it for unrestricted vanilla
inventory sessions.

The normal placement observation and one compatible, conserved inventory-slot
decrease produce a detached `SceneManagedDisplayTransferIntent`. Observe either
order of the two changes; neither mutates canonical state. The plugin polls
`TryTakeManagedTransfer`, then calls `AcceptManagedTransfer(intent)` to atomically
debit the native authoritative inventory and replace the provider's exact
display revision, or `RejectManagedTransfer(intent)` to correct the client.
`CancelManagedInteraction(playerSnapshot)` cancels only this handle's arm.

Only deposits into an **empty** display are supported. Item frames can hold a
valid native item stack; a managed weapon rack accepts one damaging native item.
No swapping, occupied-display replacement, automatic withdrawal, mannequin or
hat-rack inventory interactions are enabled by this API. A missing/mismatched
half, stale revision/session, moved/hidden target, or timeout cancels the arm and
resynchronizes inventory and the observer's display. A committed result remains
applied even if subsequent visual refresh fails (`Error` is populated).
The native decoder and local transaction fixture qualify resulting metadata and
item conservation; actual graphical placement timing remains unqualified.

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

The shared TerraRuntime `.trschem` codec now supports native region export and
global or session-bound personal FakeProvider previews. See
[schematics](schematics.md) for commands, object support and native materialization limits.
