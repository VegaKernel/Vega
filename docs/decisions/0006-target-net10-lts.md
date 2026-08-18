# 0006: Target .NET 10 LTS

## Status

Proposed

## Context

TShockNG currently targets .NET 9. The modernization work is being done while .NET 10 is already a supported LTS release and .NET 11 is still in preview.

The server is sensitive to runtime performance, GC latency, networking overhead, and JIT code quality. A framework upgrade therefore has value beyond access to newer language features, but only if the complete TShockNG/TSAPI/OTAPI stack remains compatible.

## Proposed decision

Target `net10.0` across TShockNG, TSAPI, ProtocolGuard, launcher/test projects, release workflows, and runtime/container packaging after the full compatibility CI passes.

Do not target .NET 11 while it is a preview release. Re-evaluate .NET 11 after GA with server-specific benchmarks and ecosystem compatibility checks.

## Rationale

.NET 10 is an LTS release and contains runtime/JIT/GC/networking improvements that are relevant to TShockNG's packet-processing and server workloads. It also provides a substantially longer support window than .NET 9.

The expected performance benefit is workload-dependent. Framework-version microbenchmarks must not be presented as guaranteed whole-server speedups. TShockNG should benchmark packet throughput, allocations, GC pauses, and tick latency before and after the migration.

## Compatibility requirements

The decision becomes Accepted only after all of the following pass:

- TSAPI builds and tests against `OTAPI.Upcoming 3.3.11` on .NET 10;
- ProtocolGuard builds/tests/packs on .NET 10 while consuming `TZ.Multiplicity 2.6.0`;
- the complete TShockNG solution restores, builds, tests, and packs on .NET 10;
- launcher publishing works while consuming the current OTAPI/ModFramework package assets;
- container/runtime configuration is updated and validated;
- representative legacy plugin binaries still load under the .NET 10 server runtime.

## Consequences

- New TShockNG APIs may use the .NET 10 / C# 14 baseline where useful.
- The legacy compatibility facade retains historical assembly identities; changing the target framework does not justify breaking public assembly/type names.
- .NET 11 remains an evaluation target only until a later explicit decision.
