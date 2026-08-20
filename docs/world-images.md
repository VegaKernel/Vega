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

On the first enabled load Vega uses `.wld` and builds `world.vworld` in the background. A later load reads the embedded source section and optimized tile runs from `.vworld`, but only after validating the source content fingerprint, Terraria format version, Vega schema, compiler parameters, manifest and section checksums. A stale or incompatible image is rebuilt after normal startup. A corrupt image is moved to `world.vworld.corrupt`; startup continues from `.wld`.

Terraria's normal `save`, shutdown save, and configurable autosave all write the authoritative `.wld`. After every completed local save, Vega queues one coalesced image rebuild. Concurrent save requests cannot run competing `.vworld.tmp` writers, and shutdown waits for the pending rebuild. The next start can therefore use the current image instead of deliberately taking a stale-cache fallback. There is no separate “save as `.vworld`” command because cache refresh is part of the save lifecycle. Never delete the `.wld` after an image is generated.

Generation writes `world.vworld.tmp`, flushes it to durable storage, reopens and validates the complete artifact, then atomically replaces the previous image. Neither the source `.wld` nor the previous valid image is modified before validation succeeds.

## Schema 1

The binary layout is private to `Vega.Terraria`:

```text
VegaWorldImage
|-- SourceWorld     original .wld bytes and SHA-256
|-- TileRuns        64-column chunks of fixed packed tile runs and SHA-256
|-- Manifest        schema, Terraria version, source fingerprint and compiler parameters
`-- Footer          manifest offset, length and SHA-256
```

Only the measured stock tile decoder was cached. No speculative chest, sign, entity, wiring, liquid, spatial or player-sync index was added because there is not yet a benchmarked consumer that benefits from one.

## Startup telemetry

Structured `world-image` log records and `OperationsSnapshot.WorldLoad` expose:

- cache hit/miss and machine-readable reason;
- fallback and quarantine behavior;
- source/image size;
- file read, tile decode, chest, sign, NPC, tile-entity and post-load durations;
- `.wld`, image-build, image-load and `WorldReady` durations;
- allocation and GC deltas.

## Vega 1.0 benchmark record

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

Conclusion: keep `.wld` enabled by default in Vega 1.0. Continue using the schema-1 implementation as an opt-in research path; do not claim a speedup or add Level 3 lazy/memory-mapped ownership until end-to-end results improve materially.

## Reproducing compiler measurements

```powershell
dotnet run --project benchmarks/Vega.WorldImage.Benchmarks -c Release -- small.wld medium.wld large.wld
```

The harness copies inputs into a temporary directory, builds and validates images there, emits JSON, and removes the temporary copies. It never writes beside the supplied worlds.
