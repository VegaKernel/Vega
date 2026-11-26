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
Vega has not yet implemented complete native world materialization from
`.trschem`; the sandbox supervisor still launches native `.wld` instances.

Native `schematic place` is now available from the console of an isolated sandbox
with no connected clients. `plugin.World.Schematics.PlaceAsync` exposes the same
operation with encoded data. This first materializer supports tiles and NPCs,
including relative position, direction, name, home and life where supported by
the current native NPC type. It rejects schema documents containing chest, sign,
tile-entity or world-item sections rather than silently dropping records. Marker
and metadata values remain informational in the original document. Placement
requires an area without registered native objects or active entities, checks
the native slot capacity and tile field limits, and uses the normal native NPC
allocator. The primary world and occupied sandboxes reject placement. If native
allocation fails, the previous represented tile state and newly owned NPC slots
are rolled back; arbitrary native/plugin side effects are not transactional, so
a failed placement is not a ready world template.

The native Windows sandbox smoke placed a six-by-six schematic with one Guide,
then exported the canonical region again and verified its 36-tile dimensions, a
stone tile at the expected row, and the NPC's type, name, relative position
(24,16), direction and 50 life. The native allocator was used; the smoke did
not involve joined players. Fixture and raw files are in the local
`artifacts/placement-native-smoke-*` and `artifacts/schematic-placement-fixture`
directories. The managed regression separately injects an allocation failure
and verifies tile restoration and owned NPC retirement.

One file operation per world process is admitted at a time (import, export or
inspect), bounding simultaneous detached document and codec buffers. Full game
simulation and mutable native objects remain on the owning world thread.

## Next integration boundaries

Remaining native materialization work includes native containers/signs, tile
entities and items, automatic source-to-sandbox bootstrap and persistent world
qualification. PlayerBots are deferred by the operator.
