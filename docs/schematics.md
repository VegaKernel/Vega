# TerraRuntime schematics in Vega

Vega consumes the standalone `.trschem` v1 codec from TerraRuntime unchanged
([source snapshot](../src/TerraRuntime.Schematics/UPSTREAM.md)). The codec is
independent of the Terraria version and of the server's simulation. It stores
the source content version, row-major tiles, chests, signs, eight typed tile
entity kinds, NPCs, dropped world items, markers and metadata. Inspecting or
resaving a document preserves all these records. The codec is bounded and uses
the same section directory, integrity checks and format as TerraRuntime.
`TerraRuntime.Schematics.dll` is bundled with Vega's server and `Vega` SDK
package. Plugins can use `SchematicFile` / `SchematicBinary`; the optional
`SchematicSceneProjection.Create` helper produces a shared `SceneOverlayDefinition`.

## Operator commands

All commands require `vega.scenes.manage`. File names are resolved in the current
world process's `schematics/` directory, with an optional `.trschem` extension.
Only filename stems made of ASCII letters, digits, hyphens and underscores are
accepted. An isolated sandbox has its own working directory and schematic files.

```text
schematic export arena 100 200 80 60
schematic inspect arena
fake schematic arena-preview arena 300 400
pfake schematic private-preview Alice arena 300 400
fake remove arena-preview
pfake remove private-preview
schematic place arena 300 400
```

Coordinates in the commands identify the top-left tile (not the optional
schematic anchor). Export takes a bounded native snapshot on the authoritative
world thread, then validates, serializes and saves the detached data on a worker.
The existing native tile operation ceiling of 1,048,576 tiles applies to export.
Names, directions, relative positions, life and in-region homes of NPCs are
captured; an NPC whose home is outside the capture is represented as homeless.
Tile entity kinds not represented by v1 cause explicit export failure. V1 is not
a general native save format: transient NPC/AI state, display-doll pose and misc
slots, and native world progression are not represented. The file contains the
schema's fields, not a full clone of a `.wld`.

Preview loads and projects tiles, virtual chest contents and virtual sign text
off-thread; only publication of the provider occurs on the game thread. The
personal preview is bound to the selected connection session. NPCs, dropped
items and tile entities remain in the document and are **not spawned by a
preview**. The command reports their counts. In particular, the operator cannot
accidentally alter canonical tiles or spawn entities merely by previewing a file.
The sandbox supervisor can create a new managed `.wld` instance from a base
world and supported `.trschem` region in one operation; see `create-schematic`
in [sandbox lifecycle](sandboxes.md). A portable region still cannot represent
full-world metadata or replace the native `.wld` format.

Native `schematic place` is now available from the console of an isolated sandbox
with no connected clients. `plugin.World.Schematics.PlaceAsync` exposes the same
operation with encoded data. The materializer supports tiles, NPCs (relative
position, direction, name, home and life), native 40-slot chests, signs and all
eight typed v1 tile entities: training dummy, item frame, logic sensor, display
doll, weapons rack, hat rack, food platter and teleportation pylon. It also
materializes dropped world items through the native allocator for an idle
running sandbox. Each registered object requires a complete matching standard
18-pixel framed footprint within the document; unsupported frame layouts,
missing records, overlaps or invalid native inventory items are rejected.
Native chest/sign registrations and tile entity IDs are assigned by the actual
game APIs, not reconstructed from source-world slot IDs. Logic sensor record
type/state must match its native tile frame; the native subsequent simulation
may recalculate its state. Pylon types must be valid and unique across the
target world and document. Dropped items require vacant reusable native item
slots; native `.wld` files do not save dropped items, so automatic immutable
template creation refuses documents containing them before copying a base.
Unsupported native entity kinds still reject rather than silently losing records. Marker and metadata
values remain informational in the original document. Placement requires an
area without registered native objects or active entities, checks native slot
capacity and tile field limits, and uses the normal native NPC allocator. The
primary world and occupied sandboxes reject placement. If native allocation
fails, the previous represented tile state and newly owned registrations/NPC
slots are rolled back; arbitrary native/plugin side effects are not
transactional, so a failed placement is not a ready world template.

The native Windows sandbox smoke placed a six-by-six schematic with one Guide,
then exported the canonical region again and verified its 36-tile dimensions, a
stone tile at the expected row, and the NPC's type, name, relative position
(24,16), direction and 50 life. The native allocator was used; the smoke did
not involve joined players. Fixture and raw files are in the local
`artifacts/placement-native-smoke-*` and `artifacts/schematic-placement-fixture`
directories. The managed regression separately injects an allocation failure
and verifies tile restoration and owned NPC retirement. A separate headless
native fixture uses actual chest, sign, all eight tile-entity registries, pylon
system and world-item allocator, captures and round-trips their represented
data through the codec, and injects a later NPC allocation failure to verify
rollback of every newly registered object and the exact previous world-item
slot references/cooldowns.

The native `create-schematic` Windows integration smoke then built a separate
`.wld` from a 20x10 region, started its managed backend to signed readiness,
stopped it and reset it to the generated immutable template. Base-world and
schema hashes remained unchanged; the reset instance hash matched its template.
A separate native reload and canonical region export verified the named
chest with 12 torches, sign text, item frame contents and named Guide from the
fixture. This validates these persisted fields; transient native NPC state is
still subject to the ordinary `.wld` save format. Local raw evidence is under
`artifacts/bootstrap-isolated-477ee9ca/control-native-bb3302` and
`artifacts/bootstrap-isolated-477ee9ca/reloaded-native-baa2db`.

The expanded native persistence fixture repeats create/start/stop/reset and
canonical reload with a teleportation pylon at (12,5) and PlayerAbove logic
sensor at (16,8), both restored with their native registration/state along
with the chest, sign, item frame and NPC. Raw local evidence:
`artifacts/bootstrap-isolated-477ee9ca/control-entities-642f3b` and
`artifacts/bootstrap-isolated-477ee9ca/reloaded-entities-be6570`.

One file operation per world process is admitted at a time (import, export or
inspect), bounding simultaneous detached document and codec buffers. Full game
simulation and mutable native objects remain on the owning world thread.

## Next integration boundaries

TerraRuntime `.trschem` v1 has a forty-item chest limit and no chest-capacity
field. Vega refuses expanded/nonstandard native chest capacities on capture,
rather than truncating them; use native `.wld` for those worlds, or a future
versioned shared codec. Noncanonical object frame layouts not covered by the
verified native top-left/footprint checks are also rejected pending additional
native fixtures. Comprehensive persistent-world qualification remains open.
Dropped world items remain ephemeral under vanilla `.wld` persistence.
PlayerBots are deferred by the operator.
