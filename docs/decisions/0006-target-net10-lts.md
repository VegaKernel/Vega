# 0006: Target .NET 10 LTS

## Status

Accepted

## Context

TShockNG previously targeted .NET 9. The modernization work is being done while .NET 10 is a supported LTS release and .NET 11 remains outside the production baseline.

The server is sensitive to runtime performance, GC latency, networking overhead, and JIT code quality. A framework upgrade therefore has value beyond access to newer language features, but only when the complete TShockNG/TSAPI stack remains buildable and testable on the new runtime.

## Decision

Target `net10.0` across TShockNG, TSAPI, launcher/test projects, build tooling, CI and runtime/container packaging.

Do not target a later framework until it is a stable release and has been evaluated with server-specific benchmarks and ecosystem checks.

ADR 0015 supersedes the former requirement to preserve historical TShockAPI binary compatibility during this migration. Existing compiled plugins are not an acceptance criterion for the .NET 10 baseline. The supported transition surface is source-level registration through `IHookRegistry<TEventArgs>` and `ILegacyCommandRegistry`; plugins may need to be rebuilt for TShockNG.

## Rationale

.NET 10 is an LTS release and contains runtime/JIT/GC/networking improvements that are relevant to TShockNG's packet-processing and server workloads. It also provides a longer support window than .NET 9.

The expected performance benefit is workload-dependent. Framework-version microbenchmarks must not be presented as guaranteed whole-server speedups. TShockNG should benchmark packet throughput, allocations, GC pauses, and tick latency before and after the migration.

## Validation

The .NET 10 baseline is accepted because the branch validates the production build path on .NET 10:

- the TShockNG solution restores, builds, tests and packs successfully;
- TSAPI restores, builds and tests successfully against the pinned TerrariaServerAPI/OTAPI baseline;
- launcher, test and project tooling use the .NET 10 target;
- runtime/container configuration uses the .NET 10 baseline;
- package dependencies used by the migration are pinned explicitly;
- source-facing plugin registration is covered by ADR 0015 rather than by a binary-compatibility requirement.

CI run 95 on `agent/net10-evaluation` is the acceptance run for this decision. Both the `TShockNG` and `TSAPI` jobs completed successfully.

## Consequences

- New TShockNG APIs may use the .NET 10 / C# 14 baseline where useful.
- Existing compiled plugins may require rebuilding; this is intentional under ADR 0015.
- New Core code continues to target the modern Contracts/Core architecture instead of preserving obsolete globals or public metadata solely for binary compatibility.
- Later framework upgrades require a separate evaluation and explicit decision.
