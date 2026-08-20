# Vega World Image

Terraria `.wld` remains Vega's canonical persistence and recovery format. A `.vworld` file is an optional, disposable runtime cache beside the source world. It is never the only copy of world state and is never a save target.

## Vega 1.0 policy

World images are disabled by default because the first representative benchmark set did not show a reliable startup improvement. Enable the evaluated implementation explicitly:

```text
Vega.Server --vega-world-image=on -world path/to/world.wld
```

Or select an existing image directly:

```text
Vega.Server -world path/to/world.vworld
```

Direct `.vworld` selection enables image mode automatically. Before Terraria starts,
Vega resolves the companion `.wld` as the canonical save/recovery path; the validated
image remains the actual load source. The companion `.wld` must remain beside the image.

Use `--vega-world-image=off` to state the default policy explicitly. Vega removes this option before passing arguments to Terraria.

On the first enabled load Vega uses `.wld` and builds a file-derived `world.vworld` in the background. A completed local save upgrades that cache to a runtime snapshot containing the exact tile state and liquid work queues observed after the canonical save. A later load reads the embedded source section and optimized runtime sections from `.vworld`, but only after validating the source content fingerprint, Terraria format version, Vega schema, compiler parameters, manifest and section checksums. A stale or incompatible image is rebuilt after normal startup. A corrupt image is moved to `world.vworld.corrupt`; startup continues from `.wld`.

Terraria's normal `save`, shutdown save, and configurable autosave all write the authoritative `.wld`. Immediately after every completed local save, Vega captures immutable tile and liquid state while the save caller still owns a consistent world view. Hashing, image I/O, validation and atomic publication then run through one coalesced background build queue. Concurrent save requests cannot run competing `.vworld.tmp` writers, and shutdown waits for the pending rebuild. The snapshot is bound to the exact `.wld` SHA-256; if another save changes the source before publication, that build is rejected rather than pairing old runtime state with a new source. There is no separate “save as `.vworld`” command because cache refresh is part of the save lifecycle. Never delete the `.wld` after an image is generated.

Generation writes `world.vworld.tmp`, flushes it to durable storage, reopens and validates the complete artifact, then atomically replaces the previous image. Neither the source `.wld` nor the previous valid image is modified before validation succeeds.

## Schema 2

The binary layout is private to `Vega.Terraria`:

```text
VegaWorldImage
|-- SourceWorld     original .wld bytes and SHA-256
|-- TileRuns        64-column chunks of fixed packed tile runs and SHA-256
|-- LiquidState     runtime Liquid/LiquidBuffer queues and counters, when captured
|-- Manifest        schema, Terraria version, snapshot flags, source fingerprint and compiler parameters
`-- Footer          manifest offset, length and SHA-256
```

Tile chunks are restored with at most four workers when Terraria uses the known disjoint-write collection implementation; custom tile collections use the sequential fallback. Runtime snapshots preserve liquid queue entries and their transient tile flags. During startup Vega suppresses `QuickWater`, keeps queues empty through Terraria's old settling loop, and restores the saved queues at the second `WaterCheck` boundary. The fast path requires the exact expected initialization sequence; any format, checksum, source or sequence mismatch falls back to `.wld`.

Chests, signs, NPCs and tile entities still load from the embedded canonical source. Their measured creation time is small compared with tile reconstruction and liquid settling, so schema 2 does not add speculative duplicate indexes for them.

## Startup telemetry

Structured `world-image` log records and `OperationsSnapshot.WorldLoad` expose:

- cache hit/miss and machine-readable reason;
- whether the hit contains a full runtime snapshot;
- fallback and quarantine behavior;
- source/image size;
- file read, tile decode, chest, sign, NPC, tile-entity and post-load durations;
- `.wld`, image-build, image-load and `WorldReady` durations;
- allocation and GC deltas.

## Schema 1 benchmark record

Measurements were collected on 2026-08-20 with the Release build, .NET 10, OTAPI 3.3.11 and three real local worlds. Each server run used a temporary world copy, one player slot and a dedicated listener port. "Image" is a validated warm `.vworld`; OS page-cache state was not forcibly flushed, so the figures are engineering evidence rather than a hardware-neutral promise.

| World | Dimensions | Path | Tile decode | Load body | Peak working set | Allocated | GC 0/1/2 |
|---|---:|---|---:|---:|---:|---:|---:|
| Small | 4200×1200 | `.wld` | 162.3 ms | 1465.8 ms | 554 MiB | 4.55 GiB | 284/23/1 |
| Small | 4200×1200 | `.vworld` | 384.9 ms | 1705.0 ms | 571 MiB | 8.36 GiB | 528/22/1 |
| Medium | 6400×1800 | `.wld` | 262.8 ms | 2435.5 ms | 763 MiB | 9.67 GiB | 613/46/2 |
| Medium | 6400×1800 | `.vworld` | 534.5 ms | 2639.3 ms | 792 MiB | 12.37 GiB | 784/45/2 |
| Large | 8400×2400 | `.wld` | 651.7 ms | 4138.0 ms | 1041 MiB | 17.94 GiB | 1145/79/3 |
| Large | 8400×2400 | `.vworld` | 758.6 ms | 4199.4 ms | 1095 MiB | 18.33 GiB | 1167/86/3 |

The standalone compiler benchmark recorded image build times of 253 ms, 617 ms and 504 ms, with bounded managed allocations below 0.7 MiB. Images were 17.6 MB, 39.8 MB and 65.2 MB for source files of 3.1 MB, 6.8 MB and 11.7 MB. Validation cost 13 ms, 29 ms and 48 ms respectively.

That result rejected tile-only schema 1 as a default startup path and directly motivated profiling the complete post-load phase instead of treating binary tile parsing as the whole load.

## Schema 2 runtime-snapshot smoke measurements

The same machine was used for live server comparisons after a real `save` produced schema 2. These are single engineering smoke samples, not the full cold/warm three-world enablement gate.

| World | Dimensions | Path | Tile decode | Post-load | Load body | Allocated |
|---|---:|---|---:|---:|---:|---:|
| Small | 4200×1200 | `.wld` | 240.0 ms | 753.4 ms | 1854.3 ms | 5.28 GB |
| Small | 4200×1200 | runtime `.vworld` | 163.7 ms | 84.6 ms | 1112.5 ms | 2.09 GB |
| Medium | 6400×1800 | `.wld` | 387.9 ms | 925.6 ms | 2954.9 ms | 10.61 GB |
| Medium | 6400×1800 | runtime `.vworld` | 341.5 ms | 174.3 ms | 2180.9 ms | 5.96 GB |

The meaningful gain came from restoring prepared liquid state, not from the tile decoder alone: approximately 40% faster on the small sample and 26% on the medium sample, with substantially fewer allocations. Snapshot capture after save took 192–254 ms on the small world and 501 ms on the medium world; durable publication then took 44–94 ms on the worker. Image mode remains opt-in until repeated cold/warm measurements, the large world, active-liquid snapshots and compatibility fallback are exercised together.

## Reproducing compiler measurements

```powershell
dotnet run --project benchmarks/Vega.WorldImage.Benchmarks -c Release -- small.wld medium.wld large.wld
```

The harness copies inputs into a temporary directory, builds and validates images there, emits JSON, and removes the temporary copies. It never writes beside the supplied worlds.
