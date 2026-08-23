# 0006: Target .NET 10 LTS

## Status

Superseded for active development by ADR 0024; retained as the .NET 10 production, benchmark and rollback baseline.

## Context

The modernization work targets .NET 10 as the production runtime baseline. The server is sensitive to runtime performance, GC latency, networking overhead and JIT quality, so framework changes require server-specific validation.

## Decision

Target `net10.0` for Vega first-party runtime, Core, Contracts, modules, tests and production packaging unless a later ADR changes the baseline.

Do not target a later framework until it is a stable release and has been evaluated with representative server benchmarks and ecosystem checks.

## Amendment from ADR 0016

The original TShockNG migration validated the complete TShockNG/TSAPI stack on .NET 10. That result remains useful historical migration evidence, but TSAPI is no longer part of Vega's target architecture and therefore is not a permanent acceptance dependency for future framework upgrades.

Future baseline validation must focus on the native Vega runtime path and its actual dependencies.

## Validation expectations

Framework upgrades should verify at least:

- restore/build/test/pack for Vega first-party projects;
- runtime/container packaging;
- startup overhead and readiness budgets from ADR 0017;
- packet throughput and allocation behavior;
- tick latency and GC behavior under representative load;
- native Terraria/OTAPI integration used by Vega at that time.

## Consequences

- New Vega APIs may use the .NET 10 / C# 14 baseline where useful.
- Framework-version microbenchmarks are not treated as guaranteed whole-server speedups.
- Later upgrades require a separate evaluation and explicit decision.
