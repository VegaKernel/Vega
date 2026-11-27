# Paged native tile storage

Vega uses lazy pages by default for the canonical Terraria tile **reference grid**, while keeping every native `ITile` object and its behavior unchanged.

The option is applied at startup and enabled by default:

```json
{
  "pagedTileStorageEnabled": true
}
```

Set it in `config/server.json`, or override that deployment default with `VEGA_PAGED_TILES=on` or `--vega-paged-tiles=on` on the worker command line. Use `off` to retain the stock collection. Primary and sandbox processes each own their own canonical collection. Vega installs the factory before native `Main.tile` initialization and rejects startup with paging enabled if the expected collection cannot be selected; it never replaces an already loaded world.

## Why pages instead of replacing tiles

Terraria 1.4.6.1 initially creates `Main.tile` in `Main::.cctor()` with a maximum grid of 8401 × 2401 references. `WorldGen.clearWorld` clears prior cells to null and creates or clears native objects only in the current active dimensions. The default collection allocates the entire rectangular reference array on its first access, including the unused margin of small and medium worlds.

`PagedNativeTileCollection` stores actual `ITile` references in 64 × 64 lazily allocated pages. Null reads and null writes into an unallocated page do not allocate that page. Page creation uses an atomic publish operation so simultaneous initial writes cannot lose one another. Width and height stay exactly those supplied by the native factory. Assignment is reference assignment, not a copy; aliased references and detached tiles remain valid, and native `CopyFrom`, `Clone`, clearing, coating, liquid and other tile methods are unchanged.

Pages are retained for the lifetime of the collection once allocated. Clearing a cell still releases its tile reference; a later smaller world does not automatically reclaim previously allocated empty reference pages. This avoids a concurrent page-retirement race. Fresh isolated workers benefit from the missing margins. The existing world-image loader retains its conservative serial tile-write path for custom collections; it does not replace the canonical tile grid.

## Packed tile limitation

The older FakeProvider `StructTile` packs the raw nine tile fields into 14 bytes, which remains the payload size for modern native tiles. Its `TileProvider` returns a new coordinate-based `TileReference` for each read and copies fields on assignment. That is suitable for a deliberately value-oriented overlay, but it is not equivalent to the canonical collection: repeated reads return different objects, null and empty tile are conflated, assigning the same object to two coordinates does not create a native alias, and `MemberwiseClone` of a coordinate wrapper remains attached to its backing coordinates. The older implementation also has an explicit `ITile.sTileHeader` setter that discards writes. Copying this behavior into native storage would be incorrect.

Making coordinate wrappers permanent would require a reference identity table and an object per materialized tile, cancelling much of the compact-payload saving. A packed value engine could use a distinct, explicitly value-based access API, but is not a drop-in `ICollection<ITile>` implementation. Native `Tile` object storage is intentionally not changed by the paged backend.

## Verification

The isolated `Vega.TileStorage.Benchmarks` project exercises the actual native construction hook without loading or saving any world. Run each variant in a separate process:

```powershell
dotnet run --project benchmarks/Vega.TileStorage.Benchmarks -c Release -- default small
dotnet run --project benchmarks/Vega.TileStorage.Benchmarks -c Release -- paged small
dotnet run --project benchmarks/Vega.TileStorage.Benchmarks -c Release -- default medium
dotnet run --project benchmarks/Vega.TileStorage.Benchmarks -c Release -- paged medium
```

The synthetic reference-map workload deliberately assigns the same tile object to every occupied cell, isolating reference-grid allocation and access from the unchanged per-tile object storage. It measures native `Tile` and coordinate-wrapper object allocation separately. Production evaluation also needs disposable native world load/save/reload and ordinary gameplay measurements; synthetic grid results alone do not establish a gameplay TPS improvement.

On Windows x64, with TerrariaRuntime 1.1.5 and the same Vega Release build, the disposable-world A/B gave:

| Metric | Small default | Small paged | Medium default | Medium paged |
|---|---:|---:|---:|---:|
| Reference-map allocation (synthetic, MB) | 161.37 | 41.12 | 161.37 | 95.10 |
| Idle server working set (MiB) | 559.82 | 447.69 | 767.19 | 707.79 |
| Full active scan (synthetic, ms) | 30.97 | 56.49 | 69.52 | 129.81 |

The native servers saved both fixtures successfully; the resulting worlds and rolling backups had identical tile and entity bytes, and the stock collection reloaded both saved worlds. Only native timestamp and cloud-state header bytes varied between the independent processes. The paged synthetic scan was about 1.8 times slower, and native save throughput did not improve. Paging is therefore an optional memory tradeoff, particularly for small isolated worlds, not a universal speed improvement.

### Small world: heap and network load

On Windows x64 with Terraria 1.4.6.1, both modes ran the same small world with the stock TCP transport. Separate GC heap captures before players joined showed one 161,366,448-byte rectangular tile-reference array with the default grid, versus about 41.2 MB of page arrays and page directory with paging. Both retained 5,040,000 native `Terraria.Tile` objects. Reported managed heap size fell from 484 to 369 MiB. The private commit on an idle empty server fell from 534 to 417 MiB (official Terraria 1.4.6.1: 407 MiB on the same world, using a different .NET runtime).

| Full workload (one 12-second phase per mode) | Default grid | Paged grid |
| --- | ---: | ---: |
| Working set (MiB) | 641.3 | 530.0 |
| CPU (process-seconds) | 13.56 | 13.03 |
| Movement p99 (ms) | 28.55 | 28.63 |
| Chat p99 (ms) | 64.57 | 59.83 |

All 64 simulated clients remained connected during movement, shots, chat, and the two-boss phase in both runs. These were loopback clients, not full gameplay clients. One run per mode does not establish a CPU or latency improvement. Large-world and real-client workloads require separate validation.
