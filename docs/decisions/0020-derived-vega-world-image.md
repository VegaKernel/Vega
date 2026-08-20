# ADR 0020: Derived Vega World Image for runtime loading

Status: Accepted and evaluated for Vega 1.0

## Context

Terraria `.wld` is the compatibility and persistence format understood by Terraria and the surrounding ecosystem. Replacing it with a Vega-only mandatory format would increase recovery risk, complicate interoperability and couple Core to a storage implementation detail.

At the same time, startup may repeatedly pay for binary decoding, object reconstruction and runtime-index construction that could potentially be compiled once into a representation optimized for Vega startup.

The correct optimization target is therefore a derived runtime artifact, not a new canonical world format.

## Decision

Keep Terraria `.wld` as the canonical source of truth and add an optional, disposable, versioned Vega World Image, provisionally using the `.vworld` extension.

Invariant:

```text
.wld    = canonical compatibility/persistence format
.vworld = disposable optimized runtime representation
```

The server must always be able to start from `.wld` when the runtime image is missing, stale, unsupported or corrupt.

The `.vworld` format is an internal implementation detail of `Vega.Terraria` / world infrastructure. `Vega.Core` must not depend on its physical layout, serializer, checksum scheme or file extension.

## Evolution levels

### Level 1: standard `.wld` loader

Preserve and profile the stock-compatible load path. This remains the required fallback and recovery path.

### Level 2: derived `.vworld` full-load image

This is the practical optimization target.

The first implementation should optimize full startup by:

- sequential reads;
- minimal branching/parsing on the hot path;
- bounded temporary allocations;
- fast reconstruction of the Terraria/runtime structures actually needed at startup;
- precomputation only where benchmarks prove repeat work is expensive.

### Level 3: chunked / memory-mapped backend

Research only after Level 2 is measured and stable.

Possible goals include:

- lazy section loading;
- very large worlds;
- multiple/sandboxed world contexts;
- memory-mapped or alternative backing stores.

Do not make Level 3 part of Vega 1.0 unless profiling and Terraria runtime compatibility justify the additional ownership/storage complexity.

## Format shape

The world image must be sectioned/chunked rather than a serialization of one large runtime object graph.

A representative logical layout is:

```text
VegaWorldImage
├── Header
├── WorldMetadata
├── TileSections
│   ├── Section 0
│   ├── Section 1
│   └── ...
├── Chests
├── Signs
├── TileEntities
├── RuntimeIndexes
└── Checksums
```

The exact binary layout is implementation-specific and may evolve by schema version.

## Header and compatibility metadata

The image should carry enough information to validate that it can safely represent the current source/runtime assumptions, including at least:

- Vega world-image magic/format identifier;
- Vega world-image schema version;
- Terraria world-format/version identity;
- source `.wld` fingerprint/hash;
- critical compiler/runtime-image parameters;
- section table/offset metadata;
- checksums sufficient to detect corruption.

Schema evolution must be explicit. A loader must not silently reinterpret an older incompatible layout as the current one.

## Cache validity

Do not use timestamps as the sole validity criterion.

At minimum, invalidate and rebuild when any of the following changes:

- source `.wld` content fingerprint;
- Terraria world-format/version assumptions;
- Vega world-image schema version;
- critical runtime-image compiler parameters.

Timestamps and file sizes may be used as cheap preliminary signals, but content/version metadata decides validity.

Every cache miss/invalidation should record a machine-readable reason for observability.

## Candidate contents

The runtime image may contain:

- world metadata;
- predecoded tile sections;
- chests;
- signs;
- tile entities;
- required world-state data;
- measured expensive post-load precomputation;
- runtime indexes with actual consumers.

Potential indexes include:

- chest lookup;
- tile entities by section/chunk;
- signs by section/chunk;
- active wiring/liquid areas;
- spatial indexes;
- data useful for initial player world synchronization;
- data useful to world map/operations APIs.

Every optional index requires both a real consumer and a benchmark showing measurable value.

Do not cache data that is very cheap to compute, invalidates frequently, creates heavy schema coupling or provides no measured startup/runtime benefit.

## Atomic generation

World-image generation must never threaten canonical persistence or the previous valid cache.

Required write pattern:

```text
world.vworld.tmp
        |
        v
complete write
        |
        v
flush + checksum validation
        |
        v
atomic replace
        |
        v
world.vworld
```

A crash during generation must leave `.wld` untouched and must not replace a previously valid `.vworld` with a partial artifact.

## Persistence boundary

This ADR does not change the canonical save path.

Initial world saving remains compatible with Terraria `.wld`. `.vworld` is regenerated/updated as a cache and must not become the only authoritative persistence path without a separate architectural decision. Manual, automatic and shutdown saves first complete the canonical `.wld` write and then enqueue a coalesced atomic image rebuild. Orderly shutdown waits for that rebuild so the next start does not inherit a deliberately stale cache.

Operators may select `-world path/to/world.vworld` directly. The runtime treats this as an explicit cache-mode request, resolves the companion `.wld` before Terraria argument processing, and still validates the image against that canonical source. Direct selection changes operator ergonomics, not persistence ownership.

