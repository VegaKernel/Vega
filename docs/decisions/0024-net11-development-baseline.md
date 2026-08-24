# 0024: Adopt .NET 11 Preview 7 as the development baseline

## Status

Accepted.

## Context

Phase 18 originally deferred the .NET 11 migration until the stable release. Vega now has the native runtime, persistence, module, sandbox and scene boundaries needed to evaluate the new runtime without mixing the framework transition with unfinished compatibility scaffolding. Phase 22 will substantially redesign packet preparation and game-thread budgeting, so implementing and tuning that pipeline on .NET 10 first would create a second framework-specific optimization pass immediately afterward.

The last .NET 10 baseline before this migration is commit `2a528ce8efb78e3b223f5b722bbc8a990c7ce3e7`. It remains the rollback and framework-comparison reference until .NET 11 production promotion is complete.

The selected development SDK is `.NET SDK 11.0.100-preview.7.26381.103`. .NET 11 is still a preview runtime, so adopting it for active development does not by itself make it the stable production channel.

The current Terraria integration chain uses TerrariaRuntime 1.0.0, which provides native `net11.0` runtime and hook assemblies and removes the former ModFramework dependency. Multiplicity and the resolved MonoMod stack still select compatible lower-target assets, and MonoMod retains its explicit unsupported-target warning. The full package graph and its owned exception are recorded in the [.NET 11 dependency audit](../net11-dependency-audit.md). A successful restore and build therefore do not replace the required hook, world, network and shutdown smoke tests before production promotion.

## Decision

- Target `net11.0` for all first-party Vega runtime, libraries, bundled modules, tests and benchmark harnesses during active development.
- Pin `11.0.100-preview.7.26381.103` in `global.json`, CI and release workflows, and use the matching version-specific Preview 7 container tags so framework comparisons are reproducible.
- Keep the server runtime on CoreCLR with tiered JIT and dynamic PGO. Do not introduce NativeAOT into `Vega.Server` as part of this migration.
- Enable .NET 11 Runtime Async for every first-party project from the migration baseline through the central `runtime-async=on` compiler feature. Do not silently disable it for individual projects.
- Publish the first-party library surface as the single authoritative `Vega` package on `net11.0`; do not multi-target the complete repository merely to preserve .NET 10. A future narrow package split requires an actual supported consumer and a separately measured compatibility decision.
- Consume the single `TerrariaRuntime` package directly from `Vega.Terraria`; keep its runtime-specific assets and MonoMod.Core propagation owned by TerrariaRuntime instead of duplicating fallback resolution in Vega.
- Use `sdk:11.0.100-preview.7` and `runtime:11.0.0-preview.7` for Linux container stages. Use `runtime:11.0.0-preview.7-nanoserver-ltsc2025` for Windows because the preview runtime image is not published for LTSC 2022.
- Treat RC/GA dependency revalidation, deployment-host soak, representative world startup and performance comparison as production-promotion gates rather than blockers for the development branch.

## Validation

The development migration must prove at least:

- restore, release build and the complete unit/integration/architecture test suite on the pinned SDK;
- the PostgreSQL/MySQL external-provider contracts on the same SDK;
- world-image benchmark harness compilation;
- tagged/manual release workflow use of the same SDK;
- deployable runtime layout with TerrariaRuntime, TerrariaRuntime.Hooks and the required MonoMod.Core asset;
- a clean .NET 11 container build for supported container targets before production promotion;
- no global warning suppressions added merely to make the preview compile;
- a metadata guard proving representative first-party async methods carry `MethodImplAttributes.Async`;
- a recorded .NET 10 versus .NET 11 Runtime Async baseline before Phase 22 packet-pipeline changes are attributed to the framework.

## Consequences

- Phase 22 is designed and benchmarked on the runtime Vega intends to carry forward rather than on a soon-to-be-retired development baseline.
- .NET 10 remains a deliberate rollback/benchmark point, not a second continuously supported server target.
- Preview-specific runtime or dependency failures remain visible and actionable; they are not hidden behind compatibility shims.
- Production deployment can stay on the last verified .NET 10 build until the .NET 11 RC/GA and soak gates are satisfied.
