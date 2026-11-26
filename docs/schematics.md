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

One file operation per world process is admitted at a time (import, export or
inspect), bounding simultaneous detached document and codec buffers. Full game
simulation and mutable native objects remain on the owning world thread.

## Next integration boundaries

Native materialization of a complete schematic into a new isolated sandbox
requires placement of tiles, native containers/signs, tile entities, NPCs and
items, with version/geometry/occupancy checks and safe persistence. A normal
PlayerBot must be a server-owned player under the native lifetime and world
authority, not a second simulated player store, NPC substitute, or bypass of
session admission. TerraRuntime's bot policy and navigation can be ported after
that native actor boundary is implemented and verified.