## Loader boundary

Preferred ownership:

```text
Vega.Core
    |
    v
world contracts / snapshots

Vega.Terraria
    |-- TerrariaWldLoader
    |-- VegaWorldImageCompiler
    |-- VegaWorldImageLoader
    `-- WorldRuntimeIndexes
```

The concrete type names may change, but Core must remain isolated from binary layout and serializer concerns.

## Profiling before implementation

Before implementing the derived format, profile the existing load path and measure separately:

- source file reading;
- tile decoding;
- temporary allocations;
- GC activity;
- chest/sign/tile-entity creation;
- post-load initialization;
- runtime-index construction;
- other Vega/runtime operations between file load and `WorldReady`.

The purpose is to optimize measured bottlenecks. Do not assume binary parsing dominates startup merely because it is easy to blame.

## Startup telemetry

Integrate world-image lifecycle with ADR 0017 startup telemetry.

Expose at least:

- `WldLoadDuration`;
- `WorldImageBuildDuration`;
- `WorldImageLoadDuration`;
- `PostLoadInitializationDuration`;
- `WorldReadyDuration`;
- cache hit/miss;
- invalidation reason;
- `.wld` size;
- `.vworld` size;
- validation/corruption failure;
- fallback to `.wld`;
- allocations/GC deltas where measurement is practical and cheap.

This data should also be available to the Operations API once that surface exists.

## Failure and fallback semantics

A corrupt, stale or incompatible `.vworld` must not prevent server startup.

Required behavior:

1. emit a diagnostic/metric explaining why the image is unusable;
2. discard or quarantine the invalid cache as appropriate;
3. load the canonical `.wld`;
4. continue toward `WorldReady` / `NetworkReady`;
5. rebuild the image safely when policy and runtime conditions permit.

World-image errors are cache failures, not canonical world corruption unless the `.wld` path independently fails.

## Benchmarks and default enablement

Do not claim a startup improvement before measurement.

Before enabling `.vworld` by default, benchmark representative small, medium and large production-like worlds using at least:

- cold `.wld` startup;
- warm `.wld` startup;
- cold `.vworld` startup;
- warm `.vworld` startup;
- peak memory;
- allocations;
- GC behavior;
- `WorldReady`;
- `NetworkReady`.

Default enablement should be based on repeatable evidence that the runtime image materially improves startup/load cost without unacceptable memory, complexity or reliability tradeoffs.

## Consequences

Vega can optimize world startup aggressively while preserving Terraria compatibility and recovery. The optimization remains disposable and isolated in the Terraria integration layer, which prevents a custom binary cache from becoming another architectural dependency that the rest of the server is forced to understand.

## Vega 1.0 evaluation

Schema 1 was implemented as a sectioned image containing the canonical source bytes, a chunked stream of packed tile runs, a checksummed manifest and a fixed checksummed footer. Generation uses a same-directory temporary file, durable flush, complete validation and atomic replacement. Cache selection validates the source SHA-256 fingerprint, Terraria version, schema, compiler parameters and both section checksums. Any failure returns to the canonical `.wld` loader; corrupt images are quarantined.

Representative end-to-end measurements on 4200×1200, 6400×1800 and 8400×2400 worlds did not show a repeatable startup improvement. The image was 5.6–5.8 times larger, its current tile reconstruction path was slower on small and medium worlds, and peak working set increased. Vega 1.0 therefore keeps the feature disabled by default and exposes it only through `--vega-world-image=on` for continued profiling. See [`docs/world-images.md`](../world-images.md) for measurements and operating details.

Level 3 was investigated and rejected for Vega 1.0. Terraria eagerly owns and traverses `Main.tile`, world generation and player synchronization assume a complete in-memory world, and memory mapping the cache would not remove object reconstruction or integrity validation. Lazy/mapped ownership needs a separate compatibility experiment and materially better evidence before it can replace those assumptions.

### Schema 2 runtime-snapshot addendum

Tile-only schema 1 proved that parsing was not the dominant remaining cost: Terraria still ran `QuickWater`, two `WaterCheck` passes and the liquid-settling loop after reconstruction. Schema 2 therefore captures immutable packed runtime tiles plus `Liquid` / `LiquidBuffer` entries and algorithm counters immediately after a successful canonical save. The source hash captured with that state must still match when the asynchronous image writer begins, preventing a runtime snapshot from being paired with a later `.wld`.

On a validated runtime-image hit, Vega restores tiles first, suppresses `QuickWater`, presents empty queues to the legacy settling loop, then restores the saved liquid queues at the second `WaterCheck` boundary. This preserves pending runtime liquid work instead of pretending that every save is fully settled. The optimization is accepted only for the exact observed two-check sequence; a changed sequence, invalid queue bounds, corrupt section or any other load exception triggers the existing canonical fallback.

Live smoke measurements reduced full load from 1854 to 1113 ms on a 4200×1200 world and from 2955 to 2181 ms on a 6400×1800 world. Post-load initialization fell from 753 to 85 ms and from 926 to 174 ms respectively. These results materially improve Level 2, but the feature remains opt-in pending repeated cold/warm, large-world and active-liquid validation.
