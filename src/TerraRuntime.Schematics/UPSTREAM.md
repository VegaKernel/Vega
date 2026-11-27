# Shared schematic codec

The five C# source files in this project originate from the sibling
TerraRuntime repository (`src/TerraRuntime.Schematics`), commit
`7e54ad9f0171c38daf383660e03e8539ab4a856e`. The assembly and namespace remain
`TerraRuntime.Schematics`. Vega extends the source snapshot with a backwards-
compatible v2 chest record: after the UTF-8 name, a little-endian Int32
capacity (40–200) precedes the original counted item array. Standard 40-slot
documents are still written in exactly the original v1 representation; readers
accept either version. Older TerraRuntime readers will reject v2 files, so
expanded chest compatibility requires an equivalent codec update upstream.
The project
is included in Vega's source tree to keep standalone builds reproducible without
a sibling checkout or a new package feed. When the shared codec is published as
an independent package, replace this source snapshot with that pinned package.

Vega-specific capture and scene projection live outside this project. Do not add
native Terraria, Vega runtime, or plugin dependencies to the codec.
