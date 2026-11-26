# Shared schematic codec

The five C# source files in this project are taken unchanged from the sibling
TerraRuntime repository (`src/TerraRuntime.Schematics`), commit
`7e54ad9f0171c38daf383660e03e8539ab4a856e`. The assembly and namespace remain
`TerraRuntime.Schematics` so the binary format and API are identical. The project
is included in Vega's source tree to keep standalone builds reproducible without
a sibling checkout or a new package feed. When the shared codec is published as
an independent package, replace this source snapshot with that pinned package.

Vega-specific capture and scene projection live outside this project. Do not add
native Terraria, Vega runtime, or plugin dependencies to the codec.
